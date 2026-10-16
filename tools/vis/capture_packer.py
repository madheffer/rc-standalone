"""capture_packer.py <addon> <map> [--out file]: Valve's lightmap chart packing, stage by stage.

Runs a full compile (-f) under Frida and snapshots NUVChartPacker (0923
uvchartpacker.cpp; docs/BAKED.md "Lightmap charts"):

  pack_enter / pack_leave   NUVChartPacker::PackCharts (1802272d0): the
                            PackingGeometry_t (+0x18 vertices, +0x20 stride,
                            +0x24 count, +0x28 luxel uvs, +0x30 indices,
                            +0x38 count, +0x40 triangle charts, +0x48 chart
                            count, +0x50 chart records of 0x1c) and the
                            width, height, mip and pass arguments
  init_enter / init_leave   NUVChartPacker::InitializeChartUVs (1802235d0):
                            the scale, every 0x70-byte chart record with its
                            per-corner uvs (+0x10 count, +0x18), and the
                            sorted chart order (each record's index, +0x20)
  try_leave                 NUVChartPacker::TryPack (180226370): its result
                            and the 8-byte per-chart placements it wrote

One JSON line per snapshot, arrays as hex. CS2 must be closed and 10 GB
free; the map's package is backed up and restored, cmp-checked.
"""
import argparse
import filecmp
import json
import os
import shutil
import subprocess
import sys
import threading

import frida

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "re"))
from rva_map import installed  # noqa: E402

CS2 = os.environ.get(
    "CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")

AGENT = r"""
'use strict';
function hex(p, n) {
  if (p.isNull() || n <= 0) return '';
  const b = new Uint8Array(p.readByteArray(n));
  let s = '';
  for (let i = 0; i < b.length; i++) s += b[i].toString(16).padStart(2, '0');
  return s;
}
function geometry(g) {
  const i32 = o => g.add(o).readS32(), ptr = o => g.add(o).readPointer();
  const stride = i32(0x20), verts = i32(0x24), indices = i32(0x38), charts = i32(0x48);
  return {head: hex(g, 0x68), vertices: hex(ptr(0x18), stride * verts), uvs: hex(ptr(0x28), verts * 8),
          indices: hex(ptr(0x30), indices * 4), triangle_charts: hex(ptr(0x40), (indices / 3 | 0) * 4),
          charts: hex(ptr(0x50), charts * 0x1c)};
}
// A CUtlLeanVector-like {int count; int capacity; T *data}: data is used when capacity & 0x7fffffff.
function vec(v) { return {count: v.readS32(), data: (v.add(4).readU32() & 0x7fffffff) ? v.add(8).readPointer() : ptr(0)}; }
function records(v) {
  const r = vec(v), out = [];
  for (let i = 0; i < r.count; i++) {
    const c = r.data.add(i * 0x70), u = vec(c.add(0x10));
    out.push({rec: hex(c, 0x70), uvs: hex(u.data, u.count * 8)});
  }
  return out;
}
function order(v) {
  const r = vec(v), out = [];
  for (let i = 0; i < r.count; i++) out.push(r.data.add(i * 8).readPointer().add(0x20).readS32());
  return out;
}
let pass = 0;
function hook() {
  const mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  Interceptor.attach(mod.base.add(%(pack)d), {
    onEnter(args) {
      this.g = args[0];
      send(Object.assign({stage: 'pack_enter', width: args[1].toUInt32(), height: args[2].toUInt32(),
                          mip: args[3].toUInt32() & 0xff, passes: args[4].toUInt32()}, geometry(this.g)));
    },
    onLeave(ret) { send(Object.assign({stage: 'pack_leave', result: ret.toUInt32() & 0xff}, geometry(this.g))); }
  });
  Interceptor.attach(mod.base.add(%(init)d), {
    onEnter(args) {
      this.charts = args[0]; this.order = args[1]; this.p = pass++;
      send({stage: 'init_enter', pass: this.p, scale: args[4].toUInt32() >>> 0, width: args[5].toUInt32(), height: args[6].toUInt32()});
    },
    onLeave() { send({stage: 'init_leave', pass: this.p, charts: records(this.charts), order: order(this.order)}); }
  });
  Interceptor.attach(mod.base.add(%(trypack)d), {
    onEnter(args) { this.out = args[3]; this.list = args[2]; },
    onLeave(ret) {
      const r = vec(this.out);
      send({stage: 'try_leave', result: ret.toUInt32() & 0xff, placements: hex(r.data, r.count * 8), count: r.count});
    }
  });
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
    a = p.parse_args()
    if running("cs2.exe"):
        raise SystemExit("CS2 is running; not starting")
    if running("resourcecompiler.exe"):
        raise SystemExit("another resourcecompiler is running; not starting")
    if shutil.disk_usage(os.path.splitdrive(CS2)[0] + "\\").free < 10 * 1024 ** 3:
        raise SystemExit("under 10 GB free on the game drive; not starting")
    rvas = {"pack": installed("resourcecompiler", 0x2272d0), "init": installed("resourcecompiler", 0x2235d0),
            "trypack": installed("resourcecompiler", 0x226370)}
    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "packer_capture", a.map + ".jsonl")
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = out_path + ".vpk.bak"
    if os.path.exists(vpk):
        shutil.copy2(vpk, backup)
        os.remove(vpk)
    out = open(out_path, "w")
    lock = threading.Lock()
    hooked = threading.Event()

    def on_message(msg, _data):
        if msg.get("type") != "send":
            print("agent:", msg.get("description") or msg)
        elif msg["payload"].get("hooked"):
            hooked.set()
        else:
            with lock:
                out.write(json.dumps(msg["payload"]) + "\n")

    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
            "-i", source, "-f"]
    dev = frida.get_local_device()
    pid = dev.spawn(argv, cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT % rvas)
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
    print(out_path, "(hooked)" if hooked.is_set() else "(NEVER HOOKED)")
    vrad3 = os.path.join(CS2, "game", "csgo_addons", a.addon, "_vrad3")
    if os.path.isdir(vrad3):
        shutil.rmtree(out_path + ".vrad3", ignore_errors=True)
        shutil.copytree(vrad3, out_path + ".vrad3")
    if os.path.exists(backup):
        shutil.copy2(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored", vpk, "(cmp ok)" if ok else "(CMP FAILED)")
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
