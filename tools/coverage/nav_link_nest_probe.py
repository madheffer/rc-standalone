"""nav_link_nest_probe.py [--addon s2c_rc_probe]: nav links where the collector (resourcecompiler 180f203b0) meets
the instance bake (MapDoc_BakeInstances, run by 180f1d770 before the compile steps) and the search (180fc6f40).

Written into <addon>'s content maps folder (CS2_DIR), from s2c_navlink (nav_link_specimen.py):
  - s2c_navlinkpf: s2c_navlink's world with only its three links (the prefab's map).
  - s2c_navlink3: s2c_navlink plus
      a plain group (never instanced) holding link glink;
      target group T holding links tlink and tlink_b, placed by instance I1 (yaw 90: is locatoroffset turned?)
      and I2 (scales 2 1 1: is it scaled?);
      group T2 holding an instance of T (yaw 45) and link t2link, placed by instance I3 (yaw 30): a nested copy;
      a prefab of s2c_navlinkpf (yaw 90);
      a hidden link (force_hidden) and an editorOnly one (the search's flag 1: the visibility state).
  - s2c_navlink4: s2c_navlink3 without the prefab (links in prefabs are refused: their placement is not ported).
Valve's records (capture_navgen.py) give each link's start and end and the search's order; the lump gives the copies.
"""
import argparse
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "entities"))
import prefab_nest_probes as pn  # noqa: E402

pm = pn.pm


def resolve(m, item):
    return m.by_id[item[1]] if isinstance(item, tuple) else m.by_id.get(item, item) if isinstance(item, str) else item


def link(m, name, origin, offset, hidden=False, editor_only=False):
    e = pn.element("CMapNavLink", [
        ("id", "elementid", ""), ("nodeID", "int", "0"), ("referenceID", "uint64", "0x0"),
        ("children", "element_array", []), ("variableTargetKeys", "string_array", []),
        ("variableNames", "string_array", []), ("relayPlugData", "DmePlugList", m.plugs()),
        ("connectionsData", "element_array", []),
        ("entity_properties", "EditGameClassProps", pn.element("EditGameClassProps", [
            ("id", "elementid", ""), ("classname", "string", "ai_nav_link_area"), ("targetname", "string", name)])),
        ("hitNormal", "vector3", "0 0 1"), ("isProceduralEntity", "bool", "0"),
        ("origin", "vector3", origin), ("angles", "qangle", "0 0 0"), ("scales", "vector3", "1 1 1"),
        ("transformLocked", "bool", "0"), ("force_hidden", "bool", "1" if hidden else "0"),
        ("editorOnly", "bool", "1" if editor_only else "0"), ("locatoroffset", "vector3", offset)])
    return m.fresh(e)


def group(m, origin, children):
    g = m.fresh(pn.element("CMapGroup", [
        ("id", "elementid", ""), ("nodeID", "int", "0"), ("referenceID", "uint64", "0x0"),
        ("children", "element_array", []), ("variableTargetKeys", "string_array", []),
        ("variableNames", "string_array", []), ("relayPlugData", "DmePlugList", m.plugs()),
        ("connectionsData", "element_array", []), ("origin", "vector3", origin), ("angles", "qangle", "0 0 0"),
        ("scales", "vector3", "1 1 1"), ("transformLocked", "bool", "0"), ("force_hidden", "bool", "0"),
        ("editorOnly", "bool", "0")]))
    g.get("children").extend(children)
    return g


def instance(m, target, origin, yaw, scales="1 1 1"):
    return m.fresh(pn.element("CMapInstance", [
        ("id", "elementid", ""), ("nodeID", "int", "0"), ("referenceID", "uint64", "0x0"),
        ("children", "element_array", []), ("variableTargetKeys", "string_array", []),
        ("variableNames", "string_array", []), ("relayPlugData", "DmePlugList", m.plugs()),
        ("connectionsData", "element_array", []), ("target", "element", target.get("id")),
        ("origin", "vector3", origin), ("angles", "qangle", f"0 {yaw} 0"), ("scales", "vector3", scales),
        ("transformLocked", "bool", "0"), ("force_hidden", "bool", "0"), ("editorOnly", "bool", "0"),
        ("customVisGroup", "string", ""), ("randomSeed", "int", "1"),
        ("tintColor", "color", "255 255 255 255"), ("visexclude", "bool", "0")]))


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--addon", default="s2c_rc_probe")
    a = p.parse_args()
    maps = os.path.join(pn.CS2, "content", "csgo_addons", a.addon, "maps")
    src = lambda name: os.path.join(maps, name + ".vmap")  # noqa: E731

    m = pn.Map(src("s2c_navlink"), 2026102801)
    children = m.world.get("children")
    keep = [c for c in children if resolve(m, c).type == "CMapNavLink"]
    children[:] = keep
    m.save(src("s2c_navlinkpf"))

    m = pn.Map(src("s2c_navlink"), 2026102802)
    w = m.world.get("children")
    w.append(group(m, "0 0 0", [link(m, "glink", "200 700 0", "0 0 32")]))
    t = group(m, "0 0 0", [link(m, "tlink", "0 0 0", "0 64 16"), link(m, "tlink_b", "32 0 0", "16 0 0")])
    w.append(t)
    w.append(instance(m, t, "300 560 0", 90))
    w.append(instance(m, t, "100 150 0", 0, "2 1 1"))
    t2 = group(m, "0 0 0", [instance(m, t, "0 0 0", 45), link(m, "t2link", "0 32 0", "8 8 8")])
    w.append(t2)
    w.append(instance(m, t2, "700 200 0", 30))
    w.append(m.prefab("maps/s2c_navlinkpf.vmap", "500 700 0", 90))
    w.append(link(m, "hlink", "900 100 0", "0 0 48", hidden=True))
    w.append(link(m, "elink", "900 300 0", "0 0 48", editor_only=True))
    m.save(src("s2c_navlink3"))
    w.remove(next(c for c in w if isinstance(c, pm.Element) and c.type == "CMapPrefab"))
    m.save(src("s2c_navlink4"))


if __name__ == "__main__":
    main()
