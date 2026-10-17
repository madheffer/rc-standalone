"""dispscan.py <dll> <disp> -- functions (via .pdata) with any memory operand [reg+disp]."""
import sys, pefile, capstone, bisect
pe = pefile.PE(sys.argv[1], fast_load=True); pe.parse_data_directories([3])
b = pe.OPTIONAL_HEADER.ImageBase; img = pe.get_memory_mapped_image()
disp = int(sys.argv[2], 16)
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64); md.detail = True
funcs = sorted({(e.struct.BeginAddress, e.struct.EndAddress) for e in pe.DIRECTORY_ENTRY_EXCEPTION})
pat = ('+ 0x%x]' % disp)
for s, e in funcs:
    code = img[s:e]
    for ins in md.disasm(code, b + s):
        if pat in ins.op_str and 'rip' not in ins.op_str:
            print(hex(b + s), hex(ins.address), ins.mnemonic, ins.op_str); break
