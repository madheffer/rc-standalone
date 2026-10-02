"""instance_prefab_map.py <base.vmap> <source.vmap> <out.vmap> [extra]: a small map holding one instance.

A copy of <base.vmap> with the first CMapInstance of <source.vmap> whose
target group holds a light_omni2, and that target group, added to its world
(fresh element, node and reference ids; the instance's target pointing at the
copied group). With [extra] > 0, that many plain info_target entities are
added too, to move the node count. Used as a prefab's map
(prefab_probe_map.py) to settle how the bake numbers the copies of instances
inside a prefab. dmxconvert both ways.
"""
import copy
import os
import random
import sys
import tempfile
import uuid

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "coverage"))
import probe_map as pm  # noqa: E402


def main(base, source, out, extra=0):
    work = tempfile.mkdtemp(prefix="instance_prefab_")
    texts = []
    for i, path in enumerate((base, source)):
        t = os.path.join(work, "in%d.txt" % i)
        pm.convert(path, t, "keyvalues2")
        texts.append(open(t, encoding="utf-8", errors="replace").read())
    header = texts[0].split("\n", 1)[0]
    tops = pm.parse(texts[0])
    src_tops = pm.parse(texts[1])
    src_all = [e for t in src_tops for e in pm.walk(t)]
    by_id = {e.get("id"): e for e in src_all}

    def classes(group):
        for x in pm.walk(group):
            props = x.get("entity_properties")
            if isinstance(props, pm.Element):
                yield props.get("classname")
    instance = next(e for e in src_all if e.type == "CMapInstance"
                    and isinstance(by_id.get(e.get("target")), pm.Element)
                    and "light_omni2" in set(classes(by_id[e.get("target")])))
    target = by_id[instance.get("target")]

    everything = [e for t in tops for e in pm.walk(t)]
    node = max(int(e.get("nodeID")) for e in everything if e.get("nodeID") is not None) + 1
    rng = random.Random(20261003)

    def fresh(e):
        nonlocal node
        for x in pm.walk(e):
            if x.get("id") is not None:
                x.set("id", "elementid", str(uuid.UUID(int=rng.getrandbits(128), version=4)))
            if x.get("nodeID") is not None:
                x.set("nodeID", "int", str(node))
                node += 1
            if x.get("referenceID") is not None:
                x.set("referenceID", "uint64", "0x%016x" % rng.getrandbits(64))
        return e
    # Children held by reference (to elements elsewhere in the source) are
    # inlined, or the copy would point outside its file.
    def inline(e, seen=()):
        e = copy.deepcopy(e)
        for f in e.fields:
            if isinstance(f[2], list):
                f[2] = [inline(by_id[x[1]], seen + (x[1],)) if isinstance(x, tuple) and x[1] in by_id and x[1] not in seen
                        else inline(x, seen) if isinstance(x, pm.Element) else x for x in f[2]]
            elif isinstance(f[2], pm.Element):
                f[2] = inline(f[2], seen)
        return e
    group = fresh(inline(target))
    inst = fresh(copy.deepcopy(instance))
    inst.set("target", "element", group.get("id"))
    world = next(t for t in tops if t.type == "CMapRootElement").get("world")
    world.get("children").extend([group, inst])
    template = next(x for x in pm.walk(group) if x.type == "CMapEntity")
    for i in range(int(extra)):
        e = fresh(copy.deepcopy(template))
        e.get("entity_properties").set("classname", "string", "info_target")
        world.get("children").append(e)
    out_text = os.path.join(work, "out.txt")
    open(out_text, "w", encoding="utf-8").write(pm.emit(tops, header))
    pm.convert(out_text, out, "binary")
    print("wrote", out, "instance node", inst.get("nodeID"), "group node", group.get("nodeID"), "next", node)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3], *(sys.argv[4:5] or [0]))
