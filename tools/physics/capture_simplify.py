"""capture_simplify.py <addon> <map> [--out file]: physicsbuilder's mesh simplification during a map compile.

Every CMesh_Simplify call (pb 1800c5b90 on 0924, through rva_map) is recorded
three times: 'weld' is the piece as PhysicsBuilder_MeshPieces (180016230)
hands it to CMesh_WeldPositions (1800c1ad0) just before, 'in' is the welded
copy the simplifier reads, and 'out' is what it returns. The error arrives in
xmm2, which Frida's context lacks; 18060fdf0 stores it at simplifier + 0x68
first thing, so it is read there on that function's return.

The compile is resourcecompiler -world -fshallow -phys, as corpus.py runs it.
Output: records of u32 json length, json, u32 blob length, blob (the vertex
floats then the u32 indices), as tools/hulls/capture_weld.py writes them.
CS2 must be closed.
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
let seq = 0, inside = null;

// CMesh: +0 vertex floats, +8 streams (0x28 each), +0x10 indices,
// +0x18 vertex count, +0x1c floats per vertex, +0x20 stream count,
// +0x24 index count. A stream: +0 name, +8 semantic, +0x14 first float,
// +0x18 float count, +0x1c ignored in welds, +0x20 type.
function dump(mesh, ev, extra) {
  const nv = mesh.add(0x18).readS32(), stride = mesh.add(0x1c).readS32();
  const ns = mesh.add(0x20).readS32(), ni = mesh.add(0x24).readS32();
  const streams = [];
  const sp = mesh.add(8).readPointer();
  for (let i = 0; i < ns; i++) {
    const s = sp.add(i * 0x28);
    const np = s.readPointer(), sem = s.add(8).readPointer();
    streams.push({
      name: np.isNull() ? '' : np.readUtf8String(),
      semantic: sem.isNull() ? '' : sem.readUtf8String(),
      first: s.add(0x14).readS32(), count: s.add(0x18).readS32(),
      flag: s.add(0x1c).readU8(), type: s.add(0x20).readS32()});
  }
  const vb = nv > 0 ? mesh.readPointer().readByteArray(nv * stride * 4) : new ArrayBuffer(0);
  const ib = ni > 0 ? mesh.add(0x10).readPointer().readByteArray(ni * 4) : new ArrayBuffer(0);
  const out = new Uint8Array(vb.byteLength + ib.byteLength);
  out.set(new Uint8Array(vb), 0);
  out.set(new Uint8Array(ib), vb.byteLength);
  const head = {ev: ev, id: seq, nv: nv, stride: stride, ni: ni, streams: streams};
  Object.assign(head, extra || {});
  send(head, out.buffer);
}

function hook() {
  const m = Process.findModuleByName('physicsbuilder.dll');
  if (m === null) { setTimeout(hook, 5); return; }
  send({ev: 'hooked', base: m.base.toString()});
  Interceptor.attach(m.base.add(%(weld)d), {
    onEnter(args) {
      const caller = this.returnAddress.sub(m.base).toInt32();
      if (caller >= %(pieces_lo)d && caller < %(pieces_hi)d)
        dump(args[0], 'weld', {caller: caller});
    }
  });
  Interceptor.attach(m.base.add(%(simplify)d), {
    onEnter(args) {
      this.out = args[0];
      inside = {error: null};
      dump(args[1], 'in', {});
    },
    onLeave(ret) {
      dump(this.out, 'out', {error: inside.error});
      inside = null;
      seq++;
    }
  });
  Interceptor.attach(m.base.add(%(run)d), {
    onEnter(args) { this.s = args[0]; },
    onLeave(ret) { if (inside) inside.error = this.s.add(0x68).readDouble(); }
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

    out_path = args.out or os.path.join(os.environ.get("TEMP", "."), "simplify_capture", "%s.bin" % args.map)
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

    # Analysis-build (0924) RVAs mapped to the installed build.
    rva = {
        "weld": installed("physicsbuilder", 0xc1ad0),
        "simplify": installed("physicsbuilder", 0xc5b90),
        "run": installed("physicsbuilder", 0x60fdf0),
        "pieces_lo": installed("physicsbuilder", 0x16230),
    }
    rva["pieces_hi"] = rva["pieces_lo"] + 0x1000

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
        log = []
        device.on("output", lambda p, fd, d: log.append(d.decode("utf-8", "replace")) if d else None)
        session.on("detached", lambda *a: done.set())
        started = time.time()
        device.resume(pid)
        done.wait()
        out.close()
        with open(out_path + ".log", "w", encoding="utf-8") as h:
            h.write("".join(log))
        print("%.1fs  %s  -> %s" % (time.time() - started, stats, out_path))


if __name__ == "__main__":
    main()
