"""emu_cubeblur_tables.py [--out file]: the GGX cubemap blur's filter tap tables, from resourcecompiler itself.

GenerateMips_GGXCubeMapBlur's layer (rc 1015 1800cafd0, 0923 1800caf90) uploads one 64 KB table per mip:
2048 float4 taps (direction xyz, source lod w), then 2048 float4 weights (x). The GGX table is built by
1800caa60 (0923 1800caa20), the cosine one inline in the layer (1015 1800cb566 .. 1800cb712). Both are
emulated here with unicorn on the installed resourcecompiler.dll; their tier0 imports (V_sincosf, V_log2f)
are answered by the installed tier0.dll through ctypes. No CS2 or resourcecompiler process is involved.

Records, little-endian: u32 kind (0 GGX, 1 cosine), i32 size, f32 roughness (0 for cosine), then the
65536-byte table. Default output: %TEMP%/cubeblur_tables.bin; GgxCubeMapBlurTests reads it.
"""
import argparse
import ctypes
import os
import struct
import sys

import capstone
import pefile
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE
from unicorn.x86_const import (UC_X86_REG_RSP, UC_X86_REG_RIP, UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8,
                               UC_X86_REG_R15, UC_X86_REG_RBP, UC_X86_REG_XMM0, UC_X86_REG_XMM2, UC_X86_REG_XMM10,
                               UC_X86_REG_XMM11, UC_X86_REG_XMM12, UC_X86_REG_XMM13, UC_X86_REG_XMM14,
                               UC_X86_REG_XMM15, UC_X86_REG_GS_BASE)

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")

# resourcecompiler 20261015 (0923 + 0x40).
TABLE = 0x1800caa60
COSINE_START, COSINE_END = 0x1800cb566, 0x1800cb712

STUB, HEAP, STACK, TEB, RET = 0x10000000, 0x20000000, 0x30000000, 0x40000000, 0x50000000


def f32(x):
    return struct.unpack("<f", struct.pack("<f", x))[0]


def fbits(x):
    return struct.unpack("<I", struct.pack("<f", x))[0]


class Emulator:
    def __init__(self, dll):
        os.add_dll_directory(BIN)
        t0 = ctypes.CDLL(os.path.join(BIN, "tier0.dll"))
        self.sincos = t0.V_sincosf
        self.sincos.restype = None
        self.sincos.argtypes = [ctypes.c_float, ctypes.POINTER(ctypes.c_float), ctypes.POINTER(ctypes.c_float)]
        self.log2 = t0.V_log2f
        self.log2.restype = ctypes.c_float
        self.log2.argtypes = [ctypes.c_float]

        pe = pefile.PE(dll)
        self.pe = pe
        base = pe.OPTIONAL_HEADER.ImageBase
        size = (pe.OPTIONAL_HEADER.SizeOfImage + 0xFFF) & ~0xFFF
        mu = Uc(UC_ARCH_X86, UC_MODE_64)
        mu.mem_map(base, size)
        mu.mem_write(base, pe.header)
        for s in pe.sections:
            data = s.get_data()
            mu.mem_write(base + s.VirtualAddress, data[: max(0, min(len(data), size - s.VirtualAddress))])
        for at, n in ((STUB, 0x100000), (HEAP, 0x200000), (STACK, 0x100000), (TEB, 0x10000), (RET, 0x1000)):
            mu.mem_map(at, n)
        mu.mem_write(STUB, b"\xcc" * 0x100000)
        mu.mem_write(RET, b"\xcc" * 0x1000)
        self.imports = {}
        for entry in getattr(pe, "DIRECTORY_ENTRY_IMPORT", []):
            for i, imp in enumerate(entry.imports):
                stub = STUB + len(self.imports) * 16
                self.imports[stub] = (entry.dll.decode(), imp.name.decode() if imp.name else str(imp.ordinal))
                mu.mem_write(imp.address, struct.pack("<Q", stub))
        mu.mem_write(TEB + 0x58, struct.pack("<Q", TEB + 0x1000))
        for i in range(64):
            mu.mem_write(TEB + 0x1000 + i * 8, struct.pack("<Q", TEB + 0x4000))
        mu.reg_write(UC_X86_REG_GS_BASE, TEB)
        # The CRT memset the table builder calls first (an AVX routine; done here instead).
        md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        call = next(md.disasm(pe.get_data(TABLE + 0x70 - base, 5), TABLE + 0x70))
        assert call.mnemonic == "call", call
        self.memset = int(call.op_str, 16)
        mu.hook_add(UC_HOOK_CODE, self._hook, begin=STUB, end=STUB + 0x100000)
        mu.hook_add(UC_HOOK_CODE, self._hook, begin=self.memset, end=self.memset)
        self.mu = mu
        self.calls = {"V_sincosf": 0, "V_log2f": 0}

    def _ret(self, uc):
        rsp = uc.reg_read(UC_X86_REG_RSP)
        uc.reg_write(UC_X86_REG_RIP, struct.unpack("<Q", uc.mem_read(rsp, 8))[0])
        uc.reg_write(UC_X86_REG_RSP, rsp + 8)

    def _xmm0_float(self, uc):
        return struct.unpack("<f", struct.pack("<I", uc.reg_read(UC_X86_REG_XMM0) & 0xFFFFFFFF))[0]

    def _hook(self, uc, address, size, user):
        if address == self.memset:
            rcx, rdx, r8 = uc.reg_read(UC_X86_REG_RCX), uc.reg_read(UC_X86_REG_RDX), uc.reg_read(UC_X86_REG_R8)
            uc.mem_write(rcx, bytes([rdx & 0xFF]) * r8)
            self._ret(uc)
        elif STUB <= address < STUB + 0x100000:
            dll, name = self.imports.get(address, ("?", "?"))
            if name == "V_sincosf":
                s, c = ctypes.c_float(), ctypes.c_float()
                self.sincos(self._xmm0_float(uc), ctypes.byref(s), ctypes.byref(c))
                uc.mem_write(uc.reg_read(UC_X86_REG_RDX), struct.pack("<f", s.value))
                uc.mem_write(uc.reg_read(UC_X86_REG_R8), struct.pack("<f", c.value))
            elif name == "V_log2f":
                uc.reg_write(UC_X86_REG_XMM0, fbits(self.log2(self._xmm0_float(uc))))
            else:
                raise RuntimeError(f"unexpected import {dll}!{name}")
            self.calls[name] += 1
            self._ret(uc)

    def ggx(self, size, roughness):
        mu = self.mu
        buf = HEAP
        mu.mem_write(buf, b"\xab" * 0x10000)
        rsp = STACK + 0x80000
        mu.mem_write(rsp, struct.pack("<Q", RET))
        mu.reg_write(UC_X86_REG_RSP, rsp)
        mu.reg_write(UC_X86_REG_RCX, buf)
        mu.reg_write(UC_X86_REG_RDX, size)
        mu.reg_write(UC_X86_REG_XMM2, fbits(roughness))
        mu.emu_start(TABLE, RET)
        return bytes(mu.mem_read(buf, 0x10000))

    def cosine(self, size):
        mu = self.mu
        rbp = HEAP + 0x40000
        rsp = STACK + 0x80000
        mu.mem_write(rbp + 0x410, b"\xab" * 0x10000)
        mu.reg_write(UC_X86_REG_RBP, rbp)
        mu.reg_write(UC_X86_REG_RSP, rsp)
        mu.reg_write(UC_X86_REG_R15, size)
        # The layer's registers on entry to the loop (rc 0923 1800cb06a, 1800cb095, 1800cb4b4 .. 1800cb4ea).
        for reg, value in ((UC_X86_REG_XMM10, 6.2831855), (UC_X86_REG_XMM11, 0.0), (UC_X86_REG_XMM12, 1.0),
                           (UC_X86_REG_XMM13, 0.00048828125), (UC_X86_REG_XMM14, 3.1415927),
                           (UC_X86_REG_XMM15, 2048.0)):
            mu.reg_write(reg, fbits(f32(value)))
        mu.emu_start(COSINE_START, COSINE_END)
        return bytes(mu.mem_read(rbp + 0x410, 0x10000))


def roughness_cases():
    # The layer's roughness: mip / (count - 1), or mip / (count - 4) with the cosine table on the last three
    # mips, squared with flag 2 (resourcecompiler passes 3). Every denominator a cubemap can take, both forms.
    seen = []
    for denom in range(1, 16):
        for mip in range(1, denom + 1):
            r = f32(f32(mip) / f32(denom))
            for v in (r, f32(r * r)):
                if v not in seen:
                    seen.append(v)
    return seen


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--out", default=os.path.join(os.environ.get("TEMP", "."), "cubeblur_tables.bin"))
    p.add_argument("--dll", default=os.path.join(BIN, "resourcecompiler.dll"))
    a = p.parse_args()
    emu = Emulator(a.dll)
    out = bytearray()
    sizes = (16, 32, 64, 128, 256, 512, 1024)
    for size in sizes:
        out += struct.pack("<Iif", 1, size, 0.0) + emu.cosine(size)
    for size in sizes:
        for r in roughness_cases():
            out += struct.pack("<Iif", 0, size, r) + emu.ggx(size, r)
    with open(a.out, "wb") as f:
        f.write(out)
    print(a.out, len(out) // (12 + 0x10000), "tables;", emu.calls)


if __name__ == "__main__":
    main()
