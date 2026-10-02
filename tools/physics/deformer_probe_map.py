"""deformer_probe_map.py <base.vmap> <zoo.vmap> <out.vmap>: a probe map for Hammer's deformers.

Copies the first CMapDeformerLattice (a prop_static under a B-spline
lattice) and the two CMapDeformerSimple nodes (bend deformers over a group
of props) of Valve's own zoo map (content/csgo/maps/editor/zoo/train_zoo.vmap)
into the world of a copy of <base.vmap>, node ids renumbered past the base's.
Valve's compile of it shows what the deformers do to collision and render.
Both maps go through dmxconvert to keyvalues2 and the result back to binary.
"""
import os
import re
import sys
import tempfile

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "coverage"))
import probe_map as pm  # noqa: E402


def blocks(text, type_, limit):
    out, pos = [], 0
    while len(out) < limit:
        k = text.find('"%s"' % type_, pos)
        if k < 0:
            break
        i = text.index("{", k)
        depth = 0
        for j in range(i, len(text)):
            if text[j] == "{":
                depth += 1
            elif text[j] == "}":
                depth -= 1
                if depth == 0:
                    out.append('"%s"\n%s' % (type_, text[i:j + 1]))
                    break
        pos = k + len(type_)
    return out


def main(base, zoo, out):
    work = tempfile.mkdtemp(prefix="deformer_probe_")
    base_text = os.path.join(work, "base.txt")
    pm.convert(base, base_text, "keyvalues2")
    text = open(base_text, encoding="utf-8").read()
    header = text.split("\n", 1)[0]
    tops = pm.parse(text)
    zoo_text_path = os.path.join(work, "zoo.txt")
    pm.convert(zoo, zoo_text_path, "keyvalues2")
    zoo_text = open(zoo_text_path, encoding="utf-8", errors="replace").read()
    picked = blocks(zoo_text, "CMapDeformerLattice", 1) + blocks(zoo_text, "CMapDeformerSimple", 2)
    nodes = [pm.parse(b)[0] for b in picked]
    everything = [e for t in tops for e in pm.walk(t)]
    node = max(int(e.get("nodeID")) for e in everything if e.get("nodeID") is not None) + 1
    for n in nodes:
        for x in pm.walk(n):
            if x.get("nodeID") is not None:
                x.set("nodeID", "int", str(node))
                node += 1
    world = next(t for t in tops if t.type == "CMapRootElement").get("world")
    world.get("children").extend(nodes)
    out_text = os.path.join(work, "out.txt")
    open(out_text, "w", encoding="utf-8").write(pm.emit(tops, header))
    pm.convert(out_text, out, "binary")
    print("wrote", out, "with", len(nodes), "deformers")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3])
