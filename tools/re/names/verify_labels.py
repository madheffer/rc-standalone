"""verify_labels.py <programPath> <names.txt>: each address whose function is not named as listed."""
import json, os, subprocess, sys
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import paths
prog, path = sys.argv[1], sys.argv[2]
bad = 0
for line in open(path):
    if not line.strip(): continue
    addr, name = line.split()
    r = subprocess.run([sys.executable, paths.REVA_CALL, 'get-decompilation', json.dumps({"programPath": prog, "functionNameOrAddress": "0x" + addr, "limit": 2, "offset": 1, "includeIncomingReferences": False})], capture_output=True, text=True, encoding='utf-8')
    try:
        d = json.JSONDecoder().raw_decode(r.stdout.strip())[0]
        cur = d.get('functionName')
    except Exception as e:
        cur = 'ERR ' + r.stdout[:80].replace('\n', ' ')
    if cur != name:
        bad += 1
        print(addr, name, '->', cur, flush=True)
print('mismatches', bad, flush=True)
