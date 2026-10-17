"""Summarise CpuBakeTests.Bake block logs: one row per block and EXR channel.

    python bake_summary.py <log>...

Each row: block, file, channel, luxels, our mean / Valve's, per-luxel correlation,
and the 16 x 16 bin correlation with the bin ratio's 10th / 50th / 90th percentiles.
"""
import re
import sys

LINE = re.compile(r"^\s+([ABGR]): (\d+) luxels, mean ours ([-\d.]+) valve ([-\d.]+) ratio ([-\d.]+), mean \|diff\| ([-\d.]+), correlation ([-\dA-Za-z.]+)")
BINS = re.compile(r"^\s+bins (\d+): correlation ([-\dA-Za-z.]+), ratio p10 (\S+) p50 (\S+) p90 (\S+)")
FILE = re.compile(r"^\s*(\S+\.exr) block at (\d+),(\d+):")
BLOCK = re.compile(r"block (\d+): (\d+) large-chart and (\d+) clipped")
COMPUTE = re.compile(r"compute ([\d.]+) s")


def main(paths):
    print("block  file                       ch  luxels    ratio   |diff|  corr   bins  p10    p50    p90    (large/small tris, s)")
    for path in paths:
        block = info = ""
        current = None
        row = None
        for line in open(path, encoding="utf-8", errors="replace"):
            if m := BLOCK.search(line):
                block, info = m.group(1), f"{m.group(2)}/{m.group(3)}"
            elif m := COMPUTE.search(line):
                info += f", {m.group(1)}"
            elif m := FILE.match(line):
                current = m.group(1)
            elif m := LINE.match(line):
                row = m.groups()
            elif (m := BINS.match(line)) and row:
                ch, n, _, _, ratio, diff, corr = row
                print(f"{block:>5}  {current:<26} {ch}  {n:>8}  {ratio:>6}  {diff:>6}  {corr:>5}  {m.group(2):>5}  {m.group(3):>5}  {m.group(4):>5}  {m.group(5):>5}  ({info})")
                row = None


if __name__ == "__main__":
    main(sys.argv[1:])
