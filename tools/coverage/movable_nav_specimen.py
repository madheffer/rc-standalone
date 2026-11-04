"""movable_nav_specimen.py <content maps dir> <out.vmap>: the movable nav mesh specimen (s2c_navmovable), a copy
of probe01 with func_movelinear entities whose CreateMovableNavMesh is 1 (180fb5ce0 marks their meshes,
180fb6cf0 makes each a movable mesh input, navsystem generates each on its own and the .nav lists them):
  - moverA: a flat 448 x 448 x 32 platform (probe01's first func_buyzone box, scaled) standing on the floor
    beside the seed, angles 0;
  - moverB: the same platform away from the first, yaw 30, so its matrix rotates;
  - moverC: the same with CreateMovableNavMesh 0 (the control: an ordinary entity).
The func_movelinear is probe_classes' fully keyed one (its other keys kept). Built through Valve's dmxconvert
as probe_map.py does."""
import os
import sys

import probe_map as pm
from snapshot_specimen import load, deep, find, classname, set_key

MOVERS = [("moverA", (0, 900, 16), "0 0 0", "1"), ("moverB", (900, 900, 16), "0 30 0", "1"), ("moverC", (-900, 900, 16), "0 0 0", "0")]


def main(maps, out):
    header, tops, by_id = load(os.path.join(maps, "probe01.vmap"))
    _, classes, class_ids = load(os.path.join(maps, "probe_classes.vmap"))
    root = next(t for t in tops if t.type == "CMapRootElement")
    children = root.get("world").get("children")
    everything = [e for t in tops for e in pm.walk(t)]
    node = max(int(e.get("nodeID")) for e in everything if e.get("nodeID") is not None) + 1

    mover = find(classes, class_ids, lambda e: classname(e) == "func_movelinear"
                 and e.get("entity_properties").get("CreateMovableNavMesh") == "1")
    zone = next((by_id[c[1]] if isinstance(c, tuple) else c) for c in children
                if classname(by_id[c[1]] if isinstance(c, tuple) else c) == "func_buyzone")
    added = []
    for k, (name, origin, angles, create) in enumerate(MOVERS):
        m = deep(mover, class_ids)
        m.set("origin", "vector3", " ".join(f"{x:g}" for x in origin))
        m.set("angles", "qangle", angles)
        m.set("scales", "vector3", "1 1 1")
        set_key(m, "targetname", name)
        set_key(m, "CreateMovableNavMesh", create)
        mesh = deep(zone.get("children")[0], by_id)
        mesh.set("origin", "vector3", " ".join(f"{x:g}" for x in origin))
        mesh.set("angles", "qangle", angles)
        data = mesh.get("meshData")
        # The buyzone box is a trigger (no collision): a solid dev material makes it a platform.
        data.set("materials", "string_array", ["materials/dev/dev_measuregeneric01.vmat"])
        for s in data.get("vertexData").get("streams"):
            if s.get("name") == "position:0":
                s.set("data", "vector3_array", [" ".join(f"{float(v) / t * h:g}" for v, t, h in zip(p.split(), (224, 64, 32), (224, 224, 16)))
                                                for p in s.get("data")])
        m.set("children", "element_array", [mesh])
        added.append(m)
    for k, e in enumerate(added):
        for x in pm.walk(e):
            if x.get("id") is not None:
                x.set("id", "elementid", f"4d4d4d4d-0000-4000-8000-{k:04x}{id(x) & 0xffffffff:08x}")
            if x.get("nodeID") is not None:
                x.set("nodeID", "int", str(node))
                node += 1
            if x.get("referenceID") is not None:
                x.set("referenceID", "uint64", "0x%016x" % (0x4d4d000000000000 + node))
    children.extend(added)
    for (name, *_), e in zip(MOVERS, added):
        print(f"{name}: node {e.get('nodeID')}")
    work = os.path.dirname(out)
    out_text = out + ".kv2"
    open(out_text, "w", encoding="utf-8", newline="\n").write(pm.emit(tops, header))
    pm.convert(out_text, out, "binary")
    os.remove(out_text)
    print("wrote", out)


if __name__ == "__main__":
    main(*sys.argv[1:3])
