import json, collections, re
raw = json.load(open('schema_raw.json')); C = raw['classes']; E = raw['enums']
kd = json.load(open('kv3_defaults.json'))
defs = {k: json.loads(v['json']) for k, v in kd.items() if v.get('json')}
inst = json.load(open('stock_instances.json'))
union = [x['class'] for x in json.load(open(r'../../research/particles/_class_union.json'))]
BASES = ['CParticleFunction', 'CParticleFunctionRenderer', 'CParticleFunctionInitializer', 'CParticleFunctionOperator', 'CParticleFunctionEmitter']
INPUTS = ['CParticleInput', 'CParticleFloatInput', 'CParticleVecInput', 'CParticleCollectionFloatInput', 'CPerParticleFloatInput', 'CParticleCollectionVecInput', 'CPerParticleVecInput', 'CParticleCollectionRendererFloatInput', 'CParticleCollectionRendererVecInput']
def chain(c):
    out = [c]
    while C[out[-1]]['bases']: out.append(C[out[-1]]['bases'][0]['class'])
    return out
def flat(c):
    fs = []
    for k in reversed(chain(c)):
        for f in C[k]['fields']:
            fs.append(dict(f, declaredIn=k))
    return sorted(fs, key=lambda f: f['offset'])
def refs(t):
    return re.findall(r'[A-Za-z_][\w:]*', t or '')
want = list(dict.fromkeys(union + BASES + INPUTS))
i = 0
while i < len(want):
    for f in flat(want[i]):
        for r in refs(f['type']):
            if r in C and r not in want: want.append(r)
    i += 1
by = collections.defaultdict(list)
for r in inst: by[r['obj'].get('_class')].append(r['obj'])
def key(v): return json.dumps(v, sort_keys=True)
def md(f, n):
    for m in f['metadata']:
        if m['name'] == n: return m['value']
BD = json.load(open('base_defaults.json'))
ctorchk = json.load(open('ctor_memory_check.json'))
decchk = json.load(open('ctor_decompile_check.json'))
out = {'dll': 'particles.dll', 'md5': raw['md5'], 'schemasystem_md5': raw['schemasystem_md5'], 'tier0_md5': '450b7fcd7efcddc55039fb0e7fbf7400',
       'method': 'InstallSchemaBindings in-process; class/field records read after install; defaults = KV3 from each class MGetKV3ClassDefaults (default-constructed instance serialized by Valve code) saved with tier0 SaveKV3AsJSON',
       'stockCorpus': {'files': 2679, 'source': 'game/csgo/pak01_dir.vpk + game/core/pak01_dir.vpk, every .vpcf_c, VRF 20.0.0 -d'},
       'classes': {}, 'enums': {}}
enums_used = set()
for c in want:
    info = C[c]; D = defs.get(c)
    objs = by.get(c, [])
    fields = []
    for f in flat(c):
        n = f['name']
        st = None
        if objs:
            pres = [o[n] for o in objs if n in o]
            cnt = collections.Counter(key(v) for v in pres)
            mode, mc = cnt.most_common(1)[0] if cnt else (None, 0)
            st = {'instances': len(objs), 'present': len(pres), 'absent': len(objs) - len(pres),
                  'explicitMode': json.loads(mode) if mode else None, 'explicitModeCount': mc,
                  'explicitModeShare': round(mc / len(pres), 4) if pres else None,
                  'distinctExplicit': len(cnt)}
            if D is not None and n in D and pres:
                st['explicitEqualsDefault'] = sum(1 for v in pres if key(v) == key(D[n]))
        if f['type'] in E: enums_used.add(f['type'])
        fields.append({'name': n, 'type': f['type'], 'offset': f['offset'], 'offsetHex': hex(f['offset']), 'declaredIn': f['declaredIn'],
                       'friendlyName': md(f, 'MPropertyFriendlyName'),
                       'metadata': [m['name'] for m in f['metadata']],
                       'default': D.get(n) if D is not None else (BD[c][n]['value'] if c in BD and n in BD[c] else None),
                       'defaultEvidence': (f"MGetKV3ClassDefaults({c}) fn rva {kd[c]['fnRva']}: default-constructed {c} serialized by particles.dll" if D is not None and n in D
                                           else (f"abstract class (MGetKV3ClassDefaults returns null): value is identical in the KV3 defaults of all {BD[c][n]['classesTotal']} derived classes" if c in BD and n in BD[c] else 'not serialized in class defaults')),
                       'stats': st})
    out['classes'][c] = {'inUnion': c in union, 'recordRva': info['rva'], 'size': info['size'], 'sizeHex': hex(info['size']), 'module': info['module'],
                         'baseChain': chain(c)[1:], 'manipulatorRva': info['manipulatorRva'], 'kv3DefaultsFnRva': kd.get(c, {}).get('fnRva'),
                         'classMetadata': [m['name'] for m in info['metadata']],
                         'stockInstances': len(objs),
                         'ctorMemoryCheck': ({'fieldsChecked': ctorchk[c].get('checked'), 'mismatches': ctorchk[c].get('mismatch'), 'error': ctorchk[c].get('error')} if c in ctorchk else None),
                         'ctorDecompileCheck': ({'ctor': decchk[c]['ctor'], 'agree': decchk[c]['agree'], 'disagree': decchk[c]['disagree'], 'rows': decchk[c]['rows']} if c in decchk else None),
                         'fields': fields}
for c in list(out['classes']):
    for f in out['classes'][c]['fields']:
        for r in refs(f['type']):
            if r in E: enums_used.add(r)
for e in sorted(enums_used):
    out['enums'][e] = {'recordRva': E[e]['rva'], 'size': E[e]['size'], 'values': {x['name']: x['value'] for x in E[e]['enumerators']}}
json.dump(out, open('schema.json', 'w'), indent=1)
print(len(out['classes']), 'classes', len(out['enums']), 'enums')
print(sorted(set(want) - set(union)))
