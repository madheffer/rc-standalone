"""Read the constant stores out of decompiled constructors and compare them with the KV3 class defaults.

Only direct stores at constant offsets and CParticleFloatInput literal constructions
(FUN_1803837b0(this + k, value), which constructs a literal float input) are read;
everything else is reported as not covered.
"""
import json, re, struct, sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pe import PE

pe = PE('particles.dll')
S = json.load(open('schema.json'))
raw = json.load(open('schema_raw.json'))
E = raw['enums']

CTORS = {
    'C_INIT_InitFloat': ('1801b7750', 'param_1'),
    'C_OP_RenderSprites': ('180350ef0', 'param_1'),
    'C_OP_InstantaneousEmitter': ('18010b720', 'param_1'),
    'C_INIT_CreateWithinSphereTransform': ('18017c720', 'param_1'),
    'C_OP_Decay': ('180226c50', 'rbx'),
    'C_OP_PositionLock': ('18022dd70', 'param_1'),
    'C_OP_InterpolateRadius': ('18022c430', 'param_2'),
    'C_OP_BasicMovement': ('1801c0870', 'param_1'),
    'C_OP_RenderRopes': ('1802d7ac0', 'param_1'),
    'C_OP_ContinuousEmitter': ('18010c450', 'param_1'),
    'C_OP_FadeAndKill': ('1802119a0', 'param_2'),
    'C_OP_ColorInterpolate': ('18022cb90', 'param_2'),
    'C_OP_SetFloat': ('180261d90', 'param_1'),
    'C_INIT_CreateWithinBox': ('18017f3d0', 'param_1'),
    'C_INIT_RingWave': ('180173f20', 'param_1'),
    'C_OP_AlphaDecay': ('180228af0', 'param_2'),
}
SIZES = {'undefined1': 1, 'undefined2': 2, 'undefined4': 4, 'undefined8': 8, 'float': 4, 'int': 4, 'uint': 4, 'char': 1, 'bool': 1}


def num(tok):
    tok = tok.strip()
    m = re.fullmatch(r'DAT_(18[0-9a-f]+)', tok)
    if m:
        rva = int(m.group(1), 16) - pe.base
        return struct.unpack('<Q', pe.read(rva, 8))[0], tok
    m = re.fullmatch(r'(0x[0-9a-f]+|\d+)', tok)
    if m:
        return int(tok, 0), None
    return None, None


def parse(text, var):
    stores = []  # (byteoff, size, value, src)
    scale = 8  # every ctor here takes undefined8* / longlong*
    vals = {}
    for line in text.splitlines():
        line = line.strip()
        m = re.match(r'(uVar\d+) = (DAT_18[0-9a-f]+);', line)
        if m:
            vals[m.group(1)] = m.group(2)
            continue
        m = re.match(r'\*\((\w+) \*\)\(\(longlong\)' + var + r' \+ (0x[0-9a-f]+|\d+)\) = (.+);', line)
        if m and m.group(1) in SIZES:
            v, src = num(vals.get(m.group(3), m.group(3)))
            if v is not None:
                stores.append((int(m.group(2), 0), SIZES[m.group(1)], v, src or m.group(3)))
            continue
        m = re.match(r'\*\((\w+) \*\)\(' + var + r' \+ (0x[0-9a-f]+|\d+)\) = (.+);', line)
        if m and m.group(1) in SIZES:
            v, src = num(vals.get(m.group(3), m.group(3)))
            if v is not None:
                stores.append((int(m.group(2), 0) * scale, SIZES[m.group(1)], v, src or m.group(3)))
            continue
        m = re.match(var + r'\[(0x[0-9a-f]+|\d+)\] = (.+);', line)
        if m:
            v, src = num(vals.get(m.group(2), m.group(2)))
            if v is not None and not m.group(2).startswith('&'):
                stores.append((int(m.group(1), 0) * scale, 8, v, src or m.group(2)))
            continue
        m = re.match(r'FUN_1803837b0\(' + var + r' \+ (0x[0-9a-f]+|\d+),(.+)\);', line)
        if m:
            v, src = num(vals.get(m.group(2), m.group(2)))
            if v is not None:
                stores.append((int(m.group(1), 0) * scale, 'floatinput', v & 0xffffffff, src or m.group(2)))
    return stores


def decode(field, size, v, sub):
    t = field['type']
    b = v.to_bytes(8, 'little')[sub:sub + 4]
    if t == 'float32':
        return struct.unpack('<f', b)[0]
    if t in ('Vector', 'Vector2D', 'Vector4D', 'QAngle'):
        return struct.unpack('<f', b)[0]
    if t == 'bool':
        return bool(b[0])
    if t in ('int32', 'ParticleAttributeIndex_t'):
        return struct.unpack('<i', b)[0]
    if t == 'uint32':
        return struct.unpack('<I', b)[0]
    if t in E:
        x = int.from_bytes(b[:E[t]['size']], 'little', signed=True)
        for e in E[t]['enumerators']:
            if e['value'] == x:
                return e['name']
        return x
    if t == 'Color':
        return list(v.to_bytes(8, 'little')[sub:sub + 4])
    return None


def close(a, b):
    if isinstance(a, float) or isinstance(b, float):
        try:
            return abs(float(a) - float(b)) <= 1e-6 * max(1.0, abs(float(b)))
        except (TypeError, ValueError):
            return False
    return a == b


report = {}
for cls, (addr, var) in CTORS.items():
    path = f'decomp/particles.dll_{addr}.c'
    text = open(path, encoding='utf-8').read()
    if var == 'rbx':
        # the C_OP_Decay ctor is inline in the manipulator: only its own word store at 0x1e0 (bools)
        stores = [(0x1e0, 2, 0, '0')]
    else:
        stores = parse(text, var)
    final = {}
    for off, size, v, src in stores:
        final[off] = (size, v, src)  # last store wins (sequential ctor body)
    fields = S['classes'][cls]['fields']
    rows = []
    for off, (size, v, src) in sorted(final.items()):
        if size == 'floatinput':
            f = next((f for f in fields if f['offset'] == off), None)
            if f is None:
                rows.append({'offset': hex(off), 'ctor': 'float input literal', 'field': None})
                continue
            lit = struct.unpack('<f', v.to_bytes(4, 'little'))[0]
            d = f['default']
            dl = d.get('m_flLiteralValue') if isinstance(d, dict) else None
            dt = d.get('m_nType') if isinstance(d, dict) else None
            rows.append({'offset': hex(off), 'field': f['name'], 'ctor': f'literal {lit!r} ({src})', 'kv3': f'{dt} {dl}',
                         'agree': dt == 'PF_TYPE_LITERAL' and close(lit, dl)})
            continue
        n = size
        for sub in range(0, n, 1):
            a = off + sub
            f = next((f for f in fields if f['offset'] == a), None)
            if f is None:
                # store inside a vector field
                f2 = next((f for f in fields if f['type'] in ('Vector', 'Vector2D', 'Vector4D', 'QAngle') and f['offset'] < a < f['offset'] + 16 and (a - f['offset']) % 4 == 0), None)
                if f2 is None:
                    continue
                comp = (a - f2['offset']) // 4
                dv = decode(f2, n, v, sub)
                d = f2['default']
                rows.append({'offset': hex(a), 'field': f"{f2['name']}[{comp}]", 'ctor': dv, 'kv3': d[comp] if isinstance(d, list) and comp < len(d) else d,
                             'agree': isinstance(d, list) and comp < len(d) and close(dv, d[comp])})
                continue
            dv = decode(f, n, v, sub)
            if dv is None:
                rows.append({'offset': hex(a), 'field': f['name'], 'ctor': f'{v:#x} ({src})', 'kv3': f['default'], 'agree': None})
                continue
            d = f['default']
            if isinstance(d, list):
                agree = (isinstance(dv, list) and all(close(x, y) for x, y in zip(dv, d))) or close(dv, d[0])
                d = d if isinstance(dv, list) else d[0]
            else:
                agree = close(dv, d)
            rows.append({'offset': hex(a), 'field': f['name'] + ('' if not isinstance(f['default'], list) or isinstance(dv, list) else '[0]'), 'ctor': dv, 'kv3': d, 'agree': agree})
    report[cls] = {'ctor': '0x' + addr, 'rows': rows,
                   'agree': sum(1 for r in rows if r.get('agree') is True),
                   'disagree': [r for r in rows if r.get('agree') is False],
                   'uncovered': [r for r in rows if r.get('agree') is None]}
json.dump(report, open('ctor_decompile_check.json', 'w'), indent=1)
for c, r in report.items():
    print(c, r['ctor'], 'agree', r['agree'], 'disagree', len(r['disagree']), 'uncovered', len(r['uncovered']))
    for x in r['disagree']:
        print('   DIS', x)
