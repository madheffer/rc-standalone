"""compile_args.py <out.json>: every name string resourcecompiler passes in rdx to a virtual call at
vtable slots 0x50 / 0x58 / 0x60 / 0x68 / 0x70 (the compile context's argument getters in CompileMap:
GetBool at 0x50, GetString at 0x68, ...), with the functions that ask. Heuristic: other virtual calls
with a string in rdx land here too; the calling functions tell them apart."""
import json, re, sys
import pefile, capstone
from capstone.x86 import X86_OP_MEM, X86_OP_REG

P = r"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\resourcecompiler.dll"
pe = pefile.PE(P, fast_load=True); pe.parse_data_directories([3])
b = pe.OPTIONAL_HEADER.ImageBase; img = pe.get_memory_mapped_image()
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64); md.detail = True

def cstr(va):
    try:
        s = pe.get_data(va - b, 64).split(b"\0")[0]
        t = s.decode("ascii")
        return t if re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{1,40}", t) else None
    except Exception:
        return None

found = {}
for e in pe.DIRECTORY_ENTRY_EXCEPTION:
    s, t = e.struct.BeginAddress, e.struct.EndAddress
    ins = list(md.disasm(img[s:t], b + s))
    for k, x in enumerate(ins):
        if x.mnemonic != "call" or not x.operands or x.operands[0].type != X86_OP_MEM:
            continue
        m = x.operands[0].mem
        if m.base == capstone.x86.X86_REG_RIP or m.index != 0 or m.disp not in (0x50, 0x58, 0x60, 0x68, 0x70):
            continue
        name = None
        for y in reversed(ins[max(0, k - 8):k]):
            if y.mnemonic == "lea" and y.operands[0].type == X86_OP_REG and y.reg_name(y.operands[0].reg) == "rdx" \
               and y.operands[1].type == X86_OP_MEM and y.operands[1].mem.base == capstone.x86.X86_REG_RIP:
                name = cstr(y.address + y.size + y.operands[1].mem.disp)
                break
        if name:
            found.setdefault(name, set()).add((hex(m.disp), hex(b + s)))
json.dump({n: sorted(v) for n, v in found.items()}, open(sys.argv[1], "w"), indent=1)
print(len(found), "names")
