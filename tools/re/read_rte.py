"""Read the `.rte` ray trace environment visibility is built from.

Layout in `docs/RTE.md`. Both known specimens tile exactly, which is the check
this performs first: if the sections do not add up to the file size, the layout is
wrong for that file and nothing below it should be believed.

    python read_rte.py <file.rte> [--kd 8] [--indices 8] [--triangles 4]

Find one under %TEMP%\\csgo_addons\\<addon>\\maps\\<map>.rte after a -world compile.
"""
import argparse
import math
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
        """The 48-byte record as 12 floats, undecoded."""
        return struct.unpack_from("<12f", self.data, self.triangle_at + i * 48)

    def triangle_id(self, i):
        """Slot 3. Equals the record's own index in both known specimens."""
        return int(self.triangle(i)[3])

    def coord_select(self, i):
        """(u, v, dominant) axis indices. The record stores the plane projected
        onto the two non-dominant axes, so u and v are always two of 0, 1, 2 and
        the third is implied."""
        word = struct.unpack_from("<I", self.data, self.triangle_at + i * 48 + 40)[0]
        u, v = word & 0xFF, (word >> 8) & 0xFF
        if u > 2 or v > 2 or u == v:
            return None
        return u, v, 3 - u - v

    def flags(self, i):
        """Everything in the packed word above the two axis bytes."""
        word = struct.unpack_from("<I", self.data, self.triangle_at + i * 48 + 40)[0]
        return word >> 16

    def plane(self, i):
        """The triangle's plane as a unit normal and distance, or None when the
        record is degenerate.

        The normal is a FIXED slot mapping, (slot11, slot0, slot1) as x, y and z.
        It does NOT follow coord_select, which only names the two projection axes.
        Reading it in coord_select order happens to work on a map whose geometry
        is mostly axis aligned and silently corrupts one on a map that is not.

        Slot 2 is the distance in the same arbitrary scale as the normal, so
        normalise the two together rather than assuming the scale."""
        select = self.coord_select(i)
        if select is None:
            return None
        t = self.triangle(i)
        normal = [t[11], t[0], t[1]]
        length = math.sqrt(sum(c * c for c in normal))
        if length == 0.0:
            return None
        return tuple(c / length for c in normal), t[2] / length

    def vertices(self, i):
        """The triangle's three world-space vertices, or None if degenerate.

        The record stores no vertices. It stores the plane and two barycentric
        edge equations over the plane's 2D projection, so a vertex is where the
        barycentric pair hits (0,0), (1,0) or (0,1): a 2x2 solve for the two
        projected coordinates, then the third recovered from the plane.

        Verified by rebuilding every triangle and comparing the result's bounding
        box with the one the file states in its own header. ze_hold_em_p matches
        exactly and Mako to within 1.8 units, which a wrong decode cannot do."""
        select = self.coord_select(i)
        if select is None:
            return None
        u, v, w = select
        t = self.triangle(i)
        normal = (t[11], t[0], t[1])
        if normal[w] == 0.0:
            return None

        a1, b1, c1 = t[4], t[5], t[6]
        a2, b2, c2 = t[7], t[8], t[9]
        det = a1 * b2 - a2 * b1
        if det == 0.0:
            return None

        found = []
        for first, second in ((0.0, 0.0), (1.0, 0.0), (0.0, 1.0)):
            p1, p2 = first - c1, second - c2
            along_u = (p1 * b2 - p2 * b1) / det
            along_v = (a1 * p2 - a2 * p1) / det
            along_w = (t[2] - normal[u] * along_u - normal[v] * along_v) / normal[w]
            point = [0.0, 0.0, 0.0]
            point[u], point[v], point[w] = along_u, along_v, along_w
            found.append(tuple(point))
        return found

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
        print("\n  triangle records:")
        for i in range(min(args.triangles, rte.triangles)):
            plane = rte.plane(i)
            where = "degenerate" if plane is None else (
                f"n=({plane[0][0]:+.3f},{plane[0][1]:+.3f},{plane[0][2]:+.3f}) d={plane[1]:>10.2f}")
            print(f"    {i:>4} id={rte.triangle_id(i):<7} axes={rte.coord_select(i)} "
                  f"flags={rte.flags(i):#07x}  {where}")
            print(f"         edge floats {[round(v, 4) for v in rte.triangle(i)[4:10]]}")
        print(f"    reflectivity[0] = {rte.reflectivity(0)}")


if __name__ == "__main__":
    main()
