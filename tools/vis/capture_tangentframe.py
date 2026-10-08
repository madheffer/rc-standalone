"""capture_tangentframe.py <addon> <map> [--out file] [--switches "..."]: CompressTangentFrame calls that give 180 degrees.

Runs a compile (default -world -fshallow) under Frida and hooks
CompressTangentFrame (0923 182067e80, through tools/re/rva_map.py): rcx the
normal (3 floats), rdx the tangent (4 floats, w the sign), r8b the search
flag, eax the packed word (bits 1-11 the tangent's angle over 2047). Every
call whose angle bits are 1024 (180 degrees, which a NaN tangent also gives)
is written as one JSON line: the normal and tangent as hex float bits, the
flag, the word and the caller's RVA; a final line counts all calls. Used for
ledger 55's agg_prop vertices, where Valve's word says 180 and ours 0.

CS2 must be closed and 10 GB free; the map's package is backed up first and
restored, cmp-checked, afterwards.
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

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")

AGENT = r"""
'use strict';
let total = 0;
function hex(p, n) {
  const out = [];
  for (let i = 0; i < n; i++) out.push(('00000000' + p.add(i * 4).readU32().toString(16)).slice(-8));
  return out;
}
function hook() {
  const mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  Interceptor.attach(mod.base.add(%(encode)d), {
    onEnter(args) {
      this.n = args[0];
      this.t = args[1];
      this.flag = args[2].toInt32() & 0xff;
      this.caller = this.returnAddress.sub(mod.base).toInt32();
    },
    onLeave(ret) {
      total++;
      const word = ret.toInt32() >>> 0;
      if (((word >>> 1) & 0x7ff) !== 0x400) return;
      send({normal: hex(this.n, 3), tangent: hex(this.t, 4), flag: this.flag, word: word, caller: this.caller});
    }
  });
  send({hooked: true});
}
hook();
rpc.exports = { total() { return total; } };
"""


def running(name):
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq " + name], capture_output=True, text=True).stdout
    return name.lower() in out.lower()


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("map")
    p.add_argument("--out")
    p.add_argument("--switches", default="-world -fshallow")
    a = p.parse_args()
    if running("cs2.exe"):
        raise SystemExit("CS2 is running; not starting")
    if running("resourcecompiler.exe"):
        raise SystemExit("another resourcecompiler is running; not starting")
    if shutil.disk_usage(os.path.splitdrive(CS2)[0] + "\\").free < 10 * 1024 ** 3:
        raise SystemExit("under 10 GB free on the game drive; not starting")
    encode = installed("resourcecompiler", 0x2067e80)
    out = a.out or os.path.join(os.environ.get("TEMP", "."), "tangentframe_capture", a.map + ".jsonl")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = out + ".vpk.bak"
    if os.path.exists(vpk):
        shutil.copyfile(vpk, backup)
    sink = open(out, "w")
    lock = threading.Lock()
    hooked = threading.Event()
    count = [0]

    def on_message(msg, data):
        if msg.get("type") != "send":
            print("agent:", msg.get("description") or msg)
            return
        payload = msg["payload"]
        if payload.get("hooked"):
            hooked.set()
            return
        with lock:
            sink.write(json.dumps(payload) + "\n")
            count[0] += 1

    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
            "-i", source] + a.switches.split()
    dev = frida.get_local_device()
    pid = dev.spawn(argv, cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT % {"encode": encode})
    sc.on("message", on_message)
    sc.load()
    done = threading.Event()
    ses.on("detached", lambda *x: done.set())
    dev.resume(pid)
    total = None
    while not done.wait(2):
        if running("cs2.exe"):
            subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
        try:
            total = sc.exports_sync.total()
        except Exception:
            pass
    while running("resourcecompiler.exe"):
        threading.Event().wait(2)
    sink.write(json.dumps({"calls_seen": total}) + "\n")
    sink.close()
    print(out, "(hooked)" if hooked.is_set() else "(NEVER HOOKED)", count[0], "at 180 degrees of", total)
    if os.path.exists(backup):
        shutil.copyfile(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored", vpk, "(cmp ok)" if ok else "(CMP FAILED)")
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
