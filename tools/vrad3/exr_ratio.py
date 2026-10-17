"""exr_ratio.py <image a> <image b> [channel ...]: how much brighter b is than a, per channel.

a and b are lightmap_image_save folders (channelNN.exr) or two EXR files. For
each EXR channel it prints sum(b) / sum(a) over the luxels where both are
positive, and the median per-luxel b / a, so an overall scale (a light, a
bounce) shows apart from noise. numpy reads the uncompressed half/float
scanlines in one go (a 4096 x 4096 channel is 64 MB as float32).
"""
import os
import struct
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from exr_diff import Exr  # noqa: E402

HALF = 1


def planes(path):
    e = Exr(path)
    e.file.seek(0)
    data = e.file.read()
    widths = {1: 2, 2: 4, 0: 4}
    row_bytes = sum(widths[t] * e.width for _, t in e.channels)
    out = {name: np.empty((e.height, e.width), np.float32) for name, _ in e.channels}
    first = e.offsets[0]
    contiguous = all(e.offsets[y] == first + y * (row_bytes + 8) for y in (0, e.height - 1))
    if not contiguous:
        raise SystemExit(f"{path}: scanlines not contiguous")
    block = np.frombuffer(data, np.uint8, e.height * (row_bytes + 8), first).reshape(e.height, row_bytes + 8)[:, 8:]
    at = 0
    for name, t in e.channels:
        n = widths[t] * e.width
        raw = np.ascontiguousarray(block[:, at:at + n])
        out[name] = raw.view(np.float16 if t == HALF else np.float32).astype(np.float32).reshape(e.height, e.width)
        at += n
    return out


def pairs(a, b):
    if os.path.isdir(a):
        for f in sorted(os.listdir(a)):
            if f.endswith(".exr") and os.path.exists(os.path.join(b, f)):
                yield f, os.path.join(a, f), os.path.join(b, f)
    else:
        yield os.path.basename(a), a, b


def main():
    a, b, only = sys.argv[1], sys.argv[2], set(sys.argv[3:])
    for label, pa, pb in pairs(a, b):
        if only and label not in only:
            continue
        x, y = planes(pa), planes(pb)
        for name in x:
            u, v = x[name], y[name]
            m = (u > 0) & (v > 0) & np.isfinite(u) & np.isfinite(v)
            n = int(m.sum())
            if n == 0:
                print(f"{label} {name}: no luxels both positive")
                continue
            total = float(v[m].sum(dtype=np.float64) / u[m].sum(dtype=np.float64))
            median = float(np.median(v[m] / u[m]))
            print(f"{label} {name}: n {n}  sum ratio {total:.4f}  median ratio {median:.4f}", flush=True)


if __name__ == "__main__":
    main()
