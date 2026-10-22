"""dxbc_rdef.py <file or dir>... -- every DXBC container embedded in a binary, with its RDEF constant buffers: each
buffer's name and size and each member's name, byte offset, size and type. The engine DLLs carry a few
compiled HLSL shaders whose reflection names the layouts Valve's stripped SPIR-V leaves anonymous
(PerViewConstantBuffer_t, ...)."""
import struct
import sys


def cstr(b, o):
    return b[o:b.index(b"\0", o)].decode("latin-1")


TYPES = {0: "void", 1: "bool", 2: "int", 3: "float", 19: "uint"}
CLASSES = {0: "scalar", 1: "vector", 2: "matrix_rows", 3: "matrix_cols", 4: "object", 5: "struct"}


def rdef(chunk):
    cb_count, cb_off, bind_count, bind_off, target, flags, creator = struct.unpack_from("<7I", chunk, 0)
    stride = 40 if ((target >> 8) & 0xFF) >= 5 else 24
    out = []
    for i in range(cb_count):
        name_off, var_count, var_off, size, cflags, ctype = struct.unpack_from("<6I", chunk, cb_off + i * 24)
        members = []
        for v in range(var_count):
            vn, voff, vsize, vflags, vtype, vdef = struct.unpack_from("<6I", chunk, var_off + v * stride)
            cls, typ, rows, cols, elems, mcount = struct.unpack_from("<6H", chunk, vtype)
            members.append((cstr(chunk, vn), voff, vsize, "%s %s %dx%d%s" % (CLASSES.get(cls, cls), TYPES.get(typ, typ), rows, cols,
                                                                        "[%d]" % elems if elems else ""), vflags))
        out.append((cstr(chunk, name_off), size, members))
    return out


def containers(data):
    at = 0
    while True:
        at = data.find(b"DXBC", at)
        if at < 0:
            return
        try:
            total, count = struct.unpack_from("<II", data, at + 24)
            if 32 < total < 1 << 24 and 0 < count < 32:
                yield at, data[at:at + total]
        except struct.error:
            return
        at += 4


def main():
    seen = set()
    import glob
    import os
    files = [f for a in sys.argv[1:] for f in (sorted(glob.glob(os.path.join(a, "*"))) if os.path.isdir(a) else [a])]
    for path in files:
        data = open(path, "rb").read()
        for at, c in containers(data):
            count = struct.unpack_from("<I", c, 28)[0]
            for k in range(count):
                off = struct.unpack_from("<I", c, 32 + 4 * k)[0]
                tag = c[off:off + 4]
                if tag != b"RDEF":
                    continue
                size = struct.unpack_from("<I", c, off + 4)[0]
                try:
                    bufs = rdef(c[off + 8:off + 8 + size])
                except Exception as e:
                    print("%s +0x%x: RDEF parse failed %r" % (path, at, e))
                    continue
                for name, bsize, members in bufs:
                    key = (name, bsize, tuple(m[:3] for m in members))
                    if key in seen:
                        continue
                    seen.add(key)
                    print("%s file +0x%x: cbuffer %s, %d bytes" % (path.split("/")[-1], at, name, bsize))
                    for m in members:
                        print("    +%-5d %-44s %4d bytes  %s%s" % (m[1], m[0], m[2], m[3], "" if m[4] & 2 else "  (unused)"))


if __name__ == "__main__":
    main()
