"""snapshot_specimen.py <content maps dir> <out.vmap>: the particle snapshot specimen (s2c_snapprobe), a copy of
probe01 with info_particle_system entities whose snapshot_mesh names, one each:
  - pathA: atixref's path_particle_rope_clientside (a CMapPath, 1810b5100), its nodes given distinct
    color_tint, radius_scale and radius_scale_height keys (181112010 / 18126a5a0, 181111cc0);
  - pathB: the same path with particleSnapshotSpacing 40 and only radius_scale on the nodes;
  - cable: probe_cable's cable_dynamic (a CMapCable, the same 1810b5100);
  - box: probe01's first func_buyzone box mesh as a world mesh (closed: the corner sweep only);
  - scaled: the same box with scales 2 1 0.5 (where 181083850's mesh space is, not read);
  - world: node 1 (CMapWorld's vf 0x588 is 180f7a100);
  - buyzone: the func_buyzone entity itself (a CMapEntity, also 180f7a100).
The info_particle_system is probe_classes' bare one. Built through Valve's dmxconvert as probe_map.py does."""
import os
import sys
import tempfile

import probe_map as pm


def load(path):
    work = tempfile.mkdtemp(prefix="snapshot_specimen_")
    text = os.path.join(work, "t.vmap")
    pm.convert(path, text, "keyvalues2")
    body = open(text, encoding="utf-8").read()
    tops = pm.parse(body)
    by_id = {e.get("id"): e for t in tops for e in pm.walk(t)}
    return body.split("\n", 1)[0], tops, by_id


def deep(e, by_id):
    e = by_id[e[1]] if isinstance(e, tuple) else e
    c = pm.Element(e.type)
    for name, type_, value in e.fields:
        if isinstance(value, list):
            value = [deep(v, by_id) if isinstance(v, (tuple, pm.Element)) else v for v in value]
        elif isinstance(value, (tuple, pm.Element)):
            value = deep(value, by_id)
        c.fields.append([name, type_, value])
    return c


def find(tops, by_id, pred):
    for t in tops:
        for e in pm.walk(t):
            if pred(e):
                return e
    raise LookupError("not found")


def classname(e):
    props = e.get("entity_properties")
    return props.get("classname") if isinstance(props, pm.Element) else None


def set_key(e, name, value):
    props = e.get("entity_properties")
    props.set(name, "string", value)


def main(maps, out):
    header, tops, by_id = load(os.path.join(maps, "probe01.vmap"))
    _, atix, atix_ids = load(os.path.join(maps, "atixref.vmap"))
    _, cable_tops, cable_ids = load(os.path.join(maps, "probe_cable.vmap"))
    _, classes, class_ids = load(os.path.join(maps, "probe_classes.vmap"))
    root = next(t for t in tops if t.type == "CMapRootElement")
    children = root.get("world").get("children")
    everything = [e for t in tops for e in pm.walk(t)]
    node = max(int(e.get("nodeID")) for e in everything if e.get("nodeID") is not None) + 1

    path = find(atix, atix_ids, lambda e: e.type == "CMapPath" and classname(e) == "path_particle_rope_clientside")
    cable = find(cable_tops, cable_ids, lambda e: e.type == "CMapCable")
    system = find(classes, class_ids, lambda e: classname(e) == "info_particle_system"
                  and (e.get("entity_properties").get("snapshot_mesh") in (None, "")))
    zone = next((by_id[c[1]] if isinstance(c, tuple) else c) for c in children
                if classname(by_id[c[1]] if isinstance(c, tuple) else c) == "func_buyzone")

    path_a = deep(path, atix_ids)
    tints = ["255 0 0", "0 128 255", "10 200 30", "255 255 255", "1 2 3"]
    for k, n in enumerate(e for e in path_a.get("children") if isinstance(e, pm.Element) and e.type == "CMapPathNode"):
        set_key(n, "color_tint", tints[k % len(tints)])
        set_key(n, "radius_scale", str(0.5 + k))
        set_key(n, "radius_scale_height", str(2 + k * 0.25))
    path_b = deep(path, atix_ids)
    path_b.set("particleSnapshotSpacing", "float", "40")
    for k, n in enumerate(e for e in path_b.get("children") if isinstance(e, pm.Element) and e.type == "CMapPathNode"):
        set_key(n, "radius_scale", str(1.5 + k))
    cable_c = deep(cable, cable_ids)
    box = deep(zone.get("children")[0], by_id)
    box.set("origin", "vector3", "0 0 512")
    scaled = deep(zone.get("children")[0], by_id)
    scaled.set("origin", "vector3", "512 0 512")
    scaled.set("scales", "vector3", "2 1 0.5")

    targets = [path_a, path_b, cable_c, box, scaled]
    added = list(targets)
    for k, e in enumerate(added):
        for x in pm.walk(e):
            if x.get("id") is not None:
                x.set("id", "elementid", f"5a5a5a5a-0000-4000-8000-{k:04x}{id(x) & 0xffffffff:08x}")
            if x.get("nodeID") is not None:
                x.set("nodeID", "int", str(node))
                node += 1
            if x.get("referenceID") is not None:
                x.set("referenceID", "uint64", "0x%016x" % (0x5a5a000000000000 + node))
    names = ["pathA", "pathB", "cable", "box", "scaled", "world", "buyzone"]
    ids = [t.get("nodeID") for t in targets] + ["1", zone.get("nodeID")]
    systems = []
    for k, (name, target) in enumerate(zip(names, ids)):
        s = deep(system, class_ids)
        s.set("origin", "vector3", f"{-600 + 150 * k} -600 64")
        set_key(s, "targetname", f"snap_{name}")
        set_key(s, "snapshot_mesh", str(target))
        for x in pm.walk(s):
            if x.get("id") is not None:
                x.set("id", "elementid", f"5b5b5b5b-0000-4000-8000-{k:04x}{id(x) & 0xffffffff:08x}")
            if x.get("nodeID") is not None:
                x.set("nodeID", "int", str(node))
                node += 1
            if x.get("referenceID") is not None:
                x.set("referenceID", "uint64", "0x%016x" % (0x5b5b000000000000 + node))
        systems.append(s)
    children.extend(added + systems)
    for name, target in zip(names, ids):
        print(f"snap_{name}: node {target}")
    work = tempfile.mkdtemp(prefix="snapshot_specimen_out_")
    out_text = os.path.join(work, "out.vmap")
    open(out_text, "w", encoding="utf-8", newline="\n").write(pm.emit(tops, header))
    pm.convert(out_text, out, "binary")
    print("wrote", out)


if __name__ == "__main__":
    main(*sys.argv[1:3])
