"""prop_override_map.py <base.vmap> <out.vmap>: a probe map for the static prop override keys.
Copies of the base map's first solid prop_static, each moved 256 units along x from the last, carry:
  - collision_override "csgo_railing" (a collision property with a group and two lists);
  - collision_override "CSGO_Railing" (does the property lookup fold case?);
  - surface_property_override "metal";
  - surface_property_override "Wood" (a spelling the game's table does not use).
The world physics Valve builds from it settles physicsbuilder 180153d40's override path."""
import copy, os, sys, tempfile

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "coverage"))
import probe_map as pm  # noqa: E402

OVERRIDES = [("collision_override", "csgo_railing"), ("collision_override", "CSGO_Railing"),
             ("surface_property_override", "metal"), ("surface_property_override", "Wood")]


def main(base, out):
    work = tempfile.mkdtemp(prefix="prop_override_")
    text_path = os.path.join(work, "base_text.vmap")
    pm.convert(base, text_path, "keyvalues2")
    text = open(text_path, encoding="utf-8").read()
    header = text.split("\n", 1)[0]
    tops = pm.parse(text)
    everything = [e for t in tops for e in pm.walk(t)]
    by_id = {e.get("id"): e for e in everything}
    world = next(t for t in tops if t.type == "CMapRootElement").get("world")

    def props(e):
        p = e.get("entity_properties")
        return p if isinstance(p, pm.Element) else None

    prop = next(e for e in everything if e.type == "CMapEntity" and props(e) is not None
                and props(e).get("classname") == "prop_static" and (props(e).get("solid") or "6") == "6")
    node = max(int(e.get("nodeID")) for e in everything if e.get("nodeID") is not None) + 1
    origin = [float(x) for x in prop.get("origin").split()]
    children = world.get("children")
    serial = 0
    for i, (key, value) in enumerate(OVERRIDES):
        c = copy.deepcopy(prop)
        for x in pm.walk(c):
            if x.get("id") is not None:
                serial += 1
                x.set("id", "elementid", f"5c2c{serial:04x}-0000-4000-8000-{serial:012x}")
            if x.get("nodeID") is not None:
                x.set("nodeID", "int", str(node))
                node += 1
            if x.get("referenceID") is not None:
                x.set("referenceID", "uint64", "0x%016x" % (0x5c2c000000000000 + node))
        props(c).set(key, "string", value)
        c.set("origin", "vector3", " ".join(f"{v:g}" for v in [origin[0] + 256 * (i + 1), origin[1], origin[2]]))
        children.append(c)
    text_out = os.path.join(work, "out_text.vmap")
    open(text_out, "w", encoding="utf-8").write(pm.emit(tops, header))
    pm.convert(text_out, out, "binary")
    print("wrote", out, "props from node", prop.get("nodeID"))


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
