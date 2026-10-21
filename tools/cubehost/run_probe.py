"""run_probe.py [--map probe01] [--from s2c_rc_probe] [--timeout 600] [-- extra engine args]: the game's cubemap build in cubehost.

Copies <from>'s compiled <map>.vpk into the scratch addon s2c_cubeprobe (game/csgo_addons/s2c_cubeprobe/maps), so
the user's own addons are never written, runs tools/cubehost (engine2's Source2Main in our own process, never cs2.exe)
with -insecure -nosteam -buildcubemaps, and keeps what the build leaves under %TEMP%/cubehost_run/<map>: every file
under the scratch addon's content and game folders that changed, and the console output.

Refuses unless Steam (steam.exe, steamwebhelper.exe) and CS2 are closed: the host checks this again itself.
"""
import argparse
import os
import shutil
import subprocess
import sys
import time

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
HERE = os.path.dirname(os.path.abspath(__file__))
HOST = os.path.join(HERE, "out", "bin", "win64", "cubehost.exe")
ADDON = "s2c_cubeprobe"


def running():
    out = subprocess.run(["tasklist"], capture_output=True, text=True).stdout.lower()
    return [n for n in ("steam.exe", "steamwebhelper.exe", "cs2.exe") if n in out]


def snapshot(root):
    files = {}
    for d, _, names in os.walk(root):
        for n in names:
            p = os.path.join(d, n)
            st = os.stat(p)
            files[p] = (st.st_size, st.st_mtime_ns)
    return files


def main():
    argv = sys.argv[1:]
    extra = argv[argv.index("--") + 1:] if "--" in argv else []
    argv = argv[:argv.index("--")] if "--" in argv else argv
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--map", default="probe01")
    p.add_argument("--from", dest="source", default="s2c_rc_probe")
    p.add_argument("--timeout", type=int, default=600)
    a = p.parse_args(argv)
    if (r := running()):
        raise SystemExit(f"close {', '.join(r)} first (Steam must be fully closed)")
    # The game/bin files the engine looks for beside its executable's folder (assettypes_common.txt and the rest).
    for name in os.listdir(os.path.join(CS2, "game", "bin")):
        source = os.path.join(CS2, "game", "bin", name)
        if os.path.isfile(source):
            shutil.copy2(source, os.path.join(HERE, "out", "bin", name))
    game = os.path.join(CS2, "game", "csgo_addons", ADDON)
    content = os.path.join(CS2, "content", "csgo_addons", ADDON)
    os.makedirs(os.path.join(game, "maps"), exist_ok=True)
    os.makedirs(content, exist_ok=True)
    shutil.copy2(os.path.join(CS2, "game", "csgo_addons", a.source, "maps", a.map + ".vpk"), os.path.join(game, "maps", a.map + ".vpk"))
    out = os.path.join(os.environ.get("TEMP", "."), "cubehost_run", a.map)
    os.makedirs(out, exist_ok=True)
    before = {**snapshot(game), **snapshot(content)}
    # -condebug appends to the game's console.log (the user's own): keep it aside and restore it after.
    console = os.path.join(CS2, "game", "csgo", "console.log")
    console_backup = os.path.join(out, "console.log.user")
    had_console = os.path.exists(console)
    if had_console:
        shutil.copy2(console, console_backup)
    console_start = os.path.getsize(console) if had_console else 0
    cmd = [HOST, "--", "-insecure", "-nosteam", "-nobreakpad", "-vulkan", "-novid", "-console", "-condebug", "-addon", ADDON,
           "+map", a.map, "-buildcubemaps", *extra]
    print(" ".join(cmd))
    started = time.time()
    with open(os.path.join(out, "host.log"), "wb") as log:
        try:
            code = subprocess.run(cmd, stdout=log, stderr=subprocess.STDOUT, timeout=a.timeout).returncode
        except subprocess.TimeoutExpired:
            subprocess.run(["taskkill", "/F", "/IM", "cubehost.exe"], capture_output=True)
            code = "timeout"
    after = {**snapshot(game), **snapshot(content)}
    changed = [f for f, v in after.items() if before.get(f) != v]
    for f in changed:
        rel = os.path.relpath(f, os.path.join(CS2))
        dest = os.path.join(out, rel.replace(os.sep, "__"))
        shutil.copy2(f, dest)
    if os.path.exists(console):
        with open(console, "rb") as f:
            f.seek(console_start if os.path.getsize(console) >= console_start else 0)
            with open(os.path.join(out, "console.log"), "wb") as o:
                o.write(f.read())
        if had_console:
            shutil.copy2(console_backup, console)
            os.remove(console_backup)
        else:
            os.remove(console)
    print(f"exit {code} after {time.time() - started:.0f} s; {len(changed)} files changed, kept in {out}")
    for f in changed:
        print("  " + os.path.relpath(f, CS2))


if __name__ == "__main__":
    main()
