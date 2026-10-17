import sys, os, struct
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pe import PE
p = PE(sys.argv[1]); name = sys.argv[2].encode()
o = p.data.find(name); td = p.off2rva(o) - 0x10
n, va, vs, ro, rs = p.sec('.rdata')
import re
pat = struct.pack('<I', td)
i = p.data.find(pat, ro)
while 0 <= i < ro + rs:
    col = i - 12
    sig, off, cd, tdr, ch, self_ = struct.unpack_from('<6I', p.data, col)
    if sig == 1 and self_ == p.off2rva(col):
        for r in p.find_all(struct.pack('<Q', p.base + self_)):
            vt = p.base + r + 8
            print('offset', off, 'vtable', hex(vt))
            for k in range(int(sys.argv[3]) if len(sys.argv) > 3 else 40):
                print('  ', k, hex(k*8), hex(p.q(r + 8 + k*8)))
    i = p.data.find(pat, i + 1)
