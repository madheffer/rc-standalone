"""revaq.py <command> <program> ... -- the ReVa calls used most, on a Ghidra program.

    revaq.py dec <program> <hex addr> [limit] [offset]   decompile (default 400 lines from 1)
    revaq.py str <program> <regex> [max]                  strings with referencing functions
    revaq.py xref <program> <hex addr> [to|from]          references

<program> is an alias from paths.ALIASES (rc, vis, ms, ms1001, pb, pv, sp, t0, mt, navs,
vrad3) or a Ghidra program name (resourcecompiler_20260923.dll). The ReVa
server must be up (reva_serve.py).
"""
import json
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import paths  # noqa: E402


def call(tool, args):
    r = subprocess.run([sys.executable, paths.REVA_CALL, tool, json.dumps(args)], capture_output=True, text=True, encoding="utf-8")
    return r.stdout


def main():
    if len(sys.argv) < 4:
        sys.exit(__doc__)
    cmd, prog = sys.argv[1], "/" + paths.ALIASES.get(sys.argv[2], sys.argv[2]).lstrip("/")
    rest = sys.argv[3:]
    if cmd == "dec":
        out = call("get-decompilation", {"programPath": prog, "functionNameOrAddress": "0x" + rest[0].removeprefix("0x"),
                                         "limit": int(rest[1]) if len(rest) > 1 else 400, "offset": int(rest[2]) if len(rest) > 2 else 1,
                                         "includeIncomingReferences": False})
        try:
            d = json.loads(out)
            print(d.get("decompilation") or d)
        except ValueError:
            print(out)
    elif cmd == "str":
        out = call("get-strings", {"programPath": prog, "regexPattern": rest[0], "maxCount": int(rest[1]) if len(rest) > 1 else 20,
                                   "includeReferencingFunctions": True})
        try:
            d = json.loads(out)
            for s in (d[1:] if isinstance(d, list) else d.get("strings", [])):
                print(s.get("address"), repr(s.get("content") or s.get("value")),
                      [(f.get("name"), f.get("address")) for f in s.get("referencingFunctions", [])][:8])
        except ValueError:
            print(out[:3000])
    elif cmd == "xref":
        out = call("find-cross-references", {"programPath": prog, "location": "0x" + rest[0].removeprefix("0x"),
                                             "direction": rest[1] if len(rest) > 1 else "to", "limit": 60})
        try:
            d = json.loads(out)
            for r in d.get("referencesTo", []) + d.get("referencesFrom", []):
                print(r.get("fromAddress"), "->", r.get("toAddress"), r.get("referenceType"))
        except ValueError:
            print(out[:3000])
    else:
        sys.exit(__doc__)


if __name__ == "__main__":
    main()
