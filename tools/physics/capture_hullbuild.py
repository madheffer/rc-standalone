"""capture_hullbuild.py <addon> <map> <out.json> [--full] -- the points the map builder hulls.

resourcecompiler's MapBuilder_HullBuild (0923: 18131efc0) is the map
builder's copy of RnHullCreate's build: (int *error, Vector3 *points, int
count, options *). This records every call's points, its options block
(0x40 bytes) and a short backtrace, so a brush entity's hulls can be rebuilt
by the port from Valve's own input (RnHullBuilder.BuildHull) and compared
with the shipped hulls: equal means the port's input differs, unequal means
the hull build does.

With --trimesh it also records each MapBuilder_TriangleMesh call (0923:
181308060; half-edge mesh, Vector3 *positions, int count, int *indices, int
triangles): the vertex buffer and triangles the half-edge mesh is built
from, whose vertices the hull groups are made of.

The compile is forced (-f) and overwrites the map's .vpk, so the .vpk is
backed up beside the output and restored. CS2 must be closed; the script
refuses to start if it is running or another compile is.
"""
import argparse
import filecmp
import json
import os
import shutil
import sys
import threading

import frida

from capture_physshapes import BIN, CS2, busy, low_disk
sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "re"))
from rva_map import installed  # noqa: E402

AGENT = r"""
'use strict';
function hex(b) { return Array.from(new Uint8Array(b)).map(x => x.toString(16).padStart(2, '0')).join(''); }
function trace(ctx) {
  return Thread.backtrace(ctx, Backtracer.ACCURATE).slice(0, 6).map(a => {
    const mod = Process.findModuleByAddress(a);
    return mod ? mod.name.replace('.dll', '') + '+0x' + a.sub(mod.base).toString(16) : a.toString();
  });
}
let n = 0;
function hook() {
  const rc = Process.findModuleByName('resourcecompiler.dll');
  if (rc === null) { setTimeout(hook, 5); return; }
  Interceptor.attach(rc.base.add(%(rva)d), {
    onEnter(args) {
      const count = args[2].toInt32();
      const e = {call: n++, count: count, stack: trace(this.context)};
      try {
        e.points = hex(args[1].readByteArray(count * 12));
        e.options = hex(args[3].readByteArray(0x40));
      } catch (err) { e.err = String(err); }
      send(e);
    }
  });
  if (%(trimesh)d) Interceptor.attach(rc.base.add(%(trimesh)d), {
    onEnter(args) {
      const count = args[2].toInt32(), tris = args[4].toInt32();
      const e = {trimesh: n++, count: count, triangles: tris, stack: trace(this.context)};
      try {
        e.points = hex(args[1].readByteArray(count * 12));
        e.indices = hex(args[3].readByteArray(tris * 12));
      } catch (err) { e.err = String(err); }
      send(e);
    }
  });
  send({hooked: rc.base.toString()});
}
hook();
"""


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("map")
    p.add_argument("out")
    p.add_argument("--full", action="store_true", help="a full compile rather than -world -fshallow")
    p.add_argument("--trimesh", action="store_true", help="also record MapBuilder_TriangleMesh's input")
    a = p.parse_args()
    rva = installed("resourcecompiler", 0x131efc0, build="20260923")
    trimesh = installed("resourcecompiler", 0x1308060, build="20260923") if a.trimesh else 0
    if busy():
        raise SystemExit("CS2 or another resourcecompiler is running; not starting")
    if low_disk():
        raise SystemExit("under 10 GB free on the game drive; not starting")
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = a.out + ".vpk.bak"
    if os.path.exists(vpk):
        shutil.copyfile(vpk, backup)
    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-f", "-game", os.path.join(CS2, "game", "csgo"), "-i", source]
    if not a.full:
        argv += ["-world", "-fshallow"]
    calls = []
    lock = threading.Lock()

    def on_message(msg, data):
        if msg.get("type") != "send":
            print(msg, flush=True)
            return
        pay = msg["payload"]
        if "call" not in pay and "trimesh" not in pay:
            print(pay, flush=True)
            return
        with lock:
            calls.append(pay)

    dev = frida.get_local_device()
    pid = dev.spawn(argv, cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT % {"rva": rva, "trimesh": trimesh})
    sc.on("message", on_message)
    sc.load()
    done = threading.Event()
    ses.on("detached", lambda *x: done.set())
    dev.resume(pid)
    done.wait()
    with open(a.out, "w", encoding="utf-8") as fh:
        json.dump(calls, fh)
    print(a.out, len(calls), "calls")
    if os.path.exists(vpk):
        shutil.copyfile(vpk, a.out + ".vpk")
    if os.path.exists(backup):
        shutil.copyfile(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored" if ok else "RESTORE CMP FAILED", vpk)
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
