"""movable_nav_specimen.py <content maps dir> <out.vmap> [--movelinear-only]: the movable nav mesh specimens, a copy
of probe01 with func_movelinear entities whose CreateMovableNavMesh is 1 (180fb5ce0 marks their meshes,
180fb6cf0 makes each a movable mesh input, navsystem generates each on its own and the .nav lists them):
  - moverA: a flat 448 x 448 x 32 platform (probe01's first func_buyzone box, scaled) standing on the floor
    beside the seed, angles 0;
  - moverB: the same platform away from the first, yaw 30, so its matrix rotates;
  - moverC: the same with CreateMovableNavMesh 0 (the control: an ordinary entity);
  - moverD: a func_mover with CreateMovableNavMesh and its platform, and a func_brush (moverD_child, a higher
    platform) whose parentname is moverD (180fb6e00 gathers it);
  - pnm: a point_nav_movable_mesh (probe01's first point entity renamed; Hammer does not offer the class) with ref_ent pnm_ref, navgen_on_ref_ent 1 and
    GravityFollowsRotation 1, pnm_ref a func_brush platform at yaw 45.
s2c_navmovable is the --movelinear-only build (A, B, C), s2c_navmovable2 the whole list.
The func_movelinear is probe_classes' fully keyed one (its other keys kept). Built through Valve's dmxconvert
as probe_map.py does."""
import os
import sys

import probe_map as pm
from snapshot_specimen import load, deep, find, classname, set_key

MOVERS = [("moverA", (0, 900, 16), "0 0 0", "1"), ("moverB", (900, 900, 16), "0 30 0", "1"), ("moverC", (-900, 900, 16), "0 0 0", "0")]
# (name, class, origin, angles, extra keys)
MORE = [("moverD", "func_mover", (0, -900, 16), "0 0 0", {"CreateMovableNavMesh": "1"}),
        ("moverD_child", "func_brush", (0, -900, 112), "0 0 0", {"parentname": "moverD"}),
        ("pnm_ref", "func_brush", (900, -900, 16), "0 45 0", {})]
# --clear-parents (s2c_navmovable3): the copies' parentname (probe_target, from probe_classes' mover, naming no
# entity) cleared except moverD_child's; on s2c_navmovable2 and 3 Valve found no entity for pnm's ref_ent.
# --no-point (s2c_navmovable4, with --clear-parents): without pnm, the func_mover reference.


def main(maps, out, movelinear_only=False, clear_parents=False, no_point=False):
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
    def platform(origin, angles):
        mesh = deep(zone.get("children")[0], by_id)
        mesh.set("origin", "vector3", " ".join(f"{x:g}" for x in origin))
        mesh.set("angles", "qangle", angles)
        data = mesh.get("meshData")
        data.set("materials", "string_array", ["materials/dev/dev_measuregeneric01.vmat"])
        for s in data.get("vertexData").get("streams"):
            if s.get("name") == "position:0":
                s.set("data", "vector3_array", [" ".join(f"{float(v) / t * h:g}" for v, t, h in zip(p.split(), (224, 64, 32), (224, 224, 16)))
                                                for p in s.get("data")])
        return mesh

    added = []
    for name, cls, origin, angles, keys in ([] if movelinear_only else MORE):
        m = deep(mover, class_ids)
        m.get("entity_properties").set("classname", "string", cls)
        m.set("origin", "vector3", " ".join(f"{x:g}" for x in origin))
        m.set("angles", "qangle", angles)
        m.set("scales", "vector3", "1 1 1")
        set_key(m, "targetname", name)
        set_key(m, "CreateMovableNavMesh", "0")
        if clear_parents:
            set_key(m, "parentname", "")
        for k, v in keys.items():
            set_key(m, k, v)
        m.set("children", "element_array", [platform(origin, angles)])
        added.append(m)
    # point_nav_movable_mesh is not among the classes Hammer offers: probe01's first point entity, renamed.
    point = next((by_id[c[1]] if isinstance(c, tuple) else c) for c in children
                 if (by_id[c[1]] if isinstance(c, tuple) else c).type == "CMapEntity"
                 and not (by_id[c[1]] if isinstance(c, tuple) else c).get("children"))
    pnm = deep(point, by_id)
    pnm.get("entity_properties").set("classname", "string", "point_nav_movable_mesh")
    pnm.set("origin", "vector3", "900 -1100 16")
    for k, v in {"targetname": "pnm", "ref_ent": "pnm_ref", "navgen_on_ref_ent": "1", "GravityFollowsRotation": "1"}.items():
        set_key(pnm, k, v)
    if not movelinear_only and not no_point:
        added.append(pnm)
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
    for e in added:
        print(f"{e.get('entity_properties').get('targetname')} ({classname(e)}): node {e.get('nodeID')}")
    work = os.path.dirname(out)
    out_text = out + ".kv2"
    open(out_text, "w", encoding="utf-8", newline="\n").write(pm.emit(tops, header))
    pm.convert(out_text, out, "binary")
    os.remove(out_text)
    print("wrote", out)


if __name__ == "__main__":
    main(*[a for a in sys.argv[1:] if not a.startswith("--")][:2], movelinear_only="--movelinear-only" in sys.argv,
         clear_parents="--clear-parents" in sys.argv, no_point="--no-point" in sys.argv)
