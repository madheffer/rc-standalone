"""apply_labels.py <programPath> <names.txt>: primary-label each FUN_ address, skip already named."""
import json, os, subprocess, sys
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import paths
prog, path = sys.argv[1], sys.argv[2]
def call(tool, args):
    r = subprocess.run([sys.executable, paths.REVA_CALL, tool, json.dumps(args)], capture_output=True, text=True, encoding='utf-8')
    return r.stdout
for line in open(path):
    if not line.strip(): continue
    addr, name = line.split()
    out = call('get-decompilation', {"programPath": prog, "functionNameOrAddress": "0x" + addr, "limit": 3, "offset": 1, "includeIncomingReferences": False})
    try:
        d = json.JSONDecoder().raw_decode(out.strip())[0]
    except Exception:
        print('NOFUNC', addr, name); continue
    curname = d.get('functionName', '?')
    if int(d.get('startAddress', '0x0'), 16) != int(addr, 16):
        print('NOTSTART', addr, name, d.get('startAddress')); continue
    if curname == name:
        print('same', addr, name); continue
    if not curname.startswith('FUN_'):
        print('SKIP named', addr, curname, '->', name); continue
    r = call('create-label', {"programPath": prog, "addressOrSymbol": "0x" + addr, "labelName": name, "setAsPrimary": True})
    print('ok' if '"success":true' in r else 'FAIL', addr, name)
