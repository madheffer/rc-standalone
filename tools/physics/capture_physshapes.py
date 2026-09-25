"""capture_physshapes.py <addon> <map> <out.json> [--full] [--rnmesh <out.bin>] -- physics shapes and part order.

resourcecompiler's part builder (0923: 180c28230) turns a part's compile-time
shapes into RnShapes; for triangle meshes it calls the mesh gatherer
(180c29500) with the part, whose shape list is a CUtlVector of pointers
(count +0x50, data +0x58). This records, per call, each shape's type (+0x90,
3 = mesh), name (+0xe0), surface property name (+0xf8), index and vertex
counts (+0xc8, +0xb0), the first vertex and a hash of all indices, plus the
first 0x180 bytes of the shape for layout work; with --dump, every mesh
shape's vertices and indices too.

It also logs every shape the part insert (180c28150) takes, in call order,
with the part, the shape's type and, for a mesh, its vertex count and first
vertex: the order WorldCollision.PartOrder starts from. With --rnmesh it
records the RnMeshCreate calls the part builder makes (from 180c28690,
found in the backtrace since the call goes through vphysics2's interface) in
capture_rnmesh.py's format. The compile is forced (-f): an up-to-date map
would otherwise be skipped.

A full compile overwrites the map's .vpk: back it up. CS2 must be closed, and
the script refuses to start if it is running or another compile is.
"""
import argparse
import json
import os
import struct
import subprocess
import threading

import frida

CS2 = os.environ.get(
    "CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
GATHER_RVA = 0xc29500
INSERT_RVA = 0xc28150
MESHBUILD_RVA = (0xc28690, 0xc28900)

AGENT = r"""
'use strict';
function str(p) { try { const s = p.readPointer(); return s.isNull() ? null : s.readUtf8String(); } catch (e) { return '?'; } }
function hex(b) { return Array.from(new Uint8Array(b)).map(x => x.toString(16).padStart(2, '0')).join(''); }
function hook() {
  const m = Process.findModuleByName('resourcecompiler.dll');
  if (m === null) { setTimeout(hook, 5); return; }
  let n = 0;
  Interceptor.attach(m.base.add(RVA), {
    onEnter(args) {
      const part = args[0];
      const count = part.add(0x50).readS32();
      const cap = part.add(0x54).readU32() & 0x7fffffff;
      const list = cap === 0 ? ptr(0) : part.add(0x58).readPointer();
      const shapes = [];
      for (let i = 0; i < count; i++) {
        const s = list.add(i * 8).readPointer();
        const type = s.add(0x90).readS32();
        const e = {type: type, name: str(s.add(0xe0)), surface: str(s.add(0xf8)), head: hex(s.readByteArray(0x180))};
        if (type === 3) {
          const ni = s.add(0xc8).readS32(), nv = s.add(0xb0).readS32();
          e.indices = ni; e.vertices = nv;
          try {
            const vp = s.add(0xb8).readPointer();
            e.v0 = [vp.readFloat(), vp.add(4).readFloat(), vp.add(8).readFloat()];
            const ip = s.add(0xd0).readPointer();
            let h = 2166136261 >>> 0;
            for (let k = 0; k < ni; k++) { h = Math.imul(h ^ ip.add(k * 4).readS32(), 16777619) >>> 0; }
            e.indexHash = h;
            if (DUMP) { e.vdata = hex(vp.readByteArray(nv * 12)); e.idata = hex(ip.readByteArray(ni * 4)); }
          } catch (err) { e.err = String(err); }
        }
        shapes.push(e);
      }
      send({call: n++, count: count, shapes: shapes,
            stack: Thread.backtrace(this.context, Backtracer.ACCURATE).slice(0, 12).map(a => {
              const mod = Process.findModuleByAddress(a);
              return mod ? mod.name.replace('.dll', '') + '+0x' + a.sub(mod.base).toString(16) : a.toString();
            })});
    }
  });
  Interceptor.attach(m.base.add(INSERT), {
    onEnter(args) {
      const s = args[1]; const type = s.add(0x90).readS32();
      const e = {insert: n, part: args[0].toString(), shape: s.toString(), type: type};
      if (type === 3) {
        try { e.vertices = s.add(0xb0).readS32(); e.indices = s.add(0xc8).readS32();
              const vp = s.add(0xb8).readPointer(); e.v0 = [vp.readFloat(), vp.add(4).readFloat(), vp.add(8).readFloat()]; } catch (err) { e.err = String(err); }
      }
      send(e);
    }
  });
  if (RNMESH) hookRnMesh(m);
  if (BLEND) hookBlend();
  send({hooked: m.base.add(RVA).toString()});
}
// A CMesh: vertex floats +0x00, stream table +0x08 (0x28 each: name, ...,
// semantic index +0x10, float offset +0x14, float count +0x18), indices
// +0x10, vertex count +0x18, stride in floats +0x1c, streams +0x20, indices +0x24.
function cmesh(p) {
  const nv = p.add(0x18).readS32(), stride = p.add(0x1c).readS32(), ns = p.add(0x20).readS32(), ni = p.add(0x24).readS32();
  const streams = [];
  const t = p.add(8).readPointer();
  for (let i = 0; i < ns && !t.isNull(); i++) {
    const e = t.add(i * 0x28);
    streams.push({name: str(e), index: e.add(0x10).readS32(), offset: e.add(0x14).readS32(), count: e.add(0x18).readS32()});
  }
  const o = {vertices: nv, stride: stride, indices: ni, streams: streams};
  if (nv > 0 && stride > 0) o.vdata = hex(p.readPointer().readByteArray(nv * stride * 4));
  if (ni > 0) o.idata = hex(p.add(0x10).readPointer().readByteArray(ni * 4));
  return o;
}
// physicsbuilder's blend split (0924: 180015930) and the material sampler
// it may use (18064c460): the table, the mesh in, the meshes out, and
// whether the sampler ran.
function hookBlend() {
  const pb = Process.findModuleByName('physicsbuilder.dll');
  if (pb === null) { setTimeout(hookBlend, 5); return; }
  Interceptor.attach(pb.base.add(0x15930), {
    onEnter(args) {
      const t = args[0];
      this.out = args[1];
      const table = {layers: t.readS32(), puddleChannel: t.add(4).readS32(), puddleLayer: t.add(8).readS32(),
                     sampled: t.add(0xc).readU8(), swap: t.add(0xd).readU8(), scale1: t.add(0x10).readFloat(), surfaces: t.add(0x20).readS32()};
      // Surface names: CUtlStrings at +0x28, count +0x20. Remap: ints at +0x40, count +0x38.
      table.names = [];
      for (let i = 0; i < table.surfaces; i++) table.names.push(str(t.add(0x28).readPointer().add(i * 8)));
      table.remap = [];
      const nr = t.add(0x38).readS32();
      for (let i = 0; i < nr; i++) table.remap.push(t.add(0x40).readPointer().add(i * 4).readS32());
      send({blend: 'in', table: table, mesh: cmesh(args[2])});
    },
    onLeave() {
      const n = this.out.readS32(), data = this.out.add(8).readPointer();
      const meshes = [];
      for (let i = 0; i < n; i++) meshes.push(cmesh(data.add(i * 0x198)));
      send({blend: 'out', meshes: meshes});
    }
  });
  Interceptor.attach(pb.base.add(0x64c460), {
    onEnter(args) {
      this.samples = args[4];
      const g = [0xe2a200, 0xe2a188, 0xe2a250, 0xe2a350].map(o => pb.base.add(o).readPointer().toString());
      send({sampler: 'in', globals: g});
    },
    onLeave(ret) {
      // The per-triangle layers it chose: a CUtlVector of ints (count +0, data +8).
      const n = this.samples.readS32();
      const layers = [];
      const data = n > 0 ? this.samples.add(8).readPointer() : ptr(0);
      for (let i = 0; i < n; i++) layers.push(data.add(i * 4).readS32());
      send({sampler: 'out', ret: ret.toInt32() & 0xff, samples: n, layers: layers});
    }
  });
  send({hookedBlend: pb.base.toString()});
}
function hookRnMesh(rc) {
  const v = Process.findModuleByName('vphysics2.dll');
  if (v === null) { setTimeout(() => hookRnMesh(rc), 5); return; }
  const lo = rc.base.add(MB0), hi = rc.base.add(MB1);
  let k = 0;
  Interceptor.attach(v.findExportByName('RnMeshCreate'), {
    onEnter(args) {
      // The part builder reaches RnMeshCreate through vphysics2's interface,
      // so its frame is a few up the stack, not the return address.
      const frames = Thread.backtrace(this.context, Backtracer.ACCURATE).slice(0, 6);
      if (!frames.some(ra => ra.compare(lo) >= 0 && ra.compare(hi) < 0)) return;
      const tris = args[0].toInt32(), vcount = args[3].toInt32(), opt = args[6];
      const parts = [args[1].readByteArray(tris * 12), args[2].isNull() ? null : args[2].readByteArray(tris),
                     args[4].readByteArray(vcount * 12), opt.isNull() ? null : opt.readByteArray(16)];
      let total = 0; for (const b of parts) if (b) total += b.byteLength;
      const out = new Uint8Array(total); let at = 0;
      for (const b of parts) if (b) { out.set(new Uint8Array(b), at); at += b.byteLength; }
      send({ev: 'in', id: k++, tris: tris, vcount: vcount, hasMaterials: !args[2].isNull(), hasOptions: !opt.isNull()}, out.buffer);
    }
  });
}
hook();
"""


def busy():
    out = subprocess.run(["tasklist"], capture_output=True, text=True).stdout.lower()
    return "cs2.exe" in out or "resourcecompiler.exe" in out


def low_disk():
    """Under 10 GB free on the game's drive: a compile writes the vpk there, and a crash dumps ~2.4 GB."""
    import shutil
    return shutil.disk_usage(os.path.splitdrive(CS2)[0] + "\\").free < 10 * 1024 ** 3


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("map")
    p.add_argument("out")
    p.add_argument("--full", action="store_true", help="a full compile rather than -world -fshallow")
    p.add_argument("--rnmesh", help="also record the part builder's RnMeshCreate calls here")
    p.add_argument("--dump", action="store_true", help="also dump every gathered mesh shape's vertices and indices (hex)")
    p.add_argument("--blend", action="store_true", help="also record physicsbuilder's blend splits (mesh in, meshes out) and its material sampler")
    a = p.parse_args()
    if busy():
        raise SystemExit("CS2 or another resourcecompiler is running; not starting")
    if low_disk():
        raise SystemExit("under 10 GB free on the game drive; not starting")
    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-f", "-game", os.path.join(CS2, "game", "csgo"), "-i", source]
    if not a.full:
        argv += ["-world", "-fshallow"]
    calls = []
    lock = threading.Lock()
    rn = open(a.rnmesh, "wb") if a.rnmesh else None

    def on_message(msg, data):
        if msg.get("type") != "send":
            print(msg)
            return
        pay = msg["payload"]
        if pay.get("ev") == "in":
            head = json.dumps(pay).encode()
            with lock:
                rn.write(struct.pack("<I", len(head)) + head + struct.pack("<I", len(data or b"")) + (data or b""))
            print("rnmesh", pay["id"], pay["tris"], "tris", flush=True)
            return
        if "blend" in pay or "sampler" in pay:
            with lock:
                calls.append(pay)
            print("blend" if "blend" in pay else "sampler", pay.get("blend") or pay.get("sampler"), pay.get("ret", ""), flush=True)
            return
        if "insert" in pay:
            with lock:
                calls.append(pay)
            return
        if "call" not in pay:
            print(pay)
            return
        with lock:
            calls.append(pay)
        meshes = [s for s in pay["shapes"] if s["type"] == 3]
        print("call", pay["call"], pay["count"], "shapes,", len(meshes), "meshes", " ".join(pay["stack"][:6]), flush=True)

    dev = frida.get_local_device()
    pid = dev.spawn(argv, cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    agent = (AGENT.replace("INSERT", hex(INSERT_RVA)).replace("RVA", hex(GATHER_RVA)).replace("RNMESH", "true" if rn else "false").replace("DUMP", "true" if a.dump else "false").replace("BLEND", "true" if a.blend else "false")
             .replace("MB0", hex(MESHBUILD_RVA[0])).replace("MB1", hex(MESHBUILD_RVA[1])))
    sc = ses.create_script(agent)
    sc.on("message", on_message)
    sc.load()
    done = threading.Event()
    ses.on("detached", lambda *x: done.set())
    dev.resume(pid)
    done.wait()
    if rn:
        rn.close()
    with open(a.out, "w", encoding="utf-8") as fh:
        json.dump(calls, fh, indent=1)
    print(a.out, len(calls), "calls")


if __name__ == "__main__":
    main()
