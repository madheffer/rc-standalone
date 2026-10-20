"""l5_specimen.py <base.vmap> <out.vmap> [farfield]: the M4 / L5 specimen, a copy of <base.vmap> (probe01) with
  - a baked (directlight 1) light_barn and light_omni2, each with the stock cookie
    materials/effects/lightcookies/box.vtex, so vrad3's cookie sampling shows in the lightmap and the
    probe volumes;
  - an env_volumetric_fog_controller with IndirectEnabled 1 (64 voxels a side) over probe01's play
    area, so the compile bakes a fog irradiance volume (lpv_compute_block_fog_gpu).
With "farfield" (cell_size 256) or "farfield128", also an env_farfield_probe_volume (box_mins,
box_maxs, cell_size: the keys resourcecompiler's 180240a60 reads), and bouncelight 2 on the lights and
the light_environment so the far field has lighting to gather. CS2's FGDs have no such class, so Hammer cannot place one, but the
compile still bakes it. Built through Valve's dmxconvert as tools/coverage/probe_map.py does."""
import os
import sys
import tempfile

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "coverage"))
import probe_map as pm  # noqa: E402

COOKIE = "materials/effects/lightcookies/box.vtex"


def entity(cls, props, origin, angles):
    e = pm.Element("CMapEntity")
    e.fields = [["id", "elementid", ""], ["nodeID", "int", "0"], ["referenceID", "uint64", "0x0"],
                ["children", "element_array", []], ["variableTargetKeys", "string_array", []],
                ["variableNames", "string_array", []]]
    plugs = pm.Element("DmePlugList")
    plugs.fields = [["id", "elementid", ""], ["names", "string_array", []], ["dataTypes", "int_array", []],
                    ["plugTypes", "int_array", []], ["descriptions", "string_array", []]]
    e.fields.append(["relayPlugData", "DmePlugList", plugs])
    e.fields.append(["connectionsData", "element_array", []])
    p = pm.Element("EditGameClassProps")
    p.fields = [["id", "elementid", ""], ["classname", "string", cls]] + [[k, "string", v] for k, v in props]
    e.fields.append(["entity_properties", "EditGameClassProps", p])
    e.fields += [["hitNormal", "vector3", "0 0 1"], ["isProceduralEntity", "bool", "0"],
                 ["origin", "vector3", " ".join(f"{x:g}" for x in origin)], ["angles", "qangle", angles],
                 ["scales", "vector3", "1 1 1"], ["transformLocked", "bool", "0"],
                 ["force_hidden", "bool", "0"], ["editorOnly", "bool", "0"]]
    return e


def main(base, out, extra=None):
    work = tempfile.mkdtemp(prefix="l5_specimen_")
    text_path = os.path.join(work, "base.vmap")
    pm.convert(base, text_path, "keyvalues2")
    text = open(text_path, encoding="utf-8").read()
    header = text.split("\n", 1)[0]
    tops = pm.parse(text)
    everything = [e for t in tops for e in pm.walk(t)]
    root = next(t for t in tops if t.type == "CMapRootElement")
    children = root.get("world").get("children")
    node = max(int(e.get("nodeID")) for e in everything if e.get("nodeID") is not None) + 1
    added = [
        entity("light_barn", [("targetname", "l5_barn"), ("directlight", "1"), ("brightness", "4"), ("range", "1024"),
                              ("lightcookie", COOKIE)], (512, 112, 160), "90 0 0"),
        entity("light_omni2", [("targetname", "l5_omni"), ("directlight", "1"), ("brightness", "4"), ("range", "1024"),
                               ("lightcookie", COOKIE)], (512, 900, 120), "0 0 0"),
        entity("env_volumetric_fog_controller", [("targetname", "l5_fog"), ("IsMaster", "1"), ("IndirectEnabled", "1"),
                                                 ("IndirectVoxelDim", "64"), ("box_mins", "-768 -1088 -112"),
                                                 ("box_maxs", "768 896 176")], (512, 96, 112), "0 0 0"),
    ]
    if extra in ("farfield", "farfield128"):
        # Far field lighting gathers only lights and a sky whose path-trace flags carry 0x40, which
        # bouncelight 2 ("Realtime", commented out of lights2.fgd) gives: on the two lights and on
        # the base map's light_environment (its sky).
        for e in added[:2]:
            e.get("entity_properties").set("bouncelight", "string", "2")
        for e in everything:
            props = e.get("entity_properties") if e.type == "CMapEntity" else None
            if isinstance(props, pm.Element) and props.get("classname") == "light_environment":
                props.set("bouncelight", "string", "2")
        added.append(entity("env_farfield_probe_volume", [("targetname", "l5_farfield"), ("box_mins", "-768 -1088 -112"),
                                                          ("box_maxs", "768 896 176"), ("cell_size", "128" if extra == "farfield128" else "256")], (512, 96, 112), "0 0 0"))
    for k, e in enumerate(added):
        for x in pm.walk(e):
            if x.get("id") is not None:
                x.set("id", "elementid", f"5a5a5a5a-0000-4000-8000-{k:04x}{id(x) & 0xffffffff:08x}")
        e.set("nodeID", "int", str(node + k))
        e.set("referenceID", "uint64", "0x%016x" % (0x5a5a000000000000 + k))
    children.extend(added)
    out_text = os.path.join(work, "out.vmap")
    open(out_text, "w", encoding="utf-8", newline="\n").write(pm.emit(tops, header))
    pm.convert(out_text, out, "binary")
    print("wrote", out)


if __name__ == "__main__":
    main(*sys.argv[1:4])
