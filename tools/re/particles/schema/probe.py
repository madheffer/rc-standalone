import ctypes, sys
from load import setup
ss, pt, iss, ISB = setup(); ISB(b"SchemaSystem_001", iss)
def q(a): return ctypes.c_uint64.from_address(a).value
def s(a):
    try: return ctypes.string_at(a).decode('latin1') if a else None
    except Exception: return None
f = pt + 0x59e700
for i in range(17):
    e = f + i*0x20
    t = q(e+8)
    print(s(q(e)), hex(ctypes.c_int32.from_address(e+16).value), ctypes.c_int32.from_address(e+20).value, '|', s(q(t+8)), [hex(q(t+k)) for k in range(0,0x30,8)])
    md = q(e+24); n=ctypes.c_int32.from_address(e+20).value
    for j in range(n): print('    md', s(q(md+j*16)), hex(q(md+j*16+8)))
