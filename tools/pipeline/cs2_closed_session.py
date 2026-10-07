"""cs2_closed_session.py [--maps a,b] [--skip-captures] [--no-devbox]: everything that needs CS2 closed, in one pass.

1. Fresh Valve references: compile_fresh.py s2c_rc_probe <map> (-world
   -fshallow, as the current references were made) for each map, into
   %TEMP%/fresh_refs. The old %TEMP%/nobake_refs is kept once as
   nobake_refs_pre1007, then the fresh packages replace it.
2. Captures: capture_subdivtangents.py on atixref (ledger 38);
   capture_overrides.py on probe01 and ze_hold_em_p (the override buffers and
   the saved n0.vrman behind RED2's hashes).
3. Each fresh reference's write time is printed beside the game's
   pak01_dir.vpk time, so a missing or stale one shows at once.
4. The fresh references are copied to the dev box (~/s2c/refs/nobake_refs).
5. Cleanup: crash dumps resourcecompiler left in the game's bin folder in this
   session, and any atixref.vpk a capture left in the addon's maps folder
   beyond the user's own (its backup is restored by the tools themselves).

Every step refuses while cs2.exe runs (the tools check it again themselves).
"""
import argparse
import datetime
import glob
import os
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
TEMP = os.environ.get("TEMP", ".")
ADDON = "s2c_rc_probe"
MAPS = ["atixref", "probe01", "cardtest", "ze_hold_em_p"]
DEVBOX = "dev@192.168.20.32"
KEY = os.path.expanduser("~/.ssh/vpkworker")


def running(name):
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq " + name], capture_output=True, text=True).stdout
    return name.lower() in out.lower()


def step(title):
    print(f"\n=== {title} ({datetime.datetime.now():%H:%M:%S})", flush=True)


def run(cmd):
    print(" ".join(cmd), flush=True)
    return subprocess.run(cmd).returncode


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--maps", default=",".join(MAPS))
    p.add_argument("--skip-captures", action="store_true")
    p.add_argument("--no-devbox", action="store_true")
    a = p.parse_args()
    if running("cs2.exe"):
        raise SystemExit("CS2 is running; close it first")
    started = time.time()
    maps = [m for m in a.maps.split(",") if m]
    fresh = os.path.join(TEMP, "fresh_refs")
    os.makedirs(fresh, exist_ok=True)

    step("1. fresh references")
    for m in maps:
        code = run([sys.executable, os.path.join(HERE, "compile_fresh.py"), ADDON, m, "--out", os.path.join(fresh, m + ".vpk")])
        if running("cs2.exe"):
            raise SystemExit("CS2 started; stopping")
        print(f"{m}: compile_fresh exit {code}", flush=True)

    step("2. captures")
    if not a.skip_captures:
        run([sys.executable, os.path.join(ROOT, "tools", "vis", "capture_subdivtangents.py"), ADDON, "atixref"])
        run([sys.executable, os.path.join(ROOT, "tools", "vis", "subdivtangent_stages.py"),
             os.path.join(TEMP, "subdivtangent_capture", "atixref.jsonl"), "--limit", "15"])
        # The bytes behind RED2's ___OverrideInputData___ fingerprints and n0.vrman's file CRC.
        for m in ("probe01", "ze_hold_em_p"):
            run([sys.executable, os.path.join(ROOT, "tools", "vis", "capture_overrides.py"), ADDON, m])

    step("3. reference freshness")
    pak = os.path.join(CS2, "game", "csgo", "pak01_dir.vpk")
    print(f"pak01_dir.vpk {datetime.datetime.fromtimestamp(os.path.getmtime(pak))}")
    for m in maps:
        f = os.path.join(fresh, m + ".vpk")
        print(f"{m}: {'written ' + str(datetime.datetime.fromtimestamp(os.path.getmtime(f))) if os.path.exists(f) else 'MISSING'}")
    old = os.path.join(TEMP, "nobake_refs")
    if os.path.isdir(old):
        keep = os.path.join(TEMP, "nobake_refs_pre1007")
        if not os.path.exists(keep):
            shutil.copytree(old, keep)
            print("kept the old references in", keep)
    os.makedirs(old, exist_ok=True)
    for m in maps:
        f = os.path.join(fresh, m + ".vpk")
        if os.path.exists(f):
            shutil.copyfile(f, os.path.join(old, m + ".vpk"))
            if os.path.exists(f + ".log"):
                shutil.copyfile(f + ".log", os.path.join(old, m + ".vpk.log"))

    step("4. dev box")
    if not a.no_devbox:
        files = [os.path.join(old, m + ".vpk") for m in maps if os.path.exists(os.path.join(old, m + ".vpk"))]
        if files:
            run(["scp", "-q", "-i", KEY] + files + [f"{DEVBOX}:s2c/refs/nobake_refs/"])

    step("5. cleanup")
    for dump in glob.glob(os.path.join(CS2, "game", "bin", "win64", "*.dmp")) + glob.glob(os.path.join(CS2, "game", "bin", "win64", "*.mdmp")):
        if os.path.getmtime(dump) >= started:
            os.remove(dump)
            print("removed", dump)
    stray = os.path.join(CS2, "game", "csgo_addons", ADDON, "maps", "atixref.vpk")
    if os.path.exists(stray) and os.path.getmtime(stray) >= started:
        print("NOTE: atixref.vpk in the addon was written this session; the tools restore the user's copy, check it:", stray)
    print(f"\ndone in {(time.time() - started) / 60:.1f} min")


if __name__ == "__main__":
    main()
