"""What a .vmap_c's RED2 records, across every installed map.

The map root is the one resource whose identity is assembled rather than stamped:
it carries the union of the identities of everything the compile produced, plus
the compile options it ran under.
"""
import os, sys, subprocess, tempfile, collections, json, re
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from vpk import Vpk

ROOT = r'D:\Steam\steamapps\workshop\content\730'
S2C = r'D:\_cursor projects\source2-compiler\src\Source2.Compiler.Cli\bin\Release\net10.0\s2c.exe'

tmp = tempfile.mkdtemp()
args = collections.Counter()      # (name, type, fingerprint, default) -> maps
argnames = collections.Counter()
special = collections.Counter()
inputs = collections.Counter()
userdata = collections.Counter()
keys = collections.Counter()
maps = 0

def blocks(text):
    """Crude KV3-text splitter: the array entries under each top-level key."""
    out = collections.defaultdict(list)
    key = None
    entry = {}
    for line in text.split('\n'):
        s = line.strip()
        m = re.match(r'^(m_\w+) =', s)
        if m and line.startswith('\t' + m.group(1)):
            key = m.group(1); continue
        m = re.match(r'^(\w+) = (.*)$', s)
        if m and key:
            entry[m.group(1)] = m.group(2).rstrip(',')
        if s == '},' and entry and key:
            out[key].append(entry); entry = {}
    return out

for it in sorted(os.listdir(ROOT)):
    p = os.path.join(ROOT, it, it + '.vpk')
    if not os.path.exists(p): continue
    try: outer = Vpk(p)
    except Exception: continue
    for nm in [k for k in outer.entries if k.startswith('maps/') and k.endswith('.vpk')]:
        try: v = outer.nested(nm)
        except Exception: continue
        name = nm[5:-4]
        q = f'maps/{name}.vmap_c'
        if q not in v.entries: continue
        dst = os.path.join(tmp, 'root.vmap_c')
        open(dst, 'wb').write(v.read(q))
        r = subprocess.run([S2C, 'decompile', dst, '--block', 'RED2'], capture_output=True, text=True)
        if r.returncode != 0: continue
        maps += 1
        for k in re.findall(r'^\t(m_\w+) =', r.stdout, re.M): keys[k] += 1
        parsed = blocks(r.stdout)
        for a in parsed['m_ArgumentDependencies']:
            args[(a.get('m_ParameterName'), a.get('m_ParameterType'), a.get('m_nFingerprint'), a.get('m_nFingerprintDefault'))] += 1
            argnames[a.get('m_ParameterName')] += 1
        for d in parsed['m_SpecialDependencies']:
            special[(d.get('m_String'), d.get('m_CompilerIdentifier'), d.get('m_nFingerprint'), d.get('m_nUserData'))] += 1
        for d in parsed['m_InputDependencies']:
            f = d.get('m_RelativeFilename', '')
            inputs[(f if not f.endswith('.vmap"') else '<the .vmap source>', d.get('m_bOptional'), d.get('m_bFileExists'))] += 1
        for line in re.findall(r'^\t\t(\w+) = (.*)$', r.stdout, re.M): userdata[line] += 1
    outer.close()

print(f'maps: {maps}')
print('\ntop-level RED2 keys:', [(k, c) for k, c in keys.most_common()])
print('\nspecial dependencies (identity, count):')
for k, c in special.most_common(): print(f'  {c:4d}  {k}')
print('\nargument dependency names:')
for k, c in argnames.most_common(): print(f'  {c:4d}  {k}')
print('\nargument dependency (name,type,fp,default):')
for k, c in args.most_common(40): print(f'  {c:4d}  {k}')
print('\ninput dependencies:')
for k, c in inputs.most_common(10): print(f'  {c:4d}  {k}')
print('\nsearchable user data / nested scalars:')
for k, c in userdata.most_common(10): print(f'  {c:4d}  {k}')
json.dump({'maps': maps, 'args': [[list(k), c] for k, c in args.most_common()]}, open('vmap_red2.json', 'w'), indent=1)
