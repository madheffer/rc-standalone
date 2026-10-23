"""capture_pbmeshnode.py <addon> <map> [--out file]: physicsbuilder's mesh nodes for deformed prop shapes.

physicsbuilder's InitMeshShapeFromVertices (0924: 180153ba0; (node, CUtlVector<Vector> *points, CUtlVector<int>
*indices, matrix3x4 *matrix, PropDeformer *deformer)) moves a prop shape's points by the matrix, runs them through
the deformer with an identity matrix and reverses the indices for a mirrored lattice. This records, for every call
with a deformer: the matrix and the deformer struct's first 0x80 bytes as float bits, the points on entry and on
return (after the move and the deformation), one JSON line each. CS2 must be closed; the map's .vpk is backed up
and restored.
"""
import argparse
import json
import os
import sys
import threading
import time

import frida

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "re"))
from package_guard import PackageGuard  # noqa: E402
from rva_map import installed  # noqa: E402

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")

AGENT = r"""
'use strict';
function bits(p, n) { const r = []; for (let i = 0; i < n; i++) r.push(p.add(4 * i).readU32()); return r; }
function points(v) { const n = v.readS32(); const d = v.add(8).readPointer(); return n > 0 ? bits(d, n * 3) : []; }
function hook() {
  const m = Process.findModuleByName('physicsbuilder.dll');
  if (m === null) { setTimeout(hook, 5); return; }
  send({ev: 'hooked'});
  Interceptor.attach(m.base.add(%(meshnode)d), {
    onEnter(args) {
      this.on = !args[4].isNull();
      if (!this.on) return;
      this.pts = args[1];
      this.rec = {ev: 'node', matrix: bits(args[3], 12), deformer: bits(args[4], 32), in: points(args[1])};
    },
    onLeave(ret) {
      if (!this.on) return;
      this.rec.out = points(this.pts);
      send(this.rec);
    }
  });
}
hook();
"""


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("map")
    p.add_argument("--out")
    a = p.parse_args()
    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "pbmeshnode_%s.jsonl" % a.map)
    out = open(out_path, "w", encoding="utf-8")
    stats = {}

    def on_message(message, data):
        if message["type"] != "send":
            print("agent:", message.get("description") or message, file=sys.stderr)
            return
        payload = message["payload"]
        stats[payload["ev"]] = stats.get(payload["ev"], 0) + 1
        out.write(json.dumps(payload) + "\n")

    rva = {"meshnode": installed("physicsbuilder", 0x153ba0, build="20260924")}
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    with PackageGuard(vpk, out_path + ".vpk.bak") as guard:
        source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
        argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
                "-i", source, "-world", "-fshallow", "-phys"]
        device = frida.get_local_device()
        pid = device.spawn(argv, cwd=BIN, stdio="pipe")
        guard.pid = pid
        session = device.attach(pid)
        script = session.create_script(AGENT % rva)
        script.on("message", on_message)
        script.load()
        done = threading.Event()
        session.on("detached", lambda *x: done.set())
        started = time.time()
        device.resume(pid)
        done.wait()
        out.close()
        print("%.1fs  %s  -> %s" % (time.time() - started, stats, out_path))


if __name__ == "__main__":
    main()
