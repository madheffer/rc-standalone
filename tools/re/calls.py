"""calls.py <dll alias or path> <function hex> -- every call in the function (its .pdata range, chained
unwind entries included), with imports named (call qword ptr [rip + x] through the IAT)."""
import os
import sys

import capstone
import pefile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import paths  # noqa: E402

path = sys.argv[1] if os.path.exists(sys.argv[1]) else paths.binary(sys.argv[1])
pe = pefile.PE(path, fast_load=True)
pe.parse_data_directories([1, 3])
base = pe.OPTIONAL_HEADER.ImageBase
imports = {i.address: f"{d.dll.decode()}!{(i.name or b'').decode() or i.ordinal}" for d in pe.DIRECTORY_ENTRY_IMPORT for i in d.imports}
start = int(sys.argv[2], 16)
ranges = sorted((base + e.struct.BeginAddress, base + e.struct.EndAddress) for e in pe.DIRECTORY_ENTRY_EXCEPTION)
# The function's own entry plus every later range that unwinds back into it (chained entries share
# the function's unwind info is hard to read here, so take contiguous ranges up to the next entry
# that some call targets; simpler: the entry's range and any range starting where it ends).
spans = [r for r in ranges if r[0] == start]
if not spans:
    raise SystemExit("no .pdata entry at %x" % start)
end = spans[0][1]
while True:
    nxt = [r for r in ranges if r[0] == end]
    if not nxt:
        break
    # A chained piece begins right where the previous ends only for split functions; stop at int3 padding.
    data = pe.get_data(end - base, 1)
    if data == b"\xcc":
        break
    end = nxt[0][1]
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
md.detail = True
code = pe.get_data(start - base, end - start)
for i in md.disasm(code, start):
    if i.mnemonic != "call":
        continue
    target = i.op_str
    op = i.operands[0]
    if op.type == capstone.x86.X86_OP_MEM and op.mem.base == capstone.x86.X86_REG_RIP:
        slot = i.address + i.size + op.mem.disp
        target = imports.get(slot, "[%x]" % slot)
    print("%x: %s" % (i.address, target))
