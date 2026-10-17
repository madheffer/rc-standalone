"""rcstr.py <regex> [max] [program] -- strings with referencing functions (ReVa)."""
import json, os, subprocess, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import paths
prog = sys.argv[3] if len(sys.argv) > 3 else "/resourcecompiler_20260923.dll"
args = {"programPath": prog, "regexPattern": sys.argv[1], "maxCount": int(sys.argv[2]) if len(sys.argv) > 2 else 20,
        "includeReferencingFunctions": True}
out = subprocess.run([sys.executable, paths.REVA_CALL, "get-strings", json.dumps(args)], capture_output=True, text=True).stdout
try:
    d = json.loads(out)
except Exception:
    print(out[:3000]); sys.exit()
for s in (d[1:] if isinstance(d, list) else d.get("strings", [])):
    print(s.get("address"), repr(s.get("content")), [(f["name"], f["address"]) for f in s.get("referencingFunctions", [])][:8])
