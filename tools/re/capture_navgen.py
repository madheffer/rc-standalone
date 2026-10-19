"""capture_navgen.py <addon> <map> [--out dir] [--switches "-world -phys -nav"]: navsystem's polygon pipeline, stage by stage.

Runs resourcecompiler on the map under Frida and hooks navsystem.dll (20261015, no RVA mapping
needed while that build is installed):
  1800776d0  ValidateVertices(NavGenVertexData_t *): records the vertex pool (+0x18 CUtlVector of
             Vec3D<double>: count +0x18, data +0x20).
  1800207c0  CNavGenPolyProcessorStage1 run (stage, ctx): in place on *(ctx + 0x30).
  1800207f0  CNavGenPolyProcessorStage2 run (stage, ctx): *(ctx + 0x30) in, *(ctx + 0x38) out.
Each stage call writes <out>/<n>_<name>_in.bin and _out.bin: int32 polygon count, int32 vertex
count, the raw 0x88-byte CNavGenPoly records (+0 index count, +0x48 plane?, +0x68 up to 8 inline
indices, +0x80 owned pointer), then the vertex pool as doubles. A stage name is the stage
object's CUtlString at +0x10. CS2 must be closed; the map's package is backed up and restored.
"""
import argparse
import filecmp
import os
import shutil
import struct
import subprocess
import threading

import frida

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")

AGENT = r"""
'use strict';
let pool = null;
let n = 0;
function dump(tag, vec) {
  const count = vec.readS32();
  const data = vec.add(8).readPointer();
  const polys = count > 0 ? data.readByteArray(count * 0x88) : new ArrayBuffer(0);
  let vcount = 0, verts = new ArrayBuffer(0);
  if (pool !== null) {
    vcount = pool.add(0x18).readS32();
    if (vcount > 0) verts = pool.add(0x20).readPointer().readByteArray(vcount * 24);
  }
  const head = new ArrayBuffer(8);
  const dv = new DataView(head);
  dv.setInt32(0, count, true);
  dv.setInt32(4, vcount, true);
  const all = new Uint8Array(8 + polys.byteLength + verts.byteLength);
  all.set(new Uint8Array(head), 0);
  all.set(new Uint8Array(polys), 8);
  all.set(new Uint8Array(verts), 8 + polys.byteLength);
  send({tag: tag}, all.buffer);
}
function hook() {
  const mod = Process.findModuleByName('navsystem.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  Interceptor.attach(mod.base.add(0x776d0), { onEnter(args) { pool = args[0]; } });
  // The Recast stage (180074330) and its two paths: the settings block at *(stage arg + 8) is dumped
  // whole (0x200 bytes) so the generation parameters can be read offline.
  // The third argument is the generation settings (tile, cell, region, edge, verts per poly; +0x48 the
  // hull table, 0x48 bytes an entry); the hull added to the climb height is *(DAT_180134b10) + 0x58.
  Interceptor.attach(mod.base.add(0x74330), { onEnter(args) {
    const cfg = args[0].add(8).readPointer();
    send({tag: 'recast_settings'}, cfg.readByteArray(0x200));
    send({tag: 'recast_params'}, args[2].readByteArray(0x60));
    send({tag: 'recast_hulls'}, args[2].add(0x48).readPointer().readByteArray(8 * 0x48));
    send({tag: 'recast_global'}, mod.base.add(0x134b10).readPointer().readByteArray(0x60)); } });
  // The stage input (180048b20: verts vec, index vec, physics world, bounds, NONAV boxes, count, pad):
  // each body of the world (vtable +0x1c0 count, +0x1c8 body) with its type (+0x20), AABB (+0xf8),
  // transform (+0x1b0, 0x20 bytes) and shapes (+0x38 count, +0x40 shape), then the raw output.
  // 1800a32b0 builds the per-shape triangle source; its vtable slot 3 does the work (vphysics2).
  let extractCall = 0;
  const vcall = (obj, off, ret, types) => new NativeFunction(obj.readPointer().add(off).readPointer(), ret, ['pointer'].concat(types));
  const modOff = p => { const m = Process.findModuleByAddress(p); return m === null ? p.toString() : m.name + '+0x' + p.sub(m.base).toString(16); };
  Interceptor.attach(mod.base.add(0x48b20), {
    onEnter(args) {
      this.verts = args[0];
      this.tris = args[1];
      this.call = extractCall++;
      const world = args[2];
      const bodies = [];
      const n = vcall(world, 0x1c0, 'int', [])(world);
      for (let i = 0; i < n; i++) {
        const body = vcall(world, 0x1c8, 'pointer', ['int'])(world, i);
        if (body.isNull()) { bodies.push(null); continue; }
        const aabb = Memory.alloc(32), xf = Memory.alloc(64);
        vcall(body, 0xf8, 'void', ['pointer'])(body, aabb);
        vcall(body, 0x1b0, 'void', ['pointer'])(body, xf);
        const shapes = [];
        const ns = vcall(body, 0x38, 'int', [])(body);
        for (let j = 0; j < ns; j++) {
          const s = vcall(body, 0x40, 'pointer', ['int'])(body, j);
          if (s.isNull()) { shapes.push(null); continue; }
          // The world's shape filter (+0x4b0) against navsystem's query attributes (180028ae0).
          const filter = Memory.alloc(0x80);
          new NativeFunction(mod.base.add(0x28ae0), 'pointer', ['pointer'])(filter);
          const pass = vcall(world, 0x4b0, 'uint8', ['pointer', 'pointer'])(world, s, filter);
          shapes.push({ptr: s.toString(), vtable: modOff(s.readPointer()), pass: pass,
            head: Array.from(new Uint32Array(s.readByteArray(0xb8))).map(x => x.toString(16)).join(' ')});
        }
        if (this.call === 0 && i === 0) {
          const filter = Memory.alloc(0x80);
          new NativeFunction(mod.base.add(0x28ae0), 'pointer', ['pointer'])(filter);
          send({note: 'world filter fn ' + modOff(world.readPointer().add(0x4b0).readPointer()) + ' filter ' +
            Array.from(new Uint32Array(filter.readByteArray(0x38))).map(x => x.toString(16)).join(' ')});
        }
        bodies.push({type: vcall(body, 0x20, 'uint', [])(body), vtable: modOff(body.readPointer()),
          aabb: Array.from(new Float32Array(aabb.readByteArray(24))), xf: Array.from(new Float32Array(xf.readByteArray(32))), shapes: shapes});
      }
      if (this.call === 0) {
        // The stage list's convars (180022b60): the value word at +0x58 of each.
        const convars = {};
        for (const off of [0x1349d0, 0x1349f0, 0x134a00, 0x134a20, 0x134a40, 0x134ab0, 0x134b00, 0x134b10, 0x134b20,
                           0x134b30, 0x134b50, 0x134b90, 0x134ba0, 0x134bf0, 0x134c00, 0x134c20, 0x134c30, 0x134af0, 0x134bb0])
          convars['18' + (0x0000000 + off).toString(16).padStart(7, '0')] = mod.base.add(off).readPointer().add(0x58).readByteArray(8);
        const out = {};
        for (const k in convars) {
          const dv = new DataView(convars[k]);
          out[k] = {u32: dv.getUint32(0, true), f32: dv.getFloat32(0, true), byte: dv.getUint8(0)};
        }
        send({note: 'convars ' + JSON.stringify(out)});
      }
      const b = args[3];
      send({note: 'extract ' + this.call + ' bounds ' + JSON.stringify(Array.from(new Float32Array(b.readByteArray(24)))) +
        ' markups ' + args[4] + ' x' + args[5].toInt32() + ' bodies ' + JSON.stringify(bodies)});
    },
    onLeave() {
      const nv = this.verts.readS32(), nt = this.tris.readS32();
      send({tag: 'input_' + this.call + '_verts'}, nv > 0 ? this.verts.add(8).readPointer().readByteArray(nv * 12) : new ArrayBuffer(0));
      send({tag: 'input_' + this.call + '_tris'}, nt > 0 ? this.tris.add(8).readPointer().readByteArray(nt * 4) : new ArrayBuffer(0));
    }
  });
  const seenSources = {};
  Interceptor.attach(mod.base.add(0xa32b0), {
    onEnter(args) { this.buf = args[0]; this.shape = args[1]; },
    onLeave() {
      const vt = this.buf.readPointer();
      const key = modOff(vt);
      if (seenSources[key] === undefined) {
        seenSources[key] = true;
        const slots = [];
        for (let k = 0; k < 6; k++) slots.push(modOff(vt.add(k * 8).readPointer()));
        send({note: 'triangle source vtable ' + key + ' slots ' + slots.join(' ') + ' shape vtable ' + modOff(this.shape.readPointer())});
      }
    }
  });
  Interceptor.attach(mod.base.add(0x84da0), { onEnter(args) { send({note: 'tiled recast path 180084da0'}); } });
  Interceptor.attach(mod.base.add(0x873b0), { onEnter(args) { send({note: 'oriented mesh path 1800873b0'}); } });
  // Recast internals (one dump per tile; a tile is named by its bmin, tile + 0x98):
  //  180081710 tiles -> polys (cfg): the meter-space input, float verts at cfg + 8 (count cfg + 0),
  //            int tris at cfg + 0x20 (index count cfg + 0x18), and the config words cfg[0..0x128).
  //  1800850a0 compact + regions (tile): the solid heightfield after the filters, tile + 0xf8.
  //  1800b2a10 rcBuildContours (ctx, chf): the compact heightfield with regions.
  //  180082280 collect (tile): tile + 0x80..0xf0 config, the poly mesh (+0x118) and detail mesh (+0x120).
  // The region and median-filter convars (*(1801386b8) + 0x58, *(1801386a8) + 0x58) go in recast_convars.
  Interceptor.attach(mod.base.add(0x81710), { onEnter(args) {
    const cfg = args[0];
    const nv = cfg.readS32(), ni = cfg.add(0x18).readS32();
    send({tag: 'recast_cfg'}, cfg.readByteArray(0x130));
    send({tag: 'recast_input_verts'}, cfg.add(8).readPointer().readByteArray(nv * 12));
    send({tag: 'recast_input_tris'}, cfg.add(0x20).readPointer().readByteArray(ni * 4));
    const conv = new ArrayBuffer(8), dv = new DataView(conv);
    dv.setInt32(0, mod.base.add(0x1386b8).readPointer().add(0x58).readS32(), true);
    dv.setInt32(4, mod.base.add(0x1386a8).readPointer().add(0x58).readU8(), true);
    send({tag: 'recast_convars'}, conv); } });
  function tileName(tile) {
    const b = tile.add(0x98);
    return b.readFloat().toFixed(4) + '_' + b.add(8).readFloat().toFixed(4);
  }
  function chunks(parts) {
    let n = 0;
    for (const p of parts) n += p.byteLength;
    const all = new Uint8Array(n);
    let o = 0;
    for (const p of parts) { all.set(new Uint8Array(p), o); o += p.byteLength; }
    return all.buffer;
  }
  function ints(values) {
    const b = new ArrayBuffer(values.length * 4), dv = new DataView(b);
    values.forEach((v, i) => dv.setInt32(i * 4, v, true));
    return b;
  }
  Interceptor.attach(mod.base.add(0x850a0), { onEnter(args) {
    const tile = args[0], hf = tile.add(0xf8).readPointer();
    if (hf.isNull()) return;
    const w = hf.readS32(), h = hf.add(4).readS32(), cols = hf.add(0x28).readPointer();
    const parts = [hf.readByteArray(0x28)];
    for (let i = 0; i < w * h; i++) {
      const spans = [];
      for (let s = cols.add(i * 8).readPointer(); !s.isNull(); s = s.add(8).readPointer())
        spans.push(s.readU16(), s.add(2).readU16(), s.add(4).readS32());
      parts.push(ints([spans.length / 3].concat(spans)));
    }
    send({tag: 'tile_' + tileName(tile) + '_solid'}, chunks(parts)); } });
  Interceptor.attach(mod.base.add(0xb2a10), { onEnter(args) {
    const chf = args[1];
    const w = chf.readS32(), h = chf.add(4).readS32(), n = chf.add(8).readS32();
    const parts = [chf.readByteArray(0x70), chf.add(0x48).readPointer().readByteArray(w * h * 4),
      chf.add(0x50).readPointer().readByteArray(n * 8), chf.add(0x60).readPointer().readByteArray(n)];
    const dist = chf.add(0x58).readPointer();
    parts.push(dist.isNull() ? new ArrayBuffer(0) : dist.readByteArray(n * 2));
    const bmin = chf.add(0x24);
    send({tag: 'chf_' + bmin.readFloat().toFixed(4) + '_' + bmin.add(8).readFloat().toFixed(4)}, chunks(parts)); } });
  let tiles = 0;
  Interceptor.attach(mod.base.add(0x82280), { onEnter(args) {
    const tile = args[0];
    const parts = [tile.readByteArray(0x128)];
    const pm = tile.add(0x118).readPointer(), dm = tile.add(0x120).readPointer();
    if (!pm.isNull()) {
      const nv = pm.add(0x28).readS32(), np = pm.add(0x2c).readS32(), nvp = pm.add(0x34).readS32();
      parts.push(pm.readByteArray(0x60), pm.readPointer().readByteArray(nv * 6),
        pm.add(8).readPointer().readByteArray(np * nvp * 4), pm.add(0x10).readPointer().readByteArray(np * 2),
        pm.add(0x18).readPointer().readByteArray(np * 2), pm.add(0x20).readPointer().readByteArray(np));
    }
    if (!dm.isNull()) {
      const nm = dm.add(0x18).readS32(), nv = dm.add(0x1c).readS32(), nt = dm.add(0x20).readS32();
      parts.push(ints([nm, nv, nt]), dm.readPointer().readByteArray(nm * 16),
        dm.add(8).readPointer().readByteArray(nv * 12), dm.add(0x10).readPointer().readByteArray(nt * 4));
    }
    send({tag: 'collect_' + String(tiles++).padStart(3, '0') + '_' + tileName(tile)}, chunks(parts)); } });
  // MergePolys (180073ba0) inside: its polygon lists are 0x168-byte records (up to 64 inline corners at
  // +0x68) and the pool is the vector at ctx + 0x18 (count +0, data +8). Each call writes
  // m<seq>_<fn>_in / _out: int32 count, int32 vertex count, int32 0x168, the records, the pool.
  // 180056540 (the edge list) writes m<seq>_edges: int32 count, then the 0x20-byte entries.
  function dumpWide(tag, vec, poolVec) {
    const count = vec.readS32();
    const polys = count > 0 ? vec.add(8).readPointer().readByteArray(count * 0x168) : new ArrayBuffer(0);
    const vcount = poolVec.readS32();
    const verts = vcount > 0 ? poolVec.add(8).readPointer().readByteArray(vcount * 24) : new ArrayBuffer(0);
    send({tag: tag}, chunks([ints([count, vcount, 0x168]), polys, verts]));
  }
  let m = 0;
  const wide = [[0x61730, 'Weld', 2], [0x5b3b0, 'Degens', 2], [0x5a570, 'MergeEdges', 2], [0x5ca90, 'MergeVerts', 2],
                [0x584f0, 'Final', 4]];
  for (const [rva, name, poolArg] of wide) {
    Interceptor.attach(mod.base.add(rva), {
      onEnter(args) {
        this.out = args[0];
        this.pool = poolArg === 2 ? args[2] : this.context.rsp.add(0x28).readPointer();
        this.tag = 'm' + String(m++).padStart(4, '0') + '_' + name;
        dumpWide(this.tag + '_in', args[1], this.pool);
      },
      onLeave() { dumpWide(this.tag + '_out', this.out, this.pool); }
    });
  }
  Interceptor.attach(mod.base.add(0x56540), {
    onEnter(args) { this.out = args[0]; },
    onLeave() {
      const count = this.out.readS32();
      send({tag: 'm' + String(m++).padStart(4, '0') + '_edges'},
        chunks([ints([count]), count > 0 ? this.out.add(8).readPointer().readByteArray(count * 0x20) : new ArrayBuffer(0)]));
    }
  });
  for (const [rva, kind] of [[0x207c0, 1], [0x207f0, 2]]) {
    Interceptor.attach(mod.base.add(rva), {
      onEnter(args) {
        this.ctx = args[1];
        const name = args[0].add(0x10).readPointer().readCString();
        this.tag = String(n++).padStart(3, '0') + '_' + name;
        dump(this.tag + '_in', this.ctx.add(0x30).readPointer());
      },
      onLeave(ret) {
        dump(this.tag + '_out', this.ctx.add(kind === 1 ? 0x30 : 0x38).readPointer());
      }
    });
  }
  send({hooked: true});
}
hook();
"""


def running(name):
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq " + name], capture_output=True, text=True).stdout
    return name.lower() in out.lower()


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("map")
    p.add_argument("--out")
    p.add_argument("--switches", default="-world -phys -nav")
    a = p.parse_args()
    if running("cs2.exe"):
        raise SystemExit("CS2 is running; not starting")
    if running("resourcecompiler.exe"):
        raise SystemExit("another resourcecompiler is running; not starting")
    out_dir = a.out or os.path.join(os.environ.get("TEMP", "."), "navgen_capture", a.map)
    os.makedirs(out_dir, exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = os.path.join(out_dir, "package.vpk.bak")
    if os.path.exists(vpk):
        shutil.copy2(vpk, backup)
    hooked = threading.Event()
    stages = []

    def on_message(msg, data):
        if msg.get("type") != "send":
            print("agent:", msg.get("description") or msg)
        elif msg["payload"].get("hooked"):
            hooked.set()
        elif "note" in msg["payload"]:
            print(msg["payload"]["note"])
        else:
            tag = msg["payload"]["tag"]
            with open(os.path.join(out_dir, tag + ".bin"), "wb") as f:
                f.write(data)
            if not tag.startswith(("recast_", "tile_", "chf_", "collect_", "input_", "m")):
                count, vcount = struct.unpack_from("<ii", data)
                stages.append((tag, count, vcount))

    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
            "-i", source] + a.switches.split()
    dev = frida.get_local_device()
    pid = dev.spawn(argv, cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT)
    sc.on("message", on_message)
    sc.load()
    done = threading.Event()
    ses.on("detached", lambda *x: done.set())
    dev.resume(pid)
    while not done.wait(2):
        if running("cs2.exe"):
            subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
    for tag, count, vcount in stages:
        print(f"{tag}: {count} polys, {vcount} vertices")
    print(out_dir, "(hooked)" if hooked.is_set() else "(NEVER HOOKED)")
    if os.path.exists(backup):
        shutil.copy2(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored", vpk, "(cmp ok)" if ok else "(CMP FAILED)")
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
