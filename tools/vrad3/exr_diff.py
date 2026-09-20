"""Compare two lightmap EXRs numerically.

vrad3 writes UNCOMPRESSED scanline half-float EXRs, so reading one needs no
library: a header of typed attributes, a scanline offset table, then per scanline
each channel's row in alphabetical channel order. That is the whole format, and it
is why a replacement tracer can be scored per luxel against Valve's own output
rather than by looking at screenshots.

    python exr_diff.py a.exr b.exr [--threshold 0.01]

Reports, per channel: max and mean absolute difference, RMSE, and how many pixels
differ by more than the threshold. Pixels that are invalid in BOTH images (the
lightmap atlas is mostly empty) are ignored.
"""
import argparse
import struct
import sys

HALF, FLOAT, UINT = 1, 2, 0


def read_exr(path):
    """{channel: [values]} plus (width, height). Uncompressed scanline only."""
    data = open(path, "rb").read()
    if data[:4] != b"\x76\x2f\x31\x01":
        sys.exit(f"{path}: not an EXR")

    at = 8
    attributes = {}

    def cstr(o):
        end = data.index(b"\0", o)
        return data[o:end].decode("utf-8", "replace"), end + 1

    while True:
        name, at = cstr(at)
        if not name:
            break
        _type, at = cstr(at)
        size, = struct.unpack_from("<I", data, at)
        at += 4
        attributes[name] = data[at:at + size]
        at += size

    channels = []
    blob = attributes["channels"]
    i = 0
    while i < len(blob) and blob[i] != 0:
        end = blob.index(b"\0", i)
        name = blob[i:end].decode()
        i = end + 1
        pixel_type, = struct.unpack_from("<I", blob, i)
        i += 16                                   # type, pLinear+pad, xSampling, ySampling
        channels.append((name, pixel_type))
    channels.sort()                               # EXR stores channels alphabetically

    if attributes.get("compression", b"\x00")[0] != 0:
        sys.exit(f"{path}: only uncompressed EXR is supported (vrad3 writes uncompressed)")

    x0, y0, x1, y1 = struct.unpack("<4i", attributes["dataWindow"])
    width, height = x1 - x0 + 1, y1 - y0 + 1

    # Scanline offset table, then one block per scanline: y, size, then rows.
    offsets = struct.unpack_from(f"<{height}Q", data, at)
    out = {name: [0.0] * (width * height) for name, _ in channels}
    for row in range(height):
        o = offsets[row] + 8                      # skip y and packed size
        for name, pixel_type in channels:
            if pixel_type == HALF:
                values = struct.unpack_from(f"<{width}e", data, o)
                o += width * 2
            elif pixel_type == FLOAT:
                values = struct.unpack_from(f"<{width}f", data, o)
                o += width * 4
            else:
                values = struct.unpack_from(f"<{width}I", data, o)
                o += width * 4
            out[name][row * width:(row + 1) * width] = values
    return out, (width, height)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("a")
    ap.add_argument("b")
    ap.add_argument("--threshold", type=float, default=0.01)
    args = ap.parse_args()

    left, size_a = read_exr(args.a)
    right, size_b = read_exr(args.b)
    if size_a != size_b:
        sys.exit(f"different sizes: {size_a} vs {size_b}")
    if left.keys() != right.keys():
        sys.exit(f"different channels: {sorted(left)} vs {sorted(right)}")

    print(f"{size_a[0]}x{size_a[1]}  channels {','.join(sorted(left))}")
    print(f"{'chan':>5s} {'max |d|':>12s} {'mean |d|':>12s} {'rmse':>12s} {'over thr':>12s}")
    for channel in sorted(left):
        x, y = left[channel], right[channel]
        worst = total = square = over = counted = 0
        for i in range(len(x)):
            a, b = x[i], y[i]
            if a == 0.0 and b == 0.0:
                continue                          # empty luxel in both
            d = abs(a - b)
            counted += 1
            total += d
            square += d * d
            over += d > args.threshold
            worst = max(worst, d)
        if counted == 0:
            print(f"{channel:>5s} {'both empty':>12s}")
            continue
        print(f"{channel:>5s} {worst:12.6f} {total / counted:12.6f} "
              f"{(square / counted) ** 0.5:12.6f} {over:8d} ({100 * over / counted:.2f}%)")


if __name__ == "__main__":
    main()
