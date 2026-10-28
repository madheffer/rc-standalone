"""steamaudio_inside_specimen.py <base.vmap> <out.vmap>: a copy of <base.vmap> (s2c_saprobe) with three boxes floating
over the floor, so that Steam Audio's automatic probes land inside shapes and the inside-shapes filter (rc 180fbe450)
drops them. Each stands over one of the automatic probe columns, 50 to 130 units up:
- two copies of the floor tile with nodeID 61 (176 x 112 x 2) scaled 40 times in z, one with the default physics (a
  world mesh shape; the tile's 8 faces do not make it a closed mesh), one convex_single (a hull);
- a copy of the 6-face box with nodeID 32 (256 x 256 x 64, toolstrigger) given the tile's material and scaled
  (0.5, 0.5, 1.25): a closed world mesh.
Ids, node ids and reference ids are fresh; the world lists the copies after its other children.
"""
import copy
import os
import random
import sys
import tempfile
import uuid

import probe_map as pm


def main(base, out):
    work = tempfile.mkdtemp(prefix="sainside_")
    text_path = os.path.join(work, "base.vmap")
    pm.convert(base, text_path, "keyvalues2")
    text = open(text_path, encoding="utf-8").read()
    header = text.split("\n", 1)[0]
    tops = pm.parse(text)
    everything = [x for t in tops for x in pm.walk(t)]
    source = next(t for t in tops if t.type == "CMapMesh" and t.get("nodeID") == "61")
    root = next(t for t in tops if t.type == "CMapRootElement")
    world = root.get("world")
    if not isinstance(world, pm.Element):
        world = next(t for t in tops if t.type == "CMapWorld" and t.get("id") == world[1])
    node = max(int(e.get("nodeID")) for e in everything if e.get("nodeID") is not None) + 1
    rng = random.Random(20261028)
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

    closed = next(t for t in tops if t.type == "CMapMesh" and t.get("nodeID") == "32")
    material = source.get("meshData").get("materials")
    for template, (x, y), scales, physics in ((source, (512, 216), "1 1 40", "default"), (source, (275.8, 452.2), "1 1 40", "convex_single"),
                                               (closed, (748.2, 452.2), "0.5 0.5 1.25", "default")):
        box = fresh(copy.deepcopy(template))
        box.set("origin", "vector3", f"{x} {y} 90")
        box.set("scales", "vector3", scales)
        box.set("physicsType", "string", physics)
        if template is closed:
            box.get("meshData").set("materials", "string_array", list(material))
        tops.append(box)
        world.get("children").append(("ref", box.get("id")))
    out_text = os.path.join(work, "out.vmap")
    open(out_text, "w", encoding="utf-8", newline="\n").write(pm.emit(tops, header))
    pm.convert(out_text, out, "binary")
    print("wrote", out)


if __name__ == "__main__":
    main(*sys.argv[1:])
