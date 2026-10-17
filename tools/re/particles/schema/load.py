import os
import ctypes, os, sys
from ctypes import wintypes as wt
HERE = os.path.dirname(os.path.abspath(__file__))
GBIN = os.path.join(os.environ.get("CS2_GAME_DIR", r"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game"), "bin", "win64")
k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.SetDllDirectoryW(GBIN)
k32.LoadLibraryW.restype = ctypes.c_void_p
k32.GetProcAddress.restype = ctypes.c_void_p
k32.GetProcAddress.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
def load(name):
    h = k32.LoadLibraryW(os.path.join(HERE, name))
    if not h: raise OSError(f"{name}: {ctypes.get_last_error()}")
    return h
def setup():
    ss = load("schemasystem.dll")
    pt = load("particles.dll")
    CI = ctypes.CFUNCTYPE(ctypes.c_void_p, ctypes.c_char_p, ctypes.c_void_p)(k32.GetProcAddress(ss, b"CreateInterface"))
    iss = CI(b"SchemaSystem_001", None)
    ISB = ctypes.CFUNCTYPE(ctypes.c_bool, ctypes.c_char_p, ctypes.c_void_p)(k32.GetProcAddress(pt, b"InstallSchemaBindings"))
    return ss, pt, iss, ISB
if __name__ == "__main__":
    ss, pt, iss, ISB = setup()
    print("ss", hex(ss), "particles", hex(pt), "iss", hex(iss or 0)); sys.stdout.flush()
    r = ISB(b"SchemaSystem_001", iss)
    print("install ->", r); sys.stdout.flush()
    rec = pt + 0x59d6b0
    for k in range(0, 0x70, 8):
        print(hex(k), hex(ctypes.c_uint64.from_address(rec + k).value))
    f = pt + 0x59e700
    for k in range(0, 0x60, 8):
        print('f', hex(k), hex(ctypes.c_uint64.from_address(f + k).value))
