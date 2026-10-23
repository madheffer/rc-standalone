"""cable_instance_map2.py <cablecoll.vmap> <out.vmap>: which cable settings survive an instance collapse.
A copy of <cablecoll.vmap> (cable_probe_map.py's output) whose world also holds, per entry of VARIANTS, a group with a
copy of the first collision cable_static changed as listed, placed by its own instance moved (0, 384 k, 0) and not
turned. The copies stand apart, so each tube is its own piece of the world soup."""
import copy, os, sys, tempfile

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "coverage"))
import probe_map as pm  # noqa: E402

VARIANTS = [
    {},
    {"physicsSimplificationError": ("float", "0")},
    {"physicsSimplificationError": ("float", "0.5")},
    {"physicsSimplificationError": ("float", "0"), "numSides": ("int", "24")},
    {"physicsSimplificationError": ("float", "0"), "radius": ("float", "4")},
    {"physicsSimplificationError": ("float", "0"), "tessellationSpacing": ("float", "32")},
    {"physicsSimplificationError": ("float", "0"), "flipFaces": ("bool", "1")},
    {"physicsSimplificationError": ("float", "0"), "materialName": ("string", "materials/dev/reflectivity_30.vmat")},
    {"physicsSimplificationError": ("float", "0"), "closedLoop": ("bool", "1")},
    {"physicsSimplificationError": ("float", "0"), "interpolationType": ("int", "2")},
    {"collisionEnabled": ("bool", "0")},
]


def main(base, out):
    work = tempfile.mkdtemp(prefix="cable_instance2_")
    text_path = os.path.join(work, "base_text.vmap")
    pm.convert(base, text_path, "keyvalues2")
    text = open(text_path, encoding="utf-8").read()
    header = text.split("\n", 1)[0]
    tops = pm.parse(text)
    everything = [e for t in tops for e in pm.walk(t)]
    world = next(t for t in tops if t.type == "CMapRootElement").get("world")
    world = next((e for e in everything if e.get("id") == world[1]), world) if isinstance(world, tuple) else world
    static = next(e for e in everything if e.type == "CMapCable" and e.get("collisionEnabled") == "1"
                  and e.get("entity_properties").get("classname") == "cable_static")
    node = max(int(e.get("nodeID")) for e in everything if e.get("nodeID") is not None) + 1
    serial = 0

    def fresh(e):
        nonlocal node, serial
        for x in pm.walk(e):
            if x.get("id") is not None:
                serial += 1
                x.set("id", "elementid", f"cab1e200-0000-4000-8000-{serial:012x}")
            if x.get("nodeID") is not None:
                x.set("nodeID", "int", str(node))
                node += 1
            if x.get("referenceID") is not None:
                x.set("referenceID", "uint64", "0x%016x" % (0xcab1e20000000000 + node))
        return e

    def element(type_, fields):
        e = pm.Element(type_)
        e.fields = [list(f) for f in fields]
        return e

    def common(origin):
        return [("id", "elementid", ""), ("nodeID", "int", "0"), ("referenceID", "uint64", "0x0"),
                ("children", "element_array", []), ("variableTargetKeys", "string_array", []),
                ("variableNames", "string_array", []), ("origin", "vector3", origin), ("angles", "qangle", "0 0 0"),
                ("scales", "vector3", "1 1 1"), ("transformLocked", "bool", "0"), ("force_hidden", "bool", "0"),
                ("editorOnly", "bool", "0")]

    for k, changes in enumerate(VARIANTS):
        c = copy.deepcopy(static)
        for key, (kind, value) in changes.items():
            c.set(key, kind, value)
        group = element("CMapGroup", common("0 0 0"))
        group.get("children").append(c)
        fresh(group)
        plugs = element("DmePlugList", [("id", "elementid", ""), ("names", "string_array", []), ("dataTypes", "int_array", []),
                                        ("plugTypes", "int_array", []), ("descriptions", "string_array", [])])
        inst = element("CMapInstance", common(f"0 {384 * (k + 1)} 0") + [
            ("relayPlugData", "DmePlugList", plugs), ("connectionsData", "element_array", []), ("target", "element", group.get("id"))])
        world.get("children").extend([group, fresh(inst)])
    text_out = os.path.join(work, "out_text.vmap")
    open(text_out, "w", encoding="utf-8").write(pm.emit(tops, header))
    pm.convert(text_out, out, "binary")
    print("wrote", out, len(VARIANTS), "instances, next node", node)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
