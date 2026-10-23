"""prefab_nest_probes.py [--addon s2c_rc_probe]: the probe maps for the prefab layouts the lump port refuses.

Written into <addon>'s content maps folder (CS2_DIR), from maps already there:
  - s2c_fixinner: s2c_pinst (one instance) plus three named logic_relays wired to each
    other, one output naming an entity outside the map and one whose parameter names a relay.
  - s2c_fixmid: s2c_pinst7 plus the same relays, and three prefabs of s2c_fixinner:
    fixupEntityNames on, on with useTargetNameAsPrefix and a targetName, and off.
  - s2c_prefabprobe7: s2c_rounds with a prefab of s2c_prefabprobe5 (a prefab in a prefab,
    an instance inside the inner one) and, after it, a prefab of s2c_pinst7.
  - s2c_prefabprobe8: s2c_pinst with its instance's group copied, the instance moved
    inside the copy and an instance of the copy placed (an instance of the map's own nested
    in a target group), then a prefab of s2c_pinst7 (instances inside a prefab).
  - s2c_prefabprobe9: s2c_rounds with the relays, prefabs of s2c_fixmid with fixup on, on
    with a targetName prefix and off, and one of s2c_fixinner with a targetName but the prefix
    flag off: fixup prefabs inside fixup prefabs, both prefix kinds.
  - s2c_lightinner: s2c_pinst with its light group's lights and prop also placed directly in its world.
  - s2c_prefabprobe10: s2c_rounds with a group (a relay and a fixup prefab of s2c_pinst7 whose
    useTargetNameAsPrefix is on with no targetName) placed by two instances: a prefab in an
    instanced group.
  - s2c_prefabprobe11: s2c_rounds with three prefabs of s2c_lightinner (the second with fixup):
    preprocess-written light keys on a prefab map's nodes exported three times.
  - s2c_prefabprobe12: s2c_rounds with three prefabs of s2c_lightinner at one spot: 18 overlapping
    omni2 lights for 4 shadow slots, so which get one shows the order the lights are gathered in.
Each prefab stands apart (its own origin and yaw), so the settle has nothing to stack.
dmxconvert both ways; element, node and reference ids are fresh and seeded.
"""
import argparse
import copy
import os
import random
import sys
import tempfile
import uuid

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "coverage"))
import probe_map as pm  # noqa: E402

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")


def element(type_, fields):
    e = pm.Element(type_)
    e.fields = [list(f) for f in fields]
    return e


class Map:
    """One map read to keyvalues2, with fresh ids handed out past its highest node id."""

    def __init__(self, path, seed):
        self.work = tempfile.mkdtemp(prefix="prefab_nest_")
        text = os.path.join(self.work, "in.txt")
        pm.convert(path, text, "keyvalues2")
        body = open(text, encoding="utf-8", errors="replace").read()
        self.header = body.split("\n", 1)[0]
        self.tops = pm.parse(body)
        self.all = [e for t in self.tops for e in pm.walk(t)]
        self.by_id = {e.get("id"): e for e in self.all}
        self.world = next(t for t in self.tops if t.type == "CMapRootElement").get("world")
        self.node = max(int(e.get("nodeID")) for e in self.all if e.get("nodeID") is not None) + 1
        self.rng = random.Random(seed)

    def fresh(self, e):
        for x in pm.walk(e):
            if x.get("id") is not None:
                x.set("id", "elementid", str(uuid.UUID(int=self.rng.getrandbits(128), version=4)))
            if x.get("nodeID") is not None:
                x.set("nodeID", "int", str(self.node))
                self.node += 1
            if x.get("referenceID") is not None:
                x.set("referenceID", "uint64", "0x%016x" % self.rng.getrandbits(64))
        return e

    def plugs(self):
        return element("DmePlugList", [("id", "elementid", ""), ("names", "string_array", []),
                                       ("dataTypes", "int_array", []), ("plugTypes", "int_array", []),
                                       ("descriptions", "string_array", [])])

    def relay(self, name, origin, connections):
        """A logic_relay named <name>; connections are (output, target, input, parameter, delay)."""
        conns = [element("DmeConnectionData", [
            ("id", "elementid", ""), ("outputName", "string", o), ("targetType", "int", "7"),
            ("targetName", "string", t), ("inputName", "string", i), ("overrideParam", "string", p),
            ("delay", "float", d), ("timesToFire", "int", "-1")]) for o, t, i, p, d in connections]
        props = element("EditGameClassProps", [("id", "elementid", ""), ("classname", "string", "logic_relay"),
                                               ("targetname", "string", name)])
        e = element("CMapEntity", [
            ("id", "elementid", ""), ("nodeID", "int", "0"), ("referenceID", "uint64", "0x0"),
            ("children", "element_array", []), ("variableTargetKeys", "string_array", []),
            ("variableNames", "string_array", []), ("relayPlugData", "DmePlugList", self.plugs()),
            ("connectionsData", "element_array", conns), ("entity_properties", "EditGameClassProps", props),
            ("hitNormal", "vector3", "0 0 1"), ("isProceduralEntity", "bool", "0"),
            ("origin", "vector3", origin), ("angles", "qangle", "0 0 0"), ("scales", "vector3", "1 1 1"),
            ("transformLocked", "bool", "0"), ("force_hidden", "bool", "0"), ("editorOnly", "bool", "0")])
        return self.fresh(e)

    def relays(self, prefix, z):
        """Three relays wired among themselves, one output to a name outside, one parameter naming a relay."""
        a, b, c = prefix + "relay_a", prefix + "relay_b", prefix + "relay_c"
        return [self.relay(a, f"0 0 {z}", [("OnTrigger", b, "Trigger", "", "0"),
                                           ("OnSpawn", "outer_relay", "Trigger", "", "0.5")]),
                self.relay(b, f"32 0 {z}", [("OnTrigger", c, "Trigger", "", "0"),
                                            ("OnTrigger", a, "AddOutput", "targetname " + a, "0")]),
                self.relay(c, f"64 0 {z}", [("OnTrigger", a, "Kill", "", "1"),
                                            ("OnTrigger", "!self", "SetParent", b, "0")])]

    def prefab(self, target, origin, yaw, fixup=False, use_name=False, name=""):
        e = element("CMapPrefab", [
            ("id", "elementid", ""), ("name", "string", "probe prefab"), ("nodeID", "int", "0"),
            ("referenceID", "uint64", "0x0"), ("children", "element_array", []),
            ("variableTargetKeys", "string_array", []), ("variableNames", "string_array", []),
            ("relayPlugData", "DmePlugList", self.plugs()), ("connectionsData", "element_array", []),
            ("target", "element", ""), ("variableOverrideNames", "string_array", []),
            ("variableOverrideValues", "string_array", []), ("origin", "vector3", origin),
            ("angles", "qangle", f"0 {yaw} 0"), ("scales", "vector3", "1 1 1"),
            ("transformLocked", "bool", "0"), ("force_hidden", "bool", "0"), ("editorOnly", "bool", "0"),
            ("customVisGroup", "string", ""), ("randomSeed", "int", str(self.rng.getrandbits(31))),
            ("tintColor", "color", "255 255 255 255"), ("visexclude", "bool", "0"),
            ("targetMapPath", "string", target), ("targetName", "string", name),
            ("fixupEntityNames", "bool", "1" if fixup else "0"),
            ("useTargetNameAsPrefix", "bool", "1" if use_name else "0"),
            ("loadIfNested", "bool", "1"), ("prefabRuntimeEntity", "bool", "0"), ("loadAtRuntime", "bool", "0")])
        return self.fresh(e)

    def save(self, out):
        text = os.path.join(self.work, "out.txt")
        open(text, "w", encoding="utf-8", newline="\n").write(pm.emit(self.tops, self.header))
        pm.convert(text, out, "binary")
        print("wrote", out, "next node", self.node)


def nest_own_instance(m):
    """s2c_pinst's instance moved into a copy of its target group, and an instance of that copy placed."""
    children = m.world.get("children")
    inst = next(c for c in children if isinstance(c, pm.Element) and c.type == "CMapInstance")
    target = m.by_id[inst.get("target")]
    holder = element("CMapGroup", [(f[0], f[1], f[2]) for f in target.fields])
    holder = m.fresh(copy.deepcopy(holder))
    holder.set("children", "element_array", [])
    holder.set("origin", "vector3", "0 0 0")
    children.remove(inst)
    holder.get("children").append(inst)
    children.append(holder)
    outer = m.fresh(copy.deepcopy(inst))
    outer.set("target", "element", holder.get("id"))
    outer.set("origin", "vector3", "512 -256 0")
    outer.set("angles", "qangle", "0 90 0")
    children.append(outer)
    print("holder group", holder.get("nodeID"), "inner instance", inst.get("nodeID"), "outer instance", outer.get("nodeID"))


def place_lights(m):
    """s2c_pinst's light group copied into its world as plain entities, beside the instance."""
    inst = next(c for c in m.world.get("children") if isinstance(c, pm.Element) and c.type == "CMapInstance")
    target = m.by_id[inst.get("target")]
    for child in target.get("children"):
        e = child if isinstance(child, pm.Element) else m.by_id[child[1]]
        e = m.fresh(copy.deepcopy(e))
        x, y, z = (float(v) for v in e.get("origin").split())
        e.set("origin", "vector3", f"{x - 512:g} {y:g} {z:g}")
        # Stationary and shadowed, so the lights take part in the shadow slot step.
        props = e.get("entity_properties")
        if isinstance(props, pm.Element) and props.get("classname") == "light_omni2":
            for key, value in (("directlight", "3"), ("castshadows", "1"), ("brightness", "1"), ("range", "512")):
                props.set(key, "string", value)
        m.world.get("children").append(e)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--addon", default="s2c_rc_probe")
    a = p.parse_args()
    maps = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps")
    src = lambda name: os.path.join(maps, name + ".vmap")  # noqa: E731

    m = Map(src("s2c_pinst"), 2026102301)
    m.world.get("children").extend(m.relays("", 96))
    m.save(src("s2c_fixinner"))

    m = Map(src("s2c_pinst7"), 2026102302)
    w = m.world.get("children")
    w.extend(m.relays("", 96))
    w.append(m.prefab("maps/s2c_fixinner.vmap", "-1024 1024 0", 0, fixup=True))
    w.append(m.prefab("maps/s2c_fixinner.vmap", "-2048 1024 0", 90, fixup=True, use_name=True, name="tnpre"))
    w.append(m.prefab("maps/s2c_fixinner.vmap", "-3072 1024 0", 180))
    m.save(src("s2c_fixmid"))

    m = Map(src("s2c_rounds"), 2026102303)
    w = m.world.get("children")
    w.append(m.prefab("maps/s2c_prefabprobe5.vmap", "4096 0 0", 90))
    w.append(m.prefab("maps/s2c_pinst7.vmap", "-4096 0 0", 0))
    m.save(src("s2c_prefabprobe7"))

    m = Map(src("s2c_pinst"), 2026102304)
    nest_own_instance(m)
    m.world.get("children").append(m.prefab("maps/s2c_pinst7.vmap", "4096 2048 0", 0))
    m.save(src("s2c_prefabprobe8"))

    m = Map(src("s2c_pinst"), 2026102306)
    place_lights(m)
    m.save(src("s2c_lightinner"))

    m = Map(src("s2c_rounds"), 2026102307)
    w = m.world.get("children")
    group = m.fresh(element("CMapGroup", [
        ("id", "elementid", ""), ("nodeID", "int", "0"), ("referenceID", "uint64", "0x0"),
        ("children", "element_array", []), ("variableTargetKeys", "string_array", []),
        ("variableNames", "string_array", []), ("relayPlugData", "DmePlugList", m.plugs()),
        ("connectionsData", "element_array", []), ("origin", "vector3", "0 0 0"), ("angles", "qangle", "0 0 0"),
        ("scales", "vector3", "1 1 1"), ("transformLocked", "bool", "0"), ("force_hidden", "bool", "0"),
        ("editorOnly", "bool", "0")]))
    group.get("children").append(m.relay("g_marker", "0 0 32", []))
    group.get("children").append(m.prefab("maps/s2c_pinst7.vmap", "256 0 0", 0, fixup=True, use_name=True))
    m.tops.append(group)
    for origin, yaw in (("6144 0 0", 0), ("6144 6144 0", 90)):
        inst = m.fresh(element("CMapInstance", [
            ("id", "elementid", ""), ("nodeID", "int", "0"), ("referenceID", "uint64", "0x0"),
            ("children", "element_array", []), ("variableTargetKeys", "string_array", []),
            ("variableNames", "string_array", []), ("relayPlugData", "DmePlugList", m.plugs()),
            ("connectionsData", "element_array", []), ("target", "element", group.get("id")),
            ("origin", "vector3", origin), ("angles", "qangle", f"0 {yaw} 0"), ("scales", "vector3", "1 1 1"),
            ("transformLocked", "bool", "0"), ("force_hidden", "bool", "0"), ("editorOnly", "bool", "0"),
            ("customVisGroup", "string", ""), ("randomSeed", "int", "1"),
            ("tintColor", "color", "255 255 255 255"), ("visexclude", "bool", "0")]))
        w.append(inst)
    m.save(src("s2c_prefabprobe10"))

    m = Map(src("s2c_rounds"), 2026102308)
    w = m.world.get("children")
    for k, (origin, yaw) in enumerate((("-6144 0 0", 0), ("-6144 6144 0", 90), ("-6144 -6144 0", 45))):
        w.append(m.prefab("maps/s2c_lightinner.vmap", origin, yaw, fixup=k == 1))
    m.save(src("s2c_prefabprobe11"))

    m = Map(src("s2c_rounds"), 2026102309)
    w = m.world.get("children")
    for k in range(3):
        w.append(m.prefab("maps/s2c_lightinner.vmap", "-6144 0 0", 0))
    m.save(src("s2c_prefabprobe12"))

    m = Map(src("s2c_rounds"), 2026102305)
    w = m.world.get("children")
    w.extend(m.relays("", 96))
    w.append(m.relay("outer_relay", "0 64 96", [("OnTrigger", "relay_a", "Trigger", "", "0")]))
    w.append(m.prefab("maps/s2c_fixmid.vmap", "8192 0 0", 0, fixup=True))
    w.append(m.prefab("maps/s2c_fixmid.vmap", "8192 8192 0", 90, fixup=True, use_name=True, name="outerpre"))
    w.append(m.prefab("maps/s2c_fixmid.vmap", "-8192 8192 0", 180))
    w.append(m.prefab("maps/s2c_fixinner.vmap", "-8192 -8192 0", 270, fixup=True, name="named_no_prefix"))
    m.save(src("s2c_prefabprobe9"))


if __name__ == "__main__":
    main()
