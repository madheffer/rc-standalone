"""cable_probe_map.py <base.vmap> <out.vmap>: a probe map for cable collision.
The base map's first CMapCable is copied once per entry of VARIANTS, each copy moved 96 units further along x, with
collisionEnabled set and the listed fields changed (radius, sides, tessellation spacing, interpolation, closed loop,
flipped faces, the physics simplification error, the class, the angles), and the material set to one CS2 ships. The
base cable itself is left as it was (no collision).
The world physics, entity models and settle Valve builds from it settle where a cable's collision tube goes and how
CMapCable::vf277 (rc 18114d850) shapes it."""
import copy, os, sys, tempfile

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "coverage"))
import probe_map as pm  # noqa: E402

VARIANTS = [
    {},
    {"physicsSimplificationError": ("float", "2")},
    {"physicsSimplificationError": ("float", "0.5"), "numSides": ("int", "24"), "radius": ("float", "2")},
    {"radius": ("float", "4"), "numSides": ("int", "6"), "tessellationSpacing": ("float", "32")},
    {"radius": ("float", "8"), "numSides": ("int", "3"), "physicsSimplificationError": ("float", "1")},
    {"closedLoop": ("bool", "1")},
    {"interpolationType": ("int", "0")},
    {"interpolationType": ("int", "2")},
    {"flipFaces": ("bool", "1")},
    {"tessellationSpacing": ("float", "4"), "physicsSimplificationError": ("float", "0.25")},
    {"classname": ("string", "cable_static")},
    {"physicsSimplificationError": ("float", "0")},
    {"physicsSimplificationError": ("float", "0"), "numSides": ("int", "12")},
    {"physicsSimplificationError": ("float", "0"), "numSides": ("int", "40"), "closedLoop": ("bool", "1")},
    {"physicsSimplificationError": ("float", "0"), "angles": ("qangle", "10 35 -20")},
    {"physicsSimplificationError": ("float", "0"), "classname": ("string", "cable_static"), "flipFaces": ("bool", "1")},
    {"physicsSimplificationError": ("float", "0"), "interpolationType": ("int", "2"), "tessellationSpacing": ("float", "3")},
]
# One prop_physics, so the compile builds and steps the settle world (the cables' bodies are
# in it): capture_settle.py has nothing to record on a map without props.
PROP = ("models/cs_italy/crate/italy_wood_box_1.vmdl", [0, 224, 96])

# Every copy gets a material CS2 ships (the base cable's Mako material is not there, and a
# cable_dynamic whose material is missing fails its model compile).
MATERIAL = "materials/cable/cable.vmat"


def main(base, out):
    work = tempfile.mkdtemp(prefix="cable_probe_")
    text_path = os.path.join(work, "base_text.vmap")
    pm.convert(base, text_path, "keyvalues2")
    text = open(text_path, encoding="utf-8").read()
    header = text.split("\n", 1)[0]
    tops = pm.parse(text)
    everything = [e for t in tops for e in pm.walk(t)]
    cable = next(e for e in everything if e.type == "CMapCable")
    world = next(t for t in tops if t.type == "CMapRootElement").get("world")
    world = next((e for e in everything if e.get("id") == world[1]), world) if isinstance(world, tuple) else world
    node = max(int(e.get("nodeID")) for e in everything if e.get("nodeID") is not None) + 1
    origin = [float(x) for x in cable.get("origin").split()]
    serial = 0
    for i, changes in enumerate(VARIANTS):
        c = copy.deepcopy(cable)
        for x in pm.walk(c):
            if x.get("id") is not None:
                serial += 1
                x.set("id", "elementid", f"ca81e000-0000-4000-8000-{serial:012x}")
            if x.get("nodeID") is not None:
                x.set("nodeID", "int", str(node))
                node += 1
            if x.get("referenceID") is not None:
                x.set("referenceID", "uint64", "0x%016x" % (0xca81e00000000000 + node))
        c.set("collisionEnabled", "bool", "1")
        c.set("materialName", "string", MATERIAL)
        for key, (kind, value) in changes.items():
            if key == "classname":
                c.get("entity_properties").set("classname", kind, value)
            else:
                c.set(key, kind, value)
        c.set("origin", "vector3", " ".join(f"{v:g}" for v in [origin[0] + 96 * (i + 1), origin[1], origin[2]]))
        world.get("children").append(c)
    prop = pm.Element("CMapEntity")
    prop.fields = [["id", "elementid", "ca81e000-0000-4000-8000-%012x" % (serial + 1)], ["nodeID", "int", str(node)],
                   ["referenceID", "uint64", "0x%016x" % (0xca81e00000000000 + node)], ["children", "element_array", []],
                   ["variableTargetKeys", "string_array", []], ["variableNames", "string_array", []]]
    plugs = pm.Element("DmePlugList")
    plugs.fields = [["id", "elementid", "ca81e000-0000-4000-8000-%012x" % (serial + 2)], ["names", "string_array", []],
                    ["dataTypes", "int_array", []], ["plugTypes", "int_array", []], ["descriptions", "string_array", []]]
    props = pm.Element("EditGameClassProps")
    props.fields = [["id", "elementid", "ca81e000-0000-4000-8000-%012x" % (serial + 3)], ["classname", "string", "prop_physics"],
                    ["model", "string", PROP[0]]]
    prop.fields += [["relayPlugData", "DmePlugList", plugs], ["connectionsData", "element_array", []],
                    ["entity_properties", "EditGameClassProps", props], ["hitNormal", "vector3", "0 0 1"],
                    ["isProceduralEntity", "bool", "0"], ["origin", "vector3", " ".join(f"{v:g}" for v in PROP[1])],
                    ["angles", "qangle", "0 0 0"], ["scales", "vector3", "1 1 1"], ["transformLocked", "bool", "0"],
                    ["force_hidden", "bool", "0"], ["editorOnly", "bool", "0"]]
    world.get("children").append(prop)
    text_out = os.path.join(work, "out_text.vmap")
    open(text_out, "w", encoding="utf-8").write(pm.emit(tops, header))
    pm.convert(text_out, out, "binary")
    print("wrote", out, len(VARIANTS), "cables from node", cable.get("nodeID"))


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
