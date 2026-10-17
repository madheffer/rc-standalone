import pefile, struct, re, sys, functools

class PE:
    def __init__(self, path):
        self.path = path
        self.pe = pefile.PE(path, fast_load=True)
        self.data = open(path, 'rb').read()
        self.base = self.pe.OPTIONAL_HEADER.ImageBase
        self.secs = [(s.Name.rstrip(b'\0').decode(), s.VirtualAddress, s.Misc_VirtualSize, s.PointerToRawData, s.SizeOfRawData) for s in self.pe.sections]
        self.img = bytearray(max(va + max(vs, rs) for _, va, vs, _, rs in self.secs))
        for _, va, vs, ro, rs in self.secs:
            self.img[va:va + rs] = self.data[ro:ro + rs]
        self.img = bytes(self.img)

    def sec(self, name):
        for s in self.secs:
            if s[0] == name:
                return s
    def rd(self, va, n):
        r = va - self.base
        return self.img[r:r + n]
    def q(self, va): return struct.unpack('<Q', self.rd(va, 8))[0]
    def d(self, va): return struct.unpack('<I', self.rd(va, 4))[0]
    def i(self, va): return struct.unpack('<i', self.rd(va, 4))[0]
    def f(self, va): return struct.unpack('<f', self.rd(va, 4))[0]
    def cstr(self, va, n=256):
        b = self.rd(va, n)
        return b.split(b'\0')[0].decode('latin1')
    def find_all(self, needle):
        return [m.start() + self.base for m in re.finditer(re.escape(needle), self.img)]
    def find_str(self, s):
        return [a for a in self.find_all(s.encode() + b'\0') if self.img[a - self.base - 1] == 0]
    def ptrs_to(self, va):
        return self.find_all(struct.pack('<Q', va))
    def in_text(self, va):
        _, sva, vs, _, _ = self.sec('.text')
        return self.base + sva <= va < self.base + sva + vs

    @functools.cached_property
    def riprefs(self):
        """map target -> [instr addr] for every RIP-relative operand in .text (capstone)."""
        import capstone
        md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        md.skipdata = True
        _, sva, vs, _, _ = self.sec('.text')
        code = self.img[sva:sva + vs]
        out = {}
        for ins in md.disasm(code, self.base + sva):
            if 'rip' in ins.op_str:
                m = re.search(r'rip ([+-]) (0x[0-9a-f]+|\d+)', ins.op_str)
                if m:
                    off = int(m.group(2), 0) * (1 if m.group(1) == '+' else -1)
                    out.setdefault(ins.address + ins.size + off, []).append(ins.address)
            elif ins.mnemonic in ('call', 'jmp') and ins.op_str.startswith('0x'):
                out.setdefault(int(ins.op_str, 16), []).append(ins.address)
        return out

    def dis(self, va, n=200, stop_ret=True):
        import capstone
        md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        md.detail = False
        code = self.rd(va, n * 15)
        res = []
        for ins in md.disasm(code, va):
            res.append(ins)
            if len(res) >= n or (stop_ret and ins.mnemonic in ('ret',) ):
                break
        return res
