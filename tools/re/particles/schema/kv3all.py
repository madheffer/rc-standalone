import os
"""For every class with MGetKV3ClassDefaults: call it in-process and save the returned KV3 as text and JSON."""
import ctypes, json, sys
from load import setup, k32
ss, pt, iss, ISB = setup(); assert ISB(b"SchemaSystem_001", iss)
t0 = k32.LoadLibraryW(os.path.join(os.path.join(os.environ.get("CS2_GAME_DIR", r"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game"), "bin", "win64"), "tier0.dll"))
P = ctypes.c_void_p
SaveText = ctypes.CFUNCTYPE(ctypes.c_bool, P, P, P, ctypes.c_uint)(k32.GetProcAddress(t0, b"?SaveKV3Text_NoHeader@@YA_NPEBVKeyValues3@@PEAVCUtlString@@1I@Z"))
SaveJson = ctypes.CFUNCTYPE(ctypes.c_bool, P, P, P)(k32.GetProcAddress(t0, b"?SaveKV3AsJSON@@YA_NPEBVKeyValues3@@PEAVCUtlString@@1@Z"))
def q(a): return ctypes.c_uint64.from_address(a).value
raw = json.load(open('schema_raw.json'))
out = {}
for cls, info in raw['classes'].items():
    a = pt + int(info['rva'], 16)
    nsm = ctypes.c_int16.from_address(a + 0x26).value; smp = q(a + 0x48)
    for j in range(nsm):
        if ctypes.string_at(q(smp + j*16)) != b'MGetKV3ClassDefaults': continue
        fn = q(q(smp + j*16 + 8))
        try:
            ret = ctypes.CFUNCTYPE(P)(fn)()
            if not ret: out[cls] = {"fnRva": hex(fn - pt), "error": "returned null"}; continue
            kv = q(ret)
            e = P(0); t = P(0); e2 = P(0); js = P(0)
            SaveText(kv, ctypes.byref(e), ctypes.byref(t), 0); SaveJson(kv, ctypes.byref(e2), ctypes.byref(js))
            out[cls] = {'fnRva': hex(fn - pt), 'text': ctypes.string_at(t.value).decode('utf-8') if t.value else None,
                        'json': ctypes.string_at(js.value).decode('utf-8') if js.value else None}
        except OSError as ex:
            out[cls] = {'fnRva': hex(fn - pt), 'error': str(ex)}
json.dump(out, open('kv3_defaults.json', 'w'), indent=1)
print(len(out), sum(1 for v in out.values() if 'error' in v))
