"""impcallers.py <dll> <import substring>...: functions (.pdata) that call or jump through the named imports."""
import sys, pefile, capstone
from capstone.x86 import X86_OP_MEM
pe = pefile.PE(sys.argv[1], fast_load=True); pe.parse_data_directories([1, 3])
b = pe.OPTIONAL_HEADER.ImageBase; img = pe.get_memory_mapped_image()
want = {}
for d in pe.DIRECTORY_ENTRY_IMPORT:
    for i in d.imports:
        if i.name and any(k in i.name.decode() for k in sys.argv[2:]): want[i.address] = i.name.decode()
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64); md.detail = True
for e in pe.DIRECTORY_ENTRY_EXCEPTION:
    s, t = e.struct.BeginAddress, e.struct.EndAddress
    for x in md.disasm(img[s:t], b + s):
        if x.mnemonic in ('call', 'jmp') and x.operands and x.operands[0].type == X86_OP_MEM and x.operands[0].mem.base == capstone.x86.X86_REG_RIP:
            a = x.address + x.size + x.operands[0].mem.disp
            if a in want: print(hex(b + s), hex(x.address), want[a])
