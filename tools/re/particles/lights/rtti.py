import sys, struct
sys.path.insert(0,'.')
from pe import PE
def vtables(p, cls):
    td = p.find_all(('.?AV%s@@' % cls).encode() + b'\0')
    out=[]
    for t in td:
        tdva = t - 0x10
        rva = tdva - p.base
        # COL: sig(4)=1, off, cdOff, pTypeDesc rva, pClassDesc rva, pSelf rva
        for col in p.find_all(struct.pack('<I', rva)):
            c = col - 12
            if p.d(c) != 1: continue
            off = p.d(c+4)
            for vref in p.ptrs_to(c):
                out.append((off, vref + 8))
    return out
if __name__ == '__main__':
    p = PE(sys.argv[1])
    for cls in sys.argv[2:]:
        for off, vt in vtables(p, cls):
            print(cls, 'off', hex(off), 'vtable', hex(vt))
            for k in range(int(80)):
                f = p.q(vt + 8*k)
                if not p.in_text(f): break
                print('  [%d] +0x%x %s' % (k, 8*k, hex(f)))
