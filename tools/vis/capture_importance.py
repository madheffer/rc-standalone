"""capture_importance.py <addon> <map> [--out file]: the lightmap importance volumes a full compile charts against.

Runs a full compile (-f) under Frida and hooks StaticLighting_ImportanceScales
(0923 18020f910), which PrepareChartMesh calls per chart mesh with the
lighting processor, the chart mesh, the source CMesh, its transform, the
per-triangle scale vector and the transition region:

  setter    the importance list's setter (processor vf 0x48, 1801f86f0):
            the list's count and the call stack
  volumes   once: the processor's vtable entry 0x50 (its RVA) and the list it
            returns, each volume's hull points (+0 count, +8 points, +0x10
            scale)
  scales    per call: the region, the transform (12 floats) and the
            per-triangle scales written (+0 count, +8 data)

One JSON line per record. CS2 must be closed and 10 GB free; the map's
package is backed up and restored, cmp-checked.
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
function floats(p, n) { const o = []; for (let i = 0; i < n; i++) o.push(p.add(i * 4).readFloat()); return o; }
let listed = false;
function hook() {
  const mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  Interceptor.attach(mod.base.add(%(setter)d), {
    onEnter(args) {
      const bt = Thread.backtrace(this.context, Backtracer.ACCURATE).slice(0, 6).map(a => {
        const m = Process.findModuleByAddress(a); return m ? m.name + '+' + a.sub(m.base).toString(16) : a.toString(); });
      send({stage: 'setter', list: args[1].readS32(), caller: bt});
    }
  });
  Interceptor.attach(mod.base.add(%(scales)d), {
    onEnter(args) {
      this.out = args[4];
      this.matrix = floats(args[3], 12);
      if (!listed) {
        listed = true;
        const vf = args[0].readPointer().add(0x50).readPointer();
        const get = new NativeFunction(vf, 'pointer', ['pointer']);
        const list = get(args[0]);
        const n = list.readS32(), data = list.add(8).readPointer(), vols = [];
        for (let i = 0; i < n; i++) {
          const h = data.add(i * 8).readPointer(), k = h.readS32();
          vols.push({count: k, scale: h.add(0x10).readFloat(), points: floats(h.add(8).readPointer(), k * 3), head: Array.from(new Uint8Array(h.readByteArray(0x40)))});
        }
        send({stage: 'volumes', vf: vf.sub(mod.base).toInt32(), volumes: vols});
      }
    },
    onLeave(ret) {
      const n = this.out.readS32();
      send({stage: 'scales', result: ret.toInt32() & 0xff, matrix: this.matrix, scales: floats(this.out.add(8).readPointer(), n)});
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
    rvas = {"scales": installed("resourcecompiler", 0x20f910), "setter": installed("resourcecompiler", 0x1f86f0)}
    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "importance_capture", a.map + ".jsonl")
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
    if os.path.exists(backup):
        shutil.copy2(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored", vpk, "(cmp ok)" if ok else "(CMP FAILED)")
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
