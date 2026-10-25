"""nav_round_specimen.py <probe01.vmap> <out.vmap>: the L22 specimen, a copy of probe01 with prop_static
models whose collision is a sphere or a capsule standing on its floor, so the nav world holds round
shapes: the extraction tessellates them (vphysics2 18016c9b0 / 18016f5d0) and ClipPolysToHullClearance
and AddJumps cast against them (18024bc00 / 18024e6b0).
  - dust_soccer_ball001 (one sphere), plain and at scale 3;
  - pipe01_str_128a (one capsule) lying at yaw 30 and standing (pitch 90);
  - pipe01_addon06b (a sphere and a capsule);
  - pipe01_str_64a at scale 2, rotated on two axes.
Built through Valve's dmxconvert as probe_map.py does."""
import os
import sys
import tempfile

import probe_map as pm

PROPS = [
    ("models/props/de_dust/hr_dust/dust_soccerball/dust_soccer_ball001.vmdl", (300, 300, 0), "0 0 0", "1 1 1"),
    ("models/props/de_dust/hr_dust/dust_soccerball/dust_soccer_ball001.vmdl", (700, -300, 0), "0 0 0", "3 3 3"),
    ("models/generic/pipe01/pipe01_str_128a.vmdl", (200, 800, 8), "0 30 0", "1 1 1"),
    ("models/generic/pipe01/pipe01_str_128a.vmdl", (900, 700, 0), "90 0 0", "1 1 1"),
    ("models/generic/pipe01/pipe01_addon06b.vmdl", (500, 1000, 0), "0 0 0", "1 1 1"),
    ("models/generic/pipe01/pipe01_str_64a.vmdl", (1000, -500, 8), "0 45 30", "2 2 2"),
]


def prop(model, origin, angles, scales):
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
    p.fields = [["id", "elementid", ""], ["classname", "string", "prop_static"], ["model", "string", model], ["solid", "string", "6"]]
    e.fields.append(["entity_properties", "EditGameClassProps", p])
    e.fields += [["hitNormal", "vector3", "0 0 1"], ["isProceduralEntity", "bool", "0"],
                 ["origin", "vector3", " ".join(f"{x:g}" for x in origin)], ["angles", "qangle", angles],
                 ["scales", "vector3", scales], ["transformLocked", "bool", "0"],
                 ["force_hidden", "bool", "0"], ["editorOnly", "bool", "0"]]
    return e


def main(base, out):
    work = tempfile.mkdtemp(prefix="nav_round_")
    text_path = os.path.join(work, "base.vmap")
    pm.convert(base, text_path, "keyvalues2")
    text = open(text_path, encoding="utf-8").read()
    header = text.split("\n", 1)[0]
    tops = pm.parse(text)
    everything = [e for t in tops for e in pm.walk(t)]
    root = next(t for t in tops if t.type == "CMapRootElement")
    children = root.get("world").get("children")
    node = max(int(e.get("nodeID")) for e in everything if e.get("nodeID") is not None) + 1
    added = [prop(*p) for p in PROPS]
    for k, e in enumerate(added):
        for x in pm.walk(e):
            if x.get("id") is not None:
                x.set("id", "elementid", f"7c7c7c7c-0000-4000-8000-{k:04x}{id(x) & 0xffffffff:08x}")
        e.set("nodeID", "int", str(node + k))
        e.set("referenceID", "uint64", "0x%016x" % (0x7c7c000000000000 + k))
    children.extend(added)
    out_text = os.path.join(work, "out.vmap")
    open(out_text, "w", encoding="utf-8", newline="\n").write(pm.emit(tops, header))
    pm.convert(out_text, out, "binary")
    print("wrote", out)


if __name__ == "__main__":
    main(*sys.argv[1:3])
