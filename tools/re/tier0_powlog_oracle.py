"""tier0_powlog_oracle.py [--out file]: tier0's V_powf and V_logf on the inputs colour temperature reaches.

Loads the installed tier0.dll with ctypes (no CS2 or RC needed) and writes, as little-endian float32
records (op, a, b, result; op 0 powf, 1 logf), V_logf(T / 100) and V_logf(T / 100 - 10) for every
whole kelvin T from 1000 to 40000, V_powf(T / 100 - 60, -0.1332047592) and
V_powf(T / 100 - 60, -0.0755148492) above 6600 K (the exponents' bits 0xbe0866d4 and 0xbd9aa787), and
V_powf((c + 0.055) / 1.055, 2.4) for c over [0.04045, 1] in 1/65536 steps (the sRGB curve, 18126a710).
Default output: %TEMP%/tier0_powlog.bin; Tier0MathTests reads it.
"""
import argparse
import ctypes
import os
import struct

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")


def f32(x):
    return struct.unpack("<f", struct.pack("<f", x))[0]


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--out", default=os.path.join(os.environ.get("TEMP", "."), "tier0_powlog.bin"))
    a = p.parse_args()
    os.add_dll_directory(BIN)
    t0 = ctypes.CDLL(os.path.join(BIN, "tier0.dll"))
    powf = t0.V_powf
    powf.restype = ctypes.c_float
    powf.argtypes = [ctypes.c_float, ctypes.c_float]
    logf = t0.V_logf
    logf.restype = ctypes.c_float
    logf.argtypes = [ctypes.c_float]
    e1 = struct.unpack("<f", struct.pack("<I", 0xBE0866D4))[0]
    e2 = struct.unpack("<f", struct.pack("<I", 0xBD9AA787))[0]
    out = bytearray()

    def rec(op, x, y, r):
        out.extend(struct.pack("<ifff", op, x, y, r))

    for t in range(1000, 40001):
        x = f32(f32(t) / f32(100.0))
        rec(1, x, 0.0, logf(x))
        y = f32(x - f32(10.0))
        if y > 0:
            rec(1, y, 0.0, logf(y))
        if x > 66.0:
            z = f32(x - f32(60.0))
            rec(0, z, e1, powf(z, e1))
            rec(0, z, e2, powf(z, e2))
    g = f32(2.4)
    for i in range(0, 65537):
        c = f32(i / 65536.0)
        if c <= 0.04045:
            continue
        b = f32(f32(c + f32(0.055)) / f32(1.055))
        rec(0, b, g, powf(b, g))
    with open(a.out, "wb") as f:
        f.write(out)
    print(a.out, len(out) // 16, "records")


if __name__ == "__main__":
    main()
