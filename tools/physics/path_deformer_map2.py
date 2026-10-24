"""path_deformer_map2.py <s2c_deformpath.vmap> <out.vmap>: the CMapDeformerPath cases s2c_deformpath leaves open.
A copy of <s2c_deformpath.vmap> (path_deformer_map.py's output) with, built as that script builds a variant (a copy
of its first path deformer's target cable, laid on the listed points, and a deformer over it holding a copy of
its prop and mesh):
- start and end under 0.01 apart, start stored first (0.5, 0.505) and end stored first (0.3, 0.7), and an end
  under 0.01 alone (0.004): the load's order decides the clamps;
- a closed-loop target (the cable's closedLoop set; cable_static has supports_loop);
- a hidden target (force_hidden on the cable) and a target with one hidden path node;
- a group holding a path and its deformer, placed by an instance moved (-4096 0 64) and turned (0 30 0);
- a group holding only a deformer, placed by an instance moved (-4096 2048 0), whose target path stays in the
  world;
- a CMapPrefab of maps/s2c_deformpath.vmap moved (0 4096 0) and turned (0 90 0).
Valve's compile, captured with capture_deformer.py, gives the lattice each deformer fits."""
import copy, os, sys, tempfile

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "coverage"))
import probe_map as pm  # noqa: E402

# Each variant: origin, angles, size, settings in the order they are stored, path points, and what is done to
# the path ("loop", "hidden", "hidden node").
VARIANTS = [
    {"origin": (3000, 2400, -300), "angles": (0, 0, 0), "size": (128, 32, 32),
     "settings": [("start", 0.5), ("end", 0.505)],
     "path": [(2900, 2400, -300), (3100, 2500, -300), (3300, 2400, -260)]},
    {"origin": (3000, 3000, -300), "angles": (0, 15, 0), "size": (128, 32, 32),
     "settings": [("end", 0.3), ("start", 0.7)],
     "path": [(2900, 3000, -300), (3100, 3100, -280), (3300, 3000, -300), (3500, 3100, -260)]},
    {"origin": (3000, 3600, -300), "angles": (0, 0, 0), "size": (96, 24, 24),
     "settings": [("end", 0.004)],
     "path": [(2900, 3600, -300), (3300, 3700, -300)]},
    {"origin": (3000, 4200, -300), "angles": (0, 0, 0), "size": (160, 32, 32), "settings": [], "change": "loop",
     "path": [(2900, 4100, -300), (3200, 4100, -280), (3200, 4400, -300), (2900, 4400, -320)]},
    {"origin": (3000, 4800, -300), "angles": (0, 0, 0), "size": (128, 32, 32),
     "settings": [("start", 0.1), ("end", 0.9)], "change": "hidden",
     "path": [(2900, 4800, -300), (3100, 4900, -300), (3300, 4800, -280)]},
    {"origin": (3000, 5400, -300), "angles": (0, 0, 0), "size": (128, 32, 32), "settings": [], "change": "hidden node",
     "path": [(2900, 5400, -300), (3100, 5500, -300), (3300, 5400, -280)]},
]
# The instanced variant (its path and deformer in one group) and the deformer whose path stays in the world.
INSTANCED = {"origin": (3000, 6000, -300), "angles": (0, 30, 0), "size": (128, 32, 32),
             "settings": [("start", 0.15), ("end", 0.8), ("maxError", 0.2)],
             "path": [(2900, 6000, -300), (3100, 6100, -280), (3300, 6000, -300)]}
OUTSIDE = {"origin": (3000, 6600, -300), "angles": (0, 0, 0), "size": (128, 32, 32), "settings": [],
           "path": [(2900, 6600, -300), (3100, 6700, -300), (3300, 6600, -280)]}


def load(path, work, name):
    text_path = os.path.join(work, name)
    pm.convert(path, text_path, "keyvalues2")
    text = open(text_path, encoding="utf-8").read()
    return text.split("\n", 1)[0], pm.parse(text)


def vec(v):
    return " ".join(f"{x:g}" for x in v)


def main(base, out):
    work = tempfile.mkdtemp(prefix="path_deformer2_")
    header, tops = load(base, work, "base.txt")
    everything = [e for t in tops for e in pm.walk(t)]
    by_id = {e.get("id"): e for e in everything}
    world = next(t for t in tops if t.type == "CMapRootElement").get("world")
    template = next(e for e in everything if e.type == "CMapDeformerPath")
    reference = template.get("targetPath").get("targetReferenceID")
    cable = next(e for e in everything if e.type == "CMapCable" and e.get("referenceID") == reference)
    kids = [by_id.get(x[1], x) if isinstance(x, tuple) else x for x in template.get("children")]
    prop = next(c for c in kids if isinstance(c, pm.Element) and c.type == "CMapEntity")
    mesh = next(c for c in kids if isinstance(c, pm.Element) and c.type == "CMapMesh")
    node = max(int(e.get("nodeID")) for e in everything if e.get("nodeID") is not None) + 1
    serial = 0

    def fresh(e):
        nonlocal node, serial
        for x in pm.walk(e):
            if x.get("id") is not None:
                serial += 1
                x.set("id", "elementid", f"defa8000-0000-4000-8000-{serial:012x}")
            if x.get("nodeID") is not None:
                x.set("nodeID", "int", str(node))
                node += 1
            if x.get("referenceID") is not None:
                x.set("referenceID", "uint64", "0x%016x" % (0xdefa800000000000 + node))
        return e

    def element(type_, fields):
        e = pm.Element(type_)
        e.fields = [list(f) for f in fields]
        return e

    def common(origin, angles):
        return [("id", "elementid", ""), ("nodeID", "int", "0"), ("referenceID", "uint64", "0x0"),
                ("children", "element_array", []), ("variableTargetKeys", "string_array", []),
                ("variableNames", "string_array", []), ("origin", "vector3", origin), ("angles", "qangle", angles),
                ("scales", "vector3", "1 1 1"), ("transformLocked", "bool", "0"), ("force_hidden", "bool", "0"),
                ("editorOnly", "bool", "0")]

    def plugs():
        return element("DmePlugList", [("id", "elementid", ""), ("names", "string_array", []), ("dataTypes", "int_array", []),
                                       ("plugTypes", "int_array", []), ("descriptions", "string_array", [])])

    def make_path(v):
        path = copy.deepcopy(cable)
        nodes = [x for x in path.get("children") if isinstance(x, pm.Element) and x.type == "CMapPathNode"]
        others = [x for x in path.get("children") if not (isinstance(x, pm.Element) and x.type == "CMapPathNode")]
        laid = []
        for p in v["path"]:
            n = copy.deepcopy(nodes[0])
            n.set("origin", "vector3", vec(p))
            laid.append(n)
        path.set("children", "element_array", others + laid)
        path.set("origin", "vector3", vec(v["path"][0]))
        path.set("angles", "qangle", "0 0 0")
        change = v.get("change")
        if change == "loop":
            path.set("closedLoop", "bool", "1")
        elif change == "hidden":
            path.set("force_hidden", "bool", "1")
        elif change == "hidden node":
            laid[1].set("force_hidden", "bool", "1")
        return fresh(path)

    def make_deformer(v, path):
        p = fresh(copy.deepcopy(prop))
        p.set("origin", "vector3", vec(v["origin"]))
        m = fresh(copy.deepcopy(mesh))
        m.set("origin", "vector3", vec(v["origin"]))
        fields = common(vec(v["origin"]), vec(v["angles"]))
        fields[3] = ("children", "element_array", [p, m])
        fields += [("customVisGroup", "string", ""), ("randomSeed", "int", "1959997979"), ("size", "vector3", vec(v["size"]))]
        deformer = element("CMapDeformerPath", fields)
        fresh(deformer)
        target = element("DmElement", [("id", "elementid", ""), ("name", "string", "targetPath"), ("referenceName", "string", ""),
                                       ("targetReferenceID", "uint64", path.get("referenceID"))])
        fresh(target)
        deformer.fields.append(["targetPath", "DmElement", target])
        for key, value in v["settings"]:
            deformer.fields.append([key, "float", f"{value:g}"])
        return deformer

    for v in VARIANTS:
        path = make_path(v)
        world.get("children").extend([path, make_deformer(v, path)])

    path = make_path(INSTANCED)
    group = fresh(element("CMapGroup", common(vec(INSTANCED["origin"]), "0 0 0")))
    group.get("children").extend([path, make_deformer(INSTANCED, path)])
    instance = fresh(element("CMapInstance", common(vec(INSTANCED["origin"]), "0 0 0") + [
        ("relayPlugData", "DmePlugList", plugs()), ("connectionsData", "element_array", []), ("target", "element", group.get("id"))]))
    instance.set("origin", "vector3", vec(o + d for o, d in zip(INSTANCED["origin"], (-4096, 0, 64))))
    instance.set("angles", "qangle", "0 30 0")

    path = make_path(OUTSIDE)
    lone = fresh(element("CMapGroup", common(vec(OUTSIDE["origin"]), "0 0 0")))
    lone.get("children").append(make_deformer(OUTSIDE, path))
    outside = fresh(element("CMapInstance", common(vec(o + d for o, d in zip(OUTSIDE["origin"], (-4096, 2048, 0))), "0 0 0") + [
        ("relayPlugData", "DmePlugList", plugs()), ("connectionsData", "element_array", []), ("target", "element", lone.get("id"))]))

    prefab = fresh(element("CMapPrefab", common("0 4096 0", "0 90 0") + [
        ("name", "string", "path deformer prefab"), ("relayPlugData", "DmePlugList", plugs()), ("connectionsData", "element_array", []),
        ("target", "element", ""), ("variableOverrideNames", "string_array", []), ("variableOverrideValues", "string_array", []),
        ("customVisGroup", "string", ""), ("randomSeed", "int", "1799009846"), ("tintColor", "color", "255 255 255 255"),
        ("visexclude", "bool", "0"), ("targetMapPath", "string", "maps/s2c_deformpath.vmap"), ("targetName", "string", ""),
        ("fixupEntityNames", "bool", "0"), ("useTargetNameAsPrefix", "bool", "0"), ("loadIfNested", "bool", "1"),
        ("prefabRuntimeEntity", "bool", "0"), ("loadAtRuntime", "bool", "0")]))
    world.get("children").extend([group, instance, path, lone, outside, prefab])
    text_out = os.path.join(work, "out.txt")
    open(text_out, "w", encoding="utf-8").write(pm.emit(tops, header))
    pm.convert(text_out, out, "binary")
    print("wrote", out, len(VARIANTS) + 2, "path deformers and a prefab; instance", instance.get("nodeID"), "outside", outside.get("nodeID"), "next", node)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
