"""oracle_atan2.py <out.bin> [--count N] [--phonon <phonon.dll>]: phonon 0923's statically linked atan2f (182683840)
called in this process on N argument pairs, written as (y, x, atan2f(y, x)) float triples for CrtMath.Atan2's test.

The pairs: random magnitudes over the float range with random signs, ratios spread over (0, 1] and around the
1e-4, 1/16 and table-step boundaries, exponent gaps around 13, 26 and 126, and the special values (zeros, equal
arguments). Loading phonon.dll runs no Steam Audio code but its DllMain; nothing here touches cs2.exe.
"""
import argparse
import ctypes
import os
import random
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ARCHIVED = os.path.join(HERE, "..", "re", "binaries", "phonon_20260923.dll")
ATAN2F_RVA = 0x2683840


def f32(v):
    return struct.unpack("<f", struct.pack("<f", v))[0]


def pairs(count, rng):
    out = [(1.0, 1.0), (0.0, 1.0), (1.0, 0.0), (-0.0, 1.0), (1.0, -1.0), (-1.0, -1.0), (0.5, 1.0), (1.0, 0.5)]
    for c in range(16, 257):
        for d in (-1e-7, 0.0, 1e-7):
            out.append((f32(c / 256.0 + d), 1.0))
    for u in (1e-4, 0.0625):
        for d in (-1e-6, 0.0, 1e-6):
            out.append((f32(u + d), 1.0))
    for gap in range(-140, 40):
        out.append((f32(1.3 * 2.0 ** gap), 1.7))
    while len(out) < count:
        kind = rng.random()
        if kind < 0.4:
            y, x = rng.uniform(0, 1), rng.uniform(0, 1)
        elif kind < 0.7:
            y, x = rng.uniform(-1, 1) * 2.0 ** rng.randint(-30, 30), rng.uniform(-1, 1) * 2.0 ** rng.randint(-30, 30)
        else:
            u = rng.uniform(0, 1) ** 3
            y, x = u, 1.0
            if rng.random() < 0.5:
                y, x = x, y
        out.append((f32(y), f32(x)))
    return out[:count]


def main():
    p = argparse.ArgumentParser()
    p.add_argument("out")
    p.add_argument("--count", type=int, default=200000)
    p.add_argument("--phonon", default=ARCHIVED)
    a = p.parse_args()
    dll = ctypes.CDLL(os.path.abspath(a.phonon))
    atan2f = ctypes.CFUNCTYPE(ctypes.c_float, ctypes.c_float, ctypes.c_float)(dll._handle + ATAN2F_RVA)
    with open(a.out, "wb") as f:
        for y, x in pairs(a.count, random.Random(1)):
            f.write(struct.pack("<fff", y, x, atan2f(y, x)))
    print("wrote", a.count, "triples to", a.out)


if __name__ == "__main__":
    sys.exit(main())
