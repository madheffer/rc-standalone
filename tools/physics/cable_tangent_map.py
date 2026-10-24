"""cable_tangent_map.py <s2c_deformpath.vmap> <out_tangent.vmap> <out_prefab.vmap>: explicit path tangents turned by
instances, and instances inside a prefab's map placing cables and brush entities.

<out_tangent.vmap> (s2c_tangentinst), on a copy of <s2c_deformpath.vmap> without its cables and deformers:
- cable_static cables with collision on and interpolationType 2 (each node's own tangent type), their nodes' in and
  out tangents of type 2 (authored direction) and 3 (authored tangent) set to skew vectors: one in the world, one in a
  group placed by an instance moved (-4096 0 64) and turned (10 30 5);
- in that group also a cable_dynamic (an entity model) with the same tangents, a copy of the base's func_brush and a
  path deformer over a third tangent cable (holding a prop and a mesh); a cable_dynamic in the world too.
<out_prefab.vmap> (s2c_prefabinst), on the same cleaned copy: a CMapPrefab of maps/s2c_tangentinst.vmap at
(0 8000 0) turned (5 45 0), so the instance inside its map places cables and a brush entity (ledger 128's open item).
"""
import copy, os, sys, tempfile

sys.path.insert(0, os.path.dirname(__file__))
import path_deformer_map3 as m3  # noqa: E402
import path_deformer_map4 as m4  # noqa: E402

TANGENTS = [((2, (40, 25, -10)), (3, (-30, 60, 15))), ((3, (55, -20, 30)), (2, (10, 70, -25))),
            ((2, (-35, 45, 20)), (3, (60, 10, -5)))]


def tangent_cable(b, points):
    cable = b.path(points)
    cable.set("interpolationType", "int", "2")
    cable.set("collisionEnabled", "bool", "1")
    nodes = [x for x in cable.get("children") if isinstance(x, m3.pm.Element) and x.type == "CMapPathNode"]
    for node, ((it, iv), (ot, ov)) in zip(nodes, TANGENTS):
        node.set("inTangentType", "int", str(it))
        node.set("inTangent", "vector3", m3.vec(iv))
        node.set("outTangentType", "int", str(ot))
        node.set("outTangent", "vector3", m3.vec(ov))
    return cable


def main(base, out_tangent, out_prefab):
    work = tempfile.mkdtemp(prefix="cable_tangent_")
    b = m3.Builder(base, work, 0x8600)
    everything = [e for t in b.tops for e in m3.pm.walk(t)]
    by_id = {e.get("id"): e for e in everything}
    brush = next(e for e in everything if e.type == "CMapEntity" and e.get("entity_properties") is not None
                 and e.get("entity_properties").get("classname") == "func_brush")
    brush = copy.deepcopy(brush)
    brush.set("children", "element_array", [copy.deepcopy(by_id.get(c[1], c)) if isinstance(c, tuple) else c for c in brush.get("children")])
    m4.clean(b)
    world = b.world.get("children")
    world.append(tangent_cable(b, [(2900, -2400, -300), (3100, -2300, -280), (3300, -2400, -260)]))
    inside = tangent_cable(b, [(2900, -3200, -300), (3100, -3100, -280), (3300, -3200, -260)])
    target = tangent_cable(b, [(2900, -3600, -300), (3100, -3500, -280), (3300, -3600, -260)])
    deformer = b.deformer((3000, -3600, -300), (0, 0, 0), (128, 32, 32), target, [("start", 0.1)])
    b.fresh(brush)
    for e in m3.pm.walk(brush):
        if isinstance(e, m3.pm.Element) and e.get("origin") is not None:
            o = [float(x) for x in e.get("origin").split()]
            e.set("origin", "vector3", m3.vec((o[0] + 3000, o[1] - 3400, o[2] - 300)))
    dynamic = tangent_cable(b, [(2900, -2800, -300), (3100, -2700, -280), (3300, -2800, -260)])
    dynamic.get("entity_properties").set("classname", "string", "cable_dynamic")
    world.append(tangent_cable(b, [(2900, -2000, -300), (3100, -1900, -280), (3300, -2000, -260)]))
    world[-1].get("entity_properties").set("classname", "string", "cable_dynamic")
    group = b.group((3000, -3200, -300), [inside, dynamic, target, deformer, brush])
    instance = b.instance(group, m3.add((3000, -3200, -300), (-4096, 0, 64)), (10, 30, 5))
    world.extend([group, instance])
    b.write(work, out_tangent)
    print("wrote", out_tangent, "instance", instance.get("nodeID"), "next", b.node)

    p = m3.Builder(base, work, 0x8700)
    m4.clean(p)
    p.world.get("children").append(p.prefab("maps/s2c_tangentinst.vmap", (0, 8000, 0), (5, 45, 0), 1799009856))
    p.write(work, out_prefab)
    print("wrote", out_prefab)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3])
