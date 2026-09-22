"""Compile maps with -world -vis and record every number the builder prints.

Three maps is not a sample. The visibility stages each have a count in the log,
and a rule that fits one map is not a rule, so the only way to know whether a
stage is right is to score it over a corpus wide enough to disagree with itself:
tiny probes, real zombie escape maps, prefab documents, and something enormous.

    python build_corpus.py --list
    python build_corpus.py ze_hold_em_p atixref
    python build_corpus.py --all

Writes tools/vis/corpus.tsv, one row a map, which the tests read instead of
carrying hardcoded numbers. A map that fails to compile is recorded as a failure
rather than dropped, because a silently missing map inflates a coverage claim.
"""
import argparse
import os
import re
import subprocess
import sys
import time

CS2 = r"D:\Steam\steamapps\common\Counter-Strike Global Offensive"
HERE = os.path.dirname(os.path.abspath(__file__))
CORPUS = os.path.join(HERE, "corpus.tsv")

# Chosen for spread rather than convenience: two synthetic probes, a real ZE map,
# a medium reference map, four gameplay prefabs that are almost all entities and
# almost no geometry, four environment prefabs that are the opposite, and Mako,
# which is 23 MB of ray trace scene and 1,088 seconds of visibility on its own.
#
# Three earlier picks are gone and the reason is worth keeping: ze_doom_p2,
# ze_doom_p2_d and c2m3_coaster_d are Source 1 KV text, not DMX, and the compiler
# rejects them with "Unable to determine DMX encoding". A map that cannot compile
# is not a hard map, it is not a map, and leaving it in the list would have made
# the corpus look 20% larger than it is.
MAPS = [
    ("s2c_rc_probe", "probe01"),
    ("s2c_rc_probe", "cardtest"),
    ("s2c_lighting", "ze_hold_em_p"),
    ("c2m2", "c2m2_fairgrounds_csgo_gameplay"),
    ("dkr_remix", "dkr_m2_carnival_gameplay"),
    ("dkr_remix", "dkr_ropelights_prefab"),
    ("c2m2", "dkr_m1_motel_d_gameplay"),
    ("ze_doom_p2", "ze_doom_p2_gameplay"),
    ("s2probe", "atixref"),
    ("c2m2", "c2m3_coaster_d_d_environment_prefab"),
    ("c2m2", "dkr_m1_motel_d_environment_prefab"),
    ("c2m2", "c2m2_fairgrounds_csgo_environment_prefab"),
    ("ze_doom_p2", "ze_doom_p2_environment_prefab"),
    ("dkr_remix", "dkr_m1_motel_environment_prefab"),
    ("s2c_big", "ze_ffvii_mako_reactor_v6_p"),
]

# Every count the builder prints, and the column it becomes.
PATTERNS = [
    ("triangles", r"Convert RTE with (\d+) triangles"),
    ("nodes", r"Voxelize \([\d.]+ units\) took [\d.]+ seconds \(([\d,]+) nodes\)"),
    ("regions", r"Generated clusters for (\d+) regions"),
    ("clusters", r"(\d+) clusters generated"),
    ("premerged", r"pre-merged to (\d+) clusters"),
    ("merged1", r"Merged to (\d+) clusters in first pass"),
    ("merged2", r"Merged to (\d+) clusters in second pass"),
    ("compacted_regions", r"Compacted to (\d+) regions"),
    ("compacted_clusters", r"Compacted to \d+ regions \((\d+) clusters\)"),
    ("target_clusters", r"\[target (\d+) clusters\]"),
    ("final_nodes", r"Reduced node count from \d+ to (\d+)"),
    ("final_regions", r"regions from \d+ to (\d+)"),
    ("unique_masks", r"(\d+) unique masks"),
    ("seconds", r"Visibility complete in ([\d.]+)s"),
]

COLUMNS = ["addon", "map", "status"] + [name for name, _ in PATTERNS]


def source_of(addon, name):
    """The .vmap, wherever it sits under the addon's maps folder."""
    root = os.path.join(CS2, "content", "csgo_addons", addon, "maps")
    direct = os.path.join(root, name + ".vmap")
    if os.path.exists(direct):
        return direct
    for here, _, files in os.walk(root):
        if name + ".vmap" in files:
            return os.path.join(here, name + ".vmap")
    return None


def compile_vis(addon, name):
    """Run the vis oracle and return its stdout, or None when it cannot run."""
    source = source_of(addon, name)
    if source is None:
        return None
    built = os.path.join(CS2, "game", "csgo_addons", addon, "maps", name + ".vpk")
    if os.path.exists(built):
        os.remove(built)
    run = subprocess.run(
        [os.path.join(CS2, "game", "bin", "win64", "resourcecompiler.exe"),
         "-nop4", "-game", os.path.join(CS2, "game", "csgo"), "-i", source,
         "-world", "-vis", "-fshallow"],
        capture_output=True, text=True, errors="replace", timeout=7200)
    return run.stdout + run.stderr


def parse(log):
    found = {}
    for name, pattern in PATTERNS:
        hit = re.search(pattern, log)
        if hit:
            found[name] = hit.group(1).replace(",", "")
    return found


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("maps", nargs="*", help="map names to build; default is whatever is missing")
    ap.add_argument("--all", action="store_true", help="rebuild every map, including ones already recorded")
    ap.add_argument("--list", action="store_true")
    args = ap.parse_args()

    if args.list:
        for addon, name in MAPS:
            source = source_of(addon, name)
            print(f"  {addon:<16} {name:<44} {'' if source else 'NO SOURCE'}")
        return

    have = {}
    if os.path.exists(CORPUS) and not args.all:
        for line in open(CORPUS, encoding="utf-8").read().splitlines()[1:]:
            cells = line.split("\t")
            if len(cells) > 2:
                have[(cells[0], cells[1])] = line

    wanted = [(a, n) for a, n in MAPS
              if (not args.maps or n in args.maps) and (args.all or (a, n) not in have)]
    print(f"{len(wanted)} map(s) to build, {len(have)} already recorded")

    for addon, name in wanted:
        started = time.time()
        print(f"  {name} ...", end="", flush=True)
        try:
            log = compile_vis(addon, name)
        except subprocess.TimeoutExpired:
            log = None
            print(" TIMEOUT", flush=True)
        if log is None:
            have[(addon, name)] = "\t".join([addon, name, "no source or timeout"] + [""] * len(PATTERNS))
            continue
        found = parse(log)
        status = "ok" if "nodes" in found else "no vis output"
        have[(addon, name)] = "\t".join([addon, name, status] + [found.get(k, "") for k, _ in PATTERNS])
        print(f" {status}, {time.time() - started:.0f}s, {found.get('nodes', '-')} nodes", flush=True)
        write(have)

    write(have)
    print(f"wrote {CORPUS} with {len(have)} map(s)")


def write(have):
    """Rewrite the whole file after every map.

    A big map can hold the run for an hour, and a corpus that only appears when
    the last one finishes is one nobody can use while it is building. Rewriting
    the whole file is free next to a compile.
    """
    with open(CORPUS, "w", encoding="utf-8") as out:
        out.write("\t".join(COLUMNS) + "\n")
        for key in sorted(have):
            out.write(have[key] + "\n")


if __name__ == "__main__":
    main()
