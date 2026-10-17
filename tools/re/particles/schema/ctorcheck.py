"""Construct each class in place with its schema manipulator (action 4) and compare raw memory at each field offset with the KV3 defaults."""
import ctypes, json, struct
from load import setup
ss, pt, iss, ISB = setup(); assert ISB(b"SchemaSystem_001", iss)
S = json.load(open('schema.json')); raw = json.load(open('schema_raw.json'))
E = raw['enums']
MAN = ctypes.CFUNCTYPE(ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p, ctypes.c_void_p)
res = {}
def rd(buf, off, t, d):
    b = bytes(buf[off:off+64])
    if t == 'float32': return struct.unpack_from('<f', b)[0]
    if t in ('int32', 'ParticleAttributeIndex_t'): return struct.unpack_from('<i', b)[0]
    if t == 'uint32': return struct.unpack_from('<I', b)[0]
    if t == 'bool': return bool(b[0])
    if t in ('Vector', 'QAngle'): return list(struct.unpack_from('<3f', b))
    if t == 'Vector2D': return list(struct.unpack_from('<2f', b))
    if t == 'Vector4D': return list(struct.unpack_from('<4f', b))
    if t == 'Color': return list(b[:4])
    if t in E:
        sz = E[t]['size']; v = int.from_bytes(b[:sz], 'little', signed=True)
        for x in E[t]['enumerators']:
            if x['value'] == v: return x['name']
        return v
    return None
for c, info in S['classes'].items():
    if not info['manipulatorRva'] or not info['kv3DefaultsFnRva']: continue
    buf = (ctypes.c_uint8 * (info['size'] + 64))()
    ctypes.memset(buf, 0xCD, len(buf))
    try:
        MAN(pt + int(info['manipulatorRva'], 16))(4, ctypes.addressof(buf), None)
    except OSError as e:
        res[c] = {'error': str(e)}; continue
    cmp = []
    for f in info['fields']:
        v = rd(buf, f['offset'], f['type'], f['default'])
        if v is None: continue
        d = f['default']
        if isinstance(d, float) or isinstance(v, float):
            same = d is not None and abs(float(v) - float(d)) <= 1e-6 * max(1, abs(float(d)))
        elif isinstance(v, list):
            same = isinstance(d, list) and len(d) == len(v) and all(abs(float(x) - float(y)) <= 1e-6 * max(1, abs(float(y))) for x, y in zip(v, d))
        else: same = v == d
        cmp.append({'field': f['name'], 'memory': v, 'kv3': d, 'same': same})
    res[c] = {'checked': len(cmp), 'mismatch': [x for x in cmp if not x['same']]}
json.dump(res, open('ctor_memory_check.json', 'w'), indent=1)
for c, r in res.items():
    if 'error' in r or r['mismatch']: print(c, r.get('error') or r['mismatch'][:4])
print('classes', len(res), 'fields checked', sum(r.get('checked', 0) for r in res.values()))
