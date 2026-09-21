"""Compile one map's visibility repeatedly under different VisBuilder settings.

The knobs live in a ResourceCompiler block in gameinfo.gi, so varying them means
editing that file. This edits the SHADOW install's copy (see new-shadow-cs2.ps1),
which is a real file of its own while every byte of game content stays hardlinked
to the real install. The real gameinfo.gi is never touched.

    python sweep_vis_settings.py <addon> <map> --out <dir>

Each variant writes its world_visibility.vvis_c into <dir>, so they can be scored
against each other with `s2c vis-diff`. Same-settings runs are already known to be
byte-identical, so whatever moves here is the settings and nothing else.

resourcecompiler is launched at below-normal priority: it spins up a graphics
device and saturates the CPU, and this is expected to run on a machine somebody
is using.
"""
import argparse
import os
import re
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "map-survey"))
from vpk import Vpk                                       # noqa: E402
sys.path.insert(0, HERE)
from vis_index import vis_digest                          # noqa: E402

CS2 = os.environ.get(
    "CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
SHADOW = os.environ.get("CS2_SHADOW", r"D:\cs2-shadow")

STAGES = re.compile(
    r"(Voxelize \(.*|Generated clusters for .*|Merged cluster lists .*"
    r"|Target \d+ clusters.*|Initial regions .*|Reduced node count .*"
    r"|\d+ unique masks .*|Wrote vis resource .*|Visibility complete .*)")

# Settings worth separating. The baseline writes no block at all, so it is the
# stock compile and the anchor everything else is read against.
VARIANTS = {
    "baseline": {},
    "clusters128": {"MaxVisClusters": "128"},
    "clusters512": {"MaxVisClusters": "512"},
    "voxel16": {"BaseVoxelSize": "16"},
    "voxel32": {"BaseVoxelSize": "32"},
}


def gameinfo_path():
    return os.path.join(SHADOW, "game", "csgo", "gameinfo.gi")


def write_settings(pristine, settings):
    """Put a ResourceCompiler/VisBuilder block into the shadow's gameinfo.gi."""
    if not settings:
        body = pristine
    else:
        lines = "\n".join(f'\t\t\t"{k}"\t"{v}"' for k, v in settings.items())
        block = "\tResourceCompiler\n\t{\n\t\tVisBuilder\n\t\t{\n" + lines + "\n\t\t}\n\t}\n"
        # The file is one top level GameInfo block; insert before its last brace.
        cut = pristine.rstrip().rfind("}")
        body = pristine[:cut] + block + pristine[cut:]
    with open(gameinfo_path(), "w", encoding="utf-8", newline="\n") as f:
        f.write(body)


def compile_vis(addon, map_name, full=False):
    output = os.path.join(CS2, "game", "csgo_addons", addon, "maps", map_name + ".vpk")
    if os.path.exists(output):
        os.remove(output)

    argv = [os.path.join(CS2, "game", "bin", "win64", "resourcecompiler.exe"),
            "-nop4", "-game", os.path.join(SHADOW, "game", "csgo"),
            "-i", os.path.join(SHADOW, "content", "csgo_addons", addon, "maps", map_name + ".vmap"),
            "-fshallow"]
    if not full:
        argv += ["-world", "-vis"]
    started = time.time()
    done = subprocess.run(
        argv, capture_output=True, text=True, errors="replace",
        cwd=os.path.join(CS2, "game", "bin", "win64"),
        creationflags=getattr(subprocess, "BELOW_NORMAL_PRIORITY_CLASS", 0))
    return done.stdout, time.time() - started


def extract_vis(addon, map_name):
    path = os.path.join(CS2, "game", "csgo_addons", addon, "maps", map_name + ".vpk")
    if not os.path.exists(path):
        return None
    archive = Vpk(path)
    try:
        for entry in archive.entries:
            if entry.endswith("world_visibility.vvis_c"):
                return archive.read(entry)
    finally:
        archive.close()
    return None


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("addon")
    ap.add_argument("map")
    ap.add_argument("--out", required=True, help="where to save each variant's vvis_c")
    ap.add_argument("--only", help="comma separated variant names, default all")
    ap.add_argument("--full", action="store_true",
                    help="run every phase, so the VPK is a complete map and not world plus vis only")
    args = ap.parse_args()

    if not os.path.exists(gameinfo_path()):
        sys.exit(f"no shadow install at {SHADOW}; run new-shadow-cs2.ps1 first")
    os.makedirs(args.out, exist_ok=True)

    # Keep the shadow's stock gameinfo so every variant starts from it and the
    # file is restored even if a run fails.
    backup = gameinfo_path() + ".stock"
    if not os.path.exists(backup):
        shutil.copyfile(gameinfo_path(), backup)
    with open(backup, encoding="utf-8") as f:
        pristine = f.read()

    wanted = args.only.split(",") if args.only else list(VARIANTS)
    try:
        for name in wanted:
            settings = VARIANTS[name]
            write_settings(pristine, settings)
            log, wall = compile_vis(args.addon, args.map, args.full)

            stages = [l.strip() for l in log.splitlines() if STAGES.fullmatch(l.strip())]
            print(f"=== {name}  {settings or '(stock)'}  {wall:.1f}s wall ===")
            if not stages:
                print("   NO VIS STAGES RAN - the phase skipped, this variant proves nothing")
            for line in stages:
                print("   " + line)

            vis = extract_vis(args.addon, args.map)
            if vis is None:
                print("   no vvis_c produced")
                continue
            dst = os.path.join(args.out, f"{args.map}.{name}.vvis_c")
            with open(dst, "wb") as f:
                f.write(vis)
            if args.full:
                shutil.copyfile(
                    os.path.join(CS2, "game", "csgo_addons", args.addon, "maps", args.map + ".vpk"),
                    os.path.join(args.out, f"{args.map}.{name}.vpk"))
            print(f"   saved {len(vis):,} bytes  vis digest {(vis_digest(vis) or '?')[:16]} "
                  f"-> {os.path.basename(dst)}")
    finally:
        write_settings(pristine, {})


if __name__ == "__main__":
    main()
