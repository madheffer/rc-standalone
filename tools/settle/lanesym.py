"""Trace SSE float code symbolically, all four lanes, to read the exact float
association a port has to reproduce.

    python lanesym.py <dll> <ranges> [--alias rcx=b rdx=sb ...] [--regs]

<ranges> is one or more start-end hex pairs, "1801b7d60-1801b7e94,1801b7ecc-1801b7f99",
traced in order as one straight path: pick the ranges that make up the branch
you want. Every xmm register holds four lane expressions and every 4-byte memory
slot one; a GPR aliased with --alias names loads from it ("sb[0x3c]"), and a
mov between GPRs carries the alias. Stores to the stack become named temps,
printed once ("s40 = ..."), so long chains stay readable; stores elsewhere print
in full. --regs prints the xmm registers at the end; --ssa N names every
expression longer than N characters as a temp; --quiet <reg> drops stores through
that base register (callee-saved spills through rax, say); --set name=value
presets xmmN lane 0, a GPR, or a stack slot like rsp+0x40 (@SIGN is the sign
mask), for tracing a loop body with named inputs.

Handled: scalar and packed add/sub/mul/div/min/max/sqrt, rcp/rsqrt, mov*,
shufps, unpck[lh]ps, movlhps/movhlps, insertps, dpps, xor/and/andn/or with
constant masks (sign flips and abs print as such), cmp*ps/ss, blendvps, movd
between xmm and GPRs, and float stores through GPRs. Anything else prints as
?? and poisons its destination.
"""
import re
import struct
import sys

import capstone
import pefile

XMM = re.compile(r'xmm\d+$')

SIGN, ABS = 0x80000000, 0x7FFFFFFF


class Tracer:
    def __init__(self, dll, aliases):
        self.pe = pefile.PE(dll, fast_load=True)
        self.base = self.pe.OPTIONAL_HEADER.ImageBase
        self.img = self.pe.get_memory_mapped_image()
        self.md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        self.xmm = {}
        self.gpr_alias = dict(aliases)
        self.gpr_val = {}
        self.mem = {}
        self.names = {}
        self.versions = {}
        self.ssa = 0
        self.quiet = set()

    # -- operands -----------------------------------------------------------
    def reg(self, r):
        return self.xmm.get(r) or [f'{r}.{i}' for i in range(4)]

    def rip_target(self, x, op):
        m = re.search(r'\[rip ([+-]) (0x[0-9a-f]+)\]', op)
        if not m:
            return None
        d = int(m.group(2), 16) * (1 if m.group(1) == '+' else -1)
        return x.address + x.size + d

    def const_lanes(self, t, n):
        raw = self.img[t - self.base:t - self.base + 4 * n]
        out = []
        for i in range(n):
            u = struct.unpack_from('<I', raw, 4 * i)[0]
            f = struct.unpack_from('<f', raw, 4 * i)[0]
            out.append(('mask', u) if u in (SIGN, ABS, 0xFFFFFFFF) or (u >> 23) & 0xFF == 0xFF
                       else '%.9g' % f)
        return out

    def addr(self, op):
        """Canonical name of a memory operand and the lane-0 slot key."""
        body = op.split('[', 1)[1].rstrip(']').replace(' ', '')
        m = re.fullmatch(r'([a-z0-9]+)([+-]0x[0-9a-f]+|[+-]\d+)?', body)
        if not m:
            return body, None
        base, off = m.group(1), int(m.group(2) or '0', 0)
        return base, off

    def slot_name(self, base, off):
        alias = self.gpr_alias.get(base)
        if alias:
            return f'{alias}[{off:#x}]'
        return f'[{base}{off:+#x}]'

    def load(self, x, op, n):
        t = self.rip_target(x, op)
        if t is not None:
            return self.const_lanes(t, n)
        base, off = self.addr(op)
        if off is None:
            return [f'[{base}]'] * n
        lanes = []
        for i in range(n):
            k = (base, off + 4 * i)
            if k in self.mem:
                lanes.append(self.mem[k])
            else:
                lanes.append(self.slot_name(base, off + 4 * i))
        return lanes

    def store(self, x, op, lanes):
        base, off = self.addr(op)
        if off is None:
            print(f'{x.address:x} STORE [{base}] = {lanes}')
            return
        if base in self.quiet:
            return
        for i, e in enumerate(lanes):
            k = (base, off + 4 * i)
            if base in ('rsp', 'rbp') or self.gpr_alias.get(base) == 'stack':
                name = f's{off + 4 * i:x}'.replace('-', 'm') if base == 'rsp' else f'b{off + 4 * i:x}'.replace('-', 'm')
                self.versions[name] = self.versions.get(name, -1) + 1
                if self.versions[name]:
                    name += f'_{self.versions[name]}'
                print(f'{x.address:x} {name} = {e}')
                self.mem[k] = name
            else:
                print(f'{x.address:x} STORE {self.slot_name(base, off + 4 * i)} = {e}')
                self.mem[k] = e

    def src(self, x, op, n=4):
        return self.reg(op) if XMM.match(op) else self.load(x, op, n)

    # -- lane algebra --------------------------------------------------------
    def bin(self, a, o, b):
        return self.named(f'({a} {o} {b})')

    def named(self, e):
        """With --ssa N, an expression longer than N becomes a numbered temp."""
        if not self.ssa or len(e) <= self.ssa:
            return e
        if e not in self.names:
            self.names[e] = f't{len(self.names) + 1}'
            print(f'      {self.names[e]} = {e}')
        return self.names[e]

    @staticmethod
    def logic(m, a, b):
        if isinstance(b, tuple):
            u = b[1]
            if m == 'xorps' and u == SIGN:
                return f'-({a})'
            if m == 'andps' and u == ABS:
                return f'abs({a})'
            if m == 'andps' and u == 0xFFFFFFFF:
                return a
            return f'{m}({a}, {u:#x})'
        if isinstance(a, tuple):
            return Tracer.logic(m, b, a)
        if m == 'xorps' and a == b:
            return '0'
        if b == '0' and m in ('xorps',):
            return a
        if b == '0' and m == 'andps':
            return '0'
        return f'{m}({a}, {b})'

    def step(self, x):
        m = x.mnemonic
        ops = [o.strip() for o in x.op_str.split(',')] if x.op_str else []
        at = f'{x.address:x}'
        arith = {'add': '+', 'sub': '-', 'mul': '*', 'div': '/'}
        # GPR moves carry aliases
        if m == 'mov' and len(ops) == 2 and re.fullmatch(r'r\w+|e\w+', ops[0]) and re.fullmatch(r'r\w+', ops[1]):
            if ops[1] in self.gpr_alias:
                self.gpr_alias[ops[0]] = self.gpr_alias[ops[1]]
            else:
                self.gpr_alias.pop(ops[0], None)
            return
        if m == 'lea' and len(ops) == 2:
            base, off = self.addr(ops[1])
            if off is not None and base in self.gpr_alias:
                self.gpr_alias[ops[0]] = f'{self.gpr_alias[base]}+{off:#x}'
            elif off is not None and base in ('rsp', 'rbp'):
                self.gpr_alias[ops[0]] = f'&{base}{off:+#x}'
            return
        if m == 'mov' and len(ops) == 2 and ops[1].startswith('qword ptr') and re.fullmatch(r'r\w+', ops[0]):
            # A pointer loaded from memory: name what it points at.
            base, off = self.addr(ops[1])
            self.gpr_alias[ops[0]] = f'*{self.slot_name(base, off)}' if off is not None else f'*{base}'
            return
        if m in ('mov', 'movsxd', 'movzx', 'add', 'sub', 'and', 'or', 'xor', 'shl', 'shr', 'imul', 'lea') \
                and ops and re.fullmatch(r'r\w+', ops[0]) and not (m == 'mov' and 'ptr' in ops[1]):
            if m != 'lea':
                self.gpr_alias.pop(ops[0], None)
        if m == 'mov' and len(ops) == 2 and 'ptr' in ops[1] and re.fullmatch(r'e\w+|r\d+d', ops[0]):
            self.gpr_val[ops[0]] = self.load(x, ops[1].replace('dword ptr ', ''), 1)[0]
            return
        if m == 'mov' and len(ops) == 2 and 'ptr' in ops[0] and re.fullmatch(r'e\w+|r\d+d', ops[1]):
            self.store(x, ops[0], [self.gpr_val.get(ops[1], ops[1])])
            return
        if m == 'movd' and len(ops) == 2:
            if XMM.match(ops[0]):
                self.xmm[ops[0]] = [self.gpr_val.get(ops[1], ops[1]), '0', '0', '0']
            else:
                self.gpr_val[ops[0]] = self.reg(ops[1])[0]
            return
        if m in ('movss', 'movsd', 'movaps', 'movups', 'movq', 'movdqa', 'movdqu', 'movlps'):
            n = {'movss': 1, 'movsd': 2, 'movq': 2, 'movlps': 2}.get(m, 4)
            if XMM.match(ops[0]):
                if XMM.match(ops[1]):
                    s = self.reg(ops[1])
                    d = list(self.reg(ops[0])) if n < 4 else [None] * 4
                    for i in range(n):
                        d[i] = s[i]
                    self.xmm[ops[0]] = d
                else:
                    lanes = self.load(x, ops[1], n)
                    if m == 'movlps':
                        d = list(self.reg(ops[0])); d[0:2] = lanes
                    else:
                        d = lanes + ['0'] * (4 - n)
                    self.xmm[ops[0]] = d
            else:
                self.store(x, ops[0], self.reg(ops[1])[:n])
            return
        for k, o in arith.items():
            if m in (k + 'ss', k + 'ps'):
                a = list(self.reg(ops[0]))
                b = self.src(x, ops[1], 1 if m.endswith('ss') else 4)
                for i in range(1 if m.endswith('ss') else 4):
                    a[i] = self.bin(a[i], o, b[i])
                self.xmm[ops[0]] = a
                return
        if m in ('minss', 'maxss', 'minps', 'maxps'):
            n = 1 if m.endswith('ss') else 4
            a = list(self.reg(ops[0])); b = self.src(x, ops[1], n)
            for i in range(n):
                a[i] = f'{m[:3]}({a[i]}, {b[i]})'
            self.xmm[ops[0]] = a
            return
        if m in ('sqrtss', 'sqrtps', 'rsqrtss', 'rsqrtps', 'rcpss', 'rcpps'):
            n = 1 if m.endswith('ss') else 4
            b = self.src(x, ops[1], n)
            a = list(self.reg(ops[0])) if n == 1 else [None] * 4
            for i in range(n):
                a[i] = f'{m[:-2]}({b[i]})'
            self.xmm[ops[0]] = a
            return
        if m in ('xorps', 'andps', 'andnps', 'orps', 'pxor', 'pand'):
            mm = {'pxor': 'xorps', 'pand': 'andps'}.get(m, m)
            a = list(self.reg(ops[0]))
            if XMM.match(ops[1]) and ops[1] == ops[0] and mm == 'xorps':
                self.xmm[ops[0]] = ['0'] * 4
                return
            b = self.src(x, ops[1], 4)
            self.xmm[ops[0]] = [self.logic(mm, a[i], b[i]) for i in range(4)]
            return
        if m == 'shufps':
            a, b, imm = self.reg(ops[0]), self.src(x, ops[1]), int(ops[2], 0)
            self.xmm[ops[0]] = [a[imm & 3], a[(imm >> 2) & 3], b[(imm >> 4) & 3], b[(imm >> 6) & 3]]
            return
        if m in ('unpcklps', 'unpckhps'):
            a, b = self.reg(ops[0]), self.src(x, ops[1])
            self.xmm[ops[0]] = [a[0], b[0], a[1], b[1]] if m == 'unpcklps' else [a[2], b[2], a[3], b[3]]
            return
        if m == 'movlhps':
            a, b = self.reg(ops[0]), self.reg(ops[1])
            self.xmm[ops[0]] = [a[0], a[1], b[0], b[1]]
            return
        if m == 'movhlps':
            a, b = self.reg(ops[0]), self.reg(ops[1])
            self.xmm[ops[0]] = [b[2], b[3], a[2], a[3]]
            return
        if m == 'insertps':
            imm = int(ops[2], 0)
            src = self.reg(ops[1])[(imm >> 6) & 3] if XMM.match(ops[1]) else self.load(x, ops[1], 1)[0]
            a = list(self.reg(ops[0]))
            a[(imm >> 4) & 3] = src
            for i in range(4):
                if imm & (1 << i):
                    a[i] = '0'
            self.xmm[ops[0]] = a
            return
        if m == 'dpps':
            a, b, imm = self.reg(ops[0]), self.src(x, ops[1]), int(ops[2], 0)
            t = [f'({a[i]} * {b[i]})' if imm & (0x10 << i) else '0' for i in range(4)]
            s = f'(({t[0]} + {t[1]}) + ({t[2]} + {t[3]}))'
            self.xmm[ops[0]] = [s if imm & (1 << i) else '0' for i in range(4)]
            return
        if m.startswith('cmp') and m.endswith(('ps', 'ss')):
            a, b = self.reg(ops[0]), self.src(x, ops[1])
            self.xmm[ops[0]] = [f'{m}({a[i]}, {b[i]})' for i in range(4)]
            return
        if m == 'blendps':
            a, b, imm = list(self.reg(ops[0])), self.src(x, ops[1]), int(ops[2], 0)
            self.xmm[ops[0]] = [b[i] if imm & (1 << i) else a[i] for i in range(4)]
            return
        if m == 'blendvps':
            a, b, c = self.reg(ops[0]), self.src(x, ops[1]), self.reg('xmm0')
            self.xmm[ops[0]] = [f'({c[i]} ? {b[i]} : {a[i]})' for i in range(4)]
            return
        if m in ('comiss', 'ucomiss'):
            print(f'{at} {m} {self.reg(ops[0])[0]} ? {self.src(x, ops[1], 1)[0]}')
            return
        if m == 'movmskps':
            print(f'{at} movmskps {self.reg(ops[1])}')
            return
        if m == 'call':
            print(f'{at} CALL {x.op_str}  xmm0={self.reg("xmm0")[0]} xmm1={self.reg("xmm1")[0]}  '
                  f'rcx={self.gpr_alias.get("rcx")} rdx={self.gpr_alias.get("rdx")} r8={self.gpr_alias.get("r8")}')
            self.xmm['xmm0'] = ['ret.0', 'ret.1', 'ret.2', 'ret.3']
            return
        if m.startswith('j') or m == 'ret':
            print(f'{at} {m} {x.op_str}')
            return
        if 'xmm' in x.op_str:
            print(f'{at} ?? {m} {x.op_str}')
            if ops and XMM.match(ops[0]):
                self.xmm[ops[0]] = [f'??{at}'] * 4

    def run(self, ranges):
        for a, b in ranges:
            for x in self.md.disasm(self.img[a - self.base:b - self.base], a):
                self.step(x)


def main():
    args = sys.argv[1:]
    dll, spec = args[0], args[1]
    aliases, show, ssa, quiet, sets = [], False, 0, set(), []
    rest = args[2:]
    i = 0
    while i < len(rest):
        if rest[i] == '--alias':
            i += 1
            while i < len(rest) and '=' in rest[i]:
                aliases.append(tuple(rest[i].split('=', 1)))
                i += 1
        elif rest[i] == '--set':
            i += 1
            while i < len(rest) and '=' in rest[i]:
                sets.append(tuple(rest[i].split('=', 1)))
                i += 1
        elif rest[i] == '--ssa':
            ssa = int(rest[i + 1])
            i += 2
        elif rest[i] == '--quiet':
            quiet.add(rest[i + 1])
            i += 2
        elif rest[i] == '--regs':
            show = True
            i += 1
        else:
            i += 1
    ranges = [tuple(int(p, 16) for p in r.split('-')) for r in spec.split(',')]
    t = Tracer(dll, aliases)
    t.ssa, t.quiet = ssa, quiet
    for k, v in sets:
        v = ('mask', SIGN) if v == '@SIGN' else v
        if XMM.match(k):
            lanes = v.split(',') if isinstance(v, str) else [v]
            t.xmm[k] = lanes + ['0'] * (4 - len(lanes))
        elif '+' in k or '-' in k:
            base, off = re.fullmatch(r'(\w+)([+-]0x[0-9a-f]+)', k).groups()
            t.mem[(base, int(off, 16))] = v
        else:
            t.gpr_val[k] = v
    t.run(ranges)
    if show:
        for r in sorted(t.xmm, key=lambda s: int(s[3:])):
            print(f'{r} = {t.xmm[r]}')


if __name__ == '__main__':
    main()
