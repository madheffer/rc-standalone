"""exr_diff.py A.exr B.exr: per-face difference of two cubemap EXRs as BuildCubemaps writes them (uncompressed
half scanlines, the six faces side by side). Prints, per face, the pixels with any channel off by more than 1/256 and
the largest channel difference."""
import struct
import sys

import numpy as np


def read(path):
    f = open(path, "rb").read()
    i, channels, window = 8, [], None
    while f[i] != 0:
        j = f.index(b"\0", i)
        k = f.index(b"\0", j + 1)
        size = int.from_bytes(f[k + 1:k + 5], "little")
        name, value = f[i:j], f[k + 5:k + 5 + size]
        if name == b"channels":
            p = 0
            while value[p] != 0:
                q = value.index(b"\0", p)
                channels.append((value[p:q].decode(), int.from_bytes(value[q + 1:q + 5], "little")))
                p = q + 17
        elif name == b"compression" and value != b"\0":
            raise SystemExit(f"{path}: compressed EXR")
        elif name == b"dataWindow":
            window = struct.unpack("<4i", value)
        i = k + 5 + size
    assert all(t == 1 for _, t in channels), "half channels only"
    w, h = window[2] - window[0] + 1, window[3] - window[1] + 1
    offsets = struct.unpack_from(f"<{h}Q", f, i + 1)
    img = np.zeros((h, w, len(channels)), np.float32)
    for y, o in enumerate(offsets):
        row = np.frombuffer(f, np.float16, w * len(channels), o + 8).reshape(len(channels), w)
        img[y] = row.T.astype(np.float32)
    return img, [c for c, _ in channels]


def main():
    a, ca = read(sys.argv[1])
    b, cb = read(sys.argv[2])
    assert a.shape == b.shape and ca == cb
    side = a.shape[0]
    d = np.abs(a - b)
    for face in range(a.shape[1] // side):
        fd = d[:, face * side:(face + 1) * side]
        print(f"face {face}: {int((fd > 1 / 256).any(axis=2).sum())} pixels over 1/256, largest {fd.max():.5f}, "
              f"{int((fd > 0).any(axis=2).sum())} differ")
    print("equal" if open(sys.argv[1], "rb").read() == open(sys.argv[2], "rb").read() else "bytes differ")


if __name__ == "__main__":
    main()
