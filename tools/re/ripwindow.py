"""ripwindow.py <dll alias or path> <address hex> [...] -- every instruction in .text whose RIP-relative
operand points at one of the addresses (or within 0x60 bytes after it, for fields of a global), with
the function it sits in (via .pdata)."""
import bisect
import os
import sys

import capstone
import pefile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import paths  # noqa: E402

pe = pefile.PE(paths.binary(sys.argv[1]), fast_load=True)
pe.parse_data_directories([3])
base = pe.OPTIONAL_HEADER.ImageBase
targets = [int(a, 16) for a in sys.argv[2:]]
funcs = sorted((base + e.struct.BeginAddress, base + e.struct.EndAddress) for e in pe.DIRECTORY_ENTRY_EXCEPTION)
starts = [f[0] for f in funcs]
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
md.detail = True
for s in pe.sections:
    if not s.Name.rstrip(b"\0") == b".text":
        continue
    data = s.get_data()
    va = base + s.VirtualAddress
    for f0, f1 in funcs:
        if not (va <= f0 < va + len(data)):
            continue
        code = data[f0 - va:f1 - va]
        for i in md.disasm(code, f0):
            for op in i.operands:
                if op.type == capstone.x86.X86_OP_MEM and op.mem.base == capstone.x86.X86_REG_RIP:
                    t = i.address + i.size + op.mem.disp
                    for want in targets:
                        if want <= t < want + 0x60:
                            k = bisect.bisect_right(starts, i.address) - 1
                            print("%x (fn %x): %s %s  -> %x+%x" % (i.address, funcs[k][0], i.mnemonic, i.op_str, want, t - want))
