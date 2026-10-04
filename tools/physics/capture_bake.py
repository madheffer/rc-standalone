"""capture_bake.py <addon> <map> <out.jsonl> [--full] -- CPolygonMesh state around the subdivision bake.

BakeSubdivisionForFaces (resourcecompiler 0923: 1810c65c0) runs the bake
proper (1813baa40), then merges vertices (1813a8ae0, tolerance ~1e-6) and
assigns edge smoothing. This snapshots the mesh at the wrapper's entry, at
the bake's exit and at the wrapper's exit: every face in dense order with
its loop (from the face's first half-edge, following next), each corner's
vertex handle index and position. One JSON line per snapshot.

Mesh layout (CPolygonMesh, read from the decompiles): containers of
{count, data, alloc, flags, handle count, handle table, ...}: vertices at
+0x18 (0x20 each; +0x10 attribute index), half-edges at +0x50 (0x50 each;
+0x00 vertex, +0x10 twin, +0x20 next, +0x30 face), faces at +0x88 (0x20
each; +0x00 first half-edge). Handles are 22-bit indices with a generation
in bits 22..31; a handle-table entry is 0x18 bytes: dense index at +0,
handle word at +8. Positions: the float3 attribute array of stream
(byte at +0x689), at *(mesh + 0x8d8 + stream * 0x18 + 8).

A compile overwrites the map's .vpk: back it up. CS2 must be closed.
"""
import argparse
import json
import os
import sys
import threading

import frida

from capture_physshapes import BIN, CS2, busy, low_disk

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "re"))
from rva_map import installed  # noqa: E402

# 0923 RVAs: the wrapper, the bake, AddVertexToEdge, AddEdgeToFace,
# CollapseFace, MergeVertexPair, MergeVerticesWithinDistance.
HOOKED = ["0x10c65c0", "0x13baa40", "0x137a050", "0x1376f80", "0x1384470", "0x13a8840", "0x13a8ae0"]

AGENT = r"""
'use strict';
const RVA = RVAS;
function rec(mesh, base, stride, h) {
  const idx = h & 0x3fffff;
  if (idx === 0x3fffff) return null;
  const hcount = mesh.add(base + 0x18).readU32();
  if (idx >= hcount) return null;
  const e = mesh.add(base + 0x20).readPointer().add(idx * 0x18);
  const word = e.add(8).readU32();
  if ((word & 0x3fffff) !== idx || ((word ^ h) & 0xffc00000) !== 0) return null;
  const dense = e.readS32();
  if (dense === -1) return null;
  return mesh.add(base + 8).readPointer().add(dense * stride);
}
function snapshot(mesh, tag) {
  const stream = mesh.add(0x689).readS8();
  const pos = stream >= 0 ? mesh.add(0x8d8 + stream * 0x18 + 8).readPointer() : null;
  const nf = mesh.add(0x88).readU32();
  const fdata = mesh.add(0x90).readPointer();
  const faces = [];
  for (let i = 0; i < nf; i++) {
    const f = fdata.add(i * 0x20);
    const first = f.readU32();
    const loop = [];
    let h = first, guard = 0;
    do {
      const he = rec(mesh, 0x50, 0x50, h);
      if (he === null) { loop.push(['bad', h]); break; }
      const vh = he.readU32();
      const v = rec(mesh, 0x18, 0x20, vh);
      const attr = v === null ? -1 : v.add(0x10).readS32();
      const p = (pos !== null && attr >= 0) ? [pos.add(attr * 12).readFloat(), pos.add(attr * 12 + 4).readFloat(), pos.add(attr * 12 + 8).readFloat()] : null;
      loop.push([vh & 0x3fffff, p]);
      h = he.add(0x20).readU32();
    } while ((h & 0x3fffff) !== (first & 0x3fffff) && ++guard < 4096);
    faces.push({h: f.add(0x18).readU32(), first: first & 0x3fffff, loop: loop});
  }
  send({tag: tag, mesh: mesh.toString(), vertices: mesh.add(0x18).readU32(), halfedges: mesh.add(0x50).readU32(), faces: faces});
}
// Light snapshot: dense face order as (face handle index, vertex loop).
function faces(mesh) {
  const nf = mesh.add(0x88).readU32();
  const fdata = mesh.add(0x90).readPointer();
  const out = [];
  for (let i = 0; i < nf; i++) {
    const f = fdata.add(i * 0x20);
    const first = f.readU32();
    const loop = [];
    let h = first, guard = 0;
    do {
      const he = rec(mesh, 0x50, 0x50, h);
      if (he === null) { loop.push([-2, h & 0x3fffff]); break; }
      loop.push([h & 0x3fffff, he.readU32() & 0x3fffff]);
      h = he.add(0x20).readU32();
    } while ((h & 0x3fffff) !== (first & 0x3fffff) && ++guard < 4096);
    out.push([f.add(0x18).readU32(), loop]);
  }
  return out;
}
let inBake = 0;
function opHook(m, rva, name, argIdx) {
  Interceptor.attach(m.base.add(rva), {
    onEnter(args) {
      if (!inBake || !OPS) return;
      this.on = true; this.mesh = args[0];
      this.a = argIdx.map(i => args[i].isNull() ? -1 : (args[i].readU32() & 0x3fffff));
      this.out = args[argIdx.length + 1];
    },
    onLeave(ret) {
      if (!this.on) return;
      const m = this.mesh;
      // free-list head and tail of each container (vertices, half-edges, faces)
      const free = [0x18, 0x50, 0x88].map(base => [m.add(base + 0x30).readU32(), m.add(base + 0x34).readU32()]);
      send({op: name, args: this.a, ret: ret.toInt32() & 0xff, faces: faces(m), free: free,
            counts: [m.add(0x18).readU32(), m.add(0x50).readU32(), m.add(0x88).readU32()]});
    }
  });
}
function hook() {
  const m = Process.findModuleByName('resourcecompiler.dll');
  if (m === null) { setTimeout(hook, 5); return; }
  Interceptor.attach(m.base.add(RVA['0x10c65c0']), {
    onEnter(args) { this.mesh = args[0]; snapshot(this.mesh, 'before'); },
    onLeave() { snapshot(this.mesh, 'after'); }
  });
  Interceptor.attach(m.base.add(RVA['0x13baa40']), {
    onEnter(args) { this.mesh = args[0]; inBake++; },
    onLeave() { inBake--; snapshot(this.mesh, 'baked'); }
  });
  // AddVertexToEdge(mesh, a, b, t, out), AddEdgeToFace(mesh, face, a, b, out),
  // CollapseFace(mesh, face, out): handle arguments by index.
  opHook(m, RVA['0x137a050'], 'AddVertexToEdge', [1, 2]);
  opHook(m, RVA['0x1376f80'], 'AddEdgeToFace', [1, 2, 3]);
  opHook(m, RVA['0x1384470'], 'CollapseFace', [1]);
  // The wrapper's vertex merge (1813a8ae0) and each pair it merges
  // (1813a8840: mesh, &a, &b, weight, &out), whether or not --ops is set.
  Interceptor.attach(m.base.add(RVA['0x13a8840']), {
    onEnter(args) { this.a = [args[1].readU32() & 0x3fffff, args[2].readU32() & 0x3fffff]; this.out = args[4]; },
    onLeave(ret) {
      send({op: 'MergeVertexPair', args: this.a, ret: ret.toInt32() & 0xff,
            result: this.out.isNull() ? -1 : (this.out.readU32() & 0x3fffff)});
    }
  });
  Interceptor.attach(m.base.add(RVA['0x13a8ae0']), {
    onLeave(ret) { send({op: 'MergeVerticesWithinDistance', ret: ret.toInt32()}); }
  });
  send({hooked: m.base.toString()});
}
hook();
"""


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("map")
    p.add_argument("out")
    p.add_argument("--full", action="store_true", help="a full compile rather than -world -fshallow")
    p.add_argument("--ops", action="store_true", help="also log each AddVertexToEdge / AddEdgeToFace / CollapseFace inside the bake, with the face order after it")
    a = p.parse_args()
    if busy():
        raise SystemExit("CS2 or another resourcecompiler is running; not starting")
    if low_disk():
        raise SystemExit("under 10 GB free on the game drive; not starting")
    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"), "-i", source]
    argv += ["-f"] if a.full else ["-world", "-fshallow"]
    out = open(a.out, "w", encoding="utf-8")
    lock = threading.Lock()

    def on_message(msg, data):
        if msg.get("type") != "send":
            print(msg, flush=True)
            return
        pay = msg["payload"]
        if "op" in pay:
            with lock:
                out.write(json.dumps(pay) + "\n")
            return
        if "tag" not in pay:
            print(pay, flush=True)
            return
        with lock:
            out.write(json.dumps(pay) + "\n")
        print(pay["tag"], pay["mesh"], len(pay["faces"]), "faces", flush=True)

    # The hooks were read on the 0923 build; each is found again in the
    # installed one (tools/re/rva_map.py refuses code that changed).
    rvas = {r: hex(installed("resourcecompiler", int(r, 16))) for r in HOOKED}
    print("hooks", rvas, flush=True)
    rvas = {k: int(v, 16) for k, v in rvas.items()}
    dev = frida.get_local_device()
    pid = dev.spawn(argv, cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT.replace("OPS", "true" if a.ops else "false").replace("RVAS", json.dumps(rvas)))
    sc.on("message", on_message)
    sc.load()
    done = threading.Event()
    ses.on("detached", lambda *x: done.set())
    dev.resume(pid)
    done.wait()
    out.close()
    print(a.out)


if __name__ == "__main__":
    main()
