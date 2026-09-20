"""Rebuild one map's visibility with Valve's compiler, and report what came out.

Vis is built by the `-world` phase, NOT by `-vis`: run with both and the vis
stages all print under "Building 'world'" while "Building 'vis'" prints nothing.
`-vis -f` is a hard failure, so the force flag has to be -fshallow.

That makes Valve's builder usable as an ORACLE at iteration speed - ze_hold_em_p
rebuilds its visibility in 8.5 seconds inside a 14 second invocation, against 50
seconds for a full compile - which is what a comparison harness needs.

    python rebuild_vis.py <addon> <map> [--runs 2]

Each run prints the stage timings visbuilder logs and the hash of the
world_visibility.vvis_c that landed in the map VPK, so repeated runs answer
whether the tool is deterministic before anything is compared against it.
"""
import argparse
import hashlib
import os
import re
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "map-survey"))
from vpk import Vpk                                       # noqa: E402

CS2 = os.environ.get(
    "CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")

# The stage lines worth keeping out of several hundred lines of compile noise.
STAGES = re.compile(
    r"(Voxelize \(.*|Generated clusters for .*|Merged cluster lists .*"
    r"|Compacted to .*|Target \d+ clusters.*|Initial regions .*"
    r"|Reduced node count .*|\d+ unique masks .*|Wrote vis resource .*"
    r"|Visibility complete .*|Sample vis for .*|.*useful LOS rays.*)")


def compile_world(addon, map_name, touch=False):
    """Run the world phase, which is the one that builds visibility.

    RC skips the phase when its output is current, and a skipped run prints no
    stage lines and reuses the previous file - which looks exactly like a fast
    deterministic rebuild. Touching the source does NOT force it, because the
    dependency check is on the source CRC rather than its timestamp (the same
    CRC a compiled resource records in RED2). Removing the map VPK does.

    Both phases are passed because `-world` alone rebuilds visibility but does
    not repack the VPK, so the result would be invisible."""
    source = os.path.join(CS2, "content", "csgo_addons", addon, "maps", map_name + ".vmap")
    if touch:
        output = os.path.join(CS2, "game", "csgo_addons", addon, "maps", map_name + ".vpk")
        if os.path.exists(output):
            os.remove(output)
    argv = [os.path.join(CS2, "game", "bin", "win64", "resourcecompiler.exe"),
            "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
            "-i", source,
            "-world", "-vis", "-fshallow"]
    started = time.time()
    done = subprocess.run(argv, capture_output=True, text=True, errors="replace",
                          cwd=os.path.join(CS2, "game", "bin", "win64"))
    return done.stdout, time.time() - started


def extract_vis(addon, map_name):
    """The world_visibility.vvis_c out of the map VPK the compile just wrote."""
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
    ap.add_argument("--runs", type=int, default=1)
    ap.add_argument("--keep", help="directory to save each run's vvis_c into")
    ap.add_argument("--no-force", action="store_true",
                    help="leave the existing map VPK in place (the phase will likely skip)")
    args = ap.parse_args()

    if args.keep:
        os.makedirs(args.keep, exist_ok=True)

    digests = []
    for run in range(1, args.runs + 1):
        log, wall = compile_world(args.addon, args.map, touch=not args.no_force)
        print(f"=== run {run}  ({wall:.1f}s wall) ===")
        stages = [l.strip() for l in log.splitlines() if STAGES.fullmatch(l.strip())]
        for line in stages:
            print("   " + line)
        if not stages:
            print("   NO VIS STAGES RAN - the phase was skipped as up to date, so "
                  "anything measured from this run is the previous run's output")

        vis = extract_vis(args.addon, args.map)
        if vis is None:
            print("   no world_visibility.vvis_c in the map VPK")
            continue
        digest = hashlib.sha256(vis).hexdigest()
        digests.append(digest)
        print(f"   vvis_c {len(vis):,} bytes  sha256 {digest[:16]}")
        if args.keep:
            with open(os.path.join(args.keep, f"{args.map}.run{run}.vvis_c"), "wb") as f:
                f.write(vis)

    if len(digests) > 1:
        same = len(set(digests)) == 1
        print(f"\n{len(digests)} runs: {'IDENTICAL' if same else 'DIFFERENT'} output")
        if not same:
            print("   the builder is not deterministic on this map, so a byte diff "
                  "against it is meaningless and only the sampled metrics count")


if __name__ == "__main__":
    main()
