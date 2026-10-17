import ctypes, sys
from load import setup
ss, pt, iss, ISB = setup(); ISB(b"SchemaSystem_001", iss)
def q(a): return ctypes.c_uint64.from_address(a).value
def s(a): return ctypes.string_at(a).decode('latin1')
for r in (0x5af940, 0x589e40, 0x5b13d0):
    a=pt+r; print(s(q(a+8)), [hex(q(a+k)) for k in range(0x18,0x70,8)])
    bp=q(a+0x40)
    if bp: print('  base', hex(q(bp)), s(q(q(bp+8)+8)))
