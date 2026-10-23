"""capture_cable.py <addon> <map> [--out file]: a cable's collision tube, stage by stage, during a map compile.

CMapCable::vf277 (rc 0923: 18114d850) builds a cable's physics mesh: CMapPath_IsLoop (1810b7ca0) gives the
control points, FUN_1812802d0 samples the spline, FUN_1810b8ae0 builds the tube, and with the cable's
physicsSimplificationError above 0 CMapCable_SimplifyPhysics (181156860) welds and simplifies it. Records, in call
order and tagged with the vf277 call they belong to:
  'cable'    vf277 entry: closedLoop +0x1864, sides +0x187c, spacing +0x1880, radius +0x1884, flip +0x1888,
             error +0x18a4 (the fields the function reads)
  'controls' IsLoop's control points (9 floats a node) on its return
  'samples'  FUN_1812802d0's positions on its return
  'tube'     FUN_1810b8ae0's indices and positions on its return
  'out'      vf277's indices and positions on its return
  'dme'      FUN_1810bcbc0's positions and indices on entry: the mesh CMapCable::vf200 builds (render or physics)
Blobs: floats then ints as the json says. The compile is -world -fshallow -phys with the map's .vpk moved aside and
restored (tools/package_guard.py). CS2 must be closed.
"""
import argparse
import json
import os
import struct
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
let seq = -1, inside = 0;
// CUtlVector: +0 count, +8 data.
function vec(v, size) {
  const n = v.readS32();
  return {n: n, data: n > 0 ? v.add(8).readPointer().readByteArray(n * size) : new ArrayBuffer(0)};
}
function hook() {
  const m = Process.findModuleByName('resourcecompiler.dll');
  if (m === null) { setTimeout(hook, 5); return; }
  send({ev: 'hooked'});
  Interceptor.attach(m.base.add(%(vf277)d), {
    onEnter(args) {
      seq++; inside++;
      const c = args[0];
      this.idx = args[1]; this.pos = args[2];
      send({ev: 'cable', id: seq, closed: c.add(0x1864).readU8(), sides: c.add(0x187c).readS32(),
            spacing: c.add(0x1880).readFloat(), radius: c.add(0x1884).readFloat(), flip: c.add(0x1888).readU8(),
            error: c.add(0x18a4).readFloat()});
    },
    onLeave(ret) {
      inside--;
      const i = vec(this.idx, 4), p = vec(this.pos, 12);
      const out = new Uint8Array(p.data.byteLength + i.data.byteLength);
      out.set(new Uint8Array(p.data), 0); out.set(new Uint8Array(i.data), p.data.byteLength);
      send({ev: 'out', id: seq, nv: p.n, ni: i.n}, out.buffer);
    }
  });
  Interceptor.attach(m.base.add(%(isloop)d), {
    onEnter(args) { this.c = args[2]; this.on = inside > 0; },
    onLeave(ret) { if (this.on) { const v = vec(this.c, 36); send({ev: 'controls', id: seq, n: v.n}, v.data); } }
  });
  Interceptor.attach(m.base.add(%(spline)d), {
    onEnter(args) { this.p = args[4]; this.on = inside > 0; },
    onLeave(ret) { if (this.on) { const v = vec(this.p, 12); send({ev: 'samples', id: seq, n: v.n}, v.data); } }
  });
  Interceptor.attach(m.base.add(%(tube)d), {
    onEnter(args) { this.i = args[13]; this.p = args[14]; this.on = inside > 0 && !this.i.isNull(); },
    onLeave(ret) {
      if (!this.on) return;
      const i = vec(this.i, 4), p = vec(this.p, 12);
      const out = new Uint8Array(p.data.byteLength + i.data.byteLength);
      out.set(new Uint8Array(p.data), 0); out.set(new Uint8Array(i.data), p.data.byteLength);
      send({ev: 'tube', id: seq, nv: p.n, ni: i.n}, out.buffer);
    }
  });
  Interceptor.attach(m.base.add(%(dme)d), {
    onEnter(args) {
      const i = vec(args[2], 4), p = vec(args[3], 12);
      const out = new Uint8Array(p.data.byteLength + i.data.byteLength);
      out.set(new Uint8Array(p.data), 0); out.set(new Uint8Array(i.data), p.data.byteLength);
      send({ev: 'dme', id: seq, nv: p.n, ni: i.n}, out.buffer);
    }
  });
}
hook();
"""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("addon")
    parser.add_argument("map")
    parser.add_argument("--out")
    args = parser.parse_args()
    out_path = args.out or os.path.join(os.environ.get("TEMP", "."), "cable_capture", "%s.bin" % args.map)
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    out = open(out_path, "wb")
    lock = threading.Lock()
    stats = {}

    def on_message(message, data):
        if message["type"] != "send":
            print("agent:", message.get("description") or message, file=sys.stderr)
            return
        payload = message["payload"]
        stats[payload["ev"]] = stats.get(payload["ev"], 0) + 1
        head = json.dumps(payload).encode()
        blob = data or b""
        with lock:
            out.write(struct.pack("<I", len(head)) + head + struct.pack("<I", len(blob)) + blob)

    rva = {name: installed("resourcecompiler", addr, build="20260923") for name, addr in
           {"vf277": 0x114d850, "isloop": 0x10b7ca0, "spline": 0x12802d0, "tube": 0x10b8ae0,
            "dme": 0x10bcbc0}.items()}
    vpk = os.path.join(CS2, "game", "csgo_addons", args.addon, "maps", args.map + ".vpk")
    with PackageGuard(vpk, out_path + ".vpk.bak") as guard:
        source = os.path.join(CS2, "content", "csgo_addons", args.addon, "maps", args.map + ".vmap")
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
        session.on("detached", lambda *a: done.set())
        started = time.time()
        device.resume(pid)
        done.wait()
        out.close()
        print("%.1fs  %s  -> %s" % (time.time() - started, stats, out_path))


if __name__ == "__main__":
    main()
