import sys, struct, re
sys.stdout.reconfigure(errors="backslashreplace")
exec(open('schemaenum.py').read().split("for name in sys.argv[2:]:")[0])
text = [s for s in secs if s[0] == '.text'][0]
_, tva, tvsz, traw, trsz = text
code = b[traw:traw+trsz]
refs = []  # (insn_va, target_va)
for m in re.finditer(rb'[\x48\x4c][\x8d\x8b][\x05\x0d\x15\x1d\x25\x2d\x35\x3d]', code):
    i = m.start()
    disp = struct.unpack_from('<i', code, i + 3)[0]
    refs.append((base + tva + i, base + tva + i + 7 + disp))
def strva(name):
    off = 0
    while True:
        off = b.find(name.encode() + b'\0', off + 1)
        if off < 0: return None
        if b[off-1] == 0: return off2va(off)
def strat(t):
    o = va2off(t)
    if o is None: return None
    m = re.match(rb'[\x20-\x7e]{4,100}\0', b[o:o+101])
    if m and b[o-1] == 0: return m.group()[:-1].decode()
import bisect
refs.sort()
keys = [r[0] for r in refs]
for name in sys.argv[2:]:
    va = strva(name)
    hits = [r[0] for r in refs if r[1] == va]
    print('==', name, hex(va), 'refs', [hex(h) for h in hits][:12])
    for h in hits[:8]:
        lo = bisect.bisect_left(keys, h - 0x800); hi = bisect.bisect_left(keys, h + 0x800)
        near = []
        for k in range(lo, hi):
            s = strat(refs[k][1])
            if s and s not in near: near.append(s)
        print('   @', hex(h), near[:30])
