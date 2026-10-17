import sys; sys.path.insert(0,'.')
from rtti import vtables
from xrefs import load
from disx import fstart, dis
p = load(sys.argv[1])
for cls in sys.argv[2:]:
    for off, vt in vtables(p, cls):
        if off: continue
        for ref in p.riprefs.get(vt, []):
            f = fstart(p, ref)
            print('==', cls, 'vtable', hex(vt), 'ref', hex(ref), 'fn', hex(f))
            lines = dis(p, f, maxn=400)
            for l in lines:
                if any(k in l for k in ('mov', 'call', 'lea', 'xorps')): print('   ', l)
