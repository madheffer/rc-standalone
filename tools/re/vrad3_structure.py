"""vrad3_structure.py <vrad3.dll> <vtables.json> <xrefs.json>: the names vrad3's own structure gives, as make_manual rows.

  - CVrad3Command_<name>::Run: each command class's one vtable slot
    (virtual void Run(int argc, const char **argv); the base's is _purecall);
  - CVrad3Command_<name>::Register: the dynamic initializer that stores the
    vtable and calls CVrad3Command::CVrad3Command(name), with the script
    keyword it registers;
  - the lambda bodies run by CParallelLambdaJob / CLambdaJob classes: the
    job's last slot, named after the function the lambda sits in.

Prints Python tuples for make_manual.py's VRAD3 list, sorted by address.
"""
import json
import re
import sys

sys.path.insert(0, __file__.rsplit("\\", 1)[0] if "\\" in __file__ else __file__.rsplit("/", 1)[0])
import xrefs  # noqa: E402

CTOR = "180017580"


def owner(mangled):
    """The function a lambda sits in, from '?1??Run@CVrad3Command_x@@...' or '?6??Serialize@CVrad3LightmapImage@@'."""
    found = re.findall(r"\?\?(\w+)@(\w+)@@", mangled)
    return [f"{c}::{m}" for m, c in found]


def main():
    dll, vt_path, xr_path = sys.argv[1:4]
    classes = json.load(open(vt_path))["classes"]
    d, callers, referers = xrefs.load(xr_path)
    rows = {}
    # A slot shared by several job classes is the base's (a destructor, a
    # CThreadedJob default); the lambda body is the one slot only its job has.
    use = {}
    for n, vts in classes.items():
        for slot in vts[0]["slots"]:
            use[slot] = use.get(slot, 0) + 1
    for n, vts in classes.items():
        m = re.fullmatch(r"\.\?AV(CVrad3Command_\w+)@@", n)
        if m:
            cls = m.group(1)
            v = vts[0]
            rows[v["slots"][0]] = (f"{cls}::Run", "the command (virtual void Run(int argc, const char **argv))")
            for f in referers.get(v["vtable"], []):
                fn = d["functions"][f]
                if CTOR in fn["calls"]:
                    keyword = next(iter(fn["strings"].values()), "")
                    rows[f] = (f"{cls}::Register", f"dynamic initializer: CVrad3Command(\"{keyword}\")")
            continue
        if "LambdaJob" in n and "lambda_" in n:
            where = owner(n)
            lam = re.search(r"<lambda_(\w+)>", n)
            if not where or not lam:
                continue
            label = "::".join(dict.fromkeys(where)) + f"::lambda_{lam.group(1)}"
            own = [slot for slot in vts[0]["slots"] if use[slot] == 1]
            if len(own) == 1:
                rows.setdefault(own[0], (label, "lambda job body"))
    rows[CTOR] = ("CVrad3Command::CVrad3Command", "stores the name (CUtlString) and links the command into the global list")
    for a in sorted(rows, key=lambda x: int(x, 16)):
        name, note = rows[a]
        print(f'    ("{a}", "{name}", V3, {json.dumps(note)}),')
    print(len(rows), "rows", file=sys.stderr)


if __name__ == "__main__":
    main()
