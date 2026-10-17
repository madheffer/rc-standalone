import json, collections
inst = json.load(open('stock_instances.json'))
kd = json.load(open('kv3_defaults.json'))
FD = json.loads(kd['CParticleFloatInput']['json']); VD = json.loads(kd['CParticleVecInput']['json'])
def walk(o, out):
    if isinstance(o, dict):
        t = o.get('m_nType')
        if isinstance(t, str) and t.startswith('PF_TYPE_'): out['float'].append(o)
        elif isinstance(t, str) and t.startswith('PVEC_TYPE_'): out['vec'].append(o)
        for v in o.values(): walk(v, out)
    elif isinstance(o, list):
        for v in o: walk(v, out)
out = {'float': [], 'vec': []}
for r in inst: walk(r['obj'], out)
res = {}
for kind, D in (('float', FD), ('vec', VD)):
    objs = out[kind]
    shapes = collections.Counter(('compact' if set(o) <= {'m_nType', 'm_flLiteralValue', 'm_vLiteralValue', 'm_LiteralColor'} else 'full') + ':' + o['m_nType'] for o in objs)
    full = [o for o in objs if not set(o) <= {'m_nType', 'm_flLiteralValue', 'm_vLiteralValue', 'm_LiteralColor'}]
    sub = {}
    for k in D:
        if k == 'm_nType': continue
        vals = [json.dumps(o[k], sort_keys=True) for o in full if k in o]
        c = collections.Counter(vals)
        if not c: sub[k] = {'present': 0}; continue
        m, n = c.most_common(1)[0]
        sub[k] = {'present': len(vals), 'mode': json.loads(m), 'modeCount': n, 'modeShare': round(n/len(vals), 4),
                  'kv3Default': D[k], 'modeEqualsDefault': json.loads(m) == D[k]}
    res[kind] = {'total': len(objs), 'shapes': dict(shapes.most_common()), 'fullFormCount': len(full), 'subfields': sub}
    print(kind, len(objs), 'full', len(full), shapes.most_common(8))
    for k, v in sub.items():
        if v.get('present') and not v['modeEqualsDefault']: print('   MODE!=DEFAULT', k, v)
json.dump(res, open('input_stats.json', 'w'), indent=1)
