"""nav_markup_specimen.py <probe01.vmap> <out.vmap>: the L23 nav markup specimen, a copy of probe01 with
func_nav_markup boxes over its floor (each a copy of its first func_buyzone's 448 x 128 x 64 box mesh,
scaled per axis), so every markup path of the nav build runs:
  - NONAV, rotated, over the spawn pad mesh at (944, -848): the pad's body is skipped by the extraction
    (180048b20, its bounds inside the box) and the floor under the box is clipped away (003 and 009);
  - NONAVNONSOLID (009's second tag: grown in z only);
  - navProperty_NavAttributes SPLIT and AVOID boxes (019 SplitPolysByMarkup);
  - WALKABLESEED (029 floods from it);
  - one with NavGen NONAV and NavAttributes SPLIT (one markup per entity, the first key that adds one);
  - one with two NavGen tags, "NONAV,WALKABLESEED" (a tag_list value: both registered, one markup);
  - an empty one (no markup);
  - a NONAV box holding the whole pad body at (944, -848) (the extraction leaves the body out);
  - NavAttributes "AVOID,SPLIT", and two navProperty keys the FGD does not declare (string keys:
    "FOO", and a lowercase "walkableseed" that 019 skips case-blind and 029 does not flood, strcmp).
Angles are written as Hammer keeps them, in [0, 360): resourcecompiler normalises a negative one
when it loads the node (a -30 yaw gives the matrix of 330), which the port does not model.
Built through Valve's dmxconvert as probe_map.py does."""
import copy
import os
import sys
import tempfile

import probe_map as pm

# (navProperty keys, origin, angles, half extents)
MARKUPS = [
    ([("navProperty_NavGen", "NONAV")], (944, -848, 0), "0 20 0", (160, 120, 48)),
    ([("navProperty_NavGen", "NONAVNONSOLID")], (96, 640, 0), "0 0 0", (96, 96, 32)),
    ([("navProperty_NavAttributes", "SPLIT")], (700, 900, 0), "0 45 0", (128, 64, 32)),
    ([("navProperty_NavAttributes", "AVOID")], (1100, 560, 0), "0 0 0", (96, 160, 32)),
    ([("navProperty_NavGen", "WALKABLESEED")], (300, 1200, 0), "0 0 0", (64, 64, 32)),
    ([("navProperty_NavGen", "NONAV"), ("navProperty_NavAttributes", "SPLIT")], (-100, -500, 0), "0 330 0", (80, 80, 40)),
    ([("navProperty_NavGen", "NONAV,WALKABLESEED")], (1150, 1250, 0), "0 10 0", (64, 96, 32)),
    ([], (600, -400, 0), "0 0 0", (64, 64, 32)),
    ([("navProperty_NavGen", "NONAV")], (944, -848, 0), "0 0 0", (290, 140, 48)),
    ([("navProperty_NavAttributes", "AVOID,SPLIT")], (-150, 1100, 0), "0 0 0", (64, 64, 32)),
    ([("navProperty_Custom", "FOO")], (800, 300, 0), "0 25 0", (48, 96, 32)),
    ([("navProperty_Custom2", "walkableseed")], (200, -150, 0), "0 0 0", (48, 48, 32)),
]


def main(base, out):
    work = tempfile.mkdtemp(prefix="nav_markup_")
    text_path = os.path.join(work, "base.vmap")
    pm.convert(base, text_path, "keyvalues2")
    text = open(text_path, encoding="utf-8").read()
    header = text.split("\n", 1)[0]
    tops = pm.parse(text)
    by_id = {e.get("id"): e for t in tops for e in pm.walk(t)}
    everything = [e for t in tops for e in pm.walk(t)]
    root = next(t for t in tops if t.type == "CMapRootElement")
    children = root.get("world").get("children")

    def resolve(item):
        return by_id[item[1]] if isinstance(item, tuple) else item

    def classname(e):
        props = e.get("entity_properties")
        return props.get("classname") if isinstance(props, pm.Element) else None

    template = next(resolve(c) for c in children if resolve(c).type == "CMapEntity" and classname(resolve(c)) == "func_buyzone")
    node = max(int(e.get("nodeID")) for e in everything if e.get("nodeID") is not None) + 1

    def deep(e):
        # A deep copy with every element reference resolved (the copy owns its sub-elements).
        e = resolve(e)
        c = pm.Element(e.type)
        for name, type_, value in e.fields:
            if isinstance(value, list):
                value = [deep(v) if isinstance(v, (tuple, pm.Element)) else v for v in value]
            elif isinstance(value, (tuple, pm.Element)):
                value = deep(value)
            c.fields.append([name, type_, value])
        return c

    added = []
    for k, (keys, origin, angles, half) in enumerate(MARKUPS):
        e = deep(template)
        props = e.get("entity_properties")
        props.set("classname", "string", "func_nav_markup")
        props.fields = [f for f in props.fields if f[0] in ("id", "classname")]
        props.fields.append(["targetname", "string", f"markup{k}"])
        for name, value in keys:
            props.fields.append([name, "string", value])
        o = " ".join(f"{x:g}" for x in origin)
        e.set("origin", "vector3", o)
        e.set("angles", "qangle", angles)
        mesh = e.get("children")[0]
        mesh.set("origin", "vector3", o)
        mesh.set("angles", "qangle", angles)
        data = mesh.get("meshData").get("vertexData")
        for s in data.get("streams"):
            if s.get("name") == "position:0":
                s.set("data", "vector3_array", [" ".join(f"{float(v) / t * h:g}" for v, t, h in zip(p.split(), (224, 64, 32), half))
                                                for p in s.get("data")])
        added.append(e)
    for k, e in enumerate(added):
        for x in pm.walk(e):
            if x.get("id") is not None:
                x.set("id", "elementid", f"6b6b6b6b-0000-4000-8000-{k:04x}{id(x) & 0xffffffff:08x}")
            if x.get("nodeID") is not None:
                x.set("nodeID", "int", str(node))
                node += 1
            if x.get("referenceID") is not None:
                x.set("referenceID", "uint64", "0x%016x" % (0x6b6b000000000000 + node))
    children.extend(added)
    out_text = os.path.join(work, "out.vmap")
    open(out_text, "w", encoding="utf-8", newline="\n").write(pm.emit(tops, header))
    pm.convert(out_text, out, "binary")
    print("wrote", out)


if __name__ == "__main__":
    main(*sys.argv[1:3])
