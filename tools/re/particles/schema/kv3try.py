import ctypes, json, sys
from kv3def import *
cls='C_OP_Decay'
a = pt + int(raw['classes'][cls]['rva'], 16)
smp = q(a + 0x48)
fn = q(q(smp + 8))
ret = ctypes.CFUNCTYPE(P)(fn)()
print(hex(ret-pt), [hex(q(ret+k)) for k in range(0,0x30,8)])
def save(kv):
    err = ctypes.c_void_p(0); out = ctypes.c_void_p(0)
    try:
        ok = SaveText(kv, ctypes.byref(err), ctypes.byref(out), 0)
    except OSError as e: return 'EXC '+str(e)
    return ok, (ctypes.string_at(out.value).decode() if out.value else None), (ctypes.string_at(err.value) if err.value else None)
for cand in (ret, q(ret), q(ret+8), q(ret+16), pt+0x7a5750):
    print(hex(cand), save(cand))
