"""apply_names.py <names.json> [<manual.json>] --program </path/in/ghidra> [--rtti] [--dry-run] -- write names into Ghidra.

Takes harvest_names.py output and, optionally, a hand-kept list for the same
binary ({"functions": [{"addr", "name", "module", "note"}]}), and writes
them into the ReVa-served Ghidra project:

  - a function is renamed through a primary label at its entry, which leaves
    its signature alone (set-function-prototype would lock the parameter list
    the decompiler guessed);
  - a hand-kept name always goes in and carries its module as a function tag;
  - a harvested name only replaces a default FUN_ name, so names other
    sessions set by hand stay; RTTI "Class::vfN" names only with --rtti;
  - "::" becomes "_" in Ghidra, since a label is one symbol;
  - the program is checked in at the end (kept checked out).

Needs ReVa listening on localhost:8080 (tools/re/reva_serve.py).
"""
import argparse
import concurrent.futures
import json
import os
import sys
import threading

sys.path.insert(0, os.path.dirname(__file__))
from reva_call import post, session  # noqa: E402


def call(sid, name, args):
    res, _ = post({"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": name, "arguments": args}}, sid)
    if res is None or "error" in res:
        raise RuntimeError(json.dumps(res and res.get("error")))
    parts = [c.get("text", "") for c in res["result"].get("content", [])]
    first = parts[0] if parts else ""
    return json.loads(first) if first.strip().startswith(("{", "[")) else "".join(parts)


def named(sid, program):
    """Functions that already carry a non-default name: address -> name."""
    have = {}
    start = 0
    while True:
        page = call(sid, "get-functions", {"programPath": program, "startIndex": start, "maxCount": 500, "filterDefaultNames": True})
        if isinstance(page, str):
            raise RuntimeError(f"get-functions at {start}: {page[:300]}")
        items = page.get("functions", [])
        for f in items:
            have[int(str(f["address"]), 16)] = f["name"]
        if not items or page.get("nextStartIndex") is None or page["nextStartIndex"] >= page.get("totalCount", 0):
            return have
        start = page["nextStartIndex"]


def ghidra_name(name):
    return name.replace("::", "_").replace("~", "Dtor_").replace("<", "_").replace(">", "_").replace(" ", "_")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("names")
    ap.add_argument("manual", nargs="?")
    ap.add_argument("--program", required=True)
    ap.add_argument("--rtti", action="store_true")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--threads", type=int, default=4)
    a = ap.parse_args()
    # Git Bash rewrites a leading "/" into a Windows path; take the program name either way.
    a.program = "/" + a.program.replace("\\", "/").split("/")[-1]

    sid = session()
    have = named(sid, a.program)
    print(f"{len(have)} functions already named in {a.program}")
    todo = []
    for f in json.load(open(a.names, encoding="utf-8"))["functions"]:
        addr = int(f["addr"], 16)
        if f["source"] == "rtti" and not a.rtti:
            continue
        if addr in have:
            continue
        todo.append((addr, ghidra_name(f["name"]), None))
    if a.manual:
        for f in json.load(open(a.manual, encoding="utf-8"))["functions"]:
            todo.append((int(f["addr"], 16), ghidra_name(f["name"]), f.get("module")))
    print(f"{len(todo)} to write")
    if a.dry_run:
        for t in todo[:40]:
            print(hex(t[0]), t[1], t[2] or "")
        return

    local = threading.local()

    def one(t):
        addr, name, module = t
        if not hasattr(local, "sid"):
            local.sid = session()
        s = local.sid
        call(s, "create-label", {"programPath": a.program, "addressOrSymbol": hex(addr), "labelName": name, "setAsPrimary": True})
        if module:
            call(s, "function-tags", {"programPath": a.program, "mode": "add", "function": hex(addr), "tags": [module]})
        return name

    done = failed = 0
    with concurrent.futures.ThreadPoolExecutor(a.threads) as pool:
        for fut in concurrent.futures.as_completed([pool.submit(one, t) for t in todo]):
            try:
                fut.result()
                done += 1
            except Exception as e:  # keep going; report at the end
                failed += 1
                if failed <= 5:
                    print("failed:", e)
            if (done + failed) % 1000 == 0:
                print(done + failed, "of", len(todo), flush=True)
    print(f"written {done}, failed {failed}")
    print(call(sid, "checkin-program", {"programPath": a.program, "message": f"names from {os.path.basename(a.names)}", "keepCheckedOut": True}))


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
