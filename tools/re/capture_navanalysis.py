"""capture_navanalysis.py <addon> <map> [--out dir] [--switches "-world -phys -nav"]: CS2's nav analysis.

Runs resourcecompiler on the map under Frida (CS2 closed; the map's package is backed up and
restored) and hooks server.dll 20261015, where CS2's nav mesh (CSNavMesh, gameinfo CustomNavBuild)
computes the .nav's custom data (ledger 103):

  18039a7c0  CSNavMesh::Analyze (vfn 0x60), on entry: every area as built, before the analysis.
  1814d9810  the path search (A*): its 0x80-byte request, the return, the travel distance (*+0x78)
             and the closest-area record (*+0x70, 0x1c bytes).
  1814b3a10  the nearest area to a point: its arguments and the area it returns.
  180394e20  CCSNavArea::ComputeEarliestOccupyTimes, on exit: the area and its two times.

  <out>/navan_areas.bin  u32 area count, then per area: u64 pointer, 0x490 bytes of the area, the
                         0x14-byte polygon record (*(area+0x50)+0x70)[area+8] (zeros when +8 is -1),
                         u32 edge count, then per edge u32 connection count and 0x10 bytes each.
  <out>/navan_mesh.bin   u64 CSNavMesh, its first 0x400 bytes, u64 polygon mesh (area +0x50), its first 0x200.
  <out>/navan_calls.bin  records: u32 kind (1 path, 2 nearest, 3 occupy), u32 thread id, then
                         kind 1: request 0x80, u8 return, 3 pad, f32 distance, closest 0x1c,
                                 u32 start area id, u32 goal area id, u32 closest area id;
                         kind 2: u64 mesh, pos 12, u64 arg3, u64 arg4, u64 arg5, u32 arg6 bits,
                                 hull record 0x48, u64 returned area, u32 its id;
                         kind 3: u64 area, f32 time T, f32 time CT, u32 area id;
                         kind 4: a trace through the mesh's trace object (CSNavMesh +0x50, vfn 8):
                                 start 12, end 12, u64 filter argument, the 0xd0-byte result.
                         kind 5: a vphysics2 world vfn 0x560 call (180065370, the all-hits shape cast)
                                 made from server.dll: shape descriptor 0x30, start 12, delta 12, query
                                 0x38, u32 hit count, f32 least fraction, u32 index;
                         kind 6: a shape's vfn 0x20 (the sphere cast; mesh 180242750, hull 180250b20,
                                 sphere 18024c210, capsule 18024f080) inside one, with no thread id: u32 shape type, u64
                                 shape, local start 12, local delta 12, radius, max fraction, u32
                                 returned, f32 result fraction, u32 index of the world call.
  <out>/navan_shapedata.bin  each shape once, as capture_navcast.py's navcast_shapedata.bin.
  A second Analyze call (the mesh analysed again) writes navan_areas_2.bin.
"""
import argparse
import filecmp
import os
import shutil
import subprocess
import threading

import frida

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")

AGENT = r"""
'use strict';
const SIZE = 1 << 20;
const buf = {b: new ArrayBuffer(SIZE), n: 0};
function put(bytes) {
  nput++;
  if (buf.n + bytes.byteLength > SIZE) { send({tag: 'calls'}, buf.b.slice(0, buf.n)); buf.n = 0; }
  new Uint8Array(buf.b, buf.n, bytes.byteLength).set(new Uint8Array(bytes));
  buf.n += bytes.byteLength;
}
function cat(parts) {
  let n = 0;
  for (const p of parts) n += p.byteLength;
  const out = new Uint8Array(n);
  let at = 0;
  for (const p of parts) { out.set(new Uint8Array(p), at); at += p.byteLength; }
  return out.buffer;
}
function u32(v) { const b = new ArrayBuffer(4); new DataView(b).setUint32(0, v >>> 0, true); return b; }
function u64(p) { const b = new ArrayBuffer(8); new DataView(b).setBigUint64(0, BigInt(p.toString()), true); return b; }
const seenShapes = new Set();
function dumpShape(shape, type) {
  const key = shape.toString();
  if (seenShapes.has(key)) return;
  seenShapes.add(key);
  const head = new ArrayBuffer(12);
  const dv = new DataView(head);
  dv.setBigUint64(0, BigInt(key), true);
  dv.setUint32(8, type, true);
  let parts = [head];
  if (type === 3) {
    const m = shape.add(0xc8).readPointer();
    const nn = m.add(0x18).readS32(), nv = m.add(0x30).readS32(), nt = m.add(0x48).readS32(), nw = m.add(0x60).readS32();
    parts.push(shape.add(0xb8).readByteArray(12), shape.add(0xd1).readByteArray(1), m.readByteArray(0xc0));
    parts.push(nn > 0 ? m.add(0x20).readPointer().readByteArray(nn * 0x20) : new ArrayBuffer(0));
    parts.push(nv > 0 ? m.add(0x38).readPointer().readByteArray(nv * 12) : new ArrayBuffer(0));
    parts.push(nt > 0 ? m.add(0x50).readPointer().readByteArray(nt * 12) : new ArrayBuffer(0));
    parts.push(nw > 0 ? m.add(0x68).readPointer().readByteArray(nw * 12) : new ArrayBuffer(0));
  } else if (type === 2) {
    const h = shape.add(0xc0).readPointer();
    const n = h.add(0x70).readS32();
    parts.push(shape.add(0xb8).readByteArray(4), h.readByteArray(0x80));
    parts.push(n > 0 ? h.add(0x78).readPointer().readByteArray(n * 12) : new ArrayBuffer(0));
  }
  const body = cat(parts);
  send({tag: 'shapedata'}, cat([u32(body.byteLength), body]));
}
let nput = 0, nerr = 0;
function flush() { if (buf.n) { send({tag: 'calls'}, buf.b.slice(0, buf.n)); buf.n = 0; } }
function err(e) { if (nerr++ < 5) send({note: 'hook error: ' + e}); }
let srvMod = null, analyses = 0, cvarsSent = false, base = null, traceHooked = new Set();
function dumpAreas(mesh) {
  const n = mesh.add(8).readS32();
  const arr = mesh.add(0x10).readPointer();
  const parts = [u32(n)];
  for (let i = 0; i < n; i++) {
    const a = arr.add(i * 8).readPointer();
    parts.push(u64(a), a.readByteArray(0x490));
    const idx = a.add(8).readS32();
    let edges = 4;
    if (idx === -1) parts.push(new ArrayBuffer(0x14));
    else {
      const rec = a.add(0x50).readPointer().add(0x70).readPointer().add(idx * 0x14);
      parts.push(rec.readByteArray(0x14));
      edges = rec.add(0x10).readS8();
    }
    parts.push(u32(edges));
    for (let e = 0; e < edges; e++) {
      const blk = a.add(0x70 + e * 8).readPointer();
      if (blk.isNull()) { parts.push(u32(0)); continue; }
      const c = blk.readS32();
      parts.push(u32(c));
      if (c > 0) parts.push(blk.add(8).readByteArray(c * 0x10));
    }
  }
  send({tag: 'areas', n: ++analyses}, cat(parts));
  const pm = n > 0 ? arr.readPointer().add(0x50).readPointer() : ptr(0);
  send({tag: 'mesh'}, cat([u64(mesh), mesh.readByteArray(0x400), u64(pm), pm.isNull() ? new ArrayBuffer(0x200) : pm.readByteArray(0x200)]));
}
const seenMods = new Set();
function hook() {
  for (const m of Process.enumerateModules()) {
    const k = m.name.toLowerCase();
    if (!seenMods.has(k)) { seenMods.add(k); if (/server|nav|client|game/.test(k)) send({note: 'module ' + m.name + ' ' + m.path}); }
  }
  const srv = Process.enumerateModules().find(m => m.name.toLowerCase() === 'server.dll');
  if (srv === undefined) { setTimeout(hook, 5); return; }
  send({note: 'server.dll at ' + srv.base + ' ' + srv.path});
  base = srv.base.sub(0x180000000);
  srvMod = srv;
  Interceptor.attach(base.add(0x18039a7c0), { onEnter(args) {
    dumpAreas(args[0]);
    try {
      const tr = args[0].add(0x50).readPointer();
      const vt = tr.readPointer();
      const slots = [];
      for (let i = 0; i < 6; i++) {
        const f = vt.add(i * 8).readPointer();
        const m = Process.findModuleByAddress(f);
        slots.push(i * 8 + ':' + (m ? m.name + '+' + f.sub(m.base).toString(16) : f));
      }
      const m = Process.findModuleByAddress(vt);
      send({note: 'trace object ' + tr + ' vtable ' + (m ? m.name + '+' + vt.sub(m.base).toString(16) : vt) + ' slots ' + slots.join(' ')});
    } catch (e) { err(e); }
  } });
  Interceptor.attach(base.add(0x1814d9810), {
    onEnter(args) { try {
      if (!cvarsSent) {
        cvarsSent = true;
        const vals = [];
        for (const a of [0x182343030, 0x182343040, 0x182343050, 0x182343060, 0x182343070, 0x182343080, 0x182343090, 0x181e81d18, 0x181e81d1c])
          vals.push(a.toString(16) + ': ' + (a >= 0x182000000 ? (() => { const q = base.add(a).readPointer().add(0x58); return q.readFloat() + ' / ' + q.readS32(); })()
                                                                : base.add(a).readFloat() + ' / ' + base.add(a).readS32()));
        send({note: 'cvars ' + vals.join(', ')});
      }
      this.req = args[0]; this.head = args[0].readByteArray(0x80);
      const s = args[0].add(0x18).readPointer(), g = args[0].add(0x40).readPointer();
      this.ids = cat([u32(s.isNull() ? 0xffffffff : s.add(0x60).readU32()), u32(g.isNull() ? 0xffffffff : g.add(0x60).readU32())]);
    } catch (e) { err(e); } },
    onLeave(ret) { try {
      const dp = this.req.add(0x78).readPointer();
      const cp = this.req.add(0x70).readPointer();
      const tail = new ArrayBuffer(8);
      const dv = new DataView(tail);
      dv.setUint8(0, ret.toUInt32() & 0xff);
      dv.setFloat32(4, dp.isNull() ? NaN : dp.readFloat(), true);
      const ca = cp.isNull() ? ptr(0) : cp.add(8).readPointer();
      put(cat([u32(1), u32(Process.getCurrentThreadId()), this.head, tail,
               cp.isNull() ? new ArrayBuffer(0x1c) : cp.readByteArray(0x1c), this.ids,
               u32(ca.isNull() ? 0xffffffff : ca.add(0x60).readU32())]));
    } catch (e) { err(e); } }
  });
  Interceptor.attach(base.add(0x1814b3a10), {
    onEnter(args) {
      this.rec = [u32(2), u32(Process.getCurrentThreadId()), u64(args[0]), args[1].readByteArray(12),
                  u64(args[2]), u64(args[3]), u64(args[4]), u32(args[5].toUInt32()),
                  args[6].isNull() ? new ArrayBuffer(0x48) : args[6].readByteArray(0x48)];
    },
    onLeave(ret) { try { this.rec.push(u64(ret), u32(ret.isNull() ? 0xffffffff : ret.add(0x60).readU32())); put(cat(this.rec)); } catch (e) { err(e); } }
  });
  Interceptor.attach(base.add(0x180394e20), {
    onEnter(args) { this.area = args[0]; },
    onLeave(ret) {
      put(cat([u32(3), u32(Process.getCurrentThreadId()), u64(this.area), this.area.add(200).readByteArray(8), u32(this.area.add(0x60).readU32())]));
    }
  });
  const traceHook = {
    onEnter(a) { this.s = a[1].readByteArray(12); this.e = a[2].readByteArray(12); this.f = a[3]; this.r = a[4]; },
    onLeave(ret) { try { put(cat([u32(4), u32(Process.getCurrentThreadId()), this.s, this.e, u64(this.f), this.r.readByteArray(0xd0)])); } catch (e) { err(e); } }
  };
  Interceptor.attach(base.add(0x1814eb500), traceHook);
  const nav = Process.enumerateModules().find(m => m.name.toLowerCase() === 'navsystem.dll');
  if (nav) Interceptor.attach(nav.base.add(0x79100), traceHook);
  const vp = Process.enumerateModules().find(m => m.name.toLowerCase() === 'vphysics2.dll');
  let worldActive = 0, worldIndex = -1;
  if (vp) {
    Interceptor.attach(vp.base.add(0x65370), {
      onEnter(a) {
        const ra = this.returnAddress;
        this.mine = ra.compare(srvMod.base) >= 0 && ra.compare(srvMod.base.add(srvMod.size)) < 0;
        if (!this.mine) return;
        worldIndex++;
        worldActive++;
        try { this.rec = [u32(5), u32(Process.getCurrentThreadId()), a[2].readByteArray(0x30), a[3].readByteArray(12), a[4].readByteArray(12), a[5].readByteArray(0x38)]; this.res = a[1]; } catch (e) { err(e); this.rec = null; }
      },
      onLeave(ret) {
        if (!this.mine) return;
        worldActive--;
        if (!this.rec) return;
        try {
          const n = this.res.readS32();
          const arr = this.res.add(8).readPointer();
          const best = new ArrayBuffer(4); new DataView(best).setFloat32(0, 1.0, true);
          let bf = 1.0;
          for (let i = 0; i < n; i++) { const f = arr.add(i * 0x48 + 0x34).readFloat(); if (f < bf) bf = f; }
          new DataView(best).setFloat32(0, bf, true);
          put(cat(this.rec.concat([u32(n), best, u32(worldIndex)])));
        } catch (e) { err(e); }
      }
    });
    for (const [rva, type] of [[0x24c210, 0], [0x24f080, 1], [0x250b20, 2], [0x242750, 3]]) {
      Interceptor.attach(vp.base.add(rva), {
        onEnter(a) {
          this.on = worldActive > 0;
          if (!this.on) return;
          try {
            this.rec = [u32(6), u32(type), u64(a[0]), a[2].readByteArray(12), a[3].readByteArray(12), u32(a[4].toUInt32()), u32(a[5].toUInt32())];
            this.r = a[1];
            dumpShape(a[0], type);
          } catch (e) { err(e); this.on = false; }
        },
        onLeave(ret) {
          if (!this.on) return;
          try { put(cat(this.rec.concat([u32(ret.toUInt32() & 0xff), this.r.readByteArray(4), u32(worldIndex)]))); } catch (e) { err(e); }
        }
      });
    }
  }
  for (const name of ['ExitProcess', 'TerminateProcess']) {
    const f = Module.findExportByName('kernel32.dll', name);
    if (f) Interceptor.attach(f, { onEnter() { flush(); } });
  }
  send({hooked: true});
}
hook();
rpc.exports = { flush() { flush(); }, stats() { return [nput, nerr]; } };
Process.setExceptionHandler(() => { flush(); return false; });
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
    out_dir = a.out or os.path.join(os.environ.get("TEMP", "."), "navgen_capture", a.map + "_analysis")
    os.makedirs(out_dir, exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = os.path.join(out_dir, "package.navan.bak")
    if os.path.exists(vpk):
        shutil.copy2(vpk, backup)
    calls = open(os.path.join(out_dir, "navan_calls.bin"), "wb")
    shapedata = open(os.path.join(out_dir, "navan_shapedata.bin"), "wb")
    hooked = threading.Event()

    def on_message(msg, data):
        if msg.get("type") != "send":
            print("agent:", msg.get("description") or msg)
        elif "note" in msg["payload"]:
            print(msg["payload"]["note"])
        elif msg["payload"].get("hooked"):
            hooked.set()
        elif msg["payload"]["tag"] == "shapedata":
            shapedata.write(data)
        elif msg["payload"]["tag"] == "mesh":
            with open(os.path.join(out_dir, "navan_mesh.bin"), "wb") as f:
                f.write(data)
        elif msg["payload"]["tag"] == "areas":
            name = "navan_areas.bin" if msg["payload"].get("n", 1) == 1 else f"navan_areas_{msg['payload']['n']}.bin"
            with open(os.path.join(out_dir, name), "wb") as f:
                f.write(data)
            print("areas dumped:", len(data), "bytes")
        else:
            calls.write(data)

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
    while not done.wait(5):
        try:
            sc.exports_sync.flush()
        except Exception:
            pass
        if running("cs2.exe"):
            subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
    try:
        print("hook stats (puts, errors):", sc.exports_sync.stats())
    except Exception as e:
        print("no stats:", e)
    calls.close()
    shapedata.close()
    print(os.path.join(out_dir, "navan_calls.bin"), os.path.getsize(os.path.join(out_dir, "navan_calls.bin")), "bytes")
    print("(hooked)" if hooked.is_set() else "(NEVER HOOKED)")
    if os.path.exists(vpk):
        shutil.copy2(vpk, os.path.join(out_dir, a.map + ".built.vpk"))
    if os.path.exists(backup):
        shutil.copy2(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored", vpk, "(cmp ok)" if ok else "(CMP FAILED)")
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
