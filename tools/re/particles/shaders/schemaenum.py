import struct, sys, re

path = sys.argv[1]
b = open(path, 'rb').read()
pe = struct.unpack_from('<I', b, 0x3c)[0]
nsec = struct.unpack_from('<H', b, pe + 6)[0]
optsz = struct.unpack_from('<H', b, pe + 20)[0]
base = struct.unpack_from('<Q', b, pe + 24 + 24)[0]
secs = []
o = pe + 24 + optsz
for i in range(nsec):
    name = b[o:o+8].rstrip(b'\0').decode()
    vsz, va, rsz, raw = struct.unpack_from('<IIII', b, o + 8)
    secs.append((name, va, vsz, raw, rsz))
    o += 40

def off2va(off):
    for n, va, vsz, raw, rsz in secs:
        if raw <= off < raw + rsz: return base + va + off - raw
def va2off(v):
    r = v - base
    for n, va, vsz, raw, rsz in secs:
        if va <= r < va + rsz: return raw + r - va

def cstr(v):
    off = va2off(v)
    if off is None: return None
    e = b.index(b'\0', off)
    return b[off:e].decode('latin1')

def find_ptrs(target_va):
    pat = struct.pack('<Q', target_va)
    return [m.start() for m in re.finditer(re.escape(pat), b)]

for name in sys.argv[2:]:
    soff = b.find(name.encode() + b'\0')
    while soff > 0 and b[soff-1] != 0:  # ensure full string match
        soff = b.find(name.encode() + b'\0', soff + 1)
    sva = off2va(soff)
    refs = find_ptrs(sva)
    print('==', name, hex(sva), 'refs', [hex(r) for r in refs])
    for r in refs[:1]:
        # walk enumerator array: stride guess 0x20 (name, value, metadatacount, metadata)
        for stride in (0x20, 0x18, 0x10):
            # go backwards to the start
            start = r
            while True:
                p = struct.unpack_from('<Q', b, start - stride)[0]
                s = cstr(p) if p else None
                if s and re.fullmatch(r'[A-Z][A-Z0-9_]+', s or ''): start -= stride
                else: break
            out = []
            q = start
            while True:
                p = struct.unpack_from('<Q', b, q)[0]
                s = cstr(p) if p else None
                if not (s and re.fullmatch(r'[A-Z][A-Z0-9_]+', s)): break
                val = struct.unpack_from('<q', b, q + 8)[0]
                out.append((s, val)); q += stride
            if len(out) > 1:
                print('  stride', hex(stride))
                for s, v in out: print('   ', v, s)
                break
