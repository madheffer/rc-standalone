import sys; sys.path.insert(0,'.')
from pe import PE
def fields(p, cls):
    a = p.find_str(cls)[0]
    out = []
    for r in p.ptrs_to(a):
        ci = r - 8
        size = p.i(ci + 0x20); n = p.d(ci + 0x24) & 0xffff; fp = p.q(ci + 0x30)
        if not (0 < n < 400 and fp): continue
        for k in range(n):
            f = fp + 0x20 * k
            out.append((p.i(f + 16), p.cstr(p.q(f))))
        return size, out
    return None, out
if __name__ == '__main__':
    p = PE(sys.argv[1])
    for c in sys.argv[2:]:
        size, fl = fields(p, c)
        print(c, 'size', hex(size) if size else None)
        for o, nm in fl: print('   %#06x %s' % (o, nm))
