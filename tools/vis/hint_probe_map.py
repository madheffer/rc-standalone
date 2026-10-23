"""hint_probe_map.py <base.vmap> <out.vmap>: a probe map for visibility_hint.
A copy of <base.vmap> (probe01: traced box about (-256 -976 0) to (1280 1408 288)) with visibility_hint
entities added to its world: x, y and z split hints (types 4, 5, 6) whose planes fall inside leaves, on
leaf edges and within 1.1 of an edge, overlapping one another, and voxel hints of types 0, 2, 9 and 10.
Each hint is a copy of the base map's first point entity with its keys replaced, so the element carries the
fields Hammer writes. The result is binary, through Valve's dmxconvert like the other probe maps."""
import copy, os, sys, tempfile

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "coverage"))
import probe_map as pm  # noqa: E402

# (hintType, origin, box_mins, box_maxs). The box is origin + mins to origin + maxs; angles are ignored.
HINTS = [
    # z splits: one cutting every leaf at z 100..140 (inside 32-unit leaves), one over half the map whose
    # top edge sits 0.5 above a leaf edge (under SplitMinimum, so no piece above it).
    (6, (512, 216, 120), (-2000, -2000, -20), (2000, 2000, 20)),
    (6, (0, 0, 0), (-300, -1000, 160), (500, 1500, 192.5)),
    # x splits: a slab inside leaves, one on a leaf edge, one reaching 0.6 into a box.
    (4, (330, 0, 0), (-27.3, -2000, -100), (27.3, 2000, 400)),
    (4, (0, 0, 0), (640, -500, -100), (704, 600, 400)),
    (4, (0, 0, 0), (-300, 0, -100), (-255.4, 900, 400)),
    # y splits: one overlapping the x slab, one thin (2 units) crossing everything.
    (5, (0, 215, 0), (-2000, -15, -100), (2000, 15, 400)),
    (5, (0, 0, 0), (-2000, 801, -100), (2000, 803, 400)),
    # voxel hints, so the specimen covers the table's rows too.
    (0, (64, 64, 64), (-64, -64, -64), (64, 64, 64)),
    (2, (900, 900, 100), (-128, -128, -100), (128, 128, 100)),
    (9, (0, 0, 0), (-260, -980, -10), (100, -600, 300)),
    (10, (1100, -400, 64), (-96, -96, -64), (96, 96, 64)),
]


def fmt(v):
    return " ".join(f"{x:g}" for x in v)


def main(base, out):
    work = tempfile.mkdtemp(prefix="hint_probe_")
    text_path = os.path.join(work, "base_text.vmap")
    pm.convert(base, text_path, "keyvalues2")
    text = open(text_path, encoding="utf-8").read()
    header = text.split("\n", 1)[0]
    tops = pm.parse(text)
    everything = [e for t in tops for e in pm.walk(t)]
    world = next(t for t in tops if t.type == "CMapRootElement").get("world")
    template = next(e for e in everything if e.type == "CMapEntity" and not any(
        isinstance(c, pm.Element) and c.type == "CMapMesh" for c in (e.get("children") or [])))
    node = max(int(e.get("nodeID")) for e in everything if e.get("nodeID") is not None) + 1
    for k, (kind, origin, mins, maxs) in enumerate(HINTS):
        e = copy.deepcopy(template)
        e.set("id", "elementid", f"5c2cf1{k:02x}-0000-4000-8000-{k:012x}")
        e.set("nodeID", "int", str(node + k))
        e.set("referenceID", "uint64", f"0x5c2cf1{k:010x}")
        e.set("children", "element_array", [])
        e.set("connectionsData", "element_array", [])
        e.set("origin", "vector3", fmt(origin))
        e.set("angles", "qangle", "0 0 0")
        props = pm.Element("EditGameClassProps")
        props.fields = [["id", "elementid", f"5c2cf2{k:02x}-0000-4000-8000-{k:012x}"],
                        ["classname", "string", "visibility_hint"],
                        ["box_mins", "string", fmt(mins)], ["box_maxs", "string", fmt(maxs)],
                        ["hintType", "string", str(kind)]]
        e.set("entity_properties", "EditGameClassProps", props)
        plugs = e.get("relayPlugData")
        if isinstance(plugs, pm.Element):
            plugs.set("id", "elementid", f"5c2cf3{k:02x}-0000-4000-8000-{k:012x}")
        world.get("children").append(e)
    text_out = os.path.join(work, "out_text.vmap")
    open(text_out, "w", encoding="utf-8").write(pm.emit(tops, header))
    pm.convert(text_out, out, "binary")
    print("wrote", out, len(HINTS), "hints from node", node)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
