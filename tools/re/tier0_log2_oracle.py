"""tier0_log2_oracle.py [--out file] [--count n]: tier0's V_log2f on a sweep of floats.

Loads the installed tier0.dll with ctypes (no CS2 or RC needed) and writes (x, V_log2f(x)) as little-endian
float32 pairs: every float in [0.5, 2) whose low 8 mantissa bits are zero (both sides of the sqrt(1/2)
split and the m near 1 cancellation), the powers of two from 2^-126 to 2^127 and their neighbours, and
`count` random positive normal floats and count / 10 denormals (seed 20261021), and the special
arguments (zeros, infinities, NaN, negatives). Default output: %TEMP%/tier0_log2.bin;
Tier0MathTests.Log2IsTier0s reads it.
"""
import argparse
import ctypes
import os
import random
import struct

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--out", default=os.path.join(os.environ.get("TEMP", "."), "tier0_log2.bin"))
    p.add_argument("--count", type=int, default=1_000_000)
    a = p.parse_args()
    os.add_dll_directory(BIN)
    log2 = ctypes.CDLL(os.path.join(BIN, "tier0.dll")).V_log2f
    log2.restype = ctypes.c_float
    log2.argtypes = [ctypes.c_float]
    bits = []
    for b in range(0x3F000000, 0x40000000, 0x100):
        bits.append(b)
    for e in range(1, 255):
        for d in (-1, 0, 1):
            b = (e << 23) + d
            if 0x00800000 <= b < 0x7F800000:
                bits.append(b)
    # The special paths: zeros, denormals, infinities, NaN and negatives.
    bits += [0x00000000, 0x80000000, 0x00000001, 0x00000002, 0x00000003, 0x0000ffff, 0x00400000, 0x007fffff,
             0x7F800000, 0xFF800000, 0x7FC00000, 0xBF800000, 0x80000001]
    rng = random.Random(20261021)
    bits += [rng.randrange(0x00000001, 0x00800000) for _ in range(a.count // 10)]
    bits += [rng.randrange(0x00800000, 0x7F800000) for _ in range(a.count)]
    out = bytearray()
    for b in bits:
        x = struct.unpack("<f", struct.pack("<I", b))[0]
        out += struct.pack("<If", b, log2(x))
    with open(a.out, "wb") as f:
        f.write(out)
    print(a.out, len(bits), "records")


if __name__ == "__main__":
    main()
