"""ragdoll_probe_map.py <base.vmap> <out.vmap>: a probe map for the ragdoll pre-settle pose (GROUND_TRUTH 56).
Copies of the base map's first prop_static become prop_ragdoll entities of stock models with joints
(the chicken, a CT agent), dropped from above the floor, turned, one onto another, plus the variants the
pre-settle reads: skipPreSettle, Motion Disabled (spawnflag 16384) and Start asleep (65536). The lump's
boneTransforms Valve's compile writes for each are the ground truth; s2c_rounds is the base, as for
s2c_settleround (settle_round_map.py)."""
import copy, os, sys, tempfile

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "coverage"))
import probe_map as pm  # noqa: E402

CHICKEN = "models/chicken/chicken.vmdl"
AGENT = "agents/models/ctm_sas/ctm_sas.vmdl"
# (model, dx, dy, z above the template prop, angles, extra keys)
DROPS = [(CHICKEN, 0, 0, 40, "0 0 0", {}),
         (CHICKEN, 40, 0, 80, "30 90 0", {}),
         (AGENT, 0, 60, 60, "0 0 0", {}),
         (AGENT, 80, 60, 100, "90 0 0", {}),
         (CHICKEN, 80, 64, 160, "0 45 0", {}),
         (AGENT, -80, 0, 60, "0 30 0", {"skipPreSettle": ("string", "1")}),
         (CHICKEN, -80, 60, 60, "0 0 0", {"spawnflags": ("string", "16384")}),
         (AGENT, -160, 0, 60, "0 0 0", {"spawnflags": ("string", "65536")})]


def main(base, out):
    work = tempfile.mkdtemp(prefix="ragdoll_probe_")
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

    template = next(e for e in everything if e.type == "CMapEntity" and props(e) is not None
                    and props(e).get("classname") == "prop_static")
    node = max(int(e.get("nodeID")) for e in everything if e.get("nodeID") is not None) + 1
    serial = 0
    for model, dx, dy, dz, angles, extra in DROPS:
        c = copy.deepcopy(template)
        for x in pm.walk(c):
            if x.get("id") is not None:
                serial += 1
                x.set("id", "elementid", f"7a6d0100-0000-4000-8000-{serial:012x}")
            if x.get("nodeID") is not None:
                x.set("nodeID", "int", str(node))
                node += 1
            if x.get("referenceID") is not None:
                x.set("referenceID", "uint64", "0x%016x" % (0x7a6d000000000000 + node))
        p = props(c)
        p.set("classname", "string", "prop_ragdoll")
        p.fields = [f for f in p.fields if f[0] not in ("solid", "disableshadows", "skin", "rendercolor")]
        p.set("model", "string", model)
        for key, (kind, value) in extra.items():
            p.set(key, kind, value)
        o = [float(v) for v in template.get("origin").split()]
        c.set("origin", "vector3", f"{o[0] + dx:g} {o[1] + dy:g} {o[2] + dz:g}")
        c.set("angles", "qangle", angles)
        world.get("children").append(c)
    text_out = os.path.join(work, "out_text.vmap")
    open(text_out, "w", encoding="utf-8").write(pm.emit(tops, header))
    pm.convert(text_out, out, "binary")
    print("wrote", out, len(DROPS), "prop_ragdoll")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
