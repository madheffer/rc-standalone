"""hullcast_oracle.py <out.bin> [--count N] [--seed S]: vphysics2's hull box cast on random inputs.

Loads the installed vphysics2.dll (20261015; 180251800 is instruction-identical to 20260924's)
with ctypes and calls RnHull_CastBox (180251800: out, hull, scale, transform, start, delta, half)
directly: no CS2, no resourcecompiler. A hull here is only what 180251800 reads: the reference
point (+0), the point count (+0x70) and the points (+0x78).

Each record: u32 point count, reference (12), scale (4), transform (0x30: 3x3 rows then the
translation), start (12), delta (12), half (12), the points (12 each), then Valve's answer: u8
returned, u8 start solid (+0x30), 2 pad, fraction (4), point (12), normal (12).

The cases mix boxes, random clouds and thin slabs; rays short and long (over 512 the cast clips
first), zero and tiny deltas, starts inside, grazing and parallel moves, so every branch of the
GJK cast, its time of impact and its contact depths gets exercised (Simulation/HullCast.cs,
HullCastOracleTests).
"""
import argparse
import ctypes
import math
import os
import random
import struct

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
RVA = 0x251800


def rotation(rng):
    # A random rotation (quaternion), or an axis-aligned one now and then.
    if rng.random() < 0.3:
        return [1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0]
    q = [rng.gauss(0, 1) for _ in range(4)]
    n = math.sqrt(sum(x * x for x in q))
    w, x, y, z = (v / n for v in q)
    return [1 - 2 * (y * y + z * z), 2 * (x * y + w * z), 2 * (x * z - w * y),
            2 * (x * y - w * z), 1 - 2 * (x * x + z * z), 2 * (y * z + w * x),
            2 * (x * z + w * y), 2 * (y * z - w * x), 1 - 2 * (x * x + y * y)]


def f32(v):
    return struct.unpack("<f", struct.pack("<f", v))[0]


def points(rng):
    kind = rng.random()
    if kind < 0.06:
        # A point or a segment.
        return [tuple(rng.uniform(-30, 30) for _ in range(3)) for _ in range(rng.choice([1, 2]))]
    if kind < 0.3:
        # A box.
        e = [rng.uniform(0.5, 64) for _ in range(3)]
        return [(sx * e[0], sy * e[1], sz * e[2]) for sx in (-1, 1) for sy in (-1, 1) for sz in (-1, 1)]
    if kind < 0.45:
        # A thin slab or a needle.
        e = [rng.uniform(0.01, 2), rng.uniform(1, 80), rng.uniform(1, 80)]
        rng.shuffle(e)
        return [(sx * e[0], sy * e[1], sz * e[2]) for sx in (-1, 1) for sy in (-1, 1) for sz in (-1, 1)]
    if kind < 0.55:
        # A tetrahedron or a triangle (flat).
        n = rng.choice([3, 4])
        return [tuple(rng.uniform(-40, 40) for _ in range(3)) for _ in range(n)]
    # A random cloud (its hull is what the supports see).
    n = rng.randint(4, 40)
    r = rng.uniform(2, 60)
    return [tuple(rng.gauss(0, r) for _ in range(3)) for _ in range(n)]


def touch(rng):
    # Exact contacts: integer boxes and points, no rotation, the box flush with the hull (or a set
    # integer gap or overlap), integer moves along an axis or across it.
    shape = rng.random()
    if shape < 0.6:
        e = [rng.randint(1, 32) for _ in range(3)]
        pts = [(sx * e[0], sy * e[1], sz * e[2]) for sx in (-1, 1) for sy in (-1, 1) for sz in (-1, 1)]
    elif shape < 0.8:
        pts = [tuple(float(rng.randint(-16, 16)) for _ in range(3)) for _ in range(rng.choice([1, 2, 3]))]
    else:
        pts = [tuple(float(rng.randint(-24, 24)) for _ in range(3)) for _ in range(rng.randint(4, 12))]
    pts = [tuple(float(c) for c in q) for q in pts]
    t = [1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0] + [float(rng.randint(-1000, 1000)) for _ in range(3)]
    half = tuple(float(rng.choice([0, 1, 2, 4, 8, 16])) for _ in range(3))
    axis = rng.randrange(3)
    side = rng.choice([-1, 1])
    lo = [min(q[i] for q in pts) for i in range(3)]
    hi = [max(q[i] for q in pts) for i in range(3)]
    start = [t[9 + i] + rng.randint(int(lo[i] - half[i]) - 2, int(hi[i] + half[i]) + 2) for i in range(3)]
    face = (hi[axis] + half[axis]) if side > 0 else (lo[axis] - half[axis])
    start[axis] = t[9 + axis] + face + side * rng.choice([0, 0, 0, 1, -1, 0.5])
    delta = [0.0, 0.0, 0.0]
    m = rng.random()
    if m < 0.4:
        delta[axis] = -side * rng.choice([1, 2, 8, 64, 700])
    elif m < 0.6:
        delta[axis] = side * rng.choice([1, 8, 64])
    elif m < 0.85:
        delta[(axis + rng.choice([1, 2])) % 3] = rng.choice([-64, -8, 8, 64, 600])
    elif m < 0.95:
        delta = [float(rng.randint(-40, 40)) for _ in range(3)]
    return pts, (0.0, 0.0, 0.0), 1.0, t, [float(v) for v in start], [float(v) for v in delta], half


def case(rng):
    if rng.random() < 0.35:
        return touch(rng)
    pts = [tuple(f32(c) for c in p) for p in points(rng)]
    ref = tuple(f32(rng.uniform(-5, 5)) for _ in range(3))
    scale = f32(rng.choice([1.0, 1.0, rng.uniform(0.25, 4)]))
    rot = rotation(rng)
    trans = [rng.uniform(-2000, 2000) for _ in range(3)]
    t = [f32(v) for v in rot + trans]
    half = tuple(f32(v) for v in (rng.choice([rng.uniform(0, 1), rng.uniform(1, 32)]) for _ in range(3)))
    if rng.random() < 0.1:
        half = (half[0], half[0], half[2])
    # Aim at a point near the hull's world centre.
    cx = sum(p[0] for p in pts) / len(pts) * scale
    cy = sum(p[1] for p in pts) / len(pts) * scale
    cz = sum(p[2] for p in pts) / len(pts) * scale
    wc = [cx * t[0] + cy * t[3] + cz * t[6] + t[9], cx * t[1] + cy * t[4] + cz * t[7] + t[10], cx * t[2] + cy * t[5] + cz * t[8] + t[11]]
    size = max(max(abs(c) for c in p) for p in pts) * scale + max(half)
    mode = rng.random()
    if mode < 0.15:
        start = [c + rng.uniform(-0.5, 0.5) * size for c in wc]           # likely inside
    else:
        d = [rng.gauss(0, 1) for _ in range(3)]
        n = math.sqrt(sum(x * x for x in d))
        dist = size * rng.uniform(0.8, 3) if mode < 0.85 else rng.uniform(600, 3000)
        start = [wc[i] + d[i] / n * dist for i in range(3)]
    aim = [c + rng.uniform(-1, 1) * size * rng.choice([0.2, 0.6, 1.2]) for c in wc]
    delta = [aim[i] - start[i] for i in range(3)]
    r = rng.random()
    if r < 0.05:
        delta = [0.0, 0.0, 0.0]
    elif r < 0.1:
        delta = [v * 1e-4 for v in delta]
    elif r < 0.2:
        delta = [v * rng.uniform(1.2, 4) for v in delta]
    elif r < 0.3:
        # Axis-aligned moves (nav drops and edge walks).
        k = rng.randrange(3)
        delta = [0.0, 0.0, 0.0]
        delta[k] = rng.uniform(-300, 300)
    start = [f32(v) for v in start]
    delta = [f32(v) for v in delta]
    return pts, ref, scale, t, start, delta, half


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("out")
    p.add_argument("--count", type=int, default=200000)
    p.add_argument("--seed", type=int, default=1)
    a = p.parse_args()
    os.add_dll_directory(BIN)
    dll = ctypes.WinDLL(os.path.join(BIN, "vphysics2.dll"))
    base = ctypes.cast(dll._handle, ctypes.c_void_p).value
    F = ctypes.c_float
    fn = ctypes.CFUNCTYPE(ctypes.c_ubyte, ctypes.c_void_p, ctypes.c_void_p, F, ctypes.c_void_p, ctypes.c_void_p,
                          ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int)(base + RVA)
    rng = random.Random(a.seed)
    hull = ctypes.create_string_buffer(0x100)
    out = ctypes.create_string_buffer(0x40)
    hits = 0
    with open(a.out, "wb") as f:
        for _ in range(a.count):
            pts, ref, scale, t, start, delta, half = case(rng)
            arr = (F * (3 * len(pts)))(*[c for q in pts for c in q])
            ctypes.memset(hull, 0, 0x100)
            struct.pack_into("<3f", hull, 0, *ref)
            struct.pack_into("<i", hull, 0x70, len(pts))
            struct.pack_into("<Q", hull, 0x78, ctypes.addressof(arr))
            tb = (F * 12)(*t)
            sb, db, hb = (F * 3)(*start), (F * 3)(*delta), (F * 3)(*half)
            ctypes.memset(out, 0, 0x40)
            struct.pack_into("<f", out, 0, 1.0)
            r = fn(ctypes.addressof(out), ctypes.addressof(hull), scale, ctypes.addressof(tb), ctypes.addressof(sb),
                   ctypes.addressof(db), ctypes.addressof(hb), 0)
            hits += r != 0
            f.write(struct.pack("<I3ff", len(pts), *ref, scale))
            f.write(struct.pack("<12f", *t))
            f.write(struct.pack("<9f", *start, *delta, *half))
            f.write(bytes(arr))
            f.write(struct.pack("<BBxx", r & 1, out.raw[0x30]))
            f.write(out.raw[0:0x1c])
    print(a.count, "cases,", hits, "hits ->", a.out)


if __name__ == "__main__":
    main()
