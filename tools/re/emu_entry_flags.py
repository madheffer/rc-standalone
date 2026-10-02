"""emu_entry_flags.py <resourcecompiler dll> <cases.json> <out.json>

Runs WRB_MeshEntryFlags (rva 0x252ae0, build 0923) under unicorn once per
case and records what it writes. A case is
  {"bools": {"0xhash": 0|1, ...}, "ints": {"0xhash": n}, "skybox": "name"|null,
   "size": [w, h], "rec": {"0x36": byte, "0x37": qword, "0xb4": float,
   "0x13": byte, "name12": "text"|null}}
where an attribute absent from bools/ints is not found (the call returns 0).
Output per case: {"flags": n, "size": n, "skybox": "text"}.
Imports return 0, except GetGameInfoBool/Int, which return their default.
"""
import sys, json, struct, pefile
from unicorn import *
from unicorn.x86_const import *

dll, cases_path, out_path = sys.argv[1:4]
RVA = 0x252ae0
pe = pefile.PE(dll)
base = pe.OPTIONAL_HEADER.ImageBase
size = (pe.OPTIONAL_HEADER.SizeOfImage + 0xfff) & ~0xfff
image = bytearray(size)
image[: len(pe.header)] = pe.header
for s in pe.sections:
    data = s.get_data()
    image[s.VirtualAddress: s.VirtualAddress + len(data)] = data[: size - s.VirtualAddress]
STUB, HEAP, STACK, TEB = 0x10000000, 0x20000000, 0x30000000, 0x40000000
imports = {}
for entry in getattr(pe, 'DIRECTORY_ENTRY_IMPORT', []):
    for imp in entry.imports:
        stub = STUB + 0x80000 + len(imports) * 8
        imports[stub] = (imp.name or b'').decode(errors='replace')
        struct.pack_into('<Q', image, imp.address - base, stub)


def run(case):
    mu = Uc(UC_ARCH_X86, UC_MODE_64)
    mu.mem_map(base, size)
    mu.mem_write(base, bytes(image))
    for region in (STUB, HEAP, STACK):
        mu.mem_map(region, 0x100000)
    mu.mem_map(TEB, 0x10000)
    mu.mem_write(STUB, b'\xc3' * 0x100000)
    mu.mem_write(TEB + 0x58, struct.pack('<Q', TEB + 0x1000))
    for i in range(64):
        mu.mem_write(TEB + 0x1000 + i * 8, struct.pack('<Q', TEB + 0x4000))
    mu.mem_write(TEB + 0x4000 + 0x248, struct.pack('<I', 0x80000000))
    mu.reg_write(UC_X86_REG_GS_BASE, TEB)
    mat, vt = HEAP + 0x1000, HEAP + 0x2000
    mu.mem_write(mat, struct.pack('<Q', vt))
    for k in range(0x200):
        mu.mem_write(vt + k * 8, struct.pack('<Q', STUB + k * 8))
    listp, rec, mesh, strings = HEAP + 0x3000, HEAP + 0x4000, HEAP + 0x6000, HEAP + 0x9000
    mu.mem_write(listp, struct.pack('<Q', mat))
    mu.mem_write(listp + 0x20, struct.pack('<i', 1))
    mu.mem_write(rec, struct.pack('<Q', mesh))
    mu.mem_write(rec + 0x20, struct.pack('<Q', 0 if case.get('nomaterial') else listp))
    r = case.get('rec', {})
    mu.mem_write(rec + 0x36 * 8, bytes([r.get('0x36', 0)]))
    mu.mem_write(rec + 0x37 * 8, struct.pack('<Q', r.get('0x37', 0)))
    mu.mem_write(rec + 0xb4, struct.pack('<f', r.get('0xb4', 0.0)))
    mu.mem_write(rec + 0x13 * 8, bytes([r.get('0x13', 0)]))
    if r.get('name12') is not None:
        mu.mem_write(strings + 0x800, r['name12'].encode() + b'\0')
        mu.mem_write(rec + 0x12 * 8, struct.pack('<Q', strings + 0x800))
    bools = {int(k, 16): v for k, v in case.get('bools', {}).items()}
    ints = {int(k, 16): v for k, v in case.get('ints', {}).items()}
    sky = case.get('skybox')
    w, h = case.get('size', [0, 0])
    out = HEAP + 0x10000

    def hook(uc, address, sz, user):
        if STUB <= address < STUB + 0x1000:
            slot = address - STUB
            rdx = uc.reg_read(UC_X86_REG_RDX)
            key = uc.reg_read(UC_X86_REG_R8) & 0xffffffff
            found = 0
            if slot == 0x48 and key in bools:
                uc.mem_write(rdx, bytes([bools[key]])); found = 1
            elif slot == 0x50 and key in ints:
                uc.mem_write(rdx, struct.pack('<i', ints[key])); found = 1
            elif slot == 0x60:
                if sky is not None:
                    uc.mem_write(strings, sky.encode() + b'\0')
                    uc.mem_write(rdx, struct.pack('<Q', strings)); found = 1
                else:
                    uc.mem_write(rdx, struct.pack('<Q', 0))
            elif slot == 0xa8:
                # The first call writes the width through rdx, the second the height through r8.
                n = getattr(hook, 'n', 0)
                if n == 0:
                    uc.mem_write(rdx, struct.pack('<i', w))
                else:
                    uc.mem_write(uc.reg_read(UC_X86_REG_R8), struct.pack('<i', h))
                hook.n = n + 1
            uc.reg_write(UC_X86_REG_RAX, found)
        elif address in imports:
            name = imports[address]
            if 'GetGameInfoBool' in name or 'GetGameInfoInt' in name:
                uc.reg_write(UC_X86_REG_RAX, uc.reg_read(UC_X86_REG_RDX))
            else:
                uc.reg_write(UC_X86_REG_RAX, 0)
    hook.n = 0
    mu.hook_add(UC_HOOK_CODE, hook, begin=STUB, end=STUB + 0x100000)
    sp = STACK + 0xf0000
    mu.mem_map(0xdead0000, 0x1000)
    mu.mem_write(sp, struct.pack('<Q', 0xdead0000))
    mu.reg_write(UC_X86_REG_RSP, sp)
    mu.reg_write(UC_X86_REG_RCX, out)
    mu.reg_write(UC_X86_REG_RDX, rec)
    mu.emu_start(base + RVA, 0xdead0000, count=20_000_000)
    flags = struct.unpack('<Q', mu.mem_read(out + 0x1b0, 8))[0]
    sizes = struct.unpack('<Q', mu.mem_read(out + 0x1b8, 8))[0]
    sky_ptr = struct.unpack('<Q', mu.mem_read(out + 0x1c0, 8))[0]
    sky_text = ''
    if sky_ptr:
        try:
            raw = bytes(mu.mem_read(sky_ptr, 64))
            sky_text = raw.split(b'\0')[0].decode(errors='replace')
        except UcError:
            sky_text = '?'
    return {'flags': flags, 'size': sizes, 'skybox': sky_text}


cases = json.load(open(cases_path))
json.dump([run(c) for c in cases], open(out_path, 'w'))
print(len(cases), 'cases')
