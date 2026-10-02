"""capture_deformer.py <addon> <map> [--out file]: every prop deformation of a compile.

Hooks PropDeformer_Transform (resourcecompiler 181288080 in the 09-23
analysis build, 1812883f0 in the installed 10-01 build) during a -world
-phys -fshallow compile. Each call writes a record: the deformer struct's
first 0x80 bytes, its control points (+0x48, (segments + 1) * (divisionsY +
1) * (divisionsZ + 1) float3s) and tangents (+0x60, twice as many, when the
interpolation mode is 3), the 3x4 matrix it is handed, the point stride and
count, and the points before and after. Records: u32 json length, json, u32
blob length, blob (struct, control points, tangents, matrix, points in,
points out).

CS2 must be closed; the map's package is backed up and restored.
"""
import argparse
import filecmp
import json
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
function hook() {
  const mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  Interceptor.attach(mod.base.add(0x12883f0), {
    onEnter(args) {
      const d = args[0];
      this.stride = args[2].toInt32();
      this.count = args[3].toInt32();
      this.points = args[4];
      if (d.isNull() || this.points.isNull() || this.count <= 0) { this.skip = true; return; }
      const segs = d.add(0x2c).readS32(), dy = d.add(0x30).readS32(), dz = d.add(0x34).readS32(), mode = d.add(0x38).readS32();
      const n = (segs + 1) * (dy + 1) * (dz + 1);
      const parts = [d.readByteArray(0x80)];
      const cp = d.add(0x48).readPointer();
      parts.push(n > 0 && n < 100000 && !cp.isNull() ? cp.readByteArray(n * 12) : new ArrayBuffer(0));
      const tg = d.add(0x60).readPointer();
      parts.push(mode === 3 && !tg.isNull() ? tg.readByteArray(n * 24) : new ArrayBuffer(0));
      parts.push(args[1].isNull() ? new ArrayBuffer(48) : args[1].readByteArray(48));
      const pin = new Uint8Array(this.count * 12);
      for (let i = 0; i < this.count; i++)
        pin.set(new Uint8Array(this.points.add(i * this.stride).readByteArray(12)), i * 12);
      parts.push(pin.buffer);
      this.parts = parts;
      this.sizes = parts.map(p => p.byteLength);
      this.meta = {segs: segs, dy: dy, dz: dz, mode: mode, stride: this.stride, count: this.count,
                   normals: !args[5].isNull(), tangents: !args[6].isNull()};
    },
    onLeave() {
      if (this.skip) return;
      const pout = new Uint8Array(this.count * 12);
      for (let i = 0; i < this.count; i++)
        pout.set(new Uint8Array(this.points.add(i * this.stride).readByteArray(12)), i * 12);
      this.parts.push(pout.buffer);
      this.sizes.push(pout.byteLength);
      const total = this.sizes.reduce((a, b) => a + b, 0);
      const all = new Uint8Array(total);
      let at = 0;
      for (const p of this.parts) { all.set(new Uint8Array(p), at); at += p.byteLength; }
      this.meta.sizes = this.sizes;
      send(this.meta, all.buffer);
    }
  });
}
hook();
"""


def running(name):
    return name.lower() in subprocess.run(["tasklist"], capture_output=True, text=True).stdout.lower()


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("map")
    p.add_argument("--out")
    a = p.parse_args()
    if running("cs2.exe") or running("resourcecompiler.exe"):
        raise SystemExit("CS2 or a compile is running; not starting")
    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "deformer_capture", a.map + ".bin")
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = out_path + ".vpk.bak"
    if os.path.exists(vpk):
        shutil.copyfile(vpk, backup)
        os.remove(vpk)
    out = open(out_path, "wb")
    lock = threading.Lock()
    count = [0]

    def on_message(msg, data):
        if msg.get("type") != "send":
            print("agent:", msg)
            return
        head = json.dumps(msg["payload"]).encode()
        blob = data or b""
        with lock:
            out.write(struct.pack("<I", len(head)) + head + struct.pack("<I", len(blob)) + blob)
            count[0] += 1

    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    dev = frida.get_local_device()
    pid = dev.spawn([os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
                     "-i", source, "-world", "-phys", "-fshallow"], cwd=BIN, stdio="pipe")
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
    while running("resourcecompiler.exe"):
        threading.Event().wait(2)
    out.close()
    print(out_path, count[0], "calls")
    if os.path.exists(vpk):
        shutil.copyfile(vpk, out_path + ".vpk")
    if os.path.exists(backup):
        shutil.copyfile(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored" if ok else "RESTORE CMP FAILED", vpk)
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
