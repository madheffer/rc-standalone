"""nav_link_specimen.py <probe01.vmap> <out.vmap>: the nav link specimen, a copy of probe01 with two
raised pads (copies of its first func_buyzone's box mesh, 256 x 256 x 32, dev_measuregeneric01, top at
z 272: too high to reach, so IslandRemoval drops them) and CMapNavLink nodes, which the collector
(resourcecompiler 180f203b0) takes as ai_nav_link_area entities: start = the node's world position, end
= start + its locatorOffset.
  - link0 from the floor onto pad A, at yaw 90: pad A is kept only if the offset is added unrotated
    (rotated, the end lands in the air at (60, 760, 272));
  - link1 floor to floor;
  - link2 into the air (no polygon near its end);
  - pad B has no link (removed, the control).
Built through Valve's dmxconvert as probe_map.py does."""
import os
import sys
import tempfile

import probe_map as pm

PADS = [(300, 900, 256), (800, 900, 256)]
PAD_HALF = (128, 128, 16)
# (origin, angles, locatorOffset)
LINKS = [
    ((300, 760, 0), "0 90 0", (0, 240, 272)),
    ((100, 500, 0), "0 0 0", (64, 0, 0)),
    ((600, 500, 0), "0 0 0", (0, 0, 600)),
]


def fmt(v):
    return " ".join(f"{x:g}" for x in v)


def link(k, origin, angles, offset):
    e = pm.Element("CMapNavLink")
    e.fields = [["id", "elementid", ""], ["nodeID", "int", "0"], ["referenceID", "uint64", "0x0"],
                ["children", "element_array", []], ["variableTargetKeys", "string_array", []],
                ["variableNames", "string_array", []]]
    plugs = pm.Element("DmePlugList")
    plugs.fields = [["id", "elementid", ""], ["names", "string_array", []], ["dataTypes", "int_array", []],
                    ["plugTypes", "int_array", []], ["descriptions", "string_array", []]]
    e.fields.append(["relayPlugData", "DmePlugList", plugs])
    e.fields.append(["connectionsData", "element_array", []])
    p = pm.Element("EditGameClassProps")
    p.fields = [["id", "elementid", ""], ["classname", "string", "ai_nav_link_area"], ["targetname", "string", f"link{k}"]]
    e.fields.append(["entity_properties", "EditGameClassProps", p])
    e.fields += [["hitNormal", "vector3", "0 0 1"], ["isProceduralEntity", "bool", "0"],
                 ["origin", "vector3", fmt(origin)], ["angles", "qangle", angles],
                 ["scales", "vector3", "1 1 1"], ["transformLocked", "bool", "0"],
                 ["force_hidden", "bool", "0"], ["editorOnly", "bool", "0"],
                 ["locatoroffset", "vector3", fmt(offset)]]
    return e


def main(base, out):
    work = tempfile.mkdtemp(prefix="nav_link_")
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

    zone = next(resolve(c) for c in children if resolve(c).type == "CMapEntity" and classname(resolve(c)) == "func_buyzone")
    node = max(int(e.get("nodeID")) for e in everything if e.get("nodeID") is not None) + 1
    added = []
    for origin in PADS:
        mesh = deep(zone.get("children")[0])
        mesh.set("origin", "vector3", fmt(origin))
        data = mesh.get("meshData")
        data.set("materials", "string_array", ["materials/dev/dev_measuregeneric01.vmat"])
        for s in data.get("vertexData").get("streams"):
            if s.get("name") == "position:0":
                s.set("data", "vector3_array", [" ".join(f"{float(v) / t * h:g}" for v, t, h in zip(p.split(), (224, 64, 32), PAD_HALF))
                                                for p in s.get("data")])
        added.append(mesh)
    added += [link(k, *l) for k, l in enumerate(LINKS)]
    for k, e in enumerate(added):
        for x in pm.walk(e):
            if x.get("id") is not None:
                x.set("id", "elementid", f"6c6c6c6c-0000-4000-8000-{k:04x}{id(x) & 0xffffffff:08x}")
            if x.get("nodeID") is not None:
                x.set("nodeID", "int", str(node))
                node += 1
            if x.get("referenceID") is not None:
                x.set("referenceID", "uint64", "0x%016x" % (0x6c6c000000000000 + node))
    children.extend(added)
    out_text = os.path.join(work, "out.vmap")
    open(out_text, "w", encoding="utf-8", newline="\n").write(pm.emit(tops, header))
    pm.convert(out_text, out, "binary")
    print("wrote", out)


if __name__ == "__main__":
    main(*sys.argv[1:3])
