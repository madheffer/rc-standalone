"""dis.py <dll name> <start hex> <end hex>: disassemble an installed game/bin/win64 DLL, naming imports and RIP data."""
import sys, pefile, capstone
from capstone.x86 import X86_OP_MEM
pe = pefile.PE(r"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\\" + sys.argv[1], fast_load=True); pe.parse_data_directories([1])
imp = {i.address: i.name.decode() for d in pe.DIRECTORY_ENTRY_IMPORT for i in d.imports if i.name}
a, b = int(sys.argv[2], 16), int(sys.argv[3], 16)
base = pe.OPTIONAL_HEADER.ImageBase
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64); md.detail = True
for i in md.disasm(pe.get_data(a - base, b - a), a):
    note = ""
    for op in i.operands:
        if op.type == X86_OP_MEM and op.mem.base == capstone.x86.X86_REG_RIP:
            t = i.address + i.size + op.mem.disp
            note = imp.get(t, hex(t))
    print("%x: %-8s %s%s" % (i.address, i.mnemonic, i.op_str, "   ; " + note if note else ""))
