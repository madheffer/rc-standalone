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
5. With --fixtures, the addon packages tests read (every s2c_rc_probe package,
   and FIXTURES below) that are older than the installed resourcecompiler.exe,
   plus any --fixture-maps addon/map, are rebuilt in place; each package is
   copied to %TEMP%/fixture_backups first (write time kept). s2c_rc_probe's are
   full builds (-f), as MapFixtures.CompileWithRc makes them: the lump tests
   read the bake's keys (bakedshadowindex, light_map_uniqueid...), which a
   world-only build leaves out; atixref (UNBAKED) stays world-only. FIXTURES get -world -fshallow, which refreshes
   the world and the entity lumps and keeps the rest (Mako's bake takes hours).
   The unlit world the node model tests read is step 1's references.
6. With --partial, the two partial-build facts still open (GROUND_TRUTH 55):
   capture_overrides.py on probe01 with -world -vis -fshallow (the vvis's
   ___OverrideInputData___ text), and a -world -fshallow build of probe01
   over its pre-10-07 package, which holds a world_visibility.vvis_c: whether
   a build without vis keeps it (listed after, from %TEMP%/partial_check).
7. Cleanup: crash dumps resourcecompiler left in the game's bin folder in this
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
# Addon packages the suite reads besides s2c_rc_probe's own (every one of which counts):
# --fixtures rebuilds those older than the installed resourcecompiler.exe (2026-10-16).
FIXTURES = [("ze_doom_p2", "cardtest"), ("s2c_big", "ze_ffvii_mako_reactor_v6_p")]
# s2c_rc_probe packages the tests read unbaked (BakedShadowAssignmentTests.UnbakedCompilesWriteNothing,
# and the light_omni2 shadow slot a baked atixref lump needs is not ported).
UNBAKED = {"atixref"}
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
    p.add_argument("--fixtures", action="store_true", help="also rebuild the stale addon fixture packages in place")
    p.add_argument("--fixture-maps", default="", help="addon/map,... rebuilt with --fixtures however new their package looks")
    p.add_argument("--partial", action="store_true", help="also settle the open partial-build facts (vvis fingerprint, a kept vvis)")
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

    step("5. fixtures")
    if a.fixtures:
        backups = os.path.join(TEMP, "fixture_backups")
        os.makedirs(backups, exist_ok=True)
        bin_dir = os.path.join(CS2, "game", "bin", "win64")
        rc_time = os.path.getmtime(os.path.join(bin_dir, "resourcecompiler.exe"))
        forced = {tuple(x.split("/", 1)) for x in a.fixture_maps.split(",") if "/" in x}
        probe = os.path.join(CS2, "game", "csgo_addons", ADDON, "maps")
        wanted = FIXTURES + [(ADDON, os.path.splitext(f)[0]) for f in sorted(os.listdir(probe)) if f.endswith(".vpk")]
        for addon, m in wanted:
            vpk = os.path.join(CS2, "game", "csgo_addons", addon, "maps", m + ".vpk")
            if os.path.exists(vpk) and os.path.getmtime(vpk) >= rc_time and (addon, m) not in forced:
                continue
            # A prefab's source sits under maps/prefabs/..., as MapFixtures.VmapSource finds it.
            maps_dir = os.path.join(CS2, "content", "csgo_addons", addon, "maps")
            source = next(iter(glob.glob(os.path.join(maps_dir, "**", m + ".vmap"), recursive=True)), None)
            if source is None:
                print(f"{addon}/{m}: no source, skipped")
                continue
            if os.path.exists(vpk):
                shutil.copy2(vpk, os.path.join(backups, f"{addon}__{m}.vpk"))
            if running("cs2.exe"):
                raise SystemExit("CS2 started; stopping")
            switches = ["-f"] if addon == ADDON and m not in UNBAKED else ["-world", "-fshallow"]
            with open(os.path.join(backups, f"{addon}__{m}.log"), "wb") as log:
                code = subprocess.run([os.path.join(bin_dir, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
                                       "-i", source] + switches, cwd=bin_dir, stdout=log, stderr=subprocess.STDOUT).returncode
            print(f"{addon}/{m}: rc exit {code}")

    step("6. partial builds")
    if a.partial:
        run([sys.executable, os.path.join(ROOT, "tools", "vis", "capture_overrides.py"), ADDON, "probe01",
             "--out", os.path.join(TEMP, "override_capture", "probe01_vis"), "--switches", "-world -vis -fshallow"])
        old_pkg = os.path.join(TEMP, "nobake_refs_pre1007", "probe01.vpk")
        vpk = os.path.join(CS2, "game", "csgo_addons", ADDON, "maps", "probe01.vpk")
        check = os.path.join(TEMP, "partial_check")
        os.makedirs(check, exist_ok=True)
        if os.path.exists(old_pkg) and not running("cs2.exe"):
            backup = os.path.join(check, "probe01.vpk.bak")
            if os.path.exists(vpk):
                shutil.copyfile(vpk, backup)
            shutil.copyfile(old_pkg, vpk)
            bin_dir = os.path.join(CS2, "game", "bin", "win64")
            with open(os.path.join(check, "probe01.log"), "wb") as log:
                subprocess.run([os.path.join(bin_dir, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
                                "-i", os.path.join(CS2, "content", "csgo_addons", ADDON, "maps", "probe01.vmap"), "-world", "-fshallow"],
                               cwd=bin_dir, stdout=log, stderr=subprocess.STDOUT)
            shutil.copyfile(vpk, os.path.join(check, "probe01_after.vpk"))
            if os.path.exists(backup):
                shutil.copyfile(backup, vpk)
            import vpk as vpklib
            names = sorted(vpklib.open(os.path.join(check, "probe01_after.vpk")))
            print("world_visibility kept" if any(n.endswith("world_visibility.vvis_c") for n in names) else "world_visibility DROPPED")

    step("7. cleanup")
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
