import pefile, struct, sys
class PE:
    def __init__(s, path):
        s.pe = pefile.PE(path, fast_load=True)
        s.base = s.pe.OPTIONAL_HEADER.ImageBase
        s.data = open(path,'rb').read()
        s.secs = [(x.Name.rstrip(b'\0').decode(), x.VirtualAddress, x.Misc_VirtualSize, x.PointerToRawData, x.SizeOfRawData) for x in s.pe.sections]
    def rva2off(s, rva):
        for n,va,vs,ro,rs in s.secs:
            if va <= rva < va+max(vs,rs): 
                if rva-va < rs: return ro + rva - va
                return None
        return None
    def off2rva(s, off):
        for n,va,vs,ro,rs in s.secs:
            if ro <= off < ro+rs: return va + off - ro
    def sec(s, name):
        for x in s.secs:
            if x[0]==name: return x
    def read(s, rva, n):
        o = s.rva2off(rva); return s.data[o:o+n] if o is not None else None
    def q(s, rva): return struct.unpack('<Q', s.read(rva,8))[0]
    def d(s, rva): return struct.unpack('<I', s.read(rva,4))[0]
    def i(s, rva): return struct.unpack('<i', s.read(rva,4))[0]
    def cstr(s, rva):
        o = s.rva2off(rva)
        if o is None: return None
        e = s.data.index(b'\0', o); return s.data[o:e].decode('latin1')
    def find_all(s, b):
        r=[]; o=s.data.find(b)
        while o>=0: r.append(s.off2rva(o)); o=s.data.find(b,o+1)
        return r
