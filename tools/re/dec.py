"""dec.py <dll> <hex addr> [limit] [offset] -- decompile through ReVa with every known name filled in.

<dll> is a short name (resourcecompiler, vphysics2, physicsbuilder, ...) or
a Ghidra program path. FUN_<addr> tokens in the output are replaced by the
name tools/re/names/ holds for that address (hand-kept names first, then
harvested ones, RTTI "Class::vfN" included), as "Name<FUN_addr>" so the
address stays greppable.
"""
import glob
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(__file__))
from reva_call import post, session  # noqa: E402

PROGRAMS = {
    "resourcecompiler": "/resourcecompiler_20260923.dll",
    "vphysics2": "/vphysics2_20260924.dll",
    "physicsbuilder": "/physicsbuilder_20260924.dll",
    "tier0": "/tier0_20260923.dll",
    "visbuilder": "/visbuilder_20260923.dll",
    "meshsystem": "/meshsystem_20260923.dll",
}


def names_for(program):
    stem = os.path.basename(program).lstrip("/")
    stem = os.path.splitext(stem)[0]
    here = os.path.join(os.path.dirname(__file__), "names")
    table = {}
    for path in [os.path.join(here, stem + ".json"), os.path.join(here, stem + ".manual.json")]:
        if os.path.exists(path):
            for f in json.load(open(path, encoding="utf-8"))["functions"]:
                table[f["addr"].lower()] = f["name"]
    return table


def main():
    dll = sys.argv[1]
    program = PROGRAMS.get(dll, dll if dll.startswith("/") else "/" + dll)
    addr = sys.argv[2]
    limit = int(sys.argv[3]) if len(sys.argv) > 3 else 400
    offset = int(sys.argv[4]) if len(sys.argv) > 4 else 1
    sid = session()
    res, _ = post({"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": "get-decompilation", "arguments": {
        "programPath": program, "functionNameOrAddress": "0x" + addr.lower().removeprefix("0x"),
        "limit": limit, "offset": offset, "includeIncomingReferences": False}}}, sid)
    text = res["result"]["content"][0]["text"]
    try:
        text = json.loads(text).get("decompilation") or text
    except json.JSONDecodeError:
        pass
    table = names_for(program)
    text = re.sub(r"FUN_([0-9a-f]{9,})", lambda m: f"{table[m.group(1)]}<FUN_{m.group(1)}>" if m.group(1) in table else m.group(0), text)
    sys.stdout.reconfigure(encoding="utf-8")
    print(text)


if __name__ == "__main__":
    main()
