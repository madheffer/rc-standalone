import os
import ctypes, json, sys
from load import setup, k32
ss, pt, iss, ISB = setup(); assert ISB(b"SchemaSystem_001", iss)
t0 = k32.LoadLibraryW(os.path.join(os.path.join(os.environ.get("CS2_GAME_DIR", r"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game"), "bin", "win64"), "tier0.dll"))
P = ctypes.c_void_p
SaveText = ctypes.CFUNCTYPE(ctypes.c_bool, P, P, P, ctypes.c_uint)(k32.GetProcAddress(t0, b"?SaveKV3Text_NoHeader@@YA_NPEBVKeyValues3@@PEAVCUtlString@@1I@Z"))
def q(a): return ctypes.c_uint64.from_address(a).value
raw = json.load(open('schema_raw.json'))
def defaults(cls):
    a = pt + int(raw['classes'][cls]['rva'], 16)
    nsm = ctypes.c_int16.from_address(a + 0x26).value; smp = q(a + 0x48)
    for j in range(nsm):
        if ctypes.string_at(q(smp + j*16)) == b'MGetKV3ClassDefaults':
            fn = q(q(smp + j*16 + 8))
            kv = ctypes.CFUNCTYPE(P)(fn)()
            err = ctypes.c_void_p(0); out = ctypes.c_void_p(0)
            ok = SaveText(kv, ctypes.byref(err), ctypes.byref(out), 0)
            return hex(fn - pt), ok, ctypes.string_at(out.value).decode() if out.value else None
if __name__ == '__main__':
    for c in sys.argv[1:]:
        r = defaults(c); print(c, r[0], r[1]); print(r[2])
