"""Compare two lightmap EXRs numerically.

vrad3 writes UNCOMPRESSED scanline half-float EXRs, so reading one needs no
library: a header of typed attributes, a scanline offset table, then per scanline
each channel's row in alphabetical channel order. That is the whole format, and it
is why a replacement tracer can be scored per luxel against Valve's own output
rather than by looking at screenshots.

    python exr_diff.py a.exr b.exr [--threshold 0.01]

Reports per channel: max and mean absolute difference, RMSE, and how many luxels
differ by more than the threshold. Luxels that are zero in BOTH images are skipped,
because a lightmap atlas is mostly empty.

Scanlines are compared one row at a time. A 4096x4096 lightmap is 16.7M luxels per
channel, and holding two of those as Python floats would cost gigabytes.
"""
import argparse
import struct
import sys

HALF, FLOAT = 1, 2


class Exr:
    """Just enough uncompressed-scanline EXR to read vrad3's output row by row."""

    def __init__(self, path):
        self.path = path
        self.file = open(path, "rb")
        head = self.file.read(1 << 16)
        if head[:4] != b"\x76\x2f\x31\x01":
            sys.exit(f"{path}: not an EXR")

        at, attributes = 8, {}

        def cstr(o):
            end = head.index(b"\0", o)
            return head[o:end].decode("utf-8", "replace"), end + 1

        while True:
            name, at = cstr(at)
            if not name:
                break
            _type, at = cstr(at)
            size, = struct.unpack_from("<I", head, at)
            at += 4
            attributes[name] = head[at:at + size]
            at += size

        if attributes.get("compression", b"\x00")[0] != 0:
            sys.exit(f"{path}: only uncompressed EXR is supported (vrad3 writes uncompressed)")

        self.channels = []
        blob, i = attributes["channels"], 0
        while i < len(blob) and blob[i] != 0:
            end = blob.index(b"\0", i)
            name = blob[i:end].decode()
            i = end + 1
            pixel_type, = struct.unpack_from("<I", blob, i)
            i += 16                                # type, pLinear + pad, xSampling, ySampling
            self.channels.append((name, pixel_type))
        self.channels.sort()                       # EXR stores channels alphabetically

        x0, y0, x1, y1 = struct.unpack("<4i", attributes["dataWindow"])
        self.width, self.height = x1 - x0 + 1, y1 - y0 + 1
        self.offsets = struct.unpack_from(f"<{self.height}Q", head, at)

    def row(self, y):
        """{channel: tuple of values} for one scanline."""
        self.file.seek(self.offsets[y] + 8)        # skip y and packed size
        out = {}
        for name, pixel_type in self.channels:
            if pixel_type == HALF:
                out[name] = struct.unpack(f"<{self.width}e", self.file.read(self.width * 2))
            elif pixel_type == FLOAT:
                out[name] = struct.unpack(f"<{self.width}f", self.file.read(self.width * 4))
            else:
                out[name] = struct.unpack(f"<{self.width}I", self.file.read(self.width * 4))
        return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("a")
    ap.add_argument("b")
    ap.add_argument("--threshold", type=float, default=0.01)
    args = ap.parse_args()

    left, right = Exr(args.a), Exr(args.b)
    if (left.width, left.height) != (right.width, right.height):
        sys.exit(f"different sizes: {left.width}x{left.height} vs {right.width}x{right.height}")
    names = [c for c, _ in left.channels]
    if names != [c for c, _ in right.channels]:
        sys.exit(f"different channels: {names} vs {[c for c, _ in right.channels]}")

    worst = dict.fromkeys(names, 0.0)
    total = dict.fromkeys(names, 0.0)
    square = dict.fromkeys(names, 0.0)
    over = dict.fromkeys(names, 0)
    counted = dict.fromkeys(names, 0)

    for y in range(left.height):
        a_row, b_row = left.row(y), right.row(y)
        for name in names:
            a_values, b_values = a_row[name], b_row[name]
            for i in range(left.width):
                a, b = a_values[i], b_values[i]
                if a == 0.0 and b == 0.0:
                    continue
                d = a - b if a > b else b - a
                counted[name] += 1
                total[name] += d
                square[name] += d * d
                if d > args.threshold:
                    over[name] += 1
                if d > worst[name]:
                    worst[name] = d

    print(f"{left.width}x{left.height}  channels {','.join(names)}")
    print(f"{'chan':>5s} {'max |d|':>12s} {'mean |d|':>12s} {'rmse':>12s} {'over thr':>18s}")
    for name in names:
        n = counted[name]
        if n == 0:
            print(f"{name:>5s} {'both empty':>12s}")
            continue
        print(f"{name:>5s} {worst[name]:12.6f} {total[name] / n:12.6f} "
              f"{(square[name] / n) ** 0.5:12.6f} {over[name]:10d} ({100 * over[name] / n:5.2f}%)")


if __name__ == "__main__":
    main()
