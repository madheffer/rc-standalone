"""deform_place_map.py <deformerprobe2.vmap> <cablecoll.vmap> <out.vmap>: cables under deformers, and deformers placed.
A copy of <deformerprobe2.vmap> (deformer_probe2.py's output) with:
- under each deformer, a copy of s2c_cablecoll's first collision cable_dynamic and of its first collision
  cable_static, their path nodes laid on a straight line through the deformer's props (the bend deformers: along y
  from 905 to 1355 or 1897 to 1449 at x 5658, z -340; the lattice: along x through its prop);
- a group holding a copy of the first CMapDeformerSimple (with its props, mesh and cables), placed by an instance
  moved (-4096 0 64) and turned (0 30 0);
- a CMapPrefab of maps/deformerprobe2.vmap moved (0 4096 0) and turned (0 90 0);
- one prop_physics, so the compile runs the settle.
Valve's compile settles what a deformer does to a cable's collision and how a deformed prop, mesh or cable is placed
through an instance or a prefab."""
import copy, os, sys, tempfile

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "coverage"))
import probe_map as pm  # noqa: E402

# (deformer node id, first node, last node) per deformer of deformerprobe2.
LINES = {"2142": ((5658, 905, -340), (5658, 1355, -340)), "2160": ((5658, 1897, -340), (5658, 1449, -340)),
         "2140": ((1222, -1152, -370), (1282, -1152, -370))}


def load(path, work, name):
    text_path = os.path.join(work, name)
    pm.convert(path, text_path, "keyvalues2")
    text = open(text_path, encoding="utf-8").read()
    return text.split("\n", 1)[0], pm.parse(text)


def main(base, cables, out):
    work = tempfile.mkdtemp(prefix="deform_place_")
    header, tops = load(base, work, "base.txt")
    _, cable_tops = load(cables, work, "cables.txt")
    everything = [e for t in tops for e in pm.walk(t)]
    by_id = {e.get("id"): e for e in everything}
    world = next(t for t in tops if t.type == "CMapRootElement").get("world")
    cable_all = [e for t in cable_tops for e in pm.walk(t)]
    colliding = [e for e in cable_all if e.type == "CMapCable" and e.get("collisionEnabled") == "1"]
    dynamic = next(e for e in colliding if e.get("entity_properties").get("classname") == "cable_dynamic")
    static = next(e for e in colliding if e.get("entity_properties").get("classname") == "cable_static")
    node = max(int(e.get("nodeID")) for e in everything if e.get("nodeID") is not None) + 1
    serial = 0

    def fresh(e):
        nonlocal node, serial
        for x in pm.walk(e):
            if x.get("id") is not None:
                serial += 1
                x.set("id", "elementid", f"defe1000-0000-4000-8000-{serial:012x}")
            if x.get("nodeID") is not None:
                x.set("nodeID", "int", str(node))
                node += 1
            if x.get("referenceID") is not None:
                x.set("referenceID", "uint64", "0x%016x" % (0xdefe100000000000 + node))
        return e

    def inline(e, seen=()):
        e = copy.deepcopy(e)
        for f in e.fields:
            if isinstance(f[2], list):
                f[2] = [inline(by_id[x[1]], seen + (x[1],)) if isinstance(x, tuple) and x[1] in by_id and x[1] not in seen
                        else inline(x, seen) if isinstance(x, pm.Element) else x for x in f[2]]
            elif isinstance(f[2], pm.Element):
                f[2] = inline(f[2], seen)
        return e

    def laid(cable, a, b):
        c = copy.deepcopy(cable)
        path = [x for x in c.get("children") if isinstance(x, pm.Element) and x.type == "CMapPathNode"]
        for i, n in enumerate(path):
            t = i / (len(path) - 1)
            n.set("origin", "vector3", " ".join(f"{a[k] + (b[k] - a[k]) * t:g}" for k in range(3)))
        c.set("origin", "vector3", " ".join(f"{v:g}" for v in a))
        return fresh(c)

    deformers = [e for e in everything if e.type in ("CMapDeformerSimple", "CMapDeformerLattice")]
    for d in deformers:
        a, b = LINES[d.get("nodeID")]
        d.get("children").extend([laid(dynamic, a, b), laid(static, a, b)])

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

    bend = next(d for d in deformers if d.type == "CMapDeformerSimple")
    group = element("CMapGroup", common(bend.get("origin"), "0 0 0"))
    group.get("children").append(inline(bend))
    fresh(group)
    instance = fresh(element("CMapInstance", common("-4096 0 64", "0 30 0") + [
        ("relayPlugData", "DmePlugList", plugs()), ("connectionsData", "element_array", []), ("target", "element", group.get("id"))]))
    instance.set("origin", "vector3", " ".join(f"{float(v) + o:g}" for v, o in zip(bend.get("origin").split(), (-4096, 0, 64))))
    prefab = fresh(element("CMapPrefab", common("0 4096 0", "0 90 0") + [
        ("name", "string", "deformer prefab"), ("relayPlugData", "DmePlugList", plugs()), ("connectionsData", "element_array", []),
        ("target", "element", ""), ("variableOverrideNames", "string_array", []), ("variableOverrideValues", "string_array", []),
        ("customVisGroup", "string", ""), ("randomSeed", "int", "1799009846"), ("tintColor", "color", "255 255 255 255"),
        ("visexclude", "bool", "0"), ("targetMapPath", "string", "maps/deformerprobe2.vmap"), ("targetName", "string", ""),
        ("fixupEntityNames", "bool", "0"), ("useTargetNameAsPrefix", "bool", "0"), ("loadIfNested", "bool", "1"),
        ("prefabRuntimeEntity", "bool", "0"), ("loadAtRuntime", "bool", "0")]))
    world.get("children").extend([group, instance, prefab])
    # One prop_physics, so the compile builds and steps the settle world (capture_settle.py has
    # nothing to record on a map without one).
    prop = element("CMapEntity", [("id", "elementid", ""), ("nodeID", "int", "0"), ("referenceID", "uint64", "0x0"),
                                  ("children", "element_array", []), ("variableTargetKeys", "string_array", []),
                                  ("variableNames", "string_array", [])])
    keys = element("EditGameClassProps", [("id", "elementid", ""), ("classname", "string", "prop_physics"),
                                          ("model", "string", "models/cs_italy/crate/italy_wood_box_1.vmdl")])
    prop.fields += [["relayPlugData", "DmePlugList", plugs()], ["connectionsData", "element_array", []],
                    ["entity_properties", "EditGameClassProps", keys], ["hitNormal", "vector3", "0 0 1"],
                    ["isProceduralEntity", "bool", "0"], ["origin", "vector3", "0 224 96"], ["angles", "qangle", "0 0 0"],
                    ["scales", "vector3", "1 1 1"], ["transformLocked", "bool", "0"], ["force_hidden", "bool", "0"],
                    ["editorOnly", "bool", "0"]]
    world.get("children").append(fresh(prop))
    text_out = os.path.join(work, "out.txt")
    open(text_out, "w", encoding="utf-8").write(pm.emit(tops, header))
    pm.convert(text_out, out, "binary")
    print("wrote", out, len(deformers), "deformers with cables; group", group.get("nodeID"), "instance", instance.get("nodeID"), "next", node)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3])
