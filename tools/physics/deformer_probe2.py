"""deformer_probe2.py <deformerprobe.vmap> <out.vmap>: deformed round shapes and a deformed world mesh.

Takes deformer_probe_map.py's map and adds, inside the first
CMapDeformerSimple's group, a copy of that group's first prop_static with the
model models/s2c_test/round_shapes.vmdl (spheres and capsules, which a
deformer tessellates into meshes) and a copy of the map's smallest CMapMesh
placed at that prop's origin (a world mesh under a deformer). Copies get
fresh element ids, node ids and reference ids. dmxconvert both ways.
"""
import copy
import os
import random
import sys
import tempfile
import uuid

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "coverage"))
import probe_map as pm  # noqa: E402


def main(src, out):
    work = tempfile.mkdtemp(prefix="deformer_probe2_")
    text_path = os.path.join(work, "in.txt")
    pm.convert(src, text_path, "keyvalues2")
    text = open(text_path, encoding="utf-8").read()
    header = text.split("\n", 1)[0]
    tops = pm.parse(text)
    everything = [e for t in tops for e in pm.walk(t)]
    node = max(int(e.get("nodeID")) for e in everything if e.get("nodeID") is not None) + 1
    rng = random.Random(20261003)
    used = {e.get("referenceID") for e in everything}

    def fresh(e):
        nonlocal node
        for x in pm.walk(e):
            if x.get("id") is not None:
                x.set("id", "elementid", str(uuid.UUID(int=rng.getrandbits(128), version=4)))
            if x.get("nodeID") is not None:
                x.set("nodeID", "int", str(node))
                node += 1
            if x.get("referenceID") is not None:
                while True:
                    ref = "0x%016x" % rng.getrandbits(64)
                    if ref not in used:
                        used.add(ref)
                        break
                x.set("referenceID", "uint64", ref)
        return e

    deformer = next(e for e in everything if e.type == "CMapDeformerSimple")
    group = next(c for c in deformer.get("children") if isinstance(c, pm.Element) and c.type == "CMapGroup")
    kids = group.get("children")
    prop = next(c for c in kids if isinstance(c, pm.Element) and c.type == "CMapEntity")
    shapes = fresh(copy.deepcopy(prop))
    shapes.get("entity_properties").set("model", "string", "models/s2c_test/round_shapes.vmdl")
    kids.append(shapes)

    def size(mesh):
        data = mesh.get("meshData")
        vdata = data.get("vertexData") if data else None
        streams = vdata.get("streams") if vdata else None
        return min((len(s.get("data") or []) for s in streams or [] if isinstance(s, pm.Element)), default=1 << 30)
    meshes = [e for e in everything if e.type == "CMapMesh"]
    mesh = fresh(copy.deepcopy(min(meshes, key=size)))
    mesh.set("origin", "vector3", prop.get("origin"))
    mesh.set("angles", "qangle", "0 0 0")
    kids.append(mesh)

    out_text = os.path.join(work, "out.txt")
    open(out_text, "w", encoding="utf-8").write(pm.emit(tops, header))
    pm.convert(out_text, out, "binary")
    print("wrote", out, "prop at", prop.get("origin"), "mesh of", size(mesh), "vertices")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
