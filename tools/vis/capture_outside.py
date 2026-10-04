"""capture_outside.py <addon> <map> [--out file] [--seed]: Valve's regions and their inside/outside verdicts.

With --seed it also records every GatherRays call (18004c9d0): the box, the
quality, the tallies it leaves in its result (+0x18 distance, +0x1c facing,
+0x20 behind, +0x24 insubstantial, +0x28 escaped, +0x30 rays) and whether
SeedDecide (18004b9b0) made the call, with SeedDecide's verdict; records
"gathers" of 32 bytes box + quality, 28 bytes tallies, i32 seed flag and
i32 verdict (-1 outside the seed), 72 bytes each, flushed in batches.

With --rays x,y,z[;x,y,z...] (box minimums) it records, for each gather of a
box with that minimum, its rays and the hits the tracer gave them as
NoDrawSecondLook (18004d1a0) receives them: "rays" records holding the ray
list (0x20 bytes each), then the 56-byte hit records, then for each hit with
a triangle the tracer's 48-byte triangle record (id, record).

With --march x,y,z[;...] (box minimums) it records, for each ClassifyRegion
(18002f5d0) of such a box, every MarchRay (18002f430) it makes: the origin,
the four inverse-delta floats and the answer (0 none, 1 inside, 2 outside),
as "march" records of 32 bytes each; and the status bytes and flag words of
every region record when the first such ClassifyRegion starts ("snapshot").

Hooks visbuilder's OutsideDetection (180033890 in the 09-23 analysis build,
found again in the installed one by tools/re/rva_map.py) during a -world -vis
-fshallow compile. On entry it records the sampler's region array (count at
+0x40, 16-byte records at +0x48: flags word at +4 with the leaf index above
bit 2, open-voxel mask at +8); on return the same array (bit 0 of the flags
now marks outside) and the status byte per region at +0x120 (1 inside,
2 outside). The compile is stopped once the verdicts are in, and the .rte
and .viscfg it traced are kept beside the capture as <stem>.rte/.viscfg.

Records: u32 json length, json, u32 blob length, blob.
CS2 must be closed.
"""
import argparse
import json
import os
import shutil
import struct
import subprocess
import sys
import threading

import frida

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "re"))
from rva_map import installed  # noqa: E402

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
AGENT = r"""
'use strict';
const RVA = %(rva)d, GATHER = %(gather)d, SEED = %(seed)d, WANT_SEED = %(want)s;
const SECOND = %(second)d, RAYBOXES = %(rayboxes)s;
const CLASSIFY = %(classify)d, MARCH = %(march)d, MARCHBOXES = %(marchboxes)s;
const marchThread = {};
let snapped = false;
const rayThread = {};
const batch = [];
function flush() {
  if (batch.length === 0) return;
  const buf = new ArrayBuffer(batch.length * 72), v = new DataView(buf);
  batch.forEach((r, i) => {
    const o = i * 72;
    for (let k = 0; k < 6; k++) v.setFloat32(o + k * 4, r.box[k], true);
    v.setInt32(o + 24, r.quality, true);
    for (let k = 0; k < 7; k++) v.setInt32(o + 28 + k * 4, r.t[k], true);
    v.setInt32(o + 56, r.inSeed ? 1 : 0, true);
    v.setInt32(o + 60, r.verdict, true);
  });
  send({ev: 'gathers', count: batch.length}, buf);
  batch.length = 0;
}
const seedOf = {};   // thread id -> pending record of the SeedDecide on it
function regions(p) {
  const n = p.add(0x40).readS32();
  return [n, n > 0 ? p.add(0x48).readPointer().readByteArray(n * 16) : null];
}
function concat(a, b) {
  const out = new Uint8Array(a.byteLength + b.byteLength);
  out.set(new Uint8Array(a), 0); out.set(new Uint8Array(b), a.byteLength);
  return out.buffer;
}
function hook() {
  const mod = Process.findModuleByName('visbuilder.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  Interceptor.attach(mod.base.add(RVA), {
    onEnter(args) {
      this.p = args[0];
      const [n, blob] = regions(this.p);
      send({ev: 'regions', when: 'enter', count: n}, blob);
    },
    onLeave() {
      const [n, blob] = regions(this.p);
      send({ev: 'regions', when: 'leave', count: n}, blob);
      send({ev: 'status', count: n}, n > 0 ? this.p.add(0x120).readPointer().readByteArray(n) : null);
      flush();
      send({ev: 'done'});
    }
  });
  if (MARCHBOXES.length > 0) {
    Interceptor.attach(mod.base.add(CLASSIFY), {
      onEnter(args) {
        const b = args[1];
        const lo = [b.readFloat(), b.add(4).readFloat(), b.add(8).readFloat()];
        if (!MARCHBOXES.some(m => m[0] === lo[0] && m[1] === lo[1] && m[2] === lo[2])) return;
        this.mine = true; this.rows = [];
        marchThread[this.threadId] = this;
        if (!snapped) {
          snapped = true;
          const p = args[0], n = p.add(0x40).readS32();
          send({ev: 'snapshot', count: n}, concat(p.add(0x48).readPointer().readByteArray(n * 16), p.add(0x120).readPointer().readByteArray(n)));
        }
      },
      onLeave(ret) {
        if (!this.mine) return;
        delete marchThread[this.threadId];
        const buf = new ArrayBuffer(this.rows.length * 32), v = new DataView(buf);
        this.rows.forEach((r, i) => { for (let k = 0; k < 7; k++) v.setFloat32(i * 32 + k * 4, r[k], true); v.setInt32(i * 32 + 28, r[7], true); });
        const b = this.box;
        send({ev: 'march', answer: ret.toInt32() & 0xff, count: this.rows.length}, buf);
      }
    });
    Interceptor.attach(mod.base.add(MARCH), {
      onEnter(args) {
        const owner = marchThread[this.threadId];
        if (owner === undefined) return;
        this.owner = owner;
        const o = args[1], q = args[3];
        this.row = [o.readFloat(), o.add(4).readFloat(), o.add(8).readFloat(),
                    q.readFloat(), q.add(4).readFloat(), q.add(8).readFloat(), q.add(12).readFloat()];
      },
      onLeave(ret) {
        if (!this.owner) return;
        this.row.push(ret.toInt32() & 0xff);
        this.owner.rows.push(this.row);
      }
    });
  }
  if (RAYBOXES.length > 0) {
    Interceptor.attach(mod.base.add(GATHER), {
      onEnter(args) {
        const b = args[2];
        const lo = [b.readFloat(), b.add(4).readFloat(), b.add(8).readFloat()];
        if (RAYBOXES.some(m => m[0] === lo[0] && m[1] === lo[1] && m[2] === lo[2])) { rayThread[this.threadId] = lo; this.mine = true; }
      },
      onLeave() { if (this.mine) delete rayThread[this.threadId]; }
    });
    Interceptor.attach(mod.base.add(SECOND), {
      onEnter(args) {
        const lo = rayThread[this.threadId];
        if (lo === undefined) return;
        const tracer = args[0], list = args[1], hits = args[3];
        const n = list.readS32(), rays = list.add(8).readPointer();
        const shift = tracer.add(0x80).readU32(), mask = tracer.add(0x84).readU32(), blocks = tracer.add(0x88).readU32();
        const flags = tracer.add(0x8c).readU32();
        const table = (flags & 0x7fffffff) === 0 ? null : tracer.add(0x90).readPointer();
        const tris = [];
        for (let i = 0; i < n; i++) {
          const id = hits.add(i * 56 + 0xc).readU32();
          if (id === 0xffffffff || table === null) continue;
          const blk = id >>> shift;
          if (blk >= blocks) continue;
          const rec = table.add(blk * 0x10 + 8).readPointer().add((id & mask) * 0x30);
          tris.push([id, Array.from(new Uint8Array(rec.readByteArray(0x30)))]);
        }
        send({ev: 'rays', box: lo, count: n, tris: tris}, concat(rays.readByteArray(n * 0x20), hits.readByteArray(n * 56)));
      }
    });
  }
  if (WANT_SEED) {
    Interceptor.attach(mod.base.add(SEED), {
      onEnter() { seedOf[this.threadId] = null; this.on = true; },
      onLeave(ret) {
        const r = seedOf[this.threadId];
        if (r) { r.verdict = ret.toInt32() & 0xff; batch.push(r); if (batch.length >= 4096) flush(); }
        delete seedOf[this.threadId];
      }
    });
    Interceptor.attach(mod.base.add(GATHER), {
      onEnter(args) {
        this.res = args[1]; this.box = args[2]; this.q = args[3].toInt32();
      },
      onLeave() {
        const b = [], t = [];
        for (let k = 0; k < 6; k++) b.push(this.box.add(k * 4).readFloat());
        for (const off of [0x18, 0x1c, 0x20, 0x24, 0x28, 0x2c, 0x30]) t.push(this.res.add(off).readS32());
        const r = {box: b, quality: this.q, t: t, inSeed: this.threadId in seedOf, verdict: -1};
        if (r.inSeed) seedOf[this.threadId] = r;
        else { batch.push(r); if (batch.length >= 4096) flush(); }
      }
    });
  }
  send({ev: 'hooked'});
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
    p.add_argument("--seed", action="store_true")
    p.add_argument("--rays", default="")
    p.add_argument("--march", default="")
    a = p.parse_args()
    if running("cs2.exe") or running("resourcecompiler.exe"):
        raise SystemExit("CS2 or a compile is running; not starting")
    rva = installed("visbuilder", 0x33890)
    gather, seed = installed("visbuilder", 0x4c9d0), installed("visbuilder", 0x4b9b0)
    second = installed("visbuilder", 0x4d1a0)
    rayboxes = [[float(v) for v in b.split(",")] for b in a.rays.split(";") if b]
    classify, march = installed("visbuilder", 0x2f5d0), installed("visbuilder", 0x2f430)
    marchboxes = [[float(v) for v in b.split(",")] for b in a.march.split(";") if b]
    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "vis_capture", a.map + ".outside.bin")
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = out_path + ".vpk.bak"
    if os.path.exists(vpk):
        shutil.copyfile(vpk, backup)
    out = open(out_path, "wb")
    lock = threading.Lock()
    finished = threading.Event()

    def on_message(msg, data):
        if msg.get("type") != "send":
            print("agent:", msg, flush=True)
            return
        pay = msg["payload"]
        head = json.dumps(pay).encode()
        blob = data or b""
        with lock:
            out.write(struct.pack("<I", len(head)) + head + struct.pack("<I", len(blob)) + blob)
        if pay.get("ev") not in ("gathers", "rays", "march", "snapshot"):
            print(pay, flush=True)
        if pay.get("ev") == "done":
            finished.set()

    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    dev = frida.get_local_device()
    pid = dev.spawn([os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
                     "-i", source, "-world", "-vis", "-fshallow"], cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT % {"rva": rva, "gather": gather, "seed": seed, "want": "true" if a.seed else "false",
                                       "second": second, "rayboxes": json.dumps(rayboxes),
                                       "classify": classify, "march": march, "marchboxes": json.dumps(marchboxes)})
    sc.on("message", on_message)
    sc.load()
    gone = threading.Event()
    ses.on("detached", lambda *x: gone.set())
    dev.resume(pid)
    while not finished.wait(2) and not gone.is_set():
        if running("cs2.exe"):
            break
    # The verdicts are all this needs: stop the compile before it packs.
    subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
    while running("resourcecompiler.exe"):
        threading.Event().wait(1)
    out.close()
    stem = out_path[:-len(".outside.bin")] if out_path.endswith(".outside.bin") else out_path
    scene = os.path.join(os.environ.get("TEMP", "."), "csgo_addons", a.addon, "maps", a.map)
    for ext in (".rte", ".viscfg"):
        if os.path.exists(scene + ext):
            shutil.copyfile(scene + ext, stem + ".outside" + ext)
            print("->", stem + ".outside" + ext)
    if os.path.exists(backup):
        shutil.copyfile(backup, vpk)
        os.remove(backup)
        print("restored", vpk)
    print("->", out_path)


if __name__ == "__main__":
    main()
