"""emu_cube_views.py [--dll client.dll] [--near N --far F]: the game's cubemap face views, from client.dll itself.

BuildCubemaps (client 10-15/10-21 18020e130) sets up each face's view at 18020e5bc .. 18020e728: the record
angles (record floats 3..5) and the face angles (the six-entry table at 181ad10d0, 0x28 bytes each) through
AngleQuaternion (1817445e0), their product (181745900, record first), QuaternionMatrix + MatrixAngles
(1817463a0 -> 1817439d0, so the face angles come back through V_atan2f and are no longer exact), then a view
object (constructor 18174e140) gets the record origin (floats 0..2) and those angles (18174fd60), FOV 90, near
and far (record floats 6, 7) and aspect 1 (each change rebuilding the projection, 18174fe70), and 18174f330.

The same calls are emulated here with unicorn on the client.dll image; its tier0 imports (V_sincosf,
V_atan2f, V_tanf) are answered by the installed tier0.dll through ctypes. No game process is involved.
Prints each face's view object as float rows.
"""
import argparse
import ctypes
import os
import struct

import pefile
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE
from unicorn.x86_const import (UC_X86_REG_RSP, UC_X86_REG_RIP, UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8,
                               UC_X86_REG_XMM0, UC_X86_REG_XMM1, UC_X86_REG_GS_BASE)

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
CLIENT = os.path.join(CS2, "game", "csgo", "bin", "win64", "client.dll")

ANGLE_QUATERNION = 0x1817445e0
QUATERNION_MULT = 0x181745900
QUATERNION_ANGLES = 0x1817463a0
VIEW_CONSTRUCT = 0x18174e140
VIEW_TEMPLATE_INIT, VIEW_TEMPLATE = 0x18174dd60, 0x1827274b0   # the constructor's static default view
VIEW_SET_ORIGIN_ANGLES = 0x18174fd60
VIEW_PROJECTION = 0x18174fe70
VIEW_FINISH = 0x18174f330
FACE_TABLE = 0x181ad10d0
VIEW_SIZE = 0x1000

STUB, HEAP, STACK, TEB, RET = 0x10000000, 0x20000000, 0x30000000, 0x40000000, 0x50000000


def fbits(x):
    return struct.unpack("<I", struct.pack("<f", x))[0]


def bitsf(u):
    return struct.unpack("<f", struct.pack("<I", u & 0xFFFFFFFF))[0]


def f32(x):
    return struct.unpack("<f", struct.pack("<f", x))[0]


class Emulator:
    def __init__(self, dll):
        os.add_dll_directory(BIN)
        t0 = ctypes.CDLL(os.path.join(BIN, "tier0.dll"))
        self.sincos = t0.V_sincosf
        self.sincos.restype = None
        self.sincos.argtypes = [ctypes.c_float, ctypes.POINTER(ctypes.c_float), ctypes.POINTER(ctypes.c_float)]
        self.atan2 = t0.V_atan2f
        self.atan2.restype = ctypes.c_float
        self.atan2.argtypes = [ctypes.c_float, ctypes.c_float]
        self.tan = t0.V_tanf
        self.tan.restype = ctypes.c_float
        self.tan.argtypes = [ctypes.c_float]
        pe = pefile.PE(dll)
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
            for imp in entry.imports:
                stub = STUB + len(self.imports) * 16
                self.imports[stub] = (entry.dll.decode(), imp.name.decode() if imp.name else str(imp.ordinal))
                mu.mem_write(imp.address, struct.pack("<Q", stub))
        mu.mem_write(TEB + 0x58, struct.pack("<Q", TEB + 0x1000))
        for i in range(64):
            mu.mem_write(TEB + 0x1000 + i * 8, struct.pack("<Q", TEB + 0x4000))
        mu.reg_write(UC_X86_REG_GS_BASE, TEB)
        mu.hook_add(UC_HOOK_CODE, self._hook, begin=STUB, end=STUB + 0x100000)
        self.mu = mu
        self.calls = {}
        # The constructor copies a static default built once behind a thread-safe static guard (which the
        # emulated TLS epoch skips); build it the way the guarded call does.
        self.call(VIEW_TEMPLATE_INIT, rcx=VIEW_TEMPLATE, rdx=0)

    def _ret(self, uc):
        rsp = uc.reg_read(UC_X86_REG_RSP)
        uc.reg_write(UC_X86_REG_RIP, struct.unpack("<Q", uc.mem_read(rsp, 8))[0])
        uc.reg_write(UC_X86_REG_RSP, rsp + 8)

    def _hook(self, uc, address, size, user):
        dll, name = self.imports.get(address, ("?", "?"))
        x0 = bitsf(uc.reg_read(UC_X86_REG_XMM0))
        if name == "V_sincosf":
            s, c = ctypes.c_float(), ctypes.c_float()
            self.sincos(x0, ctypes.byref(s), ctypes.byref(c))
            uc.mem_write(uc.reg_read(UC_X86_REG_RDX), struct.pack("<f", s.value))
            uc.mem_write(uc.reg_read(UC_X86_REG_R8), struct.pack("<f", c.value))
        elif name == "V_atan2f":
            uc.reg_write(UC_X86_REG_XMM0, fbits(self.atan2(x0, bitsf(uc.reg_read(UC_X86_REG_XMM1)))))
        elif name == "V_tanf":
            uc.reg_write(UC_X86_REG_XMM0, fbits(self.tan(x0)))
        else:
            raise RuntimeError(f"unexpected import {dll}!{name}")
        self.calls[name] = self.calls.get(name, 0) + 1
        self._ret(uc)

    def call(self, fn, rcx=0, rdx=0, r8=0, xmm0=None, xmm1=None):
        mu = self.mu
        rsp = STACK + 0x80000
        mu.mem_write(rsp, struct.pack("<Q", RET))
        mu.reg_write(UC_X86_REG_RSP, rsp)
        mu.reg_write(UC_X86_REG_RCX, rcx)
        mu.reg_write(UC_X86_REG_RDX, rdx)
        mu.reg_write(UC_X86_REG_R8, r8)
        if xmm0 is not None:
            mu.reg_write(UC_X86_REG_XMM0, xmm0)
        if xmm1 is not None:
            mu.reg_write(UC_X86_REG_XMM1, xmm1)
        mu.emu_start(fn, RET)
        return mu.reg_read(UC_X86_REG_XMM0)

    def face_view(self, origin, angles, near, far, face):
        """The view object after 18020e5bc .. 18020e728 for one face (bytes), and the face's angles."""
        mu = self.mu
        rec, q, ang, org, view = HEAP, HEAP + 0x200, HEAP + 0x300, HEAP + 0x400, HEAP + 0x1000
        mu.mem_write(HEAP, bytes(0x4000))
        mu.mem_write(rec, struct.pack("<8f", *origin, *angles, near, far))
        q_rec = self.call(ANGLE_QUATERNION, rcx=rec + 0xc)
        q_face = self.call(ANGLE_QUATERNION, rcx=FACE_TABLE + face * 0x28)
        mu.mem_write(q, self.call(QUATERNION_MULT, xmm0=q_rec, xmm1=q_face).to_bytes(16, "little"))
        self.call(QUATERNION_ANGLES, rcx=q, rdx=ang)
        self.call(VIEW_CONSTRUCT, rcx=view)
        mu.mem_write(org, struct.pack("<3f", *origin))
        self.call(VIEW_SET_ORIGIN_ANGLES, rcx=view, rdx=org, r8=ang)

        def f(off):
            return struct.unpack("<f", mu.mem_read(view + off, 4))[0]

        if f(0x18) != 90.0:
            mu.mem_write(view + 0x18, struct.pack("<f", 90.0))
            self.call(VIEW_PROJECTION, rcx=view)
        if f(0x20) != f32(near) or f(0x24) != f32(far):
            mu.mem_write(view + 0x20, struct.pack("<2f", near, far))
            self.call(VIEW_PROJECTION, rcx=view)
        if f(0x1c) != 1.0:
            mu.mem_write(view + 0x1c, struct.pack("<f", 1.0))
            self.call(VIEW_PROJECTION, rcx=view)
        self.call(VIEW_FINISH, rcx=view)
        return bytes(mu.mem_read(view, VIEW_SIZE)), struct.unpack("<3f", mu.mem_read(ang, 12))


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--dll", default=CLIENT)
    p.add_argument("--origin", default="512,96,112")
    p.add_argument("--near", type=float, default=1.0)
    p.add_argument("--far", type=float, default=10000.0)
    a = p.parse_args()
    emu = Emulator(a.dll)
    origin = [float(v) for v in a.origin.split(",")]
    for face in range(6):
        view, ang = emu.face_view(origin, (0.0, 0.0, 0.0), a.near, a.far, face)
        print("face %d angles %r" % (face, ang))
        fl = struct.unpack("<%df" % (len(view) // 4), view)
        for r in range(0, 0x420 // 4, 4):
            if any(fl[r:r + 4]):
                print("  +%03x %s" % (r * 4, " ".join("%15.9g" % x for x in fl[r:r + 4])))
    print(emu.calls)


if __name__ == "__main__":
    main()
