import ctypes, sys
from load import setup
ss, pt, iss, ISB = setup(); ISB(b"SchemaSystem_001", iss)
def q(a): return ctypes.c_uint64.from_address(a).value
def s(a):
    try: return ctypes.string_at(a).decode('latin1') if a else None
    except Exception: return None
lo, hi = pt+0x589000, pt+0x589000+0x2ad54c
buf = (ctypes.c_uint64 * ((hi-lo)//8)).from_address(lo)
recs=[]
for i in range(len(buf)):
    a = lo+i*8
    if buf[i]==a: recs.append(a)
print(len(recs))
import collections
kinds=collections.Counter()
for a in recs[:3]+[r for r in recs if 'PF_' in (s(q(r+8)) or '') or 'Pf' in (s(q(r+8)) or '')][:3]:
    print(hex(a-pt), s(q(a+8)), s(q(a+16)), [hex(q(a+k)) for k in range(0x18,0x70,8)])
names=[s(q(r+8)) for r in recs]
print([n for n in names if n and n.endswith('_t')][:40])
for a in recs:
    n=s(q(a+8))
    if n in ('ParticleFloatType_t','ParticleFloatMapType_t','ParticleEndcapMode_t'):
        print(hex(a-pt), n, [hex(q(a+k)) for k in range(0x18,0x60,8)])
