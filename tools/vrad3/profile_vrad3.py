"""Run vrad3 on an already-built lighting job and report where the time goes.

vrad3 narrates every stage with its own timing, so the log IS the profile - this
just runs it and adds up. Stage names that carry an index or a size are collapsed
("Compute block 7/16" and "Compute 32x32x6 LPV_976417354" are one row each), so a
map with 39 light probe volumes does not print 39 lines.

    python profile_vrad3.py <addon>                     # run vrad3, then report
    python profile_vrad3.py --log <file>                # report an existing log

The job directory is <CS2>/game/csgo_addons/<addon>/_vrad3, which resourcecompiler
leaves behind after a map build.
"""
import argparse
import collections
import os
import re
import subprocess
import sys
import time

CS2 = os.environ.get(
    "CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")

DONE = re.compile(r"^(?P<stage>.*?)\.\.\.\s*Done \((?P<secs>[\d.]+) seconds\)")
DONE_IN = re.compile(r"^(?P<stage>.*?)\s*\[?[.\d\s]*\]?\s*Done in (?P<secs>[\d.]+) seconds")
TOOK = re.compile(r"^(?P<stage>.+?) took (?P<secs>[\d.]+) seconds")

# Collapse the parts of a stage name that are an index or a size.
NORMALISE = [
    (re.compile(r"\b\d+/\d+\b"), "N/N"),
    (re.compile(r"\bLPV_\d+\b"), "LPV"),
    (re.compile(r"\b\d+x\d+x\d+\b"), "AxBxC"),
    (re.compile(r"\b\d+x\d+\b"), "AxB"),
    (re.compile(r"\s+"), " "),
]


def normalise(stage):
    stage = stage.strip()
    for pattern, replacement in NORMALISE:
        stage = pattern.sub(replacement, stage)
    return stage.strip(" .[")


def parse(lines):
    """[(stage, calls, seconds)] plus the summary lines vrad3 prints itself."""
    stages = collections.OrderedDict()
    summary = []
    for line in lines:
        line = line.rstrip("\n")
        for pattern in (DONE, DONE_IN):
            m = pattern.match(line)
            if m and m.group("stage").strip():
                stage = normalise(m.group("stage"))
                calls, secs = stages.get(stage, (0, 0.0))
                stages[stage] = (calls + 1, secs + float(m.group("secs")))
                break
        else:
            m = TOOK.match(line)
            if m:
                summary.append((m.group("stage").strip(), float(m.group("secs"))))
    return [(s, c, t) for s, (c, t) in stages.items()], summary


def run(addon):
    job = os.path.join(CS2, "game", "csgo_addons", addon, "_vrad3")
    script = os.path.join(job, "script-gpu.vrad3")
    if not os.path.exists(script):
        sys.exit(f"no lighting job at {job} - compile the map first")

    # The script's first line is the command resourcecompiler used, map included.
    with open(script, encoding="utf-8", errors="replace") as f:
        head = [next(f) for _ in range(4)]
    map_arg = next((m.group(1) for line in head for m in [re.search(r"-map (\S+)", line)] if m), None)
    if map_arg is None:
        map_arg = "maps/" + addon + ".vmap"

    argv = [os.path.join(CS2, "game", "bin", "win64", "vrad3.exe"),
            "-map", map_arg, "-script", "script-gpu.vrad3",
            "-vulkan", "-gpuraytracing", "-allthreads", "-unbufferedio"]
    started = time.time()
    done = subprocess.run(argv, cwd=job, capture_output=True, text=True, errors="replace")
    return done.stdout.splitlines(), time.time() - started


def report(lines, wall):
    stages, summary = parse(lines)
    stages.sort(key=lambda row: -row[2])
    measured = sum(t for _, _, t in stages)

    print(f"{'stage':54s} {'calls':>6s} {'seconds':>9s} {'share':>7s}")
    print("-" * 80)
    for stage, calls, secs in stages:
        if secs < 0.005:
            continue
        print(f"{stage[:54]:54s} {calls:6d} {secs:9.2f} {100 * secs / measured:6.1f}%")
    print("-" * 80)
    print(f"{'measured stages':54s} {'':6s} {measured:9.2f}")
    if wall:
        print(f"{'wall clock (includes startup and io)':54s} {'':6s} {wall:9.2f}")
    for stage, secs in summary:
        print(f"  vrad3 says: {stage} took {secs:.2f} s")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("addon", nargs="?", help="addon whose _vrad3 job to run")
    ap.add_argument("--log", help="parse this log instead of running vrad3")
    args = ap.parse_args()

    if args.log:
        with open(args.log, encoding="utf-8", errors="replace") as f:
            report(f.readlines(), wall=None)
    elif args.addon:
        lines, wall = run(args.addon)
        report(lines, wall)
    else:
        ap.error("give an addon to run, or --log to parse")


if __name__ == "__main__":
    main()
