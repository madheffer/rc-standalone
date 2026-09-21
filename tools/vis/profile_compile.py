"""Attribute a whole map compile, including the time resourcecompiler never names.

Mako Reactor compiles in 1,313 seconds and the stages it narrates account for
about 608 of them. Roughly half the compile is work the tool says nothing about,
which is larger than visibility's share and so worth finding before anyone
replaces visibility.

Two readings are printed, and the FIRST is the one to trust.

resourcecompiler times many of its own stages, and those numbers survive
anything. Take them first.

Arrival timing is the fallback for stages it does not time: every line is
stamped as it arrives, so a gap is unnamed work and the preceding line labels it.
It is only valid when the tool flushes per line, and piped to another process it
does NOT: on Mako 5,733 lines arrive in a handful of bursts, which made an
800 second "gap" that was really the whole world build replayed at once. Read the
arrival table as a hint about ordering, never as a measurement, unless the
timestamps are spread out.

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


# Anything the tool timed itself. These are immune to output buffering, which is
# why they are reported first and the arrival gaps second.
SELFTIMED = re.compile(
    r"(.*?)\s*(?:took|complete in|Done|in)\s*\(?([\d.]+)\s*(?:seconds|s)\)?\.?\s*$")


def self_reported(stamped):
    """(label, seconds) for every stage resourcecompiler timed itself."""
    found = []
    for _, line in stamped:
        text = line.strip()
        if not text or text.startswith("Failed loading"):
            continue
        hit = SELFTIMED.match(text)
        if not hit:
            continue
        what = re.sub(r"\[[0-9.]*\]", "", hit.group(1)).strip(" .	")
        try:
            seconds = float(hit.group(2))
        except ValueError:
            continue
        if what and seconds >= 0.5:
            found.append((what[:92], seconds))
    return found


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
    ap.add_argument("addon", nargs="?")
    ap.add_argument("map", nargs="?")
    ap.add_argument("--full", action="store_true", help="every phase, not just world and vis")
    ap.add_argument("--top", type=int, default=25)
    ap.add_argument("--save", help="write the timestamped log here")
    ap.add_argument("--analyze", help="re-read a saved log instead of compiling")
    args = ap.parse_args()

    if args.analyze:
        stamped = []
        with open(args.analyze, encoding="utf-8", errors="replace") as f:
            for row in f:
                # The saved stamp is right aligned, so lead with a strip or the
                # split lands on its padding and every line is discarded.
                at, _, text = row.strip().partition("  ")
                try:
                    stamped.append((float(at), text.rstrip()))
                except ValueError:
                    continue
        wall = stamped[-1][0] if stamped else 0.0
    else:
        stamped, wall = run(args.addon, args.map, args.full)
    if args.save:
        with open(args.save, "w", encoding="utf-8", newline="\n") as f:
            for at, line in stamped:
                f.write(f"{at:10.3f}  {line}\n")

    # Time between two lines is work done AFTER the first was printed, so it
    # belongs to the last MEANINGFUL line: a gap that follows a progress dot or a
    # "Failed loading resource" still belongs to whatever stage announced itself
    # before them. Attributing only to non-noise lines and dropping the rest put
    # 63% of Mako into a bogus "outside any line" bucket on the first run.
    by_label = collections.Counter()
    counts = collections.Counter()
    gaps = []
    current = "(startup, before any output)"
    previous = 0.0
    for at, line in stamped:
        by_label[current] += at - previous
        gaps.append((at - previous, at, current))
        previous = at
        if not NOISE.match(line):
            current = label(line)
            counts[current] += 1
    by_label[current] += wall - previous
    gaps.append((wall - previous, wall, current))

    accounted = sum(by_label.values())
    print(f"{args.map or args.analyze}: {wall:.1f}s wall, {len(stamped):,} output lines "
          f"({'full compile' if args.full else 'world and vis only'})\n")

    timed = self_reported(stamped)
    if timed:
        print("what resourcecompiler timed ITSELF (trust these):")
        for what, seconds in sorted(timed, key=lambda row: -row[1])[:args.top]:
            print(f"{seconds:9.2f} {seconds / wall * 100:6.1f}%  {what}")
        print()

    # A gap that ends in a burst of lines sharing one timestamp is the tool
    # flushing a block buffer, not a stage that took that long. Mako's biggest
    # "gap" is 798 seconds ending in 600+ lines at one instant: that is the whole
    # world build and half of visibility arriving at once, and reading it as one
    # stage is exactly the mistake this flag exists to stop.
    arrivals = collections.Counter(at for at, _ in stamped)
    def buffered(at):
        return arrivals[at] > 20

    print(f"{'seconds':>9s} {'share':>7s} {'times':>6s}  what had just finished")
    print("-" * 100)
    for key, seconds in by_label.most_common(args.top):
        print(f"{seconds:9.2f} {seconds / wall * 100:6.1f}% {counts[key]:6d}  {key}")
    print("-" * 100)
    print(f"{accounted:9.2f} {accounted / wall * 100:6.1f}%         of {wall:.1f}s wall attributed")

    print(f"\nlongest single silences (one gap, not a total):")
    for delta, at, line in sorted(gaps, reverse=True)[:12]:
        flag = "   <- BUFFERED FLUSH, not one stage" if buffered(at) else ""
        print(f"  {delta:8.2f}s ending at {at:8.1f}s  after: {line[:80]}{flag}")


if __name__ == "__main__":
    main()
