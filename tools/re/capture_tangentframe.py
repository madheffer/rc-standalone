"""capture_tangentframe.py <addon> <map> [--out file]: the inputs of every packed tangent frame whose tangent angle
lands on an axis step (OPEN 1.c7, ledger 249, CS2_CLOSED_QUEUE A.13).

About 20 atixref world node models differ from Valve's only in packed NORMAL words whose tangent angle is 1023 in
ours and 1024 in Valve's, or 2047 against 0. CompressTangentFrame (0923 182067e80) takes the normal (rcx, three
floats), the tangent (rdx, four floats) and a flag (r8b, the slow path through 182067b00) and returns the word in
eax. This hooks it in a -world -fshallow compile and keeps, once each, every (normal, tangent, flag, word) whose
angle bits (word >> 1 & 0x7ff) are 0, 1023, 1024 or 2047, as hex, together with the caller's return address
(installed RVA). Offline, TangentFrameCaptureProbe runs our Compress on each input (does our compressor give
Valve's word?) and looks up our differing vertices' frames among them (which input bit differs?). CS2 must be
closed; the package is backed up and restored, cmp-checked.
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

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from rva_map import installed  # noqa: E402

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")

AGENT = r"""
'use strict';
const seen = new Set();
let batch = [], calls = 0;
function hex(p, n) { return Array.from(new Uint8Array(p.readByteArray(n))).map(b => b.toString(16).padStart(2, '0')).join(''); }
function flush() { if (batch.length) { send({frames: batch, calls: calls}); batch = []; } }
function hook() {
  const mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  Interceptor.attach(mod.base.add(%(compress)d), {
    onEnter(args) { this.n = args[0]; this.t = args[1]; this.flag = args[2].toInt32() & 0xff; this.ret = this.returnAddress; },
    onLeave(ret) {
      calls++;
      const w = ret.toUInt32(), q = (w >>> 1) & 0x7ff;
      if (q !== 0 && q !== 1023 && q !== 1024 && q !== 2047) return;
      const key = hex(this.n, 12) + hex(this.t, 16) + this.flag;
      if (seen.has(key)) return;
      seen.add(key);
      batch.push({n: hex(this.n, 12), t: hex(this.t, 16), flag: this.flag, word: w >>> 0,
                  caller: this.ret.sub(mod.base).toString(16)});
      if (batch.length >= 500) flush();
    }
  });
  send({hooked: true});
}
setInterval(flush, 1000);
const exitp = Process.getModuleByName('kernel32.dll').getExportByName('ExitProcess');
Interceptor.attach(exitp, { onEnter() { flush(); Thread.sleep(0.5); } });
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
    rvas = {"compress": installed("resourcecompiler", 0x2067e80)}
    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "tangentframe_capture", a.map + ".jsonl")
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = out_path + ".vpk.bak"
    if os.path.exists(vpk):
        shutil.copyfile(vpk, backup)
        os.remove(vpk)
    out = open(out_path, "w")
    lock = threading.Lock()
    hooked = threading.Event()
    stats = {"frames": 0, "calls": 0}

    def on_message(msg, _data):
        if msg.get("type") != "send":
            print("agent:", msg.get("description") or msg)
        elif msg["payload"].get("hooked"):
            hooked.set()
        else:
            with lock:
                for f in msg["payload"]["frames"]:
                    out.write(json.dumps(f) + "\n")
                stats["frames"] += len(msg["payload"]["frames"])
                stats["calls"] = msg["payload"]["calls"]

    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
            "-i", source, "-world", "-fshallow"]
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
    print(out_path, "(hooked)" if hooked.is_set() else "(NEVER HOOKED)", stats)
    if os.path.exists(backup):
        shutil.copyfile(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored", vpk, "(cmp ok)" if ok else "(CMP FAILED)")
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
