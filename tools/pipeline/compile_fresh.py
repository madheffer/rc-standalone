"""compile_fresh.py <addon> <map> [--out file] [--full] [-- extra rc args]: a fresh compile kept aside.

Compiles <addon>'s <map>.vmap with the installed resourcecompiler (-world
-fshallow by default, the whole map with --full) and keeps the package it
writes at --out (default %TEMP%/fresh_compile/<map>.vpk). The map's own
package is backed up first and restored, cmp-checked, afterwards, so the
user's addon is left as it was.

Refuses while CS2 or another compile runs, or with under 10 GB free.
"""
import argparse
import filecmp
import os
import shutil
import subprocess
import sys

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")


def running(name):
    return name.lower() in subprocess.run(["tasklist"], capture_output=True, text=True).stdout.lower()


def main():
    argv = sys.argv[1:]
    extra = argv[argv.index("--") + 1:] if "--" in argv else []
    argv = argv[:argv.index("--")] if "--" in argv else argv
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("map")
    p.add_argument("--out")
    p.add_argument("--full", action="store_true")
    a = p.parse_args(argv)
    if running("cs2.exe") or running("resourcecompiler.exe"):
        raise SystemExit("CS2 or a compile is running; not starting")
    if shutil.disk_usage(os.path.splitdrive(CS2)[0] + "\\").free < 10 * 1024 ** 3:
        raise SystemExit("under 10 GB free on the game drive; not starting")
    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "fresh_compile", a.map + ".vpk")
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = out_path + ".orig.bak"
    if os.path.exists(vpk):
        shutil.copyfile(vpk, backup)
        os.remove(vpk)
    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    cmd = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"), "-i", source]
    if not a.full:
        cmd += ["-world", "-fshallow"]
    cmd += extra
    with open(out_path + ".log", "wb") as log:
        code = subprocess.run(cmd, cwd=BIN, stdout=log, stderr=subprocess.STDOUT).returncode
    print("rc exit", code)
    if os.path.exists(vpk):
        shutil.copyfile(vpk, out_path)
        print("kept", out_path)
    if os.path.exists(backup):
        shutil.copyfile(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored" if ok else "RESTORE CMP FAILED", vpk)
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
