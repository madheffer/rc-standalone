"""simd.py <dll> <start hex> <end hex> [reg=expr ...] -- trace packed SSE symbolically.

Like symsse.py, but every xmm register is four lanes. A 16-byte load from
[m] becomes the lanes m.0 .. m.3; shuffles, unpacks, blends and lane-wise
arithmetic follow the instruction set exactly, and every store prints the
lanes it writes, fully parenthesised. Straight-line only.

Seed registers or pointer-named memory from the command line, e.g.
  rcx=q  -> [rcx] reads as q.0 .. q.3
"""
import re
import struct
import sys

import capstone
import pefile

pe = pefile.PE(sys.argv[1], fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase
img = pe.get_memory_mapped_image()
a, b = int(sys.argv[2], 16), int(sys.argv[3], 16)
alias = dict(kv.split("=", 1) for kv in sys.argv[4:])
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)

R = {}
MEM = {}


def isreg(op):
    return re.fullmatch(r"xmm\d+", op.strip()) is not None


def const(x, op, n):
    m = re.search(r"\[rip ([+-]) (0x[0-9a-f]+)\]", op)
    if not m:
        return None
    d = int(m.group(2), 16) * (1 if m.group(1) == "+" else -1)
    t = x.address + x.size + d - base
    return ["%.9g" % v for v in struct.unpack("<%df" % n, img[t:t + 4 * n])]


def addr(op):
    s = op.split("[", 1)[1].rstrip("]").strip()
    for k, v in alias.items():
        s = re.sub(r"\b%s\b" % k, v, s)
    return s


def lanes_of(x, op, n=4):
    op = op.strip()
    if isreg(op):
        return list(R.get(op, [op + "?.%d" % i for i in range(4)]))
    c = const(x, op, n)
    if c is not None:
        return c + ["0"] * (4 - n)
    base_ = addr(op)
    m = re.match(r"(.*?)(?: \+ (0x[0-9a-f]+|\d+))?$", base_)
    root, off = m.group(1), int(m.group(2), 0) if m.group(2) else 0
    out = []
    for i in range(n):
        key = "%s+%d" % (root, off + 4 * i)
        out.append(MEM.get(key, "%s.%d" % (root, (off // 4) + i) if off % 4 == 0 else key))
    return out + ["0"] * (4 - n)


def store(x, op, lanes, n):
    base_ = addr(op)
    m = re.match(r"(.*?)(?: \+ (0x[0-9a-f]+|\d+))?$", base_)
    root, off = m.group(1), int(m.group(2), 0) if m.group(2) else 0
    for i in range(n):
        MEM["%s+%d" % (root, off + 4 * i)] = lanes[i]
        print("%x STORE [%s + %d] = %s" % (x.address, root, off + 4 * i, lanes[i]))


def bin_(p, q, o):
    return "(%s %s %s)" % (p, o, q)


PS = {"addps": "+", "subps": "-", "mulps": "*", "divps": "/"}
SS = {"addss": "+", "subss": "-", "mulss": "*", "divss": "/"}

for x in md.disasm(img[a - base:b - base], a):
    m = x.mnemonic
    ops = [o.strip() for o in x.op_str.split(",")] if x.op_str else []
    if m in PS:
        p, q = lanes_of(x, ops[0]), lanes_of(x, ops[1])
        R[ops[0]] = [bin_(p[i], q[i], PS[m]) for i in range(4)]
    elif m in SS:
        p, q = lanes_of(x, ops[0]), lanes_of(x, ops[1], 1)
        R[ops[0]] = [bin_(p[0], q[0], SS[m])] + p[1:]
    elif m in ("sqrtps",):
        q = lanes_of(x, ops[1])
        R[ops[0]] = ["sqrt(%s)" % v for v in q]
    elif m in ("sqrtss",):
        p, q = lanes_of(x, ops[0]), lanes_of(x, ops[1], 1)
        R[ops[0]] = ["sqrt(%s)" % q[0]] + p[1:]
    elif m in ("rcpps",):
        R[ops[0]] = ["rcp(%s)" % v for v in lanes_of(x, ops[1])]
    elif m in ("maxps", "minps"):
        p, q = lanes_of(x, ops[0]), lanes_of(x, ops[1])
        R[ops[0]] = ["%s(%s, %s)" % (m[:3], p[i], q[i]) for i in range(4)]
    elif m in ("maxss", "minss"):
        p, q = lanes_of(x, ops[0]), lanes_of(x, ops[1], 1)
        R[ops[0]] = ["%s(%s, %s)" % (m[:3], p[0], q[0])] + p[1:]
    elif m == "shufps":
        p, q, imm = lanes_of(x, ops[0]), lanes_of(x, ops[1]), int(ops[2], 0)
        R[ops[0]] = [p[imm & 3], p[(imm >> 2) & 3], q[(imm >> 4) & 3], q[(imm >> 6) & 3]]
    elif m == "unpcklps":
        p, q = lanes_of(x, ops[0]), lanes_of(x, ops[1])
        R[ops[0]] = [p[0], q[0], p[1], q[1]]
    elif m == "unpckhps":
        p, q = lanes_of(x, ops[0]), lanes_of(x, ops[1])
        R[ops[0]] = [p[2], q[2], p[3], q[3]]
    elif m == "movlhps":
        p, q = lanes_of(x, ops[0]), lanes_of(x, ops[1])
        R[ops[0]] = [p[0], p[1], q[0], q[1]]
    elif m == "movhlps":
        p, q = lanes_of(x, ops[0]), lanes_of(x, ops[1])
        R[ops[0]] = [q[2], q[3], p[2], p[3]]
    elif m == "blendps":
        p, q, imm = lanes_of(x, ops[0]), lanes_of(x, ops[1]), int(ops[2], 0)
        R[ops[0]] = [q[i] if imm >> i & 1 else p[i] for i in range(4)]
    elif m == "insertps":
        p, q, imm = lanes_of(x, ops[0]), lanes_of(x, ops[1]), int(ops[2], 0)
        src = q[(imm >> 6) & 3] if isreg(ops[1]) else q[0]
        out = list(p)
        out[(imm >> 4) & 3] = src
        R[ops[0]] = ["0" if imm >> i & 1 else out[i] for i in range(4)]
    elif m == "dpps":
        p, q, imm = lanes_of(x, ops[0]), lanes_of(x, ops[1]), int(ops[2], 0)
        terms = [bin_(p[i], q[i], "*") for i in range(4) if imm >> (4 + i) & 1]
        # The hardware adds lanes (0+1) and (2+3), then the two sums.
        prods = [bin_(p[i], q[i], "*") if imm >> (4 + i) & 1 else "0" for i in range(4)]
        s = bin_(bin_(prods[0], prods[1], "+"), bin_(prods[2], prods[3], "+"), "+")
        R[ops[0]] = [s if imm >> i & 1 else "0" for i in range(4)]
    elif m in ("movups", "movaps", "movdqu", "movdqa"):
        if isreg(ops[0]):
            R[ops[0]] = lanes_of(x, ops[1])
        else:
            store(x, ops[0], lanes_of(x, ops[1]), 4)
    elif m in ("movss", "movd"):
        if isreg(ops[0]) and isreg(ops[1]):
            R[ops[0]] = [lanes_of(x, ops[1])[0]] + lanes_of(x, ops[0])[1:]
        elif isreg(ops[0]):
            R[ops[0]] = [lanes_of(x, ops[1], 1)[0], "0", "0", "0"]
        else:
            store(x, ops[0], lanes_of(x, ops[1]), 1)
    elif m in ("movq", "movsd", "movlps"):
        if isreg(ops[0]):
            lo = lanes_of(x, ops[1], 2)
            keep = lanes_of(x, ops[0]) if m == "movlps" else ["0", "0", "0", "0"]
            R[ops[0]] = [lo[0], lo[1]] + keep[2:]
        else:
            store(x, ops[0], lanes_of(x, ops[1]), 2)
    elif m in ("xorps", "pxor") and ops[0] == ops[1]:
        R[ops[0]] = ["0"] * 4
    elif m == "xorps":
        R[ops[0]] = ["-(%s)" % v for v in lanes_of(x, ops[0])]
    elif m in ("comiss", "ucomiss"):
        print("%x %s %s ? %s" % (x.address, m, lanes_of(x, ops[0])[0], lanes_of(x, ops[1], 1)[0]))
    elif m == "call" or m.startswith("j") or m == "ret":
        print("%x %s %s" % (x.address, m, x.op_str))
    elif "xmm" in x.op_str:
        print("%x ?? %s %s" % (x.address, m, x.op_str))
        if ops and isreg(ops[0]):
            R[ops[0]] = ["%s(%s).%d" % (m, x.op_str, i) for i in range(4)]
