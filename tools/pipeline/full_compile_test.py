"""full_compile_test.py <addon> <map> [--valve <vpk>] [--out <dir>] [--no-cubemaps] [-- <compile switches>]

A whole compile of a map from its .vmap with only our code, scored against Valve's package (CS2 closed:
the build is heavy, and the in-game check after it needs the game):

1. s2c compile-map -i <content>/csgo_addons/<addon>/maps/<map>.vmap -outroot <out> -s2cbake <out>/bake --gpu
   (no builder named: a full build into an empty package; lighting on the GPU with no vrad3; Steam Audio as
   worldspawn's rebake option says, ledger 198), through tools/re/heavy.py.
2. s2c build-cubemaps on the result (our own cube render, no game), unless --no-cubemaps.
3. The package file by file against Valve's (--valve, default the addon's own compiled package): identical,
   different, missing and extra files, and the share of Valve's bytes in identical files, by extension.
4. The in-game step's commands: copy into game/csgo/maps, launch with -insecure and netcon, map_test.py.

Refuses while cs2.exe runs unless --cs2-open: steps 0 to 3 are our own code only (s2c starts no Valve process
and touches no game process), so with the game open they run at below-normal priority, as heavy jobs that are
not a VAC concern; step 4 still needs the game closed and relaunched with -insecure.
"""
import argparse
import collections
import os
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
TEMP = os.environ.get("TEMP", ".")
S2C = os.path.join(ROOT, "src", "Source2.Compiler.Cli", "bin", "Release", "net10.0", "s2c.exe")
HEAVY = os.path.join(ROOT, "tools", "re", "heavy.py")


def running(name):
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq " + name], capture_output=True, text=True).stdout
    return name.lower() in out.lower()


def step(title):
    print(f"\n=== {title} ({time.strftime('%H:%M:%S')})", flush=True)


def heavy(what, cmd, cs2_open):
    print(" ".join(cmd), flush=True)
    return subprocess.run([sys.executable, HEAVY, "run", "--who", "full_compile_test", "--what", what, "--gpu",
                           *(["--allow-cs2"] if cs2_open else []), "--", *cmd]).returncode


def entries(vpk_path):
    import vpk
    pak = vpk.open(vpk_path)
    return {name: pak.get_file(name).read() for name in pak}


def score(valve, ours):
    rows = collections.defaultdict(lambda: [0, 0, 0, 0, 0])  # files, same, bytes, same bytes, missing
    diff, missing = [], []
    for name, data in valve.items():
        ext = name.rsplit(".", 1)[-1]
        r = rows[ext]
        mine = ours.get(name)
        same = mine == data
        r[0] += 1
        r[1] += same
        r[2] += len(data)
        r[3] += len(data) if same else 0
        r[4] += mine is None
        if mine is None:
            missing.append(name)
        elif not same:
            diff.append((len(data), name))
    print(f"{'type':<10} {'files':>6} {'same':>6} {'missing':>8} {'bytes same':>12}")
    for ext, (f, s, b, sb, m) in sorted(rows.items(), key=lambda kv: -kv[1][2]):
        print(f"{ext:<10} {f:>6} {s:>6} {m:>8} {100 * sb / max(b, 1):>11.2f}%")
    files = sum(r[0] for r in rows.values())
    same = sum(r[1] for r in rows.values())
    total = sum(r[2] for r in rows.values())
    same_bytes = sum(r[3] for r in rows.values())
    print(f"whole package: {same} of {files} files identical, {100 * same_bytes / max(total, 1):.2f}% of Valve's bytes")
    for size, name in sorted(diff, reverse=True)[:15]:
        print(f"  differs: {name} ({size:,} bytes)")
    for name in missing[:15]:
        print(f"  missing: {name}")
    for name in sorted(set(ours) - set(valve))[:15]:
        print(f"  extra:   {name}")


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("addon")
    p.add_argument("map")
    p.add_argument("--valve", help="Valve's package to score against (default the addon's compiled one)")
    p.add_argument("--out", default=os.path.join(TEMP, "full_compile"))
    p.add_argument("--no-cubemaps", action="store_true")
    p.add_argument("--cs2-open", action="store_true", help="run steps 0 to 3 with the game open, at below-normal priority")
    p.add_argument("extra", nargs="*", help="compile switches after --, as Hammer passes them")
    a = p.parse_args()
    if running("cs2.exe") and not a.cs2_open:
        raise SystemExit("CS2 is running; close it first (or --cs2-open for steps 0 to 3)")
    if a.cs2_open:
        import ctypes
        ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(), 0x4000)  # BELOW_NORMAL, inherited
    vmap = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    valve = a.valve or os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    if not os.path.exists(vmap):
        raise SystemExit(f"no {vmap}")
    out = os.path.join(a.out, a.map)
    package = os.path.join(out, "csgo_addons", a.addon, "maps", a.map + ".vpk")
    if os.path.exists(out):
        shutil.rmtree(out)
    os.makedirs(out)
    started = time.time()

    step("0. build s2c (Release)")
    if subprocess.run(["dotnet", "build", os.path.join(ROOT, "src", "Source2.Compiler.Cli"), "-c", "Release", "-nologo", "-v", "q"]).returncode:
        raise SystemExit("build failed")

    step("1. compile-map (full build, -s2cbake)")
    code = heavy(f"compile-map {a.map}", [S2C, "compile-map", "-i", vmap, "-outroot", out, "-s2cbake", os.path.join(out, "bake"), "--gpu", *a.extra], a.cs2_open)
    print(f"compile-map exit {code} ({time.time() - started:.0f} s)", flush=True)
    if code or not os.path.exists(package):
        raise SystemExit("compile-map failed")

    if not a.no_cubemaps:
        step("2. build-cubemaps")
        code = heavy(f"build-cubemaps {a.map}", [S2C, "build-cubemaps", package, "--game", os.path.join(CS2, "game"),
                                                 "--intermediates", os.path.join(out, "cubemap_intermediates")], a.cs2_open)
        print(f"build-cubemaps exit {code} ({time.time() - started:.0f} s)", flush=True)

    step("3. against Valve's package")
    if os.path.exists(valve):
        print(f"Valve's: {valve}")
        score(entries(valve), entries(package))
    else:
        print(f"no Valve package at {valve}: pass --valve")

    step("4. in game (by hand, CS2 closed until the copy is in place)")
    maps_dir = os.path.join(CS2, "game", "csgo", "maps")
    print(f"copy {package} -> {os.path.join(maps_dir, a.map + '.vpk')} (back up any file already there)")
    print('launch: cs2.exe -tools -insecure -netconport 29000 -novid')
    print(f"then: python tools/pipeline/map_test.py {a.map}_s2c {a.map} <x,y,z of a spawn> [--cubemaps]")
    print(f"done in {time.time() - started:.0f} s; our package stays in {out}")


if __name__ == "__main__":
    main()
