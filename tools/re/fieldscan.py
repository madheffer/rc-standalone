"""fieldscan.py <dll> <off>... -- functions (via .pdata) whose code stores to [reg+off] for every listed offset."""
import sys, pefile, collections, capstone
pe = pefile.PE(sys.argv[1], fast_load=True); pe.parse_data_directories([3])
b = pe.OPTIONAL_HEADER.ImageBase; img = pe.get_memory_mapped_image()
want = [int(x, 16) for x in sys.argv[2:]]
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64); md.detail = True
from capstone.x86 import X86_OP_MEM
for e in pe.DIRECTORY_ENTRY_EXCEPTION:
    s, t = e.struct.BeginAddress, e.struct.EndAddress
    code = img[s:t]; seen = set()
    for ins in md.disasm(code, b + s):
        if len(ins.operands) >= 2 and ins.operands[0].type == X86_OP_MEM and ins.mnemonic.startswith(('mov', 'movss', 'movsd', 'movq', 'movd')):
            d = ins.operands[0].mem.disp
            if d in want: seen.add(d)
    if len(seen) == len(want):
        print(hex(b + s), hex(t - s))
