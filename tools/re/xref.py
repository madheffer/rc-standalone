"""xref.py <dll> <string> [...] | --dis <dll> <start hex> [<end hex>]

Light reverse-engineering without Ghidra (for when the PC must stay idle): find the code
that references a string by scanning .text for RIP-relative operands that land on it, and
report the enclosing function from the .pdata unwind table. --dis disassembles one function
(to its .pdata end when no end is given), naming the strings and calls it references.
"""
import bisect
import re
import struct
import sys

import capstone
import pefile


class Image:
    def __init__(self, path):
        self.pe = pefile.PE(path, fast_load=True)
        self.pe.parse_data_directories(directories=[pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_EXCEPTION"],
                                                    pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_IMPORT"]])
        self.imports = {imp.address: "%s!%s" % (d.dll.decode(), (imp.name or b"#%d" % imp.ordinal).decode())
                        for d in getattr(self.pe, "DIRECTORY_ENTRY_IMPORT", []) for imp in d.imports}
        self.base = self.pe.OPTIONAL_HEADER.ImageBase
        self.mem = self.pe.get_memory_mapped_image()
        self.text = next(s for s in self.pe.sections if s.Name.rstrip(b"\0") == b".text")
        funcs = sorted({(e.struct.BeginAddress, e.struct.EndAddress) for e in getattr(self.pe, "DIRECTORY_ENTRY_EXCEPTION", [])})
        self.starts = [f[0] for f in funcs]
        self.ends = [f[1] for f in funcs]

    def function_of(self, rva):
        i = bisect.bisect_right(self.starts, rva) - 1
        return (self.starts[i], self.ends[i]) if i >= 0 and rva < self.ends[i] else None

    def string_at(self, rva, limit=120):
        if not 0 <= rva < len(self.mem):
            return None
        m = re.match(rb"[\x09\x0a\x0d\x20-\x7e]{4,}", self.mem[rva:rva + limit])
        return m.group(0).decode("latin1") if m and self.mem[rva + len(m.group(0))] == 0 or (m and len(m.group(0)) == limit) else None

    def refs_to(self, target):
        """RVAs of instructions whose rip-relative disp32 lands on target (lea/mov forms)."""
        t0, t1 = self.text.VirtualAddress, self.text.VirtualAddress + self.text.Misc_VirtualSize
        code = self.mem[t0:t1]
        hits = []
        # 48 8d xx disp32 (lea r64, [rip+disp]) and 4c 8d; modrm with mod=00 rm=101.
        for m in re.finditer(rb"[\x48\x4c]\x8d[\x05\x0d\x15\x1d\x25\x2d\x35\x3d]", code):
            at = m.start()
            disp = struct.unpack_from("<i", code, at + 3)[0]
            if t0 + at + 7 + disp == target:
                hits.append(t0 + at)
        return hits


def find(img, needle):
    for m in re.finditer(re.escape(needle.encode()) + rb"\0", img.mem):
        rva = m.start()
        # Back up to the start of the C string.
        while rva > 0 and 0x09 <= img.mem[rva - 1] <= 0x7e:
            rva -= 1
        for r in img.refs_to(rva):
            f = img.function_of(r)
            print("%-60r ref %x in %s" % (img.mem[rva:rva + 60].split(b"\0")[0].decode("latin1"), img.base + r,
                                        "%x-%x" % (img.base + f[0], img.base + f[1]) if f else "?"))


def dis(img, start, end):
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = False
    data = img.mem[start - img.base:end - img.base]
    for i in md.disasm(data, start):
        note = ""
        m = re.search(r"\[rip ([+-]) 0x([0-9a-f]+)\]", i.op_str)
        if m:
            target = i.address + i.size + (1 if m.group(1) == "+" else -1) * int(m.group(2), 16)
            s = img.string_at(target - img.base)
            if target in img.imports:
                note = "  ; " + img.imports[target]
            elif s:
                note = "  ; %x %r" % (target, s)
            else:
                raw = img.mem[target - img.base:target - img.base + 8]
                note = "  ; %x %s %r" % (target, raw.hex(), raw.split(bytes(1))[0])
        print("%x: %-8s %s%s" % (i.address, i.mnemonic, i.op_str, note))


if __name__ == "__main__":
    if sys.argv[1] == "--dis":
        img = Image(sys.argv[2])
        start = int(sys.argv[3], 16)
        if len(sys.argv) > 4:
            end = int(sys.argv[4], 16)
        else:
            f = img.function_of(start - img.base)
            end = img.base + f[1]
        dis(img, start, end)
    else:
        img = Image(sys.argv[1])
        for s in sys.argv[2:]:
            find(img, s)
