"""steamaudio_navcover_specimen.py <base.vmap> <out.vmap> [height]: a copy of <base.vmap> (s2c_saprobe) with a closed
room over the floor at x 72..952, y -680..-120 (a roof at <height>, default 150, and four walls, all scaled copies of
the floor tile with nodeID 61, 176 x 112 x 2) and a copy of the map's point_nav_walkable inside it, so that IslandRemoval
keeps the room's nav. The walls and roof hide the room's nav from every probe outside it, which gives Steam Audio's nav
coverage appender (rc 0923 180fbcb10) areas to fill, big enough for its random points. Ids, node ids and reference ids
are fresh; the world lists the copies after its other children.
"""
import copy
import os
import random
import sys
import tempfile
import uuid

import probe_map as pm


def main(base, out, z="150"):
    work = tempfile.mkdtemp(prefix="sanavcover_")
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
    rng = random.Random(20261030)
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

    # a closed room: a roof and four walls, each a scaled copy of the tile (176 x 112 x 2)
    x0, x1, y0, y1, top = 72.0, 952.0, -680.0, -120.0, float(z)
    cx, cy, w, d = (x0 + x1) / 2, (y0 + y1) / 2, x1 - x0, y1 - y0
    for (ox, oy, oz), (sx_, sy_, sz_) in (((cx, cy, top), (w / 176, d / 112, 2)),
                                           ((cx, y0, top / 2), (w / 176, 0.1, top / 2)), ((cx, y1, top / 2), (w / 176, 0.1, top / 2)),
                                           ((x0, cy, top / 2), (0.06, d / 112, top / 2)), ((x1, cy, top / 2), (0.06, d / 112, top / 2))):
        part = fresh(copy.deepcopy(source))
        part.set("origin", "vector3", f"{ox} {oy} {oz}")
        part.set("scales", "vector3", f"{sx_} {sy_} {sz_}")
        tops.append(part)
        world.get("children").append(("ref", part.get("id")))
    seed = next(e for e in tops if e.type == "CMapEntity" and e.get("entity_properties").get("classname") == "point_nav_walkable")
    walk = fresh(copy.deepcopy(seed))  # IslandRemoval keeps only seeded islands: the room needs its own
    walk.set("origin", "vector3", f"{cx} {cy} 16")
    tops.append(walk)
    world.get("children").append(("ref", walk.get("id")))
    out_text = os.path.join(work, "out.vmap")
    open(out_text, "w", encoding="utf-8", newline="\n").write(pm.emit(tops, header))
    pm.convert(out_text, out, "binary")
    print("wrote", out)


if __name__ == "__main__":
    main(*sys.argv[1:])
