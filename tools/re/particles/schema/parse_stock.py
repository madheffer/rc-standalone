import sys, os, json, glob
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import kv3
files = glob.glob('stock/**/*.vpcf', recursive=True)
ok = 0; bad = []
inst = []  # (file, list, index, obj)
for f in files:
    try:
        d = kv3.parse(open(f, encoding='utf-8').read())
    except Exception as e:
        bad.append((f, str(e)[:100])); continue
    ok += 1
    for lst in ('m_Emitters', 'm_Initializers', 'm_Operators', 'm_Renderers', 'm_Constraints', 'm_ForceGenerators', 'm_PreEmissionOperators'):
        for k, o in enumerate(d.get(lst, []) or []):
            if isinstance(o, dict) and '_class' in o:
                inst.append({'file': f.replace(os.sep, '/'), 'list': lst, 'i': k, 'obj': o})
    inst.append({'file': f.replace(os.sep, '/'), 'list': '<root>', 'i': 0, 'obj': {k: v for k, v in d.items() if k not in ('m_Emitters', 'm_Initializers', 'm_Operators', 'm_Renderers', 'm_Constraints', 'm_ForceGenerators', 'm_PreEmissionOperators', 'm_Children')}})
print('parsed', ok, 'bad', len(bad), bad[:5], 'instances', len(inst))
json.dump(inst, open('stock_instances.json', 'w'))
