import sys, os, re
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pe import PE
import capstone
p = PE(sys.argv[1]); pat = re.compile(sys.argv[2]); ctx = re.compile(sys.argv[3]) if len(sys.argv) > 3 else None; win = int(sys.argv[4]) if len(sys.argv) > 4 else 30
n, va, vs, ro, rs = p.sec('.text')
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64); md.skipdata = True
code = p.data[ro:ro+rs]
ins = [(i.address, f'{i.mnemonic} {i.op_str}') for i in md.disasm(code, p.base + va)]
for k, (a, t) in enumerate(ins):
    if pat.search(t):
        if ctx:
            near = [x for x in ins[max(0,k-win):k+win] if ctx.search(x[1])]
            if not near: continue
            print(hex(a), t, '|', '; '.join(f'{x[0]:#x} {x[1]}' for x in near[:3]))
        else:
            print(hex(a), t)
