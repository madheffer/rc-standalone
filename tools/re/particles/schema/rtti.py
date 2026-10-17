import sys, os, struct, json
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pe import PE
def vtables(path):
    p = PE(path)
    rd = p.sec('.rdata'); dt = p.sec('.data')
    # type descriptors: ".?AV" names inside .data
    names = {}
    data = p.data
    i = data.find(b'.?AV')
    while i >= 0:
        e = data.index(b'\0', i); rva = p.off2rva(i)
        if rva: names[rva - 0x10] = data[i:e].decode('latin1')
        i = data.find(b'.?AV', e)
    # COLs in .rdata: sig=1, offset, cdOffset, typeDescRVA, classHierRVA, selfRVA
    out = {}
    n, va, vs, ro, rs = rd
    for off in range(ro, ro + rs - 24, 4):
        sig, o, cd, td, ch, self_ = struct.unpack_from('<6I', data, off)
        if sig == 1 and td in names and self_ == p.off2rva(off):
            col = p.base + self_
            # vtable: pointer to COL followed by vfuncs
            for r in p.find_all(struct.pack('<Q', col)):
                out.setdefault(names[td], []).append({'offset': o, 'vtable': hex(p.base + r + 8)})
    return out
if __name__ == '__main__':
    o = vtables(sys.argv[1]); json.dump(o, open(sys.argv[2], 'w'), indent=1); print(len(o))
