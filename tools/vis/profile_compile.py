"""Attribute a whole map compile, including the time resourcecompiler never names.

Mako Reactor compiles in 1,313 seconds and the stages it narrates account for
about 608 of them. Roughly half the compile is work the tool says nothing about,
which is larger than visibility's share and so worth finding before anyone
replaces visibility.

The trick is that stdout is the only instrument the tool offers, so use its
TIMING rather than its text: read every line as it arrives and stamp it. A long
gap between two lines IS unnamed work, and the line before the gap says what the
tool had just finished doing. Summing gaps by preceding line turns a narration
into a profile.

    python profile_compile.py <addon> <map> [--full] [--top 25]

Without --full only the world and vis phases run, which is far quicker and covers
visibility. --full is the real 22 minute compile.
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
SHADOW = os.environ.get("CS2_SHADOW", r"D:\cs2-shadow")

# Progress bars and per-resource chatter are noise as labels: collapse them so a
# gap is attributed to the activity rather than to whichever file scrolled past.
NOISE = re.compile(
    r"^\s*$|^\[?\d+[.,]?\d*%?\]?$|^\.+$|^\s*\[[0-9.]*\]\s*$"
    r"|^Failed loading resource|^\s*\+-\s|^Parameter '")
DIGITS = re.compile(r"\d[\d,._]*")


def label(line):
    """A line reduced to the shape of its message, so repeats group together."""
    text = line.strip()
    text = re.sub(r"\[[0-9.]+\]", "", text)
    text = DIGITS.sub("N", text)
    return text[:96] if text else "(blank)"


def run(addon, map_name, full):
    source = os.path.join(SHADOW, "content", "csgo_addons", addon, "maps", map_name + ".vmap")
    if not os.path.exists(source):
        source = os.path.join(CS2, "content", "csgo_addons", addon, "maps", map_name + ".vmap")
    game = os.path.join(SHADOW, "game", "csgo")
    if not os.path.exists(os.path.join(game, "gameinfo.gi")):
        game = os.path.join(CS2, "game", "csgo")

    output = os.path.join(CS2, "game", "csgo_addons", addon, "maps", map_name + ".vpk")
    if os.path.exists(output):
        os.remove(output)

    argv = [os.path.join(CS2, "game", "bin", "win64", "resourcecompiler.exe"),
            "-nop4", "-game", game, "-i", source, "-fshallow"]
    if not full:
        argv += ["-world", "-vis"]

    started = time.time()
    # Read as it arrives: waiting for exit first would both lose the timing and
    # deadlock once the pipe buffer fills.
    process = subprocess.Popen(
        argv, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
        errors="replace", bufsize=1, cwd=os.path.join(CS2, "game", "bin", "win64"),
        creationflags=getattr(subprocess, "BELOW_NORMAL_PRIORITY_CLASS", 0))

    stamped = []
    for line in process.stdout:
        stamped.append((time.time() - started, line.rstrip("\n")))
    process.wait()
    return stamped, time.time() - started


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("addon")
    ap.add_argument("map")
    ap.add_argument("--full", action="store_true", help="every phase, not just world and vis")
    ap.add_argument("--top", type=int, default=25)
    ap.add_argument("--save", help="write the timestamped log here")
    args = ap.parse_args()

    stamped, wall = run(args.addon, args.map, args.full)
    if args.save:
        with open(args.save, "w", encoding="utf-8", newline="\n") as f:
            for at, line in stamped:
                f.write(f"{at:10.3f}  {line}\n")

    # Each line's cost is the time since the previous line: that is how long the
    # tool spent doing whatever it had just announced.
    by_label = collections.Counter()
    counts = collections.Counter()
    previous = 0.0
    gaps = []
    for at, line in stamped:
        delta = at - previous
        if not NOISE.match(line):
            key = label(line)
            by_label[key] += delta
            counts[key] += 1
            gaps.append((delta, at, line.strip()[:110]))
        previous = at

    accounted = sum(by_label.values())
    print(f"{args.map}: {wall:.1f}s wall, {len(stamped):,} output lines "
          f"({'full compile' if args.full else 'world and vis only'})\n")

    print(f"{'seconds':>9s} {'share':>7s} {'times':>6s}  what had just finished")
    print("-" * 100)
    for key, seconds in by_label.most_common(args.top):
        print(f"{seconds:9.2f} {seconds / wall * 100:6.1f}% {counts[key]:6d}  {key}")
    print("-" * 100)
    print(f"{accounted:9.2f} {accounted / wall * 100:6.1f}%         attributed to a preceding line")
    print(f"{wall - accounted:9.2f} {(wall - accounted) / wall * 100:6.1f}%         "
          "before the first line and after the last")

    print(f"\nlongest single silences (one gap, not a total):")
    for delta, at, line in sorted(gaps, reverse=True)[:12]:
        print(f"  {delta:8.2f}s ending at {at:8.1f}s  after: {line}")


if __name__ == "__main__":
    main()
