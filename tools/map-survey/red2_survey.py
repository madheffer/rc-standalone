import os, sys, subprocess, collections, tempfile, re, json
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from vpk import Vpk

ROOT = r'D:\Steam\steamapps\workshop\content\730'
S2C = r'D:\_cursor projects\source2-compiler\src\Source2.Compiler.Cli\bin\Release\net10.0\s2c.exe'
tmp = tempfile.mkdtemp()
deps = collections.defaultdict(collections.Counter)
resver = collections.defaultdict(collections.Counter)
inputs = collections.defaultdict(collections.Counter)
scanned = collections.Counter()

def inspect(path):
    r = subprocess.run([S2C, 'inspect', path], capture_output=True, text=True)
    if r.returncode != 0: return None
    out = {'special': [], 'inputs': [], 'resver': None}
    sec = None
    for line in r.stdout.split('\n'):
        if line.startswith('resver'): out['resver'] = line.split()[1]
        elif line.startswith('RED2 special'): sec = 'special'
        elif line.startswith('RED2 input'): sec = 'inputs'
        elif line.startswith('RERL'): sec = None
        elif sec and line.startswith('  '):
            out[sec].append(' '.join(line.split()))
    return out

for it in sorted(os.listdir(ROOT)):
    p = os.path.join(ROOT, it, it + '.vpk')
    if not os.path.exists(p): continue
    try: outer = Vpk(p)
    except Exception: continue
    for nm in [k for k in outer.entries if k.startswith('maps/') and k.endswith('.vpk')]:
        try: v = outer.nested(nm)
        except Exception: continue
        mapname = nm[5:-4]
        wanted = [f'maps/{mapname}.vmap_c', f'maps/{mapname}/world.vwrld_c', f'maps/{mapname}/world.vrman_c',
                  f'maps/{mapname}/entities/default_ents.vents_c', f'maps/{mapname}/world_visibility.vvis_c']
        wanted += [q for q in v.entries if q.startswith(f'maps/{mapname}/worldnodes/') and q.endswith('.vwnod_c')][:1]
        for q in wanted:
            if q not in v.entries: continue
            ext = q.rsplit('.', 1)[-1]
            dst = os.path.join(tmp, 'probe.' + ext)
            open(dst, 'wb').write(v.read(q))
            r = inspect(dst)
            if not r: continue
            scanned[ext] += 1
            resver[ext][r['resver']] += 1
            for d in r['special']: deps[ext][d] += 1
            for d in r['inputs']: inputs[ext][re.sub(r'crc=\d+', 'crc=*', d.split(' ')[0] if ' ' in d else d)] += 1
    outer.close()

print('scanned:', scanned.most_common())
for ext in sorted(deps):
    print(f'\n.{ext}  (resver {dict(resver[ext])})')
    for d, n in deps[ext].most_common(12): print(f'   {n:4d}x  {d}')
    for d, n in inputs[ext].most_common(4): print(f'   input {n:4d}x  {d}')
json.dump({k: v.most_common() for k, v in deps.items()}, open('red2.json', 'w'), indent=1)
