"""capture_mapsimplify.py <addon> <map> [--out file]: resourcecompiler's own mesh simplification during a map compile.

Brush entity pieces are simplified in the map builder, not in physicsbuilder:
MapBuilder_MeshFromMapMesh (rc 0923: 181308a42's function) gathers a piece's
positions and triangles (181310620), and with physicsSimplificationError above
0 runs the edge collapse on them directly, no weld first: the input setters
181bf49f0 (triangles) and 181bf4a80 (positions), the loop 181bf9d70, then
MapBuilder_TriangleMesh (181308060) gets the result. The pre-settle world's
PhysMesh_Shapes (18105bb20) and cables (181156860) go through CMesh_Simplify
(1813cb3d0) instead, which reaches the same loop after a 1/32 weld.

Records, in call order: 'tris' and 'points' (the simplifier's input), 'run'
(the error, read at simplifier + 0x68 where 181bf9d70 stores it, the
loop's caller and a short backtrace), and 'trimesh' (181308060's input: the simplified mesh, or the
piece itself when it was not simplified). Each is [u32 json length][json]
[u32 blob length][blob]. The compile is -world -phys -fshallow with the map's
.vpk moved aside and restored (tools/package_guard.py). CS2 must be closed.
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
let seq = 0;
function hook() {
  const m = Process.findModuleByName('resourcecompiler.dll');
  if (m === null) { setTimeout(hook, 5); return; }
  send({ev: 'hooked', base: m.base.toString()});
  Interceptor.attach(m.base.add(%(tris)d), {
    onEnter(args) {
      const n = args[1].toInt32();
      send({ev: 'tris', id: seq, n: n}, n > 0 ? args[2].readByteArray(n * 12) : new ArrayBuffer(0));
    }
  });
  Interceptor.attach(m.base.add(%(points)d), {
    onEnter(args) {
      const n = args[1].toInt32();
      send({ev: 'points', id: seq, n: n}, n > 0 ? args[2].readByteArray(n * 12) : new ArrayBuffer(0));
    }
  });
  Interceptor.attach(m.base.add(%(run)d), {
    onEnter(args) {
      this.s = args[0];
      this.caller = this.returnAddress.sub(m.base).toInt32();
      this.stack = Thread.backtrace(this.context, Backtracer.ACCURATE).slice(0, 8)
        .map(a => { const mm = Process.findModuleByAddress(a); return mm ? mm.name + '+0x' + a.sub(mm.base).toString(16) : a.toString(); });
    },
    onLeave(ret) { send({ev: 'run', id: seq, error: this.s.add(0x68).readDouble(), caller: this.caller, stack: this.stack}); }
  });
  Interceptor.attach(m.base.add(%(trimesh)d), {
    onEnter(args) {
      const n = args[2].toInt32(), t = args[4].toInt32();
      const out = new Uint8Array(n * 12 + t * 12);
      if (n > 0) out.set(new Uint8Array(args[1].readByteArray(n * 12)), 0);
      if (t > 0) out.set(new Uint8Array(args[3].readByteArray(t * 12)), n * 12);
      send({ev: 'trimesh', id: seq++, n: n, t: t}, out.buffer);
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
    out_path = args.out or os.path.join(os.environ.get("TEMP", "."), "simplify_capture", "%s.map.bin" % args.map)
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

    # Analysis-build (0923) RVAs mapped to the installed build.
    rva = {
        "tris": installed("resourcecompiler", 0x1bf49f0, build="20260923"),
        "points": installed("resourcecompiler", 0x1bf4a80, build="20260923"),
        "run": installed("resourcecompiler", 0x1bf9d70, build="20260923"),
        "trimesh": installed("resourcecompiler", 0x1308060, build="20260923"),
    }
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
