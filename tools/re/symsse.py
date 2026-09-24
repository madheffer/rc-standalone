"""symsse.py <dll> <start hex> <end hex> -- trace scalar SSE float ops symbolically.

Every xmm register and stack slot holds an expression; loads from other
memory become [operand] leaves, float constants print as numbers. Stores,
compares and calls print the fully parenthesised expression, which is the
real association (Ghidra's printer drops parentheses on chained + and *).
Straight-line tracing only: branches are ignored, so read each basic block
with its own entry state in mind.
"""
import sys, struct, re, pefile, capstone

pe = pefile.PE(sys.argv[1], fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase
img = pe.get_memory_mapped_image()
a, b = int(sys.argv[2], 16), int(sys.argv[3], 16)
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)

regs = {}
mem = {}


def const(x, op):
    m = re.search(r'\[rip ([+-]) (0x[0-9a-f]+)\]', op)
    if not m:
        return None
    d = int(m.group(2), 16) * (1 if m.group(1) == '+' else -1)
    t = x.address + x.size + d - base
    return '%.9g' % struct.unpack('<f', img[t:t + 4])[0]


def val(x, op):
    op = op.strip()
    if op.startswith('xmm'):
        return regs.get(op, op + '?')
    c = const(x, op)
    if c is not None:
        return c
    key = op.replace('dword ptr ', '').replace('qword ptr ', '').replace('xmmword ptr ', '')
    return mem.get(key, key)


def key(op):
    return op.strip().replace('dword ptr ', '').replace('qword ptr ', '').replace('xmmword ptr ', '')


binops = {'addss': '+', 'subss': '-', 'mulss': '*', 'divss': '/'}
for x in md.disasm(img[a - base:b - base], a):
    m, ops = x.mnemonic, [o.strip() for o in x.op_str.split(',')] if x.op_str else []
    line = f'{x.address:x} '
    if m in binops and len(ops) == 2:
        regs[ops[0]] = f'({val(x, ops[0])} {binops[m]} {val(x, ops[1])})'
    elif m in ('maxss', 'minss'):
        regs[ops[0]] = f'{m[:3]}({val(x, ops[0])}, {val(x, ops[1])})'
    elif m == 'sqrtss':
        regs[ops[0]] = f'sqrt({val(x, ops[1])})'
    elif m in ('movss', 'movd', 'movaps', 'movups', 'movq', 'movsd', 'movdqa', 'movdqu', 'movapd'):
        if ops[0].startswith('xmm'):
            regs[ops[0]] = val(x, ops[1])
        elif ops[1].startswith('xmm'):
            k = key(ops[0])
            mem[k] = regs.get(ops[1], ops[1] + '?')
            print(line + f'STORE {k} = {mem[k]}')
        else:
            pass
    elif m in ('xorps', 'pxor', 'xorpd') and ops[0] == ops[1]:
        regs[ops[0]] = '0'
    elif m == 'xorps':
        regs[ops[0]] = f'-({val(x, ops[0])})'
    elif m in ('comiss', 'ucomiss'):
        print(line + f'{m} {val(x, ops[0])} ? {val(x, ops[1])}')
    elif m == 'cvtsi2ss':
        regs[ops[0]] = f'float({ops[1]})'
    elif m == 'call':
        print(line + f'CALL {x.op_str} xmm0={regs.get("xmm0")} xmm1={regs.get("xmm1")} xmm2={regs.get("xmm2")} xmm3={regs.get("xmm3")}')
        regs['xmm0'] = 'ret'
    elif m.startswith('j') or m == 'ret':
        print(line + f'{m} {x.op_str}')
    elif any(r in x.op_str for r in ('xmm',)):
        print(line + f'?? {m} {x.op_str}')
        if ops and ops[0].startswith('xmm'):
            regs[ops[0]] = f'{m}({x.op_str})'
