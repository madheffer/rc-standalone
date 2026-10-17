import json, collections
raw = json.load(open('schema_raw.json')); C = raw['classes']
kd = json.load(open('kv3_defaults.json'))
defs = {k: json.loads(v['json']) for k, v in kd.items() if v.get('json')}
def chain(c):
    out = [c]
    while C[out[-1]]['bases']: out.append(C[out[-1]]['bases'][0]['class'])
    return out
res = {}
for base in ('CParticleFunction', 'CParticleFunctionOperator', 'CParticleFunctionInitializer', 'CParticleFunctionEmitter', 'CParticleFunctionRenderer', 'CParticleFunctionPreEmission'):
    derived = [c for c in defs if base in chain(c)[1:]]
    fields = {}
    for b in reversed(chain(base)):
        for f in C[b]['fields']:
            vals = collections.Counter(json.dumps(defs[c].get(f['name']), sort_keys=True) for c in derived)
            m, n = vals.most_common(1)[0]
            fields[f['name']] = {'value': json.loads(m), 'classesAgreeing': n, 'classesTotal': len(derived),
                                 'others': {k: v for k, v in vals.items() if k != m}}
    res[base] = fields
json.dump(res, open('base_defaults.json', 'w'), indent=1)
for b, fs in res.items():
    for n, v in fs.items():
        if v['others']: print(b, n, v['classesAgreeing'], '/', v['classesTotal'], list(v['others'].items())[:3])
print('done')
