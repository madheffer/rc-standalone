"""emu_material_queries.py <dll> <function rva hex> [answer] [byte offsets...]

Emulates one builder function with unicorn and lists the material attribute
queries it makes: a fake mesh record (rdx) whose +0x20 list holds one fake
material, whose vtable records each call (vtable offset, hash in r8d) and
answers with `answer` (0 or 1, written to *rdx and returned in al). Imports
and unknown calls return 0. Prints one line per query, in call order.
Extra args set bytes of the mesh record as off=value (hex), e.g. 1b0=1.
"""
import sys, struct, pefile
from unicorn import *
from unicorn.x86_const import *

dll, rva = sys.argv[1], int(sys.argv[2], 16)
answer = int(sys.argv[3]) if len(sys.argv) > 3 else 0
sets = [a.split('=') for a in sys.argv[4:]]
pe = pefile.PE(dll)
base = pe.OPTIONAL_HEADER.ImageBase
size = (pe.OPTIONAL_HEADER.SizeOfImage + 0xfff) & ~0xfff
mu = Uc(UC_ARCH_X86, UC_MODE_64)
mu.mem_map(base, size)
mu.mem_write(base, pe.header)
for s in pe.sections:
    data = s.get_data()
    mu.mem_write(base + s.VirtualAddress, data[: max(0, min(len(data), size - s.VirtualAddress))])
STUB, HEAP, STACK, TEB = 0x10000000, 0x20000000, 0x30000000, 0x40000000
mu.mem_map(STUB, 0x100000); mu.mem_map(HEAP, 0x100000); mu.mem_map(STACK, 0x100000); mu.mem_map(TEB, 0x10000)
mu.mem_write(STUB, b'\xc3' * 0x100000)
# Imports to stubs (ret).
for entry in getattr(pe, 'DIRECTORY_ENTRY_IMPORT', []):
    for i, imp in enumerate(entry.imports):
        mu.mem_write(imp.address, struct.pack('<Q', STUB + 0x80000 + (i % 0x1000) * 8))
# Fake TEB: gs:[0x58] -> TLS array of pointers to zeroed blocks.
mu.mem_write(TEB + 0x58, struct.pack('<Q', TEB + 0x1000))
for i in range(64):
    mu.mem_write(TEB + 0x1000 + i * 8, struct.pack('<Q', TEB + 0x4000))
mu.reg_write(UC_X86_REG_GS_BASE, TEB)
# Material: object at HEAP+0x1000, vtable at HEAP+0x2000 with slot k -> STUB + k*8.
mat, vt = HEAP + 0x1000, HEAP + 0x2000
mu.mem_write(mat, struct.pack('<Q', vt))
for k in range(0x200):
    mu.mem_write(vt + k * 8, struct.pack('<Q', STUB + k * 8))
listp, rec = HEAP + 0x3000, HEAP + 0x4000
mu.mem_write(listp, struct.pack('<Q', mat)); mu.mem_write(listp + 0x20, struct.pack('<i', 1))
mu.mem_write(rec + 0x20, struct.pack('<Q', listp))
mu.mem_write(rec, struct.pack('<Q', HEAP + 0x6000))
for off, val in sets:
    mu.mem_write(rec + int(off, 16), bytes([int(val, 16)]))
out = HEAP + 0x8000
queries = []
def hook(uc, address, size, user):
    if STUB <= address < STUB + 0x1000:
        slot = (address - STUB) // 8 * 8
        rdx, r8 = uc.reg_read(UC_X86_REG_RDX), uc.reg_read(UC_X86_REG_R8) & 0xffffffff
        queries.append((slot, r8))
        if HEAP <= rdx < HEAP + 0x100000:
            uc.mem_write(rdx, struct.pack('<I', answer))
        uc.reg_write(UC_X86_REG_RAX, answer)
    elif STUB + 0x1000 <= address < STUB + 0x100000:
        uc.reg_write(UC_X86_REG_RAX, 0)
mu.hook_add(UC_HOOK_CODE, hook, begin=STUB, end=STUB + 0x100000)
sp = STACK + 0xf0000
mu.mem_write(sp, struct.pack('<Q', 0xdead0000))
mu.mem_map(0xdead0000, 0x1000)
mu.reg_write(UC_X86_REG_RSP, sp)
mu.reg_write(UC_X86_REG_RCX, out); mu.reg_write(UC_X86_REG_RDX, rec)
try:
    mu.emu_start(base + rva, 0xdead0000, count=5_000_000)
except UcError as e:
    print('stopped:', e, hex(mu.reg_read(UC_X86_REG_RIP)))
for slot, h in queries:
    print('vf 0x%x hash 0x%08x' % (slot, h))
print('rax 0x%x' % mu.reg_read(UC_X86_REG_RAX))
