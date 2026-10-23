"""cable_instance_map.py <cablecoll.vmap> <out.vmap>: a probe map for collision cables inside instances and prefabs.
A copy of <cablecoll.vmap> (cable_probe_map.py's output) whose world also holds:
- group A (origin -256 128 16, turned 0 15 0) with copies of four collision cables (the default cable_dynamic, the
  first cable_static, the turned cable_dynamic and the flipped cable_static) and instance B, placed by instance A
  (origin 512 -1024 48, turned 0 30 0);
- group B (origin 64 0 0) with a copy of the default cable_dynamic, placed by instance B (origin 32 -192 0, turned
  0 -45 10);
- a CMapPrefab of maps/s2c_cablecoll.vmap moved (1024 1536 64) and turned (0 90 0), as prefab_probe_map.py writes one.
Valve's compile settles how the collapse and the prefab move a cable's collision tube (world soup, cable_dynamic
models, settle world)."""
import copy, os, sys, tempfile

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "coverage"))
import probe_map as pm  # noqa: E402

# Positions in cable_probe_map.VARIANTS order among the collision cables.
PICK_A = [0, 10, 14, 15]
PICK_B = [0]


def main(base, out):
    work = tempfile.mkdtemp(prefix="cable_instance_")
    text_path = os.path.join(work, "base_text.vmap")
    pm.convert(base, text_path, "keyvalues2")
    text = open(text_path, encoding="utf-8").read()
    header = text.split("\n", 1)[0]
    tops = pm.parse(text)
    everything = [e for t in tops for e in pm.walk(t)]
    world = next(t for t in tops if t.type == "CMapRootElement").get("world")
    world = next((e for e in everything if e.get("id") == world[1]), world) if isinstance(world, tuple) else world
    colliding = [e for e in everything if e.type == "CMapCable" and e.get("collisionEnabled") == "1"]
    node = max(int(e.get("nodeID")) for e in everything if e.get("nodeID") is not None) + 1
    serial = 0

    def fresh(e):
        nonlocal node, serial
        for x in pm.walk(e):
            if x.get("id") is not None:
                serial += 1
                x.set("id", "elementid", f"cab1e000-0000-4000-8000-{serial:012x}")
            if x.get("nodeID") is not None:
                x.set("nodeID", "int", str(node))
                node += 1
            if x.get("referenceID") is not None:
                x.set("referenceID", "uint64", "0x%016x" % (0xcab1e00000000000 + node))
        return e

    def element(type_, fields):
        e = pm.Element(type_)
        e.fields = [list(f) for f in fields]
        return e

    def plugs():
        return element("DmePlugList", [("id", "elementid", ""), ("names", "string_array", []), ("dataTypes", "int_array", []),
                                       ("plugTypes", "int_array", []), ("descriptions", "string_array", [])])

    def common(origin, angles):
        return [("id", "elementid", ""), ("nodeID", "int", "0"), ("referenceID", "uint64", "0x0"),
                ("children", "element_array", []), ("variableTargetKeys", "string_array", []),
                ("variableNames", "string_array", []), ("origin", "vector3", origin), ("angles", "qangle", angles),
                ("scales", "vector3", "1 1 1"), ("transformLocked", "bool", "0"), ("force_hidden", "bool", "0"),
                ("editorOnly", "bool", "0")]

    def group(origin, angles, cables):
        g = element("CMapGroup", common(origin, angles))
        g.get("children").extend(fresh(copy.deepcopy(colliding[i])) for i in cables)
        return fresh(g)

    def instance(origin, angles, target):
        i = element("CMapInstance", common(origin, angles) + [("relayPlugData", "DmePlugList", plugs()),
                                                              ("connectionsData", "element_array", []),
                                                              ("target", "element", target.get("id"))])
        return fresh(i)

    group_b = group("64 0 0", "0 0 0", PICK_B)
    group_a = group("-256 128 16", "0 15 0", PICK_A)
    group_a.get("children").append(instance("32 -192 0", "0 -45 10", group_b))
    world.get("children").extend([group_a, group_b, instance("512 -1024 48", "0 30 0", group_a)])
    prefab = element("CMapPrefab", common("1024 1536 64", "0 90 0") + [
        ("name", "string", "cable prefab"), ("relayPlugData", "DmePlugList", plugs()), ("connectionsData", "element_array", []),
        ("target", "element", ""), ("variableOverrideNames", "string_array", []), ("variableOverrideValues", "string_array", []),
        ("customVisGroup", "string", ""), ("randomSeed", "int", "1799009846"), ("tintColor", "color", "255 255 255 255"),
        ("visexclude", "bool", "0"), ("targetMapPath", "string", "maps/s2c_cablecoll.vmap"), ("targetName", "string", ""),
        ("fixupEntityNames", "bool", "0"), ("useTargetNameAsPrefix", "bool", "0"), ("loadIfNested", "bool", "1"),
        ("prefabRuntimeEntity", "bool", "0"), ("loadAtRuntime", "bool", "0")])
    world.get("children").append(fresh(prefab))
    text_out = os.path.join(work, "out_text.vmap")
    open(text_out, "w", encoding="utf-8").write(pm.emit(tops, header))
    pm.convert(text_out, out, "binary")
    print("wrote", out, "groups", group_a.get("nodeID"), group_b.get("nodeID"), "next node", node)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
