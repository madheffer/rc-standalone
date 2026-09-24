"""Run a map compile under Frida and record the map builder's 1/32 weld.

Every call of the CMesh weld (FUN_1812d80c0, the map builder welds each
per-material piece of a map mesh at 1/32 before the physics and render
paths see it) is dumped twice, on entry and on return: its stream layout,
its vertices and its indices. A replay feeds the entry mesh through the
port and compares with the return mesh.

    python capture_weld.py <addon> <map> [--out file] [--limit n] [--phys]

  --phys  also record each physics piece's triangle mesh (positions after
          the weld and the entity transform, and the triangles)
  --xform also record each mesh transform: its matrix, and the positions
          before and after

Output is a stream of records: u32 json length, json, u32 blob length, blob.
The blob is the vertex floats then the u32 indices.
"""
import argparse
import json
import os
import struct
import sys
import threading
import time

import frida

CS2 = os.environ.get(
    "CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
# resourcecompiler.dll 2026-09-24 (unchanged from 09-23 here).
WELD_RVA = 0x12d80c0
# FUN_181308060(mesh, float3 *positions, count, int *indices, triangles):
# the triangle mesh the map builder hulls a brush piece from.
PHYS_RVA = 0x1308060
# FUN_1802b1ff0(mesh, float[12] matrix, ...): moves a map mesh piece into
# the space it is built in (twice for a brush entity).
XFORM_RVA = 0x2b1ff0

AGENT = r"""
'use strict';
const LIMIT = %(limit)d, PHYS = %(dophys)s, XFORM = %(doxform)s;
let seq = 0;

// CMesh: +0 vertex floats, +8 streams (0x28 each), +0x10 indices,
// +0x18 vertex count, +0x1c floats per vertex, +0x20 stream count,
// +0x24 index count. A stream: +0 name, +8 semantic, +0x14 first float,
// +0x18 float count, +0x1c ignored in welds, +0x20 type.
function dump(mesh, ev, id, caller) {
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
  // The map builder drops the tangent stream before welding; anything
  // with one is a model compile's weld.
  if (streams.some(x => x.name === 'tangent'))
    return;
  const vb = mesh.readPointer().readByteArray(nv * stride * 4);
  const ib = ni > 0 ? mesh.add(0x10).readPointer().readByteArray(ni * 4) : new ArrayBuffer(0);
  const out = new Uint8Array(vb.byteLength + ib.byteLength);
  out.set(new Uint8Array(vb), 0);
  out.set(new Uint8Array(ib), vb.byteLength);
  send({ev: ev, id: id, nv: nv, stride: stride, ni: ni, streams: streams, caller: caller}, out.buffer);
}

// A mesh's positions (its first three floats per vertex), or null when it
// carries a tangent stream (a model compile, not the map builder).
function positions(mesh) {
  const nv = mesh.add(0x18).readS32(), stride = mesh.add(0x1c).readS32();
  const ns = mesh.add(0x20).readS32(), sp = mesh.add(8).readPointer();
  for (let i = 0; i < ns; i++) {
    const np = sp.add(i * 0x28).readPointer();
    if (!np.isNull() && np.readUtf8String() === 'tangent')
      return null;
  }
  const vb = mesh.readPointer();
  const out = new Float32Array(nv * 3);
  for (let v = 0; v < nv; v++)
    for (let k = 0; k < 3; k++)
      out[v * 3 + k] = vb.add((v * stride + k) * 4).readFloat();
  return out.buffer;
}

// resourcecompiler.exe loads the DLL after start; hook it once it is there.
function hook() {
  const m = Process.findModuleByName('resourcecompiler.dll');
  if (m === null) {
    setTimeout(hook, 5);
    return;
  }
  send({ev: 'hooked', base: m.base.toString()});
  attach(m.base);
  if (XFORM)
    Interceptor.attach(m.base.add(%(xform)d), {
      onEnter(args) {
        this.mesh = args[0];
        this.m = args[1].readByteArray(48);
        this.before = positions(args[0]);
      },
      onLeave(ret) {
        if (this.before === null)
          return;
        const after = positions(this.mesh);
        const out = new Uint8Array(48 + this.before.byteLength * 2);
        out.set(new Uint8Array(this.m), 0);
        out.set(new Uint8Array(this.before), 48);
        out.set(new Uint8Array(after), 48 + this.before.byteLength);
        send({ev: 'xform', id: seq, n: this.before.byteLength / 12}, out.buffer);
      }
    });
  if (PHYS)
    Interceptor.attach(m.base.add(%(phys)d), {
      onEnter(args) {
        const n = args[2].toInt32(), t = args[4].toInt32();
        const pb = args[1].readByteArray(n * 12);
        const ib = args[3].readByteArray(t * 12);
        const out = new Uint8Array(n * 12 + t * 12);
        out.set(new Uint8Array(pb), 0);
        out.set(new Uint8Array(ib), n * 12);
        send({ev: 'phys', id: seq, n: n, t: t}, out.buffer);
      }
    });
}
hook();

function attach(base) {
Interceptor.attach(base.add(%(rva)d), {
  onEnter(args) {
    this.mesh = args[0];
    this.id = seq++;
    this.on = this.id < LIMIT;
    // The tolerance arrives in xmm1, which the context does not show; the
    // call site tells the map builder's 1/32 weld from the others.
    this.caller = this.returnAddress.sub(base).toInt32();
    if (this.on)
      dump(this.mesh, 'in', this.id, this.caller);
  },
  onLeave(ret) {
    if (this.on)
      dump(this.mesh, 'out', this.id, this.caller);
  }
});
}
"""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("addon")
    parser.add_argument("map")
    parser.add_argument("--out")
    parser.add_argument("--limit", type=int, default=1 << 30)
    parser.add_argument("--phys", action="store_true")
    parser.add_argument("--xform", action="store_true")
    parser.add_argument("--full", action="store_true", help="a full compile (children too) rather than -world -fshallow")
    args = parser.parse_args()

    out_path = args.out or os.path.join(
        os.environ.get("TEMP", "."), "weld_capture", "%s.bin" % args.map)
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

    # A current VPK makes RC skip the compile.
    vpk = os.path.join(CS2, "game", "csgo_addons", args.addon, "maps", args.map + ".vpk")
    if os.path.exists(vpk):
        os.remove(vpk)
    source = os.path.join(CS2, "content", "csgo_addons", args.addon, "maps", args.map + ".vmap")
    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4",
            "-game", os.path.join(CS2, "game", "csgo"), "-i", source]
    if not args.full:
        argv += ["-world", "-fshallow"]

    device = frida.get_local_device()
    pid = device.spawn(argv, cwd=BIN, stdio="pipe")
    session = device.attach(pid)
    script = session.create_script(AGENT % {
        "rva": WELD_RVA, "limit": args.limit, "phys": PHYS_RVA,
        "dophys": str(args.phys).lower(), "xform": XFORM_RVA,
        "doxform": str(args.xform).lower()})
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
