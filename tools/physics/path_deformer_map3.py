"""path_deformer_map3.py <s2c_deformpath.vmap> <out_nest.vmap> <out_prefabs.vmap>: the CMapDeformerPath cases
s2c_deformpath2 leaves open, in two maps built from copies of <s2c_deformpath.vmap> (path_deformer_map.py's output),
each deformer as path_deformer_map2.py builds one (a copy of the first deformer's target cable laid on the listed
points, and a deformer over it holding a copy of its prop and mesh).

<out_nest.vmap> (s2c_deformnest), path deformers inside path deformers:
- an outer deformer holding a mesh and an inner deformer whose target path stays in the world;
- the same with the inner deformer's target path a child of the outer deformer (the outer recentre moves it);
- a deformer whose own target path is its child;
- the second case in a group placed by an instance moved (-4096 0 64) and turned (0 30 0);
- the first case in a group placed by an instance moved (-4096 0 0) and turned (10 -20 5).

<out_prefabs.vmap> (s2c_deformpath3), prefabs of path deformer maps:
- two CMapPrefabs of maps/s2c_deformpath.vmap, at (8192 0 0) unturned and at (8192 -4096 128) turned (10 45 0);
- a CMapPrefab of maps/s2c_deformpath2.vmap (which holds a prefab of s2c_deformpath: a nested prefab) at
  (-8192 0 0) turned (0 180 0);
- a CMapPrefab of maps/s2c_deformnest.vmap at (0 9000 0) turned (0 60 0).
Valve's compile, captured with capture_deformer.py, gives the lattice each deformer fits."""
import copy, os, sys, tempfile

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "coverage"))
import probe_map as pm  # noqa: E402


def vec(v):
    return " ".join(f"{x:g}" for x in v)


def add(a, b):
    return tuple(x + y for x, y in zip(a, b))


class Builder:
    """A copy of the base map and path_deformer_map2.py's element makers, with fresh ids from <serial_base>."""

    def __init__(self, base, work, serial_base):
        text_path = os.path.join(work, os.path.basename(base) + ".txt")
        pm.convert(base, text_path, "keyvalues2")
        text = open(text_path, encoding="utf-8").read()
        self.header, self.tops = text.split("\n", 1)[0], pm.parse(text)
        everything = [e for t in self.tops for e in pm.walk(t)]
        by_id = {e.get("id"): e for e in everything}
        self.world = next(t for t in self.tops if t.type == "CMapRootElement").get("world")
        template = next(e for e in everything if e.type == "CMapDeformerPath")
        reference = template.get("targetPath").get("targetReferenceID")
        self.cable = next(e for e in everything if e.type == "CMapCable" and e.get("referenceID") == reference)
        kids = [by_id.get(x[1], x) if isinstance(x, tuple) else x for x in template.get("children")]
        self.prop = next(c for c in kids if isinstance(c, pm.Element) and c.type == "CMapEntity")
        self.mesh = next(c for c in kids if isinstance(c, pm.Element) and c.type == "CMapMesh")
        self.node = max(int(e.get("nodeID")) for e in everything if e.get("nodeID") is not None) + 1
        self.serial = 0
        self.serial_base = serial_base

    def fresh(self, e):
        for x in pm.walk(e):
            if x.get("id") is not None:
                self.serial += 1
                x.set("id", "elementid", f"defa{self.serial_base:04x}-0000-4000-8000-{self.serial:012x}")
            if x.get("nodeID") is not None:
                x.set("nodeID", "int", str(self.node))
                self.node += 1
            if x.get("referenceID") is not None:
                x.set("referenceID", "uint64", "0x%016x" % ((0xdefa << 48) + (self.serial_base << 32) + self.node))
        return e

    @staticmethod
    def element(type_, fields):
        e = pm.Element(type_)
        e.fields = [list(f) for f in fields]
        return e

    @staticmethod
    def common(origin, angles):
        return [("id", "elementid", ""), ("nodeID", "int", "0"), ("referenceID", "uint64", "0x0"),
                ("children", "element_array", []), ("variableTargetKeys", "string_array", []),
                ("variableNames", "string_array", []), ("origin", "vector3", origin), ("angles", "qangle", angles),
                ("scales", "vector3", "1 1 1"), ("transformLocked", "bool", "0"), ("force_hidden", "bool", "0"),
                ("editorOnly", "bool", "0")]

    def plugs(self):
        return self.element("DmePlugList", [("id", "elementid", ""), ("names", "string_array", []), ("dataTypes", "int_array", []),
                                            ("plugTypes", "int_array", []), ("descriptions", "string_array", [])])

    def path(self, points):
        path = copy.deepcopy(self.cable)
        nodes = [x for x in path.get("children") if isinstance(x, pm.Element) and x.type == "CMapPathNode"]
        others = [x for x in path.get("children") if not (isinstance(x, pm.Element) and x.type == "CMapPathNode")]
        laid = []
        for p in points:
            n = copy.deepcopy(nodes[0])
            n.set("origin", "vector3", vec(p))
            laid.append(n)
        path.set("children", "element_array", others + laid)
        path.set("origin", "vector3", vec(points[0]))
        path.set("angles", "qangle", "0 0 0")
        return self.fresh(path)

    def deformer(self, origin, angles, size, path, settings=(), extra=()):
        """A path deformer over <path> holding a prop and a mesh at its origin, then <extra>."""
        p = self.fresh(copy.deepcopy(self.prop))
        p.set("origin", "vector3", vec(origin))
        m = self.fresh(copy.deepcopy(self.mesh))
        m.set("origin", "vector3", vec(origin))
        fields = self.common(vec(origin), vec(angles))
        fields[3] = ("children", "element_array", [p, m])
        fields += [("customVisGroup", "string", ""), ("randomSeed", "int", "1959997979"), ("size", "vector3", vec(size))]
        d = self.element("CMapDeformerPath", fields)
        self.fresh(d)
        target = self.element("DmElement", [("id", "elementid", ""), ("name", "string", "targetPath"), ("referenceName", "string", ""),
                                            ("targetReferenceID", "uint64", path.get("referenceID"))])
        self.fresh(target)
        d.fields.append(["targetPath", "DmElement", target])
        for key, value in settings:
            d.fields.append([key, "float", f"{value:g}"])
        d.get("children").extend(extra)
        return d

    def mesh_at(self, origin):
        m = self.fresh(copy.deepcopy(self.mesh))
        m.set("origin", "vector3", vec(origin))
        return m

    def group(self, origin, children):
        g = self.fresh(self.element("CMapGroup", self.common(vec(origin), "0 0 0")))
        g.get("children").extend(children)
        return g

    def instance(self, group, origin, angles):
        return self.fresh(self.element("CMapInstance", self.common(vec(origin), vec(angles)) + [
            ("relayPlugData", "DmePlugList", self.plugs()), ("connectionsData", "element_array", []), ("target", "element", group.get("id"))]))

    def prefab(self, target_map, origin, angles, seed):
        return self.fresh(self.element("CMapPrefab", self.common(vec(origin), vec(angles)) + [
            ("name", "string", "path deformer prefab"), ("relayPlugData", "DmePlugList", self.plugs()), ("connectionsData", "element_array", []),
            ("target", "element", ""), ("variableOverrideNames", "string_array", []), ("variableOverrideValues", "string_array", []),
            ("customVisGroup", "string", ""), ("randomSeed", "int", str(seed)), ("tintColor", "color", "255 255 255 255"),
            ("visexclude", "bool", "0"), ("targetMapPath", "string", target_map), ("targetName", "string", ""),
            ("fixupEntityNames", "bool", "0"), ("useTargetNameAsPrefix", "bool", "0"), ("loadIfNested", "bool", "1"),
            ("prefabRuntimeEntity", "bool", "0"), ("loadAtRuntime", "bool", "0")]))

    def write(self, work, out):
        text_out = os.path.join(work, os.path.basename(out) + ".txt")
        open(text_out, "w", encoding="utf-8").write(pm.emit(self.tops, self.header))
        pm.convert(text_out, out, "binary")


def nested(b, y, inner_path_inside):
    """An outer deformer at (3000 y -300) holding a mesh and an inner deformer; returns (nodes for the parent, outer)."""
    outer_path = b.path([(2800, y, -300), (3000, y + 100, -300), (3200, y, -280)])
    inner_path = b.path([(2900, y, -200), (3000, y - 40, -190), (3100, y - 50, -200)])
    inner = b.deformer((3000, y, -200), (0, 15, 0), (96, 24, 24), inner_path, [("start", 0.1), ("end", 0.9)])
    extra = [b.mesh_at((3000, y + 64, -300)), inner] + ([inner_path] if inner_path_inside else [])
    outer = b.deformer((3000, y, -300), (0, 0, 0), (256, 64, 64), outer_path, [("maxError", 0.3)], extra)
    return [outer_path, outer] + ([] if inner_path_inside else [inner_path]), outer


def nest_map(base, out, work):
    b = Builder(base, work, 0x8100)
    world = b.world.get("children")
    world.extend(nested(b, -2400, False)[0])
    world.extend(nested(b, -3200, True)[0])
    own_path = b.path([(2900, -4000, -300), (3100, -3900, -300), (3300, -4000, -280)])
    lone = b.deformer((3000, -4000, -300), (0, 0, 0), (128, 32, 32), own_path, [("start", 0.2)], [own_path])
    world.append(lone)
    g1 = b.group((3000, -4800, -300), nested(b, -4800, True)[0])
    i1 = b.instance(g1, add((3000, -4800, -300), (-4096, 0, 64)), (0, 30, 0))
    g2 = b.group((3000, -5600, -300), nested(b, -5600, False)[0])
    i2 = b.instance(g2, add((3000, -5600, -300), (-4096, 0, 0)), (10, -20, 5))
    world.extend([g1, i1, g2, i2])
    b.write(work, out)
    print("wrote", out, "instances", i1.get("nodeID"), i2.get("nodeID"), "next", b.node)


def prefab_map(base, out, work):
    b = Builder(base, work, 0x8200)
    b.world.get("children").extend([
        b.prefab("maps/s2c_deformpath.vmap", (8192, 0, 0), (0, 0, 0), 1799009847),
        b.prefab("maps/s2c_deformpath.vmap", (8192, -4096, 128), (10, 45, 0), 1799009848),
        b.prefab("maps/s2c_deformpath2.vmap", (-8192, 0, 0), (0, 180, 0), 1799009849),
        b.prefab("maps/s2c_deformnest.vmap", (0, 9000, 0), (0, 60, 0), 1799009850),
    ])
    b.write(work, out)
    print("wrote", out, "next", b.node)


def main(base, out_nest, out_prefabs):
    work = tempfile.mkdtemp(prefix="path_deformer3_")
    nest_map(base, out_nest, work)
    prefab_map(base, out_prefabs, work)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3])
