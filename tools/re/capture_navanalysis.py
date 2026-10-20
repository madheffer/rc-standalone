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
  <out>/navan_calls.bin  records: u32 kind (1 path, 2 nearest, 3 occupy), u32 thread id, then
                         kind 1: request 0x80, u8 return, 3 pad, f32 distance, closest 0x1c;
                         kind 2: u64 mesh, pos 12, u64 arg3, u64 arg4, u64 arg5, u32 arg6 bits,
                                 hull record 0x48, u64 returned area;
                         kind 3: u64 area, f32 time T, f32 time CT.
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
function flush() { if (buf.n) { send({tag: 'calls'}, buf.b.slice(0, buf.n)); buf.n = 0; } }
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
  send({tag: 'areas'}, cat(parts));
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
  const base = srv.base.sub(0x180000000);
  Interceptor.attach(base.add(0x18039a7c0), { onEnter(args) { dumpAreas(args[0]); } });
  Interceptor.attach(base.add(0x1814d9810), {
    onEnter(args) { this.req = args[0]; this.head = args[0].readByteArray(0x80); },
    onLeave(ret) {
      const dp = this.req.add(0x78).readPointer();
      const cp = this.req.add(0x70).readPointer();
      const tail = new ArrayBuffer(8);
      const dv = new DataView(tail);
      dv.setUint8(0, ret.toUInt32() & 0xff);
      dv.setFloat32(4, dp.isNull() ? NaN : dp.readFloat(), true);
      put(cat([u32(1), u32(Process.getCurrentThreadId()), this.head, tail,
               cp.isNull() ? new ArrayBuffer(0x1c) : cp.readByteArray(0x1c)]));
    }
  });
  Interceptor.attach(base.add(0x1814b3a10), {
    onEnter(args) {
      this.rec = [u32(2), u32(Process.getCurrentThreadId()), u64(args[0]), args[1].readByteArray(12),
                  u64(args[2]), u64(args[3]), u64(args[4]), u32(args[5].toUInt32()),
                  args[6].isNull() ? new ArrayBuffer(0x48) : args[6].readByteArray(0x48)];
    },
    onLeave(ret) { this.rec.push(u64(ret)); put(cat(this.rec)); }
  });
  Interceptor.attach(base.add(0x180394e20), {
    onEnter(args) { this.area = args[0]; },
    onLeave(ret) {
      put(cat([u32(3), u32(Process.getCurrentThreadId()), u64(this.area), this.area.add(200).readByteArray(8)]));
    }
  });
  send({hooked: true});
}
hook();
rpc.exports = { flush() { flush(); } };
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
    hooked = threading.Event()

    def on_message(msg, data):
        if msg.get("type") != "send":
            print("agent:", msg.get("description") or msg)
        elif "note" in msg["payload"]:
            print(msg["payload"]["note"])
        elif msg["payload"].get("hooked"):
            hooked.set()
        elif msg["payload"]["tag"] == "areas":
            with open(os.path.join(out_dir, "navan_areas.bin"), "wb") as f:
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
    calls.close()
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
