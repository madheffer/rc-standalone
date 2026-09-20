import os, sys, struct, collections, json
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from vpk import Vpk, container_blocks
from s2id import id_for_path, rerl_entries

ROOT = r'D:\Steam\steamapps\workshop\content\730'

def read_at(v, path, off, n):
    ao, el, preload, ai = v.entries[path]
    if preload and off + n <= len(preload): return preload[off:off+n]
    v.fh.seek(v.base + v.data_off + ao + off)
    return v.fh.read(n)

ok = mism = 0
pairs = set()
mismatch_examples = []
by_ext_ok = collections.Counter(); by_ext_bad = collections.Counter()
files = 0
for it in sorted(os.listdir(ROOT)):
    p = os.path.join(ROOT, it, it + '.vpk')
    if not os.path.exists(p): continue
    try: outer = Vpk(p)
    except Exception: continue
    for nm in [k for k in outer.entries if k.startswith('maps/') and k.endswith('.vpk')]:
        try: v = outer.nested(nm)
        except Exception: continue
        for path in v.entries:
            if not path.endswith('_c'): continue
            try:
                head = read_at(v, path, 0, 256)
                cb = container_blocks(head)
                if not cb: continue
                hv, rv, blocks = cb
                if 'RERL' not in blocks: continue
                off, size = blocks['RERL']
                if size == 0 or size > 8_000_000: continue
                blob = read_at(v, path, off, size)
                files += 1
                ext = path.rsplit('.', 1)[-1]
                for rid, name in rerl_entries(blob, 0, size):
                    if not name: continue
                    if id_for_path(name) == rid:
                        ok += 1; by_ext_ok[ext] += 1
                    else:
                        mism += 1; by_ext_bad[ext] += 1
                        if len(mismatch_examples) < 12:
                            mismatch_examples.append((path, name, f'{rid:016x}', f'{id_for_path(name):016x}'))
                    pairs.add((name, rid))
            except Exception:
                pass
    outer.close()

print(f'files with RERL: {files}')
print(f'refs checked: {ok+mism}   distinct (path,id) pairs: {len(pairs)}')
print(f'id matches: {ok}   mismatches: {mism}')
print('by extension ok:', by_ext_ok.most_common())
print('by extension bad:', by_ext_bad.most_common())
for m in mismatch_examples: print('  MISMATCH', m)
json.dump({'ok': ok, 'mism': mism, 'pairs': len(pairs)}, open('rerl.json','w'))
