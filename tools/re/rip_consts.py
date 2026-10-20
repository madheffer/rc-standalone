"""rip_consts.py <dll alias or path> <start hex> <end hex> -- every rip-relative memory operand in a range,
with the float and double it holds: the constants a function reads, for exact ports."""
import os
import struct
import sys

import capstone
import pefile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import paths  # noqa: E402

pe = pefile.PE(paths.binary(sys.argv[1]), fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase
start, end = int(sys.argv[2], 16), int(sys.argv[3], 16)
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
md.detail = True
for i in md.disasm(pe.get_data(start - base, end - start), start):
    for op in i.operands:
        if op.type == capstone.x86.X86_OP_MEM and op.mem.base == capstone.x86.X86_REG_RIP:
            a = i.address + i.size + op.mem.disp
            raw = pe.get_data(a - base, 8)
            if len(raw) < 8:
                continue
            f = struct.unpack('<f', raw[:4])[0]
            d = struct.unpack('<d', raw)[0]
            print("%x: %-8s %-40s -> %x  f=%r (0x%08x)  d=%r" % (i.address, i.mnemonic, i.op_str, a, f, struct.unpack('<I', raw[:4])[0], d))
