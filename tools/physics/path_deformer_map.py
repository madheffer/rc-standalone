"""path_deformer_map.py <deformerprobe2.vmap> <cablecoll.vmap> <out.vmap>: a probe map for CMapDeformerPath.
A copy of <deformerprobe2.vmap> (deformer_probe2.py's output) with, per entry of VARIANTS:
- a path: a copy of s2c_cablecoll's first cable_static, collision off, its nodes moved to the listed points (the
  first node's tangent types kept for every node; a cable is a CMapPath, which is what the deformer looks for);
- a CMapDeformerPath at the listed origin and angles with the listed size and settings (an unlisted setting is left
  out, so the factory's stands), its targetPath naming the path by referenceID;
- under the deformer, a copy of the lattice's prop_static and of the map's smallest CMapMesh, both at the deformer's
  origin.
Valve's compile, captured with capture_deformer.py, gives the lattice each deformer fits along its path."""
import copy, os, sys, tempfile

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "coverage"))
import probe_map as pm  # noqa: E402

VARIANTS = [
    {"origin": (3000, 0, -300), "angles": (0, 0, 0), "size": (128, 32, 32), "settings": {},
     "path": [(3000, 0, -300), (3200, 100, -300), (3400, 0, -250)]},
    {"origin": (3000, 600, -300), "angles": (0, 30, 0), "size": (200, 48, 24),
     "settings": {"start": 0.2, "end": 0.85, "maxError": 0.1},
     "path": [(2900, 500, -320), (3100, 700, -260), (3300, 560, -300), (3500, 800, -200)]},
    {"origin": (3000, 1200, -300), "angles": (10, -20, 5), "size": (96, 16, 64),
     "settings": {"start": 0.05, "maxError": 3.0},
     "path": [(2950, 1150, -310), (3350, 1250, -290)]},
    {"origin": (3000, 1800, -300), "angles": (0, 90, 0), "size": (300, 24, 40),
     "settings": {"end": 0.6, "maxError": 0.02},
     "path": [(3000, 1800, -300), (3100, 2000, -280), (3300, 2000, -300), (3400, 1800, -340), (3600, 1850, -300)]},
]


def load(path, work, name):
    text_path = os.path.join(work, name)
    pm.convert(path, text_path, "keyvalues2")
    text = open(text_path, encoding="utf-8").read()
    return text.split("\n", 1)[0], pm.parse(text)


def vec(v):
    return " ".join(f"{x:g}" for x in v)


def main(base, cables, out):
    work = tempfile.mkdtemp(prefix="path_deformer_")
    header, tops = load(base, work, "base.txt")
    _, cable_tops = load(cables, work, "cables.txt")
    everything = [e for t in tops for e in pm.walk(t)]
    world = next(t for t in tops if t.type == "CMapRootElement").get("world")
    cable_all = [e for t in cable_tops for e in pm.walk(t)]
    cable = next(e for e in cable_all if e.type == "CMapCable" and e.get("entity_properties").get("classname") == "cable_static")
    lattice = next(e for e in everything if e.type == "CMapDeformerLattice")
    prop = next(c for c in lattice.get("children") if isinstance(c, pm.Element) and c.type == "CMapEntity")
    meshes = [e for e in everything if e.type == "CMapMesh"]
    mesh = min(meshes, key=lambda m: len(m.get("meshData").get("vertexData").get("streams")[0].get("data")))
    node = max(int(e.get("nodeID")) for e in everything if e.get("nodeID") is not None) + 1
    serial = 0

    def fresh(e):
        nonlocal node, serial
        for x in pm.walk(e):
            if x.get("id") is not None:
                serial += 1
                x.set("id", "elementid", f"defa7000-0000-4000-8000-{serial:012x}")
            if x.get("nodeID") is not None:
                x.set("nodeID", "int", str(node))
                node += 1
            if x.get("referenceID") is not None:
                x.set("referenceID", "uint64", "0x%016x" % (0xdefa700000000000 + node))
        return e

    def element(type_, fields):
        e = pm.Element(type_)
        e.fields = [list(f) for f in fields]
        return e

    for v in VARIANTS:
        path = copy.deepcopy(cable)
        path.set("collisionEnabled", "bool", "0")
        nodes = [x for x in path.get("children") if isinstance(x, pm.Element) and x.type == "CMapPathNode"]
        template = nodes[0]
        others = [x for x in path.get("children") if not (isinstance(x, pm.Element) and x.type == "CMapPathNode")]
        laid = []
        for p in v["path"]:
            n = copy.deepcopy(template)
            n.set("origin", "vector3", vec(p))
            laid.append(n)
        path.set("children", "element_array", others + laid)
        path.set("origin", "vector3", vec(v["path"][0]))
        path.set("angles", "qangle", "0 0 0")
        fresh(path)
        world.get("children").append(path)

        p = fresh(copy.deepcopy(prop))
        p.set("origin", "vector3", vec(v["origin"]))
        m = fresh(copy.deepcopy(mesh))
        m.set("origin", "vector3", vec(v["origin"]))
        fields = [("id", "elementid", ""), ("nodeID", "int", "0"), ("referenceID", "uint64", "0x0"),
                  ("children", "element_array", [p, m]), ("variableTargetKeys", "string_array", []),
                  ("variableNames", "string_array", []), ("origin", "vector3", vec(v["origin"])),
                  ("angles", "qangle", vec(v["angles"])), ("scales", "vector3", "1 1 1"), ("transformLocked", "bool", "0"),
                  ("force_hidden", "bool", "0"), ("editorOnly", "bool", "0"), ("customVisGroup", "string", ""),
                  ("randomSeed", "int", "1959997979"), ("size", "vector3", vec(v["size"]))]
        reference = element("DmElement", [("id", "elementid", ""), ("name", "string", "targetPath"), ("referenceName", "string", ""),
                                                  ("targetReferenceID", "uint64", path.get("referenceID"))])
        fields.append(("targetPath", "DmElement", reference))
        for key, value in v["settings"].items():
            fields.append((key, "float", f"{value:g}"))
        deformer = element("CMapDeformerPath", fields)
        fresh(deformer)
        world.get("children").append(deformer)
    text_out = os.path.join(work, "out.txt")
    open(text_out, "w", encoding="utf-8").write(pm.emit(tops, header))
    pm.convert(text_out, out, "binary")
    print("wrote", out, len(VARIANTS), "path deformers; next node", node)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3])
