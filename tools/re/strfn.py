"""strfn.py <regex>... -- for each log string matching, print the functions that use it."""
import os, sys, json, subprocess
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import paths
def call(tool, args):
    out = subprocess.run([sys.executable, paths.REVA_CALL, tool, json.dumps(args)], capture_output=True, text=True, encoding="utf-8").stdout
    return json.loads(out)
for rx in sys.argv[1:]:
    d = call("get-strings", {"programPath": "/visbuilder_20260923.dll", "regexPattern": rx})
    for s in d:
        if not isinstance(s, dict) or "address" not in s: continue
        x = call("find-cross-references", {"programPath": "/visbuilder_20260923.dll", "location": s["address"], "direction": "to"})
        fns = sorted({(r["fromFunction"] or {}).get("entry", "?") + " " + ((r["fromFunction"] or {}).get("name") or "") for r in x.get("referencesTo", [])})
        print("%-70s %s" % (s["content"].strip()[:70], "; ".join(fns)))
