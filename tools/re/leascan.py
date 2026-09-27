"""leascan.py <dll> <off>: functions with `lea rcx, [reg+off]` followed within 4 instructions by a call; prints call target."""
import sys, pefile, capstone
from capstone.x86 import X86_OP_MEM, X86_OP_REG, X86_OP_IMM
pe = pefile.PE(sys.argv[1], fast_load=True); pe.parse_data_directories([1, 3])
b = pe.OPTIONAL_HEADER.ImageBase; img = pe.get_memory_mapped_image(); off = int(sys.argv[2], 16)
imports = {}
for d in getattr(pe, 'DIRECTORY_ENTRY_IMPORT', []):
    for i in d.imports:
        if i.name: imports[i.address] = i.name.decode()
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64); md.detail = True
for e in pe.DIRECTORY_ENTRY_EXCEPTION:
    s, t = e.struct.BeginAddress, e.struct.EndAddress
    ins = list(md.disasm(img[s:t], b + s))
    for k, x in enumerate(ins):
        if x.mnemonic == 'lea' and x.operands[0].type == X86_OP_REG and x.reg_name(x.operands[0].reg) == 'rcx' and x.operands[1].type == X86_OP_MEM and x.operands[1].mem.disp == off:
            for y in ins[k+1:k+5]:
                if y.mnemonic in ('call', 'jmp'):
                    op = y.operands[0]; tgt = '?'
                    if op.type == X86_OP_IMM: tgt = hex(op.imm)
                    elif op.type == X86_OP_MEM and op.mem.base == capstone.x86.X86_REG_RIP:
                        a = y.address + y.size + op.mem.disp; tgt = imports.get(a, hex(a))
                    print(hex(b + s), hex(x.address), y.mnemonic, tgt); break
