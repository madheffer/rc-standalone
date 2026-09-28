"""settle_round_map.py <base.vmap> <out.vmap>: a probe map for the settle of sphere and capsule shapes.
Copies of the base map's prop_static soccer ball and round-shapes model become prop_physics dropped
from above the floor, turned, some onto each other, so the settle meets sphere-sphere, sphere-capsule,
capsule-capsule, sphere-hull, capsule-hull and sphere/capsule against the world mesh. The settled
poses Valve's compile writes into the lump are the ground truth (EntityLumpAgainstValveTests)."""
import copy, os, sys, tempfile

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "coverage"))
import probe_map as pm  # noqa: E402

# (model substring, dx, dy, z above the base prop, angles)
DROPS = [("soccer", 0, 0, 40, "0 0 0"), ("soccer", 3, 2, 70, "10 20 30"), ("soccer", -2, 1, 100, "0 0 0"),
         ("round_shapes", 0, 0, 60, "20 30 40"), ("round_shapes", 30, 10, 120, "0 45 90"),
         ("soccer", 30, 10, 200, "0 0 0"), ("round_shapes", -40, -20, 30, "90 0 0")]


def main(base, out):
    work = tempfile.mkdtemp(prefix="settle_round_")
    text_path = os.path.join(work, "base_text.vmap")
    pm.convert(base, text_path, "keyvalues2")
    text = open(text_path, encoding="utf-8").read()
    header = text.split("\n", 1)[0]
    tops = pm.parse(text)
    everything = [e for t in tops for e in pm.walk(t)]
    world = next(t for t in tops if t.type == "CMapRootElement").get("world")

    def props(e):
        p = e.get("entity_properties")
        return p if isinstance(p, pm.Element) else None

    statics = [e for e in everything if e.type == "CMapEntity" and props(e) is not None
               and props(e).get("classname") == "prop_static"]
    node = max(int(e.get("nodeID")) for e in everything if e.get("nodeID") is not None) + 1
    serial = 0
    for i, (model, dx, dy, dz, angles) in enumerate(DROPS):
        template = next(e for e in statics if model in (props(e).get("model") or ""))
        c = copy.deepcopy(template)
        for x in pm.walk(c):
            if x.get("id") is not None:
                serial += 1
                x.set("id", "elementid", f"5e7100{serial:02x}-0000-4000-8000-{serial:012x}")
            if x.get("nodeID") is not None:
                x.set("nodeID", "int", str(node))
                node += 1
            if x.get("referenceID") is not None:
                x.set("referenceID", "uint64", "0x%016x" % (0x5e71000000000000 + node))
        p = props(c)
        p.set("classname", "string", "prop_physics")
        p.fields = [f for f in p.fields if f[0] not in ("solid", "disableshadows")]
        o = [float(v) for v in template.get("origin").split()]
        c.set("origin", "vector3", f"{o[0] + dx:g} {o[1] + dy:g} {o[2] + dz:g}")
        c.set("angles", "qangle", angles)
        world.get("children").append(c)
    text_out = os.path.join(work, "out_text.vmap")
    open(text_out, "w", encoding="utf-8").write(pm.emit(tops, header))
    pm.convert(text_out, out, "binary")
    print("wrote", out, len(DROPS), "prop_physics")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
