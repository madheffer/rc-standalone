"""navfile.py <.nav> [029 out .bin] -- split a CS2 .nav (version 36) into its sections, as SaveNavMeshToBuffer
writes them (rc 1818d1320), and with a stage 029 capture compare the corner pool and polygons."""
import struct
import sys


def kv3_end(b, at):
    """End of a binary KV3 v5 block at at (the trailer 00 dd ee ff closes it)."""
    return b.index(b"\x00\xdd\xee\xff", at) + 4


def parse(b):
    o = {}
    magic, ver, sub = struct.unpack_from("<III", b)
    p = 12
    o["analyzed"] = b[p]; p += 1
    nstr = struct.unpack_from("<H", b, p)[0]; p += 2
    strs = []
    for _ in range(nstr):
        ln = struct.unpack_from("<H", b, p)[0]; p += 2
        strs.append(b[p:p + ln]); p += ln
    o["strings"] = strs
    o["empty_string_used"] = b[p]; p += 1
    p = (p + 7) & ~7
    o["kv1"] = (p, kv3_end(b, p)); p = o["kv1"][1]
    nc = struct.unpack_from("<I", b, p)[0]; p += 4
    o["corners"] = [struct.unpack_from("<3f", b, p + 12 * i) for i in range(nc)]; p += 12 * nc
    npoly = struct.unpack_from("<I", b, p)[0]; p += 4
    polys = []
    for _ in range(npoly):
        k = b[p]; p += 1
        idx = list(struct.unpack_from(f"<{k}I", b, p)); p += 4 * k
        mm = struct.unpack_from("<I", b, p)[0]; p += 4
        polys.append((idx, mm))
    o["polys"] = polys
    o["unk2"] = struct.unpack_from("<I", b, p)[0]; p += 4
    o["movable"] = struct.unpack_from("<I", b, p)[0]; p += 4
    p = (p + 7) & ~7
    o["kv2"] = (p, kv3_end(b, p)); p = o["kv2"][1]
    na = struct.unpack_from("<I", b, p)[0]; p += 4
    areas = []
    for _ in range(na):
        aid, attr, hull, poly, f = struct.unpack_from("<IqBIf", b, p); p += 21
        conns = []
        for _ in polys[poly][0]:
            c = struct.unpack_from("<I", b, p)[0]; p += 4
            conns.append([struct.unpack_from("<II", b, p + 8 * j) for j in range(c)]); p += 8 * c
        hs = b[p]; enc = struct.unpack_from("<I", b, p + 1)[0]; p += 5
        la = struct.unpack_from("<I", b, p)[0]; p += 4 + 4 * la
        lb = struct.unpack_from("<I", b, p)[0]; p += 4 + 4 * lb
        areas.append(dict(id=aid, attr=attr, hull=hull, poly=poly, f=f, conns=conns, hs=hs, enc=enc))
    o["areas"] = areas
    o["after_areas"] = p
    return o


def main():
    b = open(sys.argv[1], "rb").read()
    o = parse(b)
    print(len(b), "bytes;", len(o["corners"]), "corners,", len(o["polys"]), "polys,", len(o["areas"]), "areas; kv1", o["kv1"], "kv2", o["kv2"],
          "strings", o["strings"], "empty", o["empty_string_used"], "after areas at", o["after_areas"])
    for a in o["areas"][:6]:
        print(" ", a)
    if len(sys.argv) > 2:
        c = open(sys.argv[2], "rb").read()
        n, nv = struct.unpack_from("<ii", c)
        pool = [struct.unpack_from("<3d", c, 8 + n * 0x88 + 24 * i) for i in range(nv)]
        gp = [list(struct.unpack_from(f"<{struct.unpack_from('<i', c, 8 + i * 0x88)[0]}i", c, 8 + i * 0x88 + 0x68)) for i in range(n)]
        same_pool = [tuple(struct.unpack("<3f", struct.pack("<3f", *v))) for v in pool] == [tuple(v) for v in o["corners"]]
        print("pool equal as floats in order:", same_pool, "; polys equal in order:", gp == [p[0] for p in o["polys"]])
        if not same_pool:
            for i in range(min(8, nv)):
                print("  ", pool[i], o["corners"][i])


if __name__ == "__main__":
    main()
