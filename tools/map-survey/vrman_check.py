import os, sys, struct, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from vpk import Vpk, container_blocks

ROOT = r'D:\Steam\steamapps\workshop\content\730'
ok = bad = 0; lists = collections.Counter(); sizes = []
ent_blocks = collections.Counter(); ent_sample = []
for it in sorted(os.listdir(ROOT)):
    p = os.path.join(ROOT, it, it + '.vpk')
    if not os.path.exists(p): continue
    try: outer = Vpk(p)
    except Exception: continue
    for nm in [k for k in outer.entries if k.startswith('maps/') and k.endswith('.vpk')]:
        try: v = outer.nested(nm)
        except Exception: continue
        for path in v.entries:
            if path.endswith('.vrman_c'):
                try:
                    buf = v.read(path)
                    hv, rv, bl = container_blocks(buf)
                    off, size = bl['DATA']
                    rel, cnt = struct.unpack_from('<iI', buf, off)
                    outer_at = off + rel
                    total = 0
                    for i in range(cnt):
                        irel, icnt = struct.unpack_from('<iI', buf, outer_at + i*8)
                        iat = outer_at + i*8 + irel
                        for j in range(icnt):
                            sp = iat + j*4
                            srel, = struct.unpack_from('<i', buf, sp)
                            s = buf[sp+srel: buf.index(b'\0', sp+srel)].decode('utf-8')
                            assert 0 < len(s) < 300 and off <= sp+srel < off+size, s
                            total += 1
                    ok += 1; lists[(cnt, total)] += 1; sizes.append(size)
                except Exception as e:
                    bad += 1
                    if bad < 4: print('vrman FAIL', path, type(e).__name__, e)
            elif path.endswith('.vmdl_c') and '/entities/' in path:
                try:
                    head = v.read(path)[:512] if v.size(path) < 512 else None
                    ao, el, pre, ai = v.entries[path]
                    v.fh.seek(v.base + v.data_off + ao); head = v.fh.read(256)
                    cb = container_blocks(head)
                    if cb:
                        ent_blocks[' '.join(sorted(cb[2]))] += 1
                        if len(ent_sample) < 3 and 'MBUF' not in cb[2]: ent_sample.append((path, v.size(path), cb[2]))
                except Exception: pass
    outer.close()
print(f'vrman parsed with the 3-level self-relative layout: {ok} ok, {bad} failed')
print('  (outer count, total strings) histogram:', lists.most_common(8))
print(f'  DATA sizes: min {min(sizes)} max {max(sizes)} mean {sum(sizes)//len(sizes)}')
print('\nentities/*.vmdl_c block layouts:', ent_blocks.most_common())
for s in ent_sample: print('   sample', s[0].split("/")[-1], s[1], 'B', {k:(v[1]) for k,v in s[2].items()})
