"""fgd_classes.py <out.json>: every entity class the game's FGDs declare (core, csgo_core, csgo), with its
kind (@PointClass, @SolidClass, ...), bases, helper calls and the FGD it came from. @include order is
followed so a later definition of a class replaces an earlier one, as the FGD loader does."""
import json, os, re, sys

GAME = r"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game"
ROOTS = ["core", "csgo_core", "csgo"]
DECL = re.compile(r'^\s*@(\w+)((?:[^=@{]|\{[^}]*\})*)=\s*([A-Za-z0-9_]+)\s*(?::\s*"([^"]*)")?', re.M)

def files():
    for r in ROOTS:
        d = os.path.join(GAME, r)
        for root, dirs, names in os.walk(d):
            dirs[:] = [x for x in dirs if x != "import_scripts"]  # Source 1 import FGDs
            for n in names:
                if n.endswith(".fgd"):
                    yield os.path.join(root, n)

classes = {}
for path in files():
    text = open(path, encoding="utf-8", errors="replace").read()
    for m in DECL.finditer(text):
        kind, helpers, name, desc = m.group(1), m.group(2), m.group(3), m.group(4) or ""
        if kind.lower() in ("include", "mapsize", "materialexclusion", "autovisgroup", "overrideclass", "exclude", "version"):
            continue
        bases = re.findall(r'base\(([^)]*)\)', helpers)
        bases = [b.strip() for bs in bases for b in bs.split(",") if b.strip()]
        meta = dict(re.findall(r'(\w+)\s*=\s*("[^"]*"|\w+)', "".join(re.findall(r'\{([^}]*)\}', helpers))))
        calls = re.findall(r'(\w+)\(', re.sub(r'\{[^}]*\}', '', helpers))
        classes[name] = {"kind": kind, "bases": bases, "helpers": sorted(set(c for c in calls if c != "base")),
                         "fgd": os.path.relpath(path, GAME).replace("\\", "/"), "desc": desc[:120], "metadata": meta}
json.dump(classes, open(sys.argv[1], "w"), indent=1)
kinds = {}
for c in classes.values():
    kinds[c["kind"]] = kinds.get(c["kind"], 0) + 1
print(len(classes), "classes", kinds)
