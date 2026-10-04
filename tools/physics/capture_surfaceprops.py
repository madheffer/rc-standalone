"""capture_surfaceprops.py <addon> <map> <out.json> -- RED2's surface_prop list as CompilePhysics builds it.

CModelDocCompileInstance::CompilePhysics (resourcecompiler 0923: 18032e3e0)
walks the node loop's parts and, for every shape, adds the shape's surface
string (+0xf0) to the symbol table at +0x458 (the call at 18032ebd0). RED2's
m_SubassetReferences.surface_prop lists that table's non-empty strings in
insertion order. This records every one of those calls: the part, the shape,
its surface string, its type (+0x90) and its material (+0xf8), in order.

The compile rewrites the map's .vpk, so it is backed up first and restored
after. CS2 must be closed, and the script refuses to start if it is running or
another compile is.
"""
import argparse
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
function cstr(p) { try { const s = p.readPointer(); return s.isNull() ? null : s.readUtf8String(); } catch (e) { return '?'; } }
let seq = 0;
function hook(m) {
  Interceptor.attach(m.base.add(%(site)d), {
    onEnter() {
      const c = this.context, part = c.r14, index = c.r15.toInt32() / 8;
      const shape = part.add(0x58).readPointer().add(index * 8).readPointer();
      send({seq: seq++, part: part.toString(), shape: index, surface: cstr(shape.add(0xf0)),
            type: shape.add(0x90).readS32(), material: cstr(shape.add(0xf8)), table: c.rcx.toString()});
    }
  });
  send({hooked: m.base.toString()});
}
const found = Process.findModuleByName('resourcecompiler.dll');
if (found) hook(found);
else Process.attachModuleObserver({
  onAdded(m) { if (m.name.toLowerCase() === 'resourcecompiler.dll') hook(m); }
});
"""


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("map")
    p.add_argument("out")
    a = p.parse_args()
    site = installed("resourcecompiler", 0x32ebd0)
    if busy():
        raise SystemExit("CS2 or another resourcecompiler is running; not starting")
    if low_disk():
        raise SystemExit("under 10 GB free on the game drive; not starting")
    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = vpk + ".capture_backup"
    if os.path.exists(vpk):
        shutil.move(vpk, backup)
    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
            "-i", source, "-world", "-phys", "-fshallow"]
    calls = []
    lock = threading.Lock()

    def on_message(msg, data):
        if msg.get("type") != "send":
            print(msg, flush=True)
            return
        pay = msg["payload"]
        if "seq" not in pay:
            print(pay, flush=True)
            return
        with lock:
            calls.append(pay)

    try:
        dev = frida.get_local_device()
        pid = dev.spawn(argv, cwd=BIN, stdio="pipe")
        ses = dev.attach(pid)
        sc = ses.create_script(AGENT % {"site": site})
        sc.on("message", on_message)
        sc.load()
        done = threading.Event()
        log = []
        dev.on("output", lambda pid_, fd, d: log.append(d.decode("utf-8", "replace")) if d else None)
        ses.on("detached", lambda *x: done.set())
        dev.resume(pid)
        done.wait()
        with open(a.out + ".log", "w", encoding="utf-8") as fh:
            fh.write("".join(log))
    finally:
        if os.path.exists(backup):
            shutil.move(backup, vpk)
    with open(a.out, "w", encoding="utf-8") as fh:
        json.dump(calls, fh)
    print(a.out, len(calls), "calls")


if __name__ == "__main__":
    main()
