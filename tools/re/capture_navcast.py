"""capture_navcast.py <addon> <map> [--out dir] [--switches "-world -phys -nav"]: the shape casts navsystem runs.

Runs resourcecompiler on the map under Frida (CS2 closed; the map's package is backed up and restored)
and records, for every CVPhys2World vfn 0x558 call (vphysics2 20261015 180065360 -> CRnWorld::CastShape
1801f5080) made from navsystem.dll, and for every shape cast (shape vtable +0x40: mesh 1802421c0,
hull 180250540, sphere 18024bc00, capsule 18024e6b0) made inside one:

  <out>/navcast_world.bin   records of 0x100 bytes: u32 kind 1, u32 navsystem caller RVA, the shape
                            descriptor (0x30), start (12), delta (12), query (0x38), result (0x48).
  <out>/navcast_shapes.bin  records of 0x80 bytes: u32 kind 2, u32 shape type (0 sphere 1 capsule
                            2 hull 3 mesh), u64 shape pointer, transform (0x30), centre (12), delta (12),
                            half extents (12), max fraction (4), returned (4), result fraction (4),
                            u32 index of the world call it belongs to.
  <out>/navcast_shapedata.bin  each shape once, the first time it is cast against: u32 length of the
                            rest, u64 shape pointer, u32 type, then for a mesh the shape's scale
                            (+0xb8, 12), its flag byte +0xd1, the RnMesh_t (shape +0xc8) header 0xc0
                            bytes, nodes (0x20 each), vertices (12), triangles (12), wings (+0x60 count, +0x68, 12 each); for a hull the
                            scale (+0xb8, 4) and the hull's first 0x80 bytes then its points
                            (+0x70 count, +0x78, 12 each).

--with-navgen also loads capture_navgen.py's agent into the same process, so its stage files
(<n>_<stage>_in.bin / _out.bin, the same layout) come from the run the casts do: Valve's polygons
differ from run to run, so a cast replay needs both from one run. --stop-after <prefix> kills the
compile once that stage's _out file is written (default 029, before Compute Approach areas).

The records go through a 1 MB buffer flushed as it fills. The RVAs are 20261015's; check them with
patch_check.py after an update.
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
const bufs = {world: {b: new ArrayBuffer(SIZE), n: 0}, shapes: {b: new ArrayBuffer(SIZE), n: 0}};
const seenShapes = new Set();
function put(name, bytes) {
  const s = bufs[name];
  if (s.n + bytes.byteLength > SIZE) { send({tag: name}, s.b.slice(0, s.n)); s.n = 0; }
  new Uint8Array(s.b, s.n, bytes.byteLength).set(new Uint8Array(bytes));
  s.n += bytes.byteLength;
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
function flush() { for (const name in bufs) { const s = bufs[name]; if (s.n) { send({tag: name}, s.b.slice(0, s.n)); s.n = 0; } } }
let navs = null, active = 0, worldIndex = -1;
function hook() {
  const vp = Process.findModuleByName('vphysics2.dll');
  navs = Process.findModuleByName('navsystem.dll');
  if (vp === null || navs === null) { setTimeout(hook, 5); return; }
  Interceptor.attach(vp.base.add(0x65360), {
    onEnter(args) {
      const ra = this.returnAddress;
      this.mine = ra.compare(navs.base) >= 0 && ra.compare(navs.base.add(navs.size)) < 0;
      if (!this.mine) return;
      worldIndex++;
      active++;
      this.rec = new ArrayBuffer(0x100);
      const u8 = new Uint8Array(this.rec);
      const dv = new DataView(this.rec);
      dv.setUint32(0, 1, true);
      dv.setUint32(4, ra.sub(navs.base).toUInt32(), true);
      u8.set(new Uint8Array(args[2].readByteArray(0x30)), 8);
      u8.set(new Uint8Array(args[3].readByteArray(12)), 0x38);
      u8.set(new Uint8Array(args[4].readByteArray(12)), 0x44);
      u8.set(new Uint8Array(args[5].readByteArray(0x38)), 0x50);
      this.result = args[1];
    },
    onLeave(ret) {
      if (!this.mine) return;
      active--;
      new Uint8Array(this.rec).set(new Uint8Array(this.result.readByteArray(0x48)), 0x88);
      put('world', this.rec);
    }
  });
  for (const [rva, type] of [[0x24bc00, 0], [0x24e6b0, 1], [0x250540, 2], [0x2421c0, 3]]) {
    Interceptor.attach(vp.base.add(rva), {
      onEnter(args) {
        this.on = active > 0;
        if (!this.on) return;
        this.rec = new ArrayBuffer(0x80);
        const u8 = new Uint8Array(this.rec);
        const dv = new DataView(this.rec);
        dv.setUint32(0, 2, true);
        dv.setUint32(4, type, true);
        dv.setBigUint64(8, BigInt(args[0].toString()), true);
        u8.set(new Uint8Array(args[2].readByteArray(0x30)), 0x10);
        u8.set(new Uint8Array(args[3].readByteArray(12)), 0x40);
        u8.set(new Uint8Array(args[4].readByteArray(12)), 0x4c);
        u8.set(new Uint8Array(args[5].readByteArray(12)), 0x58);
        dv.setUint32(0x64, args[6].toUInt32(), true);
        this.result = args[1];
        dumpShape(args[0], type);
      },
      onLeave(ret) {
        if (!this.on) return;
        const dv = new DataView(this.rec);
        dv.setUint32(0x68, ret.toUInt32() & 0xff, true);
        dv.setFloat32(0x6c, this.result.readFloat(), true);
        dv.setUint32(0x70, worldIndex, true);
        put('shapes', this.rec);
      }
    });
  }
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
    p.add_argument("--with-navgen", action="store_true")
    p.add_argument("--stop-after", default="029")
    a = p.parse_args()
    if running("cs2.exe"):
        raise SystemExit("CS2 is running; not starting")
    if running("resourcecompiler.exe"):
        raise SystemExit("another resourcecompiler is running; not starting")
    out_dir = a.out or os.path.join(os.environ.get("TEMP", "."), "navgen_capture", a.map + "_in")
    os.makedirs(out_dir, exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = os.path.join(out_dir, "package.navcast.bak")
    if os.path.exists(vpk):
        shutil.copy2(vpk, backup)
    files = {name: open(os.path.join(out_dir, f"navcast_{name}.bin"), "wb") for name in ("world", "shapes", "shapedata")}
    hooked = threading.Event()

    def on_message(msg, data):
        if msg.get("type") != "send":
            print("agent:", msg.get("description") or msg)
        elif msg["payload"].get("hooked"):
            hooked.set()
        else:
            files[msg["payload"]["tag"]].write(data)

    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
            "-i", source] + a.switches.split()
    dev = frida.get_local_device()
    pid = dev.spawn(argv, cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT)
    sc.on("message", on_message)
    sc.load()
    stop = threading.Event()
    if a.with_navgen:
        import capture_navgen

        def on_navgen(msg, data):
            if msg.get("type") != "send":
                print("navgen agent:", msg.get("description") or msg)
            elif "tag" in msg["payload"]:
                tag = msg["payload"]["tag"]
                with open(os.path.join(out_dir, tag + ".bin"), "wb") as f:
                    f.write(data)
                if tag.startswith(a.stop_after + "_") and tag.endswith("_out"):
                    stop.set()
        ng = ses.create_script(capture_navgen.AGENT)
        ng.on("message", on_navgen)
        ng.load()
    done = threading.Event()
    ses.on("detached", lambda *x: done.set())
    # Flush before the process ends: watch for navsystem's last stage by polling the RPC.
    dev.resume(pid)
    while not done.wait(5):
        try:
            sc.exports_sync.flush()
        except Exception:
            pass
        if running("cs2.exe") or stop.is_set():
            if stop.is_set():
                print("stage", a.stop_after, "done; stopping the compile")
                try:
                    sc.exports_sync.flush()
                except Exception:
                    pass
            subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
    for f in files.values():
        f.close()
    for name in files:
        path = os.path.join(out_dir, f"navcast_{name}.bin")
        print(path, os.path.getsize(path), "bytes")
    print("(hooked)" if hooked.is_set() else "(NEVER HOOKED)")
    if os.path.exists(backup):
        shutil.copy2(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored", vpk, "(cmp ok)" if ok else "(CMP FAILED)")
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
