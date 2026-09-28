"""probe_map.py <base.vmap> <fgd_keys.json> <fgd_classes.json> <out.vmap>: a probe map holding every placeable
FGD class twice, added to the world of a copy of <base.vmap>:
  - "<class>" with only its classname, so the compile fills every key from the FGD;
  - "<class>" with every key set to a non-default value of its type, so every key type is converted
    (String keys with a default keep it: some are numbers to the compile).
Solid classes get a copy of the base map's first brush entity mesh. The map goes through Valve's dmxconvert
to keyvalues2 and back, so the result is a binary vmap in the base's format. fgd_keys.json comes from
CoverageKeysProbe (COVERAGE_KEYS=<out.json>), fgd_classes.json from fgd_classes.py."""
import copy, json, os, random, re, subprocess, sys, tempfile, uuid

GAME = r"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game"
DMXCONVERT = os.path.join(GAME, "bin", "win64", "dmxconvert.exe")
PLACEABLE = ("PointClass", "SolidClass", "NPCClass", "KeyFrameClass", "MoveClass", "FilterClass",
             "PathNodeClass", "PathClass", "CableClass")
# Keys the entity element carries as its own fields, not in entity_properties.
ELEMENT_KEYS = {"classname", "origin", "angles", "scales"}
VALUES = {"String": "probe", "EntityName": "probe_target", "Integer": "7", "Int32": "3", "Boolean": "1",
          "Float": "1.5", "Vector": "1.5 -2 3", "Vector2": "0.5 0.25", "Angle": "10 20 30", "Color": "12 34 56",
          "Flags": "1", "Resource": ""}

# ---- keyvalues2 ----------------------------------------------------------------------------------------------
TOKEN = re.compile(r'"((?:[^"\\]|\\.)*)"|([{}\[\],])|<!--.*?-->|\s+', re.S)

def tokens(text):
    out = []
    for m in TOKEN.finditer(text):
        if m.group(1) is not None:
            out.append(("s", m.group(1)))
        elif m.group(2):
            out.append((m.group(2), None))
    return out

class Element:
    def __init__(self, type_):
        self.type = type_
        self.fields = []  # [name, type, value]; value: str, Element, or list

    def get(self, name):
        return next((f[2] for f in self.fields if f[0] == name), None)

    def set(self, name, type_, value):
        for f in self.fields:
            if f[0] == name:
                f[1], f[2] = type_, value
                return
        self.fields.append([name, type_, value])

def parse(text):
    toks, i = tokens(text), [0]
    def take(kind=None):
        t = toks[i[0]]
        i[0] += 1
        if kind and t[0] != kind:
            raise ValueError(f"expected {kind} at token {i[0]}, got {t}")
        return t
    def body(type_):
        e = Element(type_)
        take("{")
        while toks[i[0]][0] != "}":
            name = take("s")[1]
            ftype = take("s")[1]
            if ftype.endswith("_array"):
                take("[")
                items = []
                while toks[i[0]][0] != "]":
                    if toks[i[0]][0] == ",":
                        take()
                        continue
                    s = take("s")[1]
                    if ftype == "element_array" and toks[i[0]][0] == "{":
                        items.append(body(s))
                    elif ftype == "element_array":
                        items.append(("ref", take("s")[1]))
                    else:
                        items.append(s)
                take("]")
                e.fields.append([name, ftype, items])
            elif toks[i[0]][0] == "{":
                e.fields.append([name, ftype, body(ftype)])
            else:
                e.fields.append([name, ftype, take("s")[1]])
        take("}")
        return e
    tops = []
    while i[0] < len(toks):
        tops.append(body(take("s")[1]))
    return tops

def emit(tops, header):
    out = [header]
    def element(e, d):
        out.append("\t" * d + "{")
        for name, ftype, value in e.fields:
            pad = "\t" * (d + 1)
            if isinstance(value, Element):
                out.append(f'{pad}"{name}" "{ftype}"')
                element(value, d + 1)
            elif isinstance(value, list):
                out.append(f'{pad}"{name}" "{ftype}"')
                out.append(pad + "[")
                for k, item in enumerate(value):
                    comma = "," if k + 1 < len(value) else ""
                    if isinstance(item, Element):
                        out.append(f'{pad}\t"{item.type}"')
                        element(item, d + 2)
                        out[-1] += comma
                    elif isinstance(item, tuple):
                        out.append(f'{pad}\t"element" "{item[1]}"{comma}')
                    else:
                        out.append(f'{pad}\t"{item}"{comma}')
                out.append(pad + "]")
            else:
                out.append(f'{pad}"{name}" "{ftype}" "{value}"')
        out.append("\t" * d + "}")
    for e in tops:
        out.append(f'"{e.type}"')
        element(e, 0)
    return "\n".join(out) + "\n"

def walk(e):
    yield e
    for _, _, v in e.fields:
        if isinstance(v, Element):
            yield from walk(v)
        elif isinstance(v, list):
            for item in v:
                if isinstance(item, Element):
                    yield from walk(item)

# ---- probe ---------------------------------------------------------------------------------------------------
def convert(src, dst, encoding):
    r = subprocess.run([DMXCONVERT, "-i", src, "-o", dst, "-oe", encoding], capture_output=True, text=True)
    if r.returncode != 0 or not os.path.exists(dst):
        raise RuntimeError(f"dmxconvert failed: {r.stdout}{r.stderr}")

def main(base, keys_path, classes_path, out):
    keys = json.load(open(keys_path))
    fgd = json.load(open(classes_path))
    work = tempfile.mkdtemp(prefix="probe_map_")
    text_path = os.path.join(work, "base_text.vmap")
    convert(base, text_path, "keyvalues2")
    text = open(text_path, encoding="utf-8").read()
    header = text.split("\n", 1)[0]
    tops = parse(text)
    by_id = {e.get("id"): e for t in tops for e in walk(t)}
    root = next(t for t in tops if t.type == "CMapRootElement")
    world = root.get("world")
    children = world.get("children")
    everything = [e for t in tops for e in walk(t)]

    def resolve(item):
        return by_id[item[1]] if isinstance(item, tuple) else item

    # The template mesh: the first brush entity's first mesh child, and its offset from the entity.
    def classname(e):
        props = e.get("entity_properties")
        return props.get("classname") if isinstance(props, Element) else None
    brush = next(e for e in everything if e.type == "CMapEntity" and any(
        resolve(c).type == "CMapMesh" for c in (e.get("children") or [])))
    mesh = next(resolve(c) for c in brush.get("children") if resolve(c).type == "CMapMesh")
    bo = [float(x) for x in brush.get("origin").split()]
    mo = [float(x) for x in mesh.get("origin").split()]
    offset = [m - b for m, b in zip(mo, bo)]

    node = max(int(e.get("nodeID")) for e in everything if e.get("nodeID") is not None) + 1
    rng = random.Random(20260928)
    used = {e.get("referenceID") for e in everything}

    def fresh(e):
        nonlocal node
        for x in walk(e):
            if x.get("id") is not None:
                x.set("id", "elementid", str(uuid.UUID(int=rng.getrandbits(128), version=4)))
            if x.get("nodeID") is not None:
                x.set("nodeID", "int", str(node))
                node += 1
            if x.get("referenceID") is not None:
                while True:
                    ref = "0x%016x" % rng.getrandbits(64)
                    if ref not in used:
                        used.add(ref)
                        break
                x.set("referenceID", "uint64", ref)
        return e

    def entity(cls, props, origin, angles, solid):
        e = Element("CMapEntity")
        e.fields = [["id", "elementid", ""], ["nodeID", "int", "0"], ["referenceID", "uint64", "0x0"],
                    ["children", "element_array", []], ["variableTargetKeys", "string_array", []],
                    ["variableNames", "string_array", []]]
        plugs = Element("DmePlugList")
        plugs.fields = [["id", "elementid", ""], ["names", "string_array", []], ["dataTypes", "int_array", []],
                        ["plugTypes", "int_array", []], ["descriptions", "string_array", []]]
        e.fields.append(["relayPlugData", "DmePlugList", plugs])
        e.fields.append(["connectionsData", "element_array", []])
        p = Element("EditGameClassProps")
        p.fields = [["id", "elementid", ""], ["classname", "string", cls]] + [[k, "string", v] for k, v in props]
        e.fields.append(["entity_properties", "EditGameClassProps", p])
        e.fields += [["hitNormal", "vector3", "0 0 1"], ["isProceduralEntity", "bool", "0"],
                     ["origin", "vector3", " ".join(f"{x:g}" for x in origin)], ["angles", "qangle", angles],
                     ["scales", "vector3", "1 1 1"], ["transformLocked", "bool", "0"],
                     ["force_hidden", "bool", "0"], ["editorOnly", "bool", "0"]]
        if solid:
            m = copy.deepcopy(mesh)
            # Stock materials only: the probe compiles in an addon that has none of the base map's own.
            material = "materials/tools/toolstrigger.vmat" if cls.startswith("trigger_") else "materials/dev/reflectivity_30.vmat"
            for x in walk(m):
                for f in x.fields:
                    if isinstance(f[2], str) and f[2].endswith(".vmat"):
                        f[2] = material
                    elif isinstance(f[2], list):
                        f[2] = [material if isinstance(s, str) and s.endswith(".vmat") else s for s in f[2]]
            m.set("origin", "vector3", " ".join(f"{o + d:g}" for o, d in zip(origin, offset)))
            e.set("children", "element_array", [m])
        return fresh(e)

    names = sorted(n for n, c in fgd.items() if c["kind"] in PLACEABLE and n in keys and n != "worldspawn")
    added = [entity("info_target", [("targetname", "probe_target")], (0, 0, 64), "0 0 0", False)]
    for k, cls in enumerate(names):
        solid = keys[cls]["solid"]
        x, y = -2048 + (k % 32) * 128, -2048 + (k // 32) * 256
        added.append(entity(cls, [], (x, y, 64), "0 0 0", solid))
        values = []
        for key in keys[cls]["keys"]:
            if key["name"].lower() in ELEMENT_KEYS:
                continue
            value = VALUES.get(key["type"], "probe")
            # A String key the compile reads as a number (directlight) keeps its default; the rest get text.
            if key["type"] == "String" and key["default"] is not None:
                value = key["default"]
            if key["name"] == "targetname":
                value = f"probe_{cls}"
            values.append((key["name"], value))
        added.append(entity(cls, values, (x, y + 128, 64), "10 20 30", solid))
    children.extend(added)

    out_text = os.path.join(work, "probe_text.vmap")
    open(out_text, "w", encoding="utf-8", newline="\n").write(emit(tops, header))
    convert(out_text, out, "binary")
    print(f"{len(names)} classes, {len(added)} entities -> {out}")

if __name__ == "__main__":
    main(*sys.argv[1:5])
