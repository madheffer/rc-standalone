"""instance_nest_map.py <s2c_deformpath.vmap> <out.vmap>: instances inside instance targets (s2c_instnest).

Built on a copy of <s2c_deformpath.vmap> with path_deformer_map3.py's Builder:
- an inner group holding a mesh, a cable, a path deformer over its own cable (holding a prop and a mesh), a copy of
  the base's lattice deformer (with its props) and a copy of the base's func_brush;
- nested: an outer group holding the inner group and an instance of it moved (0 600 50) and turned (0 25 0), placed
  by an instance moved (-4096 0 64) and turned (5 40 0) (the inner instance's target is inside the outer target, so
  the inner copy is made from the outer copy);
- control: the same with the inner group outside the outer one (the inner copy is made from the original).
Valve's compile (capture_deformer.py, capture_deformerevents.py, corpus.py) gives what each copy becomes."""
import copy, os, sys, tempfile

sys.path.insert(0, os.path.dirname(__file__))
import path_deformer_map3 as m3  # noqa: E402


def inner_group(b, y):
    origin = (3000, y, -300)
    cable = b.path([(2900, y - 200, -300), (3100, y - 150, -290), (3300, y - 200, -300)])
    own = b.path([(2900, y + 200, -300), (3100, y + 300, -290), (3300, y + 200, -280)])
    deformer = b.deformer((3000, y + 200, -300), (0, 0, 0), (128, 32, 32), own, [("start", 0.1)])
    lattice = b.fresh(copy.deepcopy(b.lattice))
    shift = (0, y - 1200 - 0, 0)
    for e in m3.pm.walk(lattice):
        if isinstance(e, m3.pm.Element) and e.get("origin") is not None:
            o = [float(x) for x in e.get("origin").split()]
            e.set("origin", "vector3", m3.vec((o[0] + 1800, o[1] + y + 1200, o[2] + 100)))
    brush = b.fresh(copy.deepcopy(b.brush))
    for e in m3.pm.walk(brush):
        if isinstance(e, m3.pm.Element) and e.get("origin") is not None:
            o = [float(x) for x in e.get("origin").split()]
            e.set("origin", "vector3", m3.vec((o[0] + 3000, o[1] + y + 500, o[2] - 300)))
    return b.group(origin, [b.mesh_at((3000, y, -300)), cable, own, deformer, lattice, brush])


def main(base, out):
    work = tempfile.mkdtemp(prefix="instance_nest_")
    b = m3.Builder(base, work, 0x8500)
    everything = [e for t in b.tops for e in m3.pm.walk(t)]
    by_id = {e.get("id"): e for e in everything}

    def resolved(e):
        return by_id.get(e[1], e) if isinstance(e, tuple) else e

    b.lattice = next(e for e in everything if e.type == "CMapDeformerLattice")
    b.brush = next(e for e in everything if e.type == "CMapEntity" and e.get("entity_properties") is not None
                   and e.get("entity_properties").get("classname") == "func_brush")
    for e in (b.lattice, b.brush):
        e.set("children", "element_array", [copy.deepcopy(resolved(c)) for c in e.get("children")])
    world = b.world.get("children")

    inner = inner_group(b, -2400)
    inner_instance = b.instance(inner, m3.add((3000, -2400, -300), (0, 600, 50)), (0, 25, 0))
    outer = b.group((3000, -2400, -300), [inner, inner_instance])
    outer_instance = b.instance(outer, m3.add((3000, -2400, -300), (-4096, 0, 64)), (5, 40, 0))
    world.extend([outer, outer_instance])

    inner2 = inner_group(b, -6000)
    inner2_instance = b.instance(inner2, m3.add((3000, -6000, -300), (0, 600, 50)), (0, 25, 0))
    outer2 = b.group((3000, -6000, -300), [inner2_instance])
    outer2_instance = b.instance(outer2, m3.add((3000, -6000, -300), (-4096, 0, 64)), (5, 40, 0))
    world.extend([inner2, outer2, outer2_instance])
    b.write(work, out)
    print("wrote", out, "outer", outer_instance.get("nodeID"), "control", outer2_instance.get("nodeID"), "next", b.node)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
