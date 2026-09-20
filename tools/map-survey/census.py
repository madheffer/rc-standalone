import os, sys, struct, collections, json
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from vpk import Vpk, container_blocks, kv3_facts

ROOT = r'D:\Steam\steamapps\workshop\content\730'

def read_at(v, path, off, n):
    ao, el, preload, ai = v.entries[path]
    if preload:
        head = preload[off:off+n]
        if len(head) >= n: return head
    v.fh.seek(v.base + v.data_off + ao + off)
    return v.fh.read(n)

def role(p):
    # p like maps/<name>/<group>/<file> or maps/<name>.<ext>
    parts = p.split('/')
    if len(parts) == 2: return 'map root: ' + parts[1].split('.', 1)[-1]
    if len(parts) >= 4: return parts[2]
    return parts[-1].rsplit('.', 1)[0]

bytes_by_role = collections.Counter()
count_by_role = collections.Counter()
bytes_by_ext  = collections.Counter()
count_by_ext  = collections.Counter()
blocks_by_ext = collections.defaultdict(collections.Counter)
kv3_by_ext    = collections.defaultdict(collections.Counter)
resver_by_ext = collections.defaultdict(collections.Counter)
maps_done = 0

items = sorted(os.listdir(ROOT))
for it in items:
    outer_path = os.path.join(ROOT, it, it + '.vpk')
    if not os.path.exists(outer_path): continue
    try:
        outer = Vpk(outer_path)
    except Exception:
        continue
    inner_names = [k for k in outer.entries if k.startswith('maps/') and k.endswith('.vpk')]
    for nm in inner_names:
        try:
            v = outer.nested(nm)
        except Exception as e:
            print('nested fail', it, nm, e); continue
        maps_done += 1
        for p in v.entries:
            sz = v.size(p)
            ext = p.rsplit('.', 1)[-1] if '.' in p.rsplit('/',1)[-1] else '(none)'
            bytes_by_role[role(p)] += sz; count_by_role[role(p)] += 1
            bytes_by_ext[ext] += sz;      count_by_ext[ext] += 1
            if not p.endswith('_c') or sz < 32: continue
            try:
                head = read_at(v, p, 0, 512)
                cb = container_blocks(head)
                if not cb: continue
                hv, rv, blocks = cb
                resver_by_ext[ext][rv] += 1
                blocks_by_ext[ext][' '.join(sorted(blocks))] += 1
                for fourcc in ('DATA', 'RED2', 'CTRL', 'PHYS', 'MDAT'):
                    if fourcc not in blocks: continue
                    off, bsz = blocks[fourcc]
                    if bsz < 24: 
                        kv3_by_ext[ext][(fourcc, 'empty')] += 1; continue
                    chunk = read_at(v, p, off, 24)
                    f = kv3_facts(chunk, 0)
                    kv3_by_ext[ext][(fourcc, f if f else 'not-kv3')] += 1
            except Exception as e:
                blocks_by_ext[ext]['ERR ' + type(e).__name__] += 1
    outer.close()

out = {
 'maps': maps_done,
 'bytes_by_role': bytes_by_role.most_common(),
 'count_by_role': count_by_role.most_common(),
 'bytes_by_ext': bytes_by_ext.most_common(),
 'count_by_ext': count_by_ext.most_common(),
 'blocks_by_ext': {k: v.most_common(6) for k, v in blocks_by_ext.items()},
 'kv3_by_ext': {k: [[str(a), b] for a, b in v.most_common(8)] for k, v in kv3_by_ext.items()},
 'resver_by_ext': {k: v.most_common(5) for k, v in resver_by_ext.items()},
}
json.dump(out, open('census.json','w'), indent=1)
print('maps scanned:', maps_done)
tot = sum(bytes_by_role.values())
print(f'total inner bytes: {tot/1e9:.2f} GB')
print('\n-- bytes by role --')
for k, b in bytes_by_role.most_common(20):
    print(f'{b/1e6:10.1f} MB  {100*b/tot:5.1f}%  {count_by_role[k]:7d} files  {k}')
print('\n-- bytes by extension --')
for k, b in bytes_by_ext.most_common(15):
    print(f'{b/1e6:10.1f} MB  {100*b/tot:5.1f}%  {count_by_ext[k]:7d} files  .{k}')
