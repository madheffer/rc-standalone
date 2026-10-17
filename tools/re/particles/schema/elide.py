import json, collections, math
inst = json.load(open('stock_instances.json'))
kd = json.load(open('kv3_defaults.json'))
defs = {k: json.loads(v['json']) for k, v in kd.items() if v.get('json')}
def eq(a, b):
    if isinstance(a, (int, float)) and isinstance(b, (int, float)) and not isinstance(a, bool) and not isinstance(b, bool):
        return abs(a - b) <= 1e-6 * max(1, abs(b))
    if isinstance(a, list) and isinstance(b, list): return len(a) == len(b) and all(eq(x, y) for x, y in zip(a, b))
    if isinstance(a, dict) and isinstance(b, dict): return a.keys() == b.keys() and all(eq(a[k], b[k]) for k in a)
    return a == b
tot = present = same = 0; samefields = collections.Counter(); unknown = collections.Counter()
for r in inst:
    o = r['obj']; c = o.get('_class')
    if c not in defs: unknown[c] += 1; continue
    D = defs[c]
    for k, v in o.items():
        if k == '_class': continue
        present += 1
        if k not in D: samefields[('NOTINDEF', c, k)] += 1; continue
        if eq(v, D[k]): same += 1; samefields[(c, k)] += 1
print('present', present, 'equal-to-default', same)
print(samefields.most_common(40)); print('no defaults for', unknown.most_common(10))
def peq(a, b):
    # file value a (possibly partial dict) against default b
    if isinstance(a, dict) and isinstance(b, dict): return all(k in b and peq(a[k], b[k]) for k in a)
    return eq(a, b)
semi = collections.Counter(); dictpresent = collections.Counter()
for r in inst:
    o = r['obj']; c = o.get('_class')
    if c not in defs: continue
    D = defs[c]
    for k, v in o.items():
        if k in D and isinstance(v, dict) and isinstance(D[k], dict):
            dictpresent[k] += 1
            if peq(v, D[k]): semi[(c, k, json.dumps(v)[:120])] += 1
print('dict fields present', sum(dictpresent.values()), 'semantically default', sum(semi.values()))
print(semi.most_common(15))
