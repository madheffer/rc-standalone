"""entitylist_refusal_probes.py [--addon s2c_rc_probe]: probe maps for the bake layouts MapEntityList refuses.

Written into <addon>'s content maps folder (CS2_DIR), from maps already there:
  - s2c_plainpf: s2c_pinst with its instance removed (its target group stays as a plain group): a prefab
    map with no instances.
  - s2c_aheadpf1: s2c_pinst with its instance moved ahead of its target group: in a prefab, an instance that
    joins the world ahead of a target with no other referrer.
  - s2c_aheadpf2: s2c_aheadpf1 plus a second instance of the target after it: one other referrer.
  - s2c_hidepf: s2c_rounds with prefabs: s2c_pinst7 force_hidden, s2c_plainpf editorOnly, s2c_plainpf
    force_hidden, then s2c_plainpf and s2c_pinst7 shown: hidden prefabs, with and without instances in
    their map, beside shown ones.
  - s2c_pfintarget: s2c_rounds with two groups in its world, each a relay and a prefab of s2c_plainpf;
    the first placed by two instances, the second by one: a prefab inside an instance target.
  - s2c_aheadprobe: s2c_rounds with a prefab of s2c_aheadpf1 and one of s2c_aheadpf2.
Each prefab or instance stands apart (its own origin and yaw), so the settle has nothing to stack.
dmxconvert both ways; element, node and reference ids are fresh and seeded.
"""
import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from prefab_nest_probes import CS2, Map, element, pm  # noqa: E402


def world_instance(m):
    return next(c for c in m.world.get("children") if isinstance(c, pm.Element) and c.type == "CMapInstance")


def instance(m, target, origin, yaw):
    return m.fresh(element("CMapInstance", [
        ("id", "elementid", ""), ("nodeID", "int", "0"), ("referenceID", "uint64", "0x0"),
        ("children", "element_array", []), ("variableTargetKeys", "string_array", []),
        ("variableNames", "string_array", []), ("relayPlugData", "DmePlugList", m.plugs()),
        ("connectionsData", "element_array", []), ("target", "element", target),
        ("origin", "vector3", origin), ("angles", "qangle", f"0 {yaw} 0"), ("scales", "vector3", "1 1 1"),
        ("transformLocked", "bool", "0"), ("force_hidden", "bool", "0"), ("editorOnly", "bool", "0"),
        ("customVisGroup", "string", ""), ("randomSeed", "int", "1"),
        ("tintColor", "color", "255 255 255 255"), ("visexclude", "bool", "0")]))


def group(m):
    return m.fresh(element("CMapGroup", [
        ("id", "elementid", ""), ("nodeID", "int", "0"), ("referenceID", "uint64", "0x0"),
        ("children", "element_array", []), ("variableTargetKeys", "string_array", []),
        ("variableNames", "string_array", []), ("relayPlugData", "DmePlugList", m.plugs()),
        ("connectionsData", "element_array", []), ("origin", "vector3", "0 0 0"), ("angles", "qangle", "0 0 0"),
        ("scales", "vector3", "1 1 1"), ("transformLocked", "bool", "0"), ("force_hidden", "bool", "0"),
        ("editorOnly", "bool", "0")]))


def ahead(m):
    """The world's instance moved to just before its target group in the world's children."""
    children = m.world.get("children")
    inst = world_instance(m)
    target = m.by_id[inst.get("target")]
    at = next(i for i, c in enumerate(children) if c is target or (not isinstance(c, pm.Element) and c[1] == target.get("id")))
    children.remove(inst)
    children.insert(at, inst)
    print("instance", inst.get("nodeID"), "ahead of target", target.get("nodeID"))
    return inst


def hidden(e, key):
    e.set(key, "bool", "1")
    return e


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--addon", default="s2c_rc_probe")
    a = p.parse_args()
    maps = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps")
    src = lambda name: os.path.join(maps, name + ".vmap")  # noqa: E731

    m = Map(src("s2c_pinst"), 2026102901)
    m.world.get("children").remove(world_instance(m))
    m.save(src("s2c_plainpf"))

    m = Map(src("s2c_pinst"), 2026102902)
    ahead(m)
    m.save(src("s2c_aheadpf1"))

    m = Map(src("s2c_pinst"), 2026102903)
    inst = ahead(m)
    m.world.get("children").append(instance(m, inst.get("target"), "-1024 -1024 0", 90))
    m.save(src("s2c_aheadpf2"))

    m = Map(src("s2c_rounds"), 2026102904)
    w = m.world.get("children")
    w.append(hidden(m.prefab("maps/s2c_pinst7.vmap", "4096 0 0", 0), "force_hidden"))
    w.append(hidden(m.prefab("maps/s2c_plainpf.vmap", "-4096 0 0", 90), "editorOnly"))
    w.append(hidden(m.prefab("maps/s2c_plainpf.vmap", "0 4096 0", 180), "force_hidden"))
    w.append(m.prefab("maps/s2c_plainpf.vmap", "0 -4096 0", 270))
    w.append(m.prefab("maps/s2c_pinst7.vmap", "4096 4096 0", 0))
    m.save(src("s2c_hidepf"))

    m = Map(src("s2c_rounds"), 2026102905)
    w = m.world.get("children")
    for k, placements in enumerate(((("6144 0 0", 0), ("6144 6144 0", 90)), (("-6144 0 0", 180),))):
        g = group(m)
        g.get("children").append(m.relay(f"g{k}_marker", "0 0 32", []))
        g.get("children").append(m.prefab("maps/s2c_plainpf.vmap", "256 0 0", 0))
        w.append(g)
        for origin, yaw in placements:
            w.append(instance(m, g.get("id"), origin, yaw))
    m.save(src("s2c_pfintarget"))

    m = Map(src("s2c_rounds"), 2026102906)
    w = m.world.get("children")
    w.append(m.prefab("maps/s2c_aheadpf1.vmap", "4096 0 0", 0))
    w.append(m.prefab("maps/s2c_aheadpf2.vmap", "-4096 0 0", 90))
    m.save(src("s2c_aheadprobe"))


if __name__ == "__main__":
    main()
