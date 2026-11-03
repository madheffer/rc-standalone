"""edge_collapse_map.py [--node N] [--base <vmap>] [--out <vmap>]: a specimen for the subdivision bake's edge
collapse (PHYSICS.md "An edge between two coincident baked vertices"; MergeVertices 1813a8840 -> CollapseEdge
181383600 / 181382170).

probe01 (s2c_rc_probe) with one mesh (node --node, default 92) subdivided: every face corner gets level 1
(subdivisionData.subdivisionLevels), and one vertex is moved onto the far end of its first edge
(vertexEdgeIndices -> edgeVertexIndices), so that edge has length 0. The bake's vertex merge (1e-6) then finds
two vertices joined by an edge, which Valve collapses. Written as s2c_edgecollapse.vmap (a new name; nothing
existing is overwritten unless --out says so). The map goes through Valve's dmxconvert to keyvalues2 and back
(tools/coverage/probe_map.py), so the result is a binary vmap in the base's format.

Compile it with Valve's tools (CS2 may be open): tools/pipeline/compile_fresh.py s2c_rc_probe s2c_edgecollapse
-world -fshallow --cs2-open --out %TEMP%/fresh_compile/s2c_edgecollapse.world.vpk
"""
import argparse
import os
import sys
import tempfile

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "coverage"))
import probe_map as pm  # noqa: E402

CONTENT = os.path.join(os.path.dirname(pm.GAME), "content", "csgo_addons", "s2c_rc_probe", "maps")


def array(lines, start, end, name):
    """(first value line, values) of the int/vector array attribute `name` inside lines[start:end]."""
    for i in range(start, end):
        s = lines[i].strip()
        if s.startswith('"' + name + '"') and s.endswith('_array"'):
            j = i + 2  # the '[' line, then values
            vals = []
            while lines[j].strip() != "]":
                vals.append(lines[j].strip().rstrip(",").strip('"'))
                j += 1
            return i + 2, vals
    raise SystemExit(f"{name} not found")


def set_value(lines, first, k, value, last):
    lines[first + k] = lines[first + k].split('"')[0] + '"' + value + '"' + ("" if last else ",")


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--node", type=int, default=92)
    p.add_argument("--vertex", type=int, default=0, help="the vertex moved onto its first edge's far end")
    p.add_argument("--base", default=os.path.join(CONTENT, "probe01.vmap"))
    p.add_argument("--out", default=os.path.join(CONTENT, "s2c_edgecollapse.vmap"))
    p.add_argument("--dz", type=float, default=0.0,
                   help="leave the moved vertex this far above the target in z (a tiny edge instead of a zero one)")
    a = p.parse_args()
    if os.path.exists(a.out) and not os.path.basename(a.out).lower().startswith("s2c_edgecollapse"):
        raise SystemExit(f"{a.out} exists; pick a new name")
    with tempfile.TemporaryDirectory() as tmp:
        text = os.path.join(tmp, "base.vmap")
        pm.convert(a.base, text, "keyvalues2")
        with open(text, encoding="utf-8") as f:
            lines = f.read().split("\n")
        start = next(i for i, l in enumerate(lines) if l.strip() == f'"nodeID" "int" "{a.node}"')
        end = next(i for i in range(start, len(lines)) if lines[i].strip().startswith('"CMapMesh"') or
                   lines[i].strip().startswith('"CMapEntity"') or lines[i].strip().startswith('"CMapGroup"'))
        vfirst, vedge = array(lines, start, end, "vertexEdgeIndices")
        vdfirst, vdata = array(lines, start, end, "vertexDataIndices")
        _, evert = array(lines, start, end, "edgeVertexIndices")
        pos_at = next(i for i in range(start, end) if lines[i].strip() == '"name" "string" "position:0"')
        pfirst, pos = array(lines, pos_at, end, "data")
        lfirst, levels = array(lines, start, end, "subdivisionLevels")
        v = a.vertex
        to = int(evert[int(vedge[v])])
        src, dst = int(vdata[to]), int(vdata[v])
        print(f"node {a.node}: vertex {v} (data {dst}, {pos[dst]}) -> vertex {to}'s position {pos[src]}")
        x, y, z = (float(c) for c in pos[src].split())
        target = pos[src] if a.dz == 0 else f"{pos[src].rsplit(' ', 1)[0]} {z + a.dz!r}"
        set_value(lines, pfirst, dst, target, dst == len(pos) - 1)
        for k in range(len(levels)):
            set_value(lines, lfirst, k, "1", k == len(levels) - 1)
        print(f"{len(levels)} corners at level 1")
        edited = os.path.join(tmp, "edited.vmap")
        with open(edited, "w", encoding="utf-8") as f:
            f.write("\n".join(lines))
        pm.convert(edited, a.out, "binary")
    print(f"wrote {a.out}")


if __name__ == "__main__":
    main()
