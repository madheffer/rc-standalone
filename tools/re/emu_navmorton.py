"""emu_navmorton.py <navsystem dll> <out.json> [cases]: the polygon BVH's leaf keys (build 20261015;
20261023 has the same code at the same addresses) under unicorn, for random cases.

- 180044fb0(out int[16], x uint[16], y uint[16], z uint[16]): the 16-wide Morton code of 10-bit
  coordinates. Written as "morton": a list of {"x", "y", "z", "code"}.
- 180047aa0(bvh, out): per item its (index, key) pair from the quantised boxes the BVH holds at
  bvh+0x160 (blocks of 16 items, 0xc0 bytes: min x, max x, min y, max y, min z, max z, 16 int16 each);
  the count is bvh+0. Written as "keys": a list of {"boxes": [[minx, maxx, miny, maxy, minz, maxz]...],
  "pairs": [[index, key]...]}, boxes drawn from the whole int16 range and from narrow ones.
"""
import json
import random
import struct
import sys

import pefile
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9, UC_X86_REG_RSP

RVA = 0x44fb0
dll, out_path = sys.argv[1:3]
cases = int(sys.argv[3]) if len(sys.argv) > 3 else 4000
pe = pefile.PE(dll)
base = pe.OPTIONAL_HEADER.ImageBase
size = (pe.OPTIONAL_HEADER.SizeOfImage + 0xfff) & ~0xfff
image = bytearray(size)
image[: len(pe.header)] = pe.header
for s in pe.sections:
    data = s.get_data()
    image[s.VirtualAddress: s.VirtualAddress + len(data)] = data[: size - s.VirtualAddress]
DATA, STACK, RET = 0x20000000, 0x30000000, 0x40000000
mu = Uc(UC_ARCH_X86, UC_MODE_64)
mu.mem_map(base, size)
mu.mem_write(base, bytes(image))
mu.mem_map(DATA, 0x10000)
mu.mem_map(STACK, 0x100000)
mu.mem_map(RET, 0x1000)
mu.mem_write(RET, b"\xc3")
rng = random.Random(20261025)
out = []
for _ in range(cases // 16):
    axes = [[rng.randrange(1024) for _ in range(16)] for _ in range(3)]
    for k, values in enumerate(axes):
        mu.mem_write(DATA + 0x100 + k * 0x40, struct.pack("<16I", *values))
    sp = STACK + 0x80000
    mu.mem_write(sp, struct.pack("<Q", RET))
    mu.reg_write(UC_X86_REG_RSP, sp)
    mu.reg_write(UC_X86_REG_RCX, DATA)
    mu.reg_write(UC_X86_REG_RDX, DATA + 0x100)
    mu.reg_write(UC_X86_REG_R8, DATA + 0x140)
    mu.reg_write(UC_X86_REG_R9, DATA + 0x180)
    mu.emu_start(base + RVA, RET)
    codes = struct.unpack("<16I", mu.mem_read(DATA, 0x40))
    for i in range(16):
        out.append({"x": axes[0][i], "y": axes[1][i], "z": axes[2][i], "code": codes[i]})
keys = []
for case in range(cases // 64):
    n = rng.randrange(1, 70)
    wide = case % 2 == 0
    boxes = []
    for _ in range(n):
        box = []
        for _ in range(3):
            if wide:
                a, b = rng.randrange(-32768, 32768), rng.randrange(-32768, 32768)
            else:
                a = rng.randrange(-40, 40) + rng.choice([-32768, -1, 0, 32700])
                b = rng.randrange(-40, 40) + rng.choice([-32768, -1, 0, 32700])
            a, b = max(-32768, min(32767, a)), max(-32768, min(32767, b))
            box += [min(a, b), max(a, b)]
        boxes.append(box)
    blocks = (n + 15) // 16
    data = bytearray(blocks * 0xc0)
    for i, box in enumerate(boxes):
        for k, v in enumerate(box):
            struct.pack_into("<h", data, (i >> 4) * 0xc0 + k * 0x20 + (i & 15) * 2, v)
    for i in range(n, blocks * 16):
        for k in range(6):
            struct.pack_into("<H", data, (i >> 4) * 0xc0 + k * 0x20 + (i & 15) * 2, 0x7fff if k % 2 == 0 else 0x8000)
    mu.mem_write(DATA + 0x1000, bytes(data))
    bvh = bytearray(0x200)
    struct.pack_into("<i", bvh, 0, n)
    struct.pack_into("<Q", bvh, 0x160, DATA + 0x1000)
    mu.mem_write(DATA + 0x400, bytes(bvh))
    mu.mem_write(DATA + 0x8000, bytes(blocks * 16 * 8))
    sp = STACK + 0x80000
    mu.mem_write(sp, struct.pack("<Q", RET))
    mu.reg_write(UC_X86_REG_RSP, sp)
    mu.reg_write(UC_X86_REG_RCX, DATA + 0x400)
    mu.reg_write(UC_X86_REG_RDX, DATA + 0x8000)
    mu.emu_start(base + 0x47aa0, RET)
    pairs = struct.unpack("<%di" % (blocks * 32), mu.mem_read(DATA + 0x8000, blocks * 16 * 8))
    keys.append({"boxes": boxes, "pairs": [[pairs[2 * i], pairs[2 * i + 1] & 0xffffffff] for i in range(n)]})
json.dump({"morton": out, "keys": keys}, open(out_path, "w"))
print(len(out), "morton cases,", len(keys), "key cases ->", out_path)
