"""Install particles.dll schema bindings in-process and dump every class and enum record."""
import ctypes, json, sys
from load import setup
ss, pt, iss, ISB = setup(); assert ISB(b"SchemaSystem_001", iss)
def q(a): return ctypes.c_uint64.from_address(a).value
def i32(a): return ctypes.c_int32.from_address(a).value
def u8(a): return ctypes.c_uint8.from_address(a).value
def i16(a): return ctypes.c_int16.from_address(a).value
def i64(a): return ctypes.c_int64.from_address(a).value
LO, HI = pt + 0x1000, pt + 0x589000 + 0x2ad54c
def inmod(a): return LO <= a < HI
def s(a):
    if not a: return None
    try: return ctypes.string_at(a, 512).split(b'\0')[0].decode('latin1')
    except Exception: return None
def mdval(name, d):
    if not d: return None
    # string-valued metadata hold a const char* at the data pointer
    p = q(d)
    if inmod(p):
        v = s(p)
        if v is not None and v.isprintable(): return v
    if name in ('MPropertySortPriority',): return i32(d)
    return {'raw32': i32(d), 'rawf': ctypes.c_float.from_address(d).value, 'rva': hex(d - pt) if inmod(d) else None}
def metadata(p, n):
    out = []
    for j in range(n):
        nm = s(q(p + j*16)); out.append({'name': nm, 'value': mdval(nm, q(p + j*16 + 8))})
    return out
def tname(t):
    return s(q(t + 8)) if t else None
def tinfo(t):
    return {'name': tname(t), 'category': u8(t + 0x18), 'atomic': u8(t + 0x19)}
scope = None
lo, hi = pt + 0x589000, pt + 0x589000 + 0x2ad54c
buf = (ctypes.c_uint64 * ((hi - lo)//8)).from_address(lo)
recs = [lo + i*8 for i in range(len(buf)) if buf[i] == lo + i*8]
classes, enums = {}, {}
for a in recs:
    name = s(q(a + 8)); mod = s(q(a + 16))
    if inmod(q(a + 0x20)) and not inmod(q(a + 0x30)) and u8(a + 0x18) in (1, 2, 4, 8):
        n = i16(a + 0x1c)
        ep = q(a + 0x20)
        ens = [{'name': s(q(ep + j*0x20)), 'value': i64(ep + j*0x20 + 8),
                'metadata': metadata(q(ep + j*0x20 + 0x18), i32(ep + j*0x20 + 0x10))} for j in range(n)]
        enums[name] = {'rva': hex(a - pt), 'module': mod, 'size': u8(a + 0x18), 'enumerators': ens,
                       'metadata': metadata(q(a + 0x28), i16(a + 0x1e))}
        continue
    size = i32(a + 0x20); nf = i16(a + 0x24); nsm = i16(a + 0x26)
    align = u8(a + 0x28); nbase = u8(a + 0x29)
    fp = q(a + 0x30); bp = q(a + 0x38); smp = q(a + 0x48); fn = q(a + 0x68)
    fields = []
    for j in range(nf):
        e = fp + j*0x20
        t = q(e + 8)
        fields.append({'name': s(q(e)), 'type': tname(t), 'typeCategory': u8(t + 0x18) if t else None,
                       'offset': i32(e + 16), 'metadata': metadata(q(e + 24), i32(e + 20)), 'recordRva': hex(e - pt)})
    bases = [{'offset': i32(bp + j*16), 'class': s(q(q(bp + j*16 + 8) + 8))} for j in range(nbase)] if bp else []
    classes[name] = {'rva': hex(a - pt), 'module': mod, 'size': size, 'align': align, 'bases': bases,
                     'fields': fields, 'metadata': metadata(smp, nsm),
                     'manipulatorRva': hex(fn - pt) if fn and inmod(fn) else None}
json.dump({'dll': 'particles.dll', 'md5': '3f9ae2ff51c610d29884b51aaf570a83', 'schemasystem_md5': 'e2fb50a2f3b3e8fbb321b95af1ce0433',
           'classes': classes, 'enums': enums}, open('schema_raw.json', 'w'), indent=1)
print(len(classes), 'classes', len(enums), 'enums')
