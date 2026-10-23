"""simplify_probe_map.py <base.vmap> <out.vmap> [--types]: a probe map for Hammer's mesh physics simplification.
Every CMapMesh of the base gets physicsSimplificationOverride 1 and a physicsSimplificationError cycling through
ERRORS, so physicsbuilder simplifies each per-material piece (CMesh_Simplify, pb 1800c5b90) at a spread of
tolerances. With --types, meshes also cycle their physicsType through
TYPES (entity meshes too), so the mesh, convex_single and convex_multi paths each see simplified pieces. Meshes
set to "none" stay so.
The world physics and entity models Valve builds from it settle where the simplifier sits in the piece chain."""
import os, sys, tempfile

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "coverage"))
import probe_map as pm  # noqa: E402

ERRORS = ["0.25", "1", "4", "16", "64"]
TYPES = ["default", "mesh", "convex_single", "convex_multi"]


def main(base, out, types):
    work = tempfile.mkdtemp(prefix="simplify_probe_")
    text_path = os.path.join(work, "base_text.vmap")
    pm.convert(base, text_path, "keyvalues2")
    text = open(text_path, encoding="utf-8").read()
    header = text.split("\n", 1)[0]
    tops = pm.parse(text)
    # Children are element references by id (or, rarely, inline elements).
    parents = {}
    for top in tops:
        for e in pm.walk(top):
            for c in e.get("children") or []:
                key = c[1] if isinstance(c, tuple) else c.get("id") if isinstance(c, pm.Element) else None
                if key is not None:
                    parents[key] = e
    n = 0
    for top in tops:
        for e in pm.walk(top):
            if e.type != "CMapMesh" or e.get("physicsType") == "none":
                continue
            e.set("physicsSimplificationOverride", "bool", "1")
            e.set("physicsSimplificationError", "float", ERRORS[n % len(ERRORS)])
            parent = parents.get(e.get("id"))
            if types:
                e.set("physicsType", "string", TYPES[(n // len(ERRORS) + (parent is not None and parent.type == "CMapWorld")) % len(TYPES)])
            n += 1
    text_out = os.path.join(work, "out_text.vmap")
    open(text_out, "w", encoding="utf-8").write(pm.emit(tops, header))
    pm.convert(text_out, out, "binary")
    print("wrote", out, n, "meshes")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], "--types" in sys.argv[3:])
