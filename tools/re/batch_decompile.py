"""batch_decompile.py <program path> <xrefs.json> <out dir> [--range lo:hi ...] [--workers N]: every function's decompile to a file.

Through the ReVa server (tools/re/reva_call.py's HTTP endpoint), one file per
function (<out>/<addr>.c), with up to three tries each since the decompiler's
10 s limit trips on a loaded machine. Functions already written are skipped,
so a stopped run resumes. Functions come from the xrefs.py index.

    python tools/re/batch_decompile.py /vrad3_20261001.dll %TEMP%/vrad3re/xrefs.json %TEMP%/vrad3re/dec --range 180001000:180090000
"""
import argparse
import json
import os
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import paths  # noqa: E402
REVA_CALL = paths.REVA_CALL


def decompile(program, addr):
    args = json.dumps({"programPath": program, "functionNameOrAddress": "0x" + addr, "limit": 4000, "offset": 1,
                       "includeIncomingReferences": False})
    for _ in range(3):
        r = subprocess.run([sys.executable, REVA_CALL, "get-decompilation", args], capture_output=True, text=True)
        try:
            d = json.loads(r.stdout)
            text = d.get("decompilation")
            if text and "timed out" not in text[:80]:
                return text
        except json.JSONDecodeError:
            pass
    return None


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("program")
    p.add_argument("xrefs")
    p.add_argument("out")
    p.add_argument("--range", action="append", default=[])
    p.add_argument("--workers", type=int, default=3)
    a = p.parse_args()
    # Git Bash turns a leading-slash argument into a Windows path; take the bare name too.
    a.program = "/" + a.program.replace("\\", "/").rsplit("/", 1)[-1]
    os.makedirs(a.out, exist_ok=True)
    funcs = sorted(json.load(open(a.xrefs))["functions"], key=lambda x: int(x, 16))
    if a.range:
        spans = [tuple(int(x, 16) for x in r.split(":")) for r in a.range]
        funcs = [f for f in funcs if any(lo <= int(f, 16) < hi for lo, hi in spans)]
    todo = [f for f in funcs if not os.path.exists(os.path.join(a.out, f + ".c"))]
    print(len(funcs), "functions,", len(todo), "to do", flush=True)
    failed = []

    def one(f):
        text = decompile(a.program, f)
        if text is None:
            failed.append(f)
            return
        with open(os.path.join(a.out, f + ".c"), "w", encoding="utf-8") as h:
            h.write(text)

    with ThreadPoolExecutor(a.workers) as pool:
        for k, _ in enumerate(pool.map(one, todo)):
            if k % 50 == 0:
                print(k, "done,", len(failed), "failed", flush=True)
    print("failed:", " ".join(failed), flush=True)


if __name__ == "__main__":
    main()
