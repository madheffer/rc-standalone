"""capture_argdeps.py <addon> <map> [--out file] [--full] [-- extra rc args]: each resource's argument rows in read order.

Hooks the edit info writer (09-23 FUN_18016c650, 10-15 0x18016c880) on entry: the compile context
holds the m_ArgumentDependencies rows at +0x158 (count) / +0x160 (24-byte entries: CUtlString name,
CUtlString type, fingerprint, default) in the order the steps first read them (the recorder
FUN_180174f90 appends a row it has not seen to the context and each parent). The writer then
std::sorts them by V_stricmp_fast on the name (FUN_18017a9c0), so this is the sort's input. One JSON
line per resource: its names at +0x80 / +0x90 and the rows. Ledger 119.

CS2 must be closed (the compile is killed if it starts), 10 GB must be free, and the map's package is
backed up first and restored, cmp-checked, afterwards; the compile's own package is kept at <out>.vpk.
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

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
# resourcecompiler.dll 2026-10-15 (port_addr.py from the 09-23 build's 18016c650).
WRITER_RVA = 0x16c880

AGENT = r"""
'use strict';
function cstr(p) { return p.isNull() ? null : p.readUtf8String(); }
function hook() {
  const mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  send({ev: 'hooked'});
  Interceptor.attach(mod.base.add(%(rva)d), {
    onEnter(args) {
      const ctx = args[0];
      const n = ctx.add(0x158).readS32();
      const base = ctx.add(0x160).readPointer();
      const rows = [];
      for (let i = 0; i < n; i++) {
        const e = base.add(i * 24);
        rows.push([cstr(e.readPointer()), cstr(e.add(8).readPointer()), e.add(16).readU32(), e.add(20).readU32()]);
      }
      send({ev: 'args', a: cstr(ctx.add(0x80).readPointer()), b: cstr(ctx.add(0x90).readPointer()), rows: rows});
    }
  });
}
hook();
"""


def running(name):
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq " + name], capture_output=True, text=True).stdout
    return name.lower() in out.lower()


def main():
    argv = sys.argv[1:]
    extra = argv[argv.index("--") + 1:] if "--" in argv else []
    argv = argv[:argv.index("--")] if "--" in argv else argv
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("map")
    p.add_argument("--out")
    p.add_argument("--full", action="store_true", help="a full compile rather than -world -fshallow")
    a = p.parse_args(argv)
    if running("cs2.exe") or running("resourcecompiler.exe"):
        raise SystemExit("CS2 or a compile is running; not starting")
    if shutil.disk_usage(os.path.splitdrive(CS2)[0] + "\\").free < 10 * 1024 ** 3:
        raise SystemExit("under 10 GB free on the game drive; not starting")

    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "argdeps", a.map + ".jsonl")
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = out_path + ".vpk.bak"
    if os.path.exists(vpk):
        shutil.copy2(vpk, backup)  # keeps the write time (tests take a newer package as a compile's output)
        os.remove(vpk)  # a current package makes RC skip the compile

    out = open(out_path, "w", encoding="utf-8")
    lock = threading.Lock()
    stats = {}

    def on_message(msg, data):
        if msg.get("type") != "send":
            print("agent:", msg.get("description") or msg, file=sys.stderr)
            return
        pay = msg["payload"]
        stats[pay["ev"]] = stats.get(pay["ev"], 0) + 1
        if pay["ev"] == "args":
            with lock:
                out.write(json.dumps(pay) + "\n")

    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    cmd = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"), "-i", source]
    if not a.full:
        cmd += ["-world", "-fshallow"]
    cmd += extra
    dev = frida.get_local_device()
    pid = dev.spawn(cmd, cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT % {"rva": WRITER_RVA})
    sc.on("message", on_message)
    sc.load()
    done = threading.Event()
    ses.on("detached", lambda *x: done.set())
    dev.resume(pid)
    killed = False
    while not done.wait(2):
        if running("cs2.exe"):
            subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
            subprocess.run(["taskkill", "/F", "/IM", "vrad3.exe"], capture_output=True)
            killed = True
    out.close()
    print(out_path, stats, "(killed: cs2 started)" if killed else "")

    if os.path.exists(vpk):
        shutil.copyfile(vpk, out_path + ".vpk")
    if os.path.exists(backup):
        shutil.copy2(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored", vpk, "(cmp ok)" if ok else "(CMP FAILED)")
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
