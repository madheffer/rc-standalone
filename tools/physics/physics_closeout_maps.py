"""physics_closeout_maps.py <maps dir>: the probes for the physics rules ported from the decompile alone (ledger 128, 130).

From <maps dir>/deformerprobe2.vmap (deformer_probe2.py's output), writes:
- s2c_pinstbrush.vmap: the probe with a group holding a copy of its first brush entity (an entity
  with a mesh), placed by a turned instance; the prefab target below;
- s2c_physmore.vmap: the probe with
  - a group holding a copy of its lattice deformer and the props under it, placed by a turned
    instance (the clone copies the lattice's control point locators, MapInstances.ClonedLocators);
  - a turned CMapPrefab of maps/s2c_pinstbrush.vmap (an instance inside a prefab's map placing a
    brush entity, EntityPhysicsModels);
  - one prop_physics, so the compile runs the settle;
- s2c_physbezier.vmap: the probe with its lattice in BEZIER mode and one prop_physics: which handles
  the load leaves (capture_deformer.py records the struct's tangents in mode 3).
"""
import copy, os, sys, tempfile

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "coverage"))
import probe_map as pm  # noqa: E402


def load(path, work, name):
    text_path = os.path.join(work, name)
    pm.convert(path, text_path, "keyvalues2")
    text = open(text_path, encoding="utf-8").read()
    return text.split("\n", 1)[0], pm.parse(text)


def element(type_, fields):
    e = pm.Element(type_)
    e.fields = [list(f) for f in fields]
    return e


def common(origin, angles):
    return [("id", "elementid", ""), ("nodeID", "int", "0"), ("referenceID", "uint64", "0x0"),
            ("children", "element_array", []), ("variableTargetKeys", "string_array", []),
            ("variableNames", "string_array", []), ("origin", "vector3", origin), ("angles", "qangle", angles),
            ("scales", "vector3", "1 1 1"), ("transformLocked", "bool", "0"), ("force_hidden", "bool", "0"),
            ("editorOnly", "bool", "0")]


def plugs():
    return element("DmePlugList", [("id", "elementid", ""), ("names", "string_array", []), ("dataTypes", "int_array", []),
                                   ("plugTypes", "int_array", []), ("descriptions", "string_array", [])])


class Builder:
    """One map made from the base: fresh element ids and node ids past the base's."""

    def __init__(self, base, work, tag):
        self.header, self.tops = load(base, work, f"{tag}.txt")
        self.everything = [e for t in self.tops for e in pm.walk(t)]
        self.by_id = {e.get("id"): e for e in self.everything}
        self.world = next(t for t in self.tops if t.type == "CMapRootElement").get("world")
        self.node = max(int(e.get("nodeID")) for e in self.everything if e.get("nodeID") is not None) + 1
        self.serial = 0
        self.tag = tag

    def fresh(self, e):
        for x in pm.walk(e):
            if x.get("id") is not None:
                self.serial += 1
                x.set("id", "elementid", f"c1{self.tag[:3].encode().hex()}-0000-4000-8000-{self.serial:012x}")
            if x.get("nodeID") is not None:
                x.set("nodeID", "int", str(self.node))
                self.node += 1
            if x.get("referenceID") is not None:
                x.set("referenceID", "uint64", "0x%016x" % (0xc105e00000000000 + self.node))
        return e

    def inline(self, e, seen=()):
        e = copy.deepcopy(e)
        for f in e.fields:
            if isinstance(f[2], list):
                f[2] = [self.inline(self.by_id[x[1]], seen + (x[1],)) if isinstance(x, tuple) and x[1] in self.by_id and x[1] not in seen
                        else self.inline(x, seen) if isinstance(x, pm.Element) else x for x in f[2]]
            elif isinstance(f[2], pm.Element):
                f[2] = self.inline(f[2], seen)
        return e

    def instanced(self, node, offset, angles):
        """A group holding a copy of node where it stands, and a turned instance of it moved by offset."""
        group = element("CMapGroup", common(node.get("origin") or "0 0 0", "0 0 0"))
        group.get("children").append(self.inline(node))
        self.fresh(group)
        at = " ".join(f"{float(v) + o:g}" for v, o in zip((node.get("origin") or "0 0 0").split(), offset))
        instance = self.fresh(element("CMapInstance", common(at, angles) + [
            ("relayPlugData", "DmePlugList", plugs()), ("connectionsData", "element_array", []), ("target", "element", group.get("id"))]))
        self.world.get("children").extend([group, instance])
        return group, instance

    def prefab(self, target, origin, angles):
        p = self.fresh(element("CMapPrefab", common(origin, angles) + [
            ("name", "string", "closeout prefab"), ("relayPlugData", "DmePlugList", plugs()), ("connectionsData", "element_array", []),
            ("target", "element", ""), ("variableOverrideNames", "string_array", []), ("variableOverrideValues", "string_array", []),
            ("customVisGroup", "string", ""), ("randomSeed", "int", "1799009846"), ("tintColor", "color", "255 255 255 255"),
            ("visexclude", "bool", "0"), ("targetMapPath", "string", target), ("targetName", "string", ""),
            ("fixupEntityNames", "bool", "0"), ("useTargetNameAsPrefix", "bool", "0"), ("loadIfNested", "bool", "1"),
            ("prefabRuntimeEntity", "bool", "0"), ("loadAtRuntime", "bool", "0")]))
        self.world.get("children").append(p)
        return p

    def prop_physics(self, origin):
        prop = element("CMapEntity", [("id", "elementid", ""), ("nodeID", "int", "0"), ("referenceID", "uint64", "0x0"),
                                      ("children", "element_array", []), ("variableTargetKeys", "string_array", []),
                                      ("variableNames", "string_array", [])])
        keys = element("EditGameClassProps", [("id", "elementid", ""), ("classname", "string", "prop_physics"),
                                              ("model", "string", "models/cs_italy/crate/italy_wood_box_1.vmdl")])
        prop.fields += [["relayPlugData", "DmePlugList", plugs()], ["connectionsData", "element_array", []],
                        ["entity_properties", "EditGameClassProps", keys], ["hitNormal", "vector3", "0 0 1"],
                        ["isProceduralEntity", "bool", "0"], ["origin", "vector3", origin], ["angles", "qangle", "0 0 0"],
                        ["scales", "vector3", "1 1 1"], ["transformLocked", "bool", "0"], ["force_hidden", "bool", "0"],
                        ["editorOnly", "bool", "0"]]
        self.world.get("children").append(self.fresh(prop))

    def write(self, work, out):
        text_out = os.path.join(work, f"{self.tag}_out.txt")
        open(text_out, "w", encoding="utf-8").write(pm.emit(self.tops, self.header))
        pm.convert(text_out, out, "binary")
        print("wrote", out, "next node", self.node)


def main(maps):
    base = os.path.join(maps, "deformerprobe2.vmap")
    work = tempfile.mkdtemp(prefix="closeout_")

    b = Builder(base, work, "pinstbrush")
    # A buy zone: its tool material resolves, where the probe's func_brush names a missing one and has no model.
    brush = next(e for e in b.everything if e.type == "CMapEntity"
                 and e.get("entity_properties").get("classname") == "func_buyzone"
                 and any(isinstance(c, pm.Element) and c.type == "CMapMesh" for c in e.get("children")))
    b.instanced(brush, (0, -2048, 0), "0 45 0")
    b.write(work, os.path.join(maps, "s2c_pinstbrush.vmap"))

    m = Builder(base, work, "physmore")
    lattice = next(e for e in m.everything if e.type == "CMapDeformerLattice")
    m.instanced(lattice, (0, 2048, 32), "0 30 0")
    m.prefab("maps/s2c_pinstbrush.vmap", "4096 0 0", "0 90 0")
    m.prop_physics("0 224 96")
    m.write(work, os.path.join(maps, "s2c_physmore.vmap"))

    z = Builder(base, work, "physbezier")
    for e in z.everything:
        if e.type == "CMapDeformerLattice":
            e.set("interpolationMode", "string", "BEZIER")
    z.prop_physics("0 224 96")
    z.write(work, os.path.join(maps, "s2c_physbezier.vmap"))


if __name__ == "__main__":
    main(sys.argv[1])
