"""Read the `.rte` ray trace environment visibility is built from.

Layout in `docs/RTE.md`. Both known specimens tile exactly, which is the check
this performs first: if the sections do not add up to the file size, the layout is
wrong for that file and nothing below it should be believed.

    python read_rte.py <file.rte> [--kd 8] [--indices 8] [--triangles 4]

Find one under %TEMP%\\csgo_addons\\<addon>\\maps\\<map>.rte after a -world compile.
"""
import argparse
import struct
import sys


class Rte:
    """A parsed ray trace environment."""

    def __init__(self, data):
        self.data = data
        if len(data) < 64:
            raise ValueError("too short to hold a header")
        head = struct.unpack_from("<9I", data, 0)
        self.version = head[0]
        self.nodes = head[3]          # kd nodes
        self.triangles = head[4]      # also repeated at [6] and [8]
        self.indices = head[5] - 1    # one entry is a root or sentinel
        self.mins = struct.unpack_from("<3f", data, 0x24)
        self.maxs = struct.unpack_from("<3f", data, 0x30)
        self.trailing = struct.unpack_from("<I", data, 0x3C)[0]

        at = 64
        self.node_at, at = at, at + self.nodes * 8
        self.triangle_at, at = at, at + self.triangles * 48
        self.index_at, at = at, at + self.indices * 4
        self.surface_at, at = at, at + self.triangles * 8
        self.reflectivity_at, at = at, at + self.triangles * 12
        self.total = at

    @property
    def tiles(self):
        """Whether the sections account for the file exactly."""
        return self.total == len(self.data)

    def node(self, i):
        """(split position, packed child and axis word)."""
        return struct.unpack_from("<fI", self.data, self.node_at + i * 8)

    def index(self, i):
        return struct.unpack_from("<I", self.data, self.index_at + i * 4)[0]

    def triangle(self, i):
        """The 48-byte record as 12 floats. It is a cache-optimized form, a plane
        plus edge equations, NOT three vertices; the fields are not decoded."""
        return struct.unpack_from("<12f", self.data, self.triangle_at + i * 48)

    def reflectivity(self, i):
        return struct.unpack_from("<3f", self.data, self.reflectivity_at + i * 12)

    def sections(self):
        return [
            ("header", 0, 64),
            ("kd nodes", self.node_at, self.nodes * 8),
            ("triangles", self.triangle_at, self.triangles * 48),
            ("leaf indices", self.index_at, self.indices * 4),
            ("per-triangle", self.surface_at, self.triangles * 8),
            ("reflectivity", self.reflectivity_at, self.triangles * 12),
        ]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("file")
    ap.add_argument("--kd", type=int, default=0, help="dump this many kd nodes")
    ap.add_argument("--indices", type=int, default=0)
    ap.add_argument("--triangles", type=int, default=0)
    args = ap.parse_args()

    rte = Rte(open(args.file, "rb").read())
    print(f"{args.file}")
    print(f"  version {rte.version}, trailing field {rte.trailing}")
    print(f"  bounds  {tuple(round(v, 1) for v in rte.mins)} .. {tuple(round(v, 1) for v in rte.maxs)}")
    print(f"  {rte.nodes:,} kd nodes, {rte.triangles:,} triangles, {rte.indices:,} leaf indices")
    print()
    for name, at, size in rte.sections():
        print(f"  {at:>12,}  {name:<14} {size:>12,}")
    print(f"  {'':>12}  {'TOTAL':<14} {rte.total:>12,}  of {len(rte.data):,} "
          f"{'EXACT' if rte.tiles else 'MISMATCH, layout is wrong for this file'}")
    if not rte.tiles:
        sys.exit(1)

    if args.kd:
        print("\n  kd nodes (split, packed):")
        for i in range(min(args.kd, rte.nodes)):
            split, packed = rte.node(i)
            print(f"    {i:>6} {split:>14.3f}  {packed:>12}  leaf={packed & 1}  payload={packed >> 1}")
    if args.indices:
        print("\n  leaf indices:")
        got = [rte.index(i) for i in range(min(args.indices, rte.indices))]
        print("    " + ", ".join(str(v) for v in got))
        over = sum(1 for i in range(min(rte.indices, 100000)) if rte.index(i) >= rte.triangles)
        print(f"    of the first {min(rte.indices, 100000):,}, {over} are >= the triangle count")
    if args.triangles:
        print("\n  triangle records (12 floats each, fields not decoded):")
        for i in range(min(args.triangles, rte.triangles)):
            print(f"    {i:>4} {[round(v, 3) for v in rte.triangle(i)]}")
        print(f"    reflectivity[0] = {rte.reflectivity(0)}")


if __name__ == "__main__":
    main()
