"""class_refs.py <fgd_classes.json> <out.json>: which compile-side DLLs name each entity class as a
string (whole, NUL-delimited). A class a builder names is one it treats specially; the rest only go
into the entity lump as their keys."""
import json, os, re, sys

BIN = r"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64"
DLLS = ["resourcecompiler.dll", "physicsbuilder.dll", "visbuilder.dll", "vrad3.dll", "worldrenderer.dll",
        "meshsystem.dll", "navsystem.dll", "animationsystem.dll", "resourcesystem.dll", "materialsystem2.dll",
        "scenesystem.dll", "soundsystem.dll", "particles.dll", "vphysics2.dll", "hammer.dll", "tools/hammer.dll"]

classes = json.load(open(sys.argv[1]))
names = [n for n, c in classes.items() if c["kind"] not in ("BaseClass", "ModelGameData", "ModelAnimEvent", "ModelBreakCommand", "VData", "struct", "Struct")]
refs = {n: [] for n in names}
for dll in DLLS:
    p = os.path.join(BIN, dll)
    if not os.path.exists(p):
        continue
    data = open(p, "rb").read()
    strings = set(m.group(1).decode() for m in re.finditer(rb'\x00([a-z][a-z0-9_]{2,63})\x00', data))
    for n in names:
        if n in strings:
            refs[n].append(dll)
json.dump(refs, open(sys.argv[2], "w"), indent=1)
named = {n: r for n, r in refs.items() if r}
print(len(names), "classes;", len(named), "named by a compile DLL")
by = {}
for r in named.values():
    for d in r:
        by[d] = by.get(d, 0) + 1
print(by)
