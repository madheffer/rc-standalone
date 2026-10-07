"""subdivtangent_stages.py <capture_subdivtangents jsonl> [--mesh 0x...]: which stage changes the tangents.

For each mesh that went through BakeSubdivisionForFaces, Valve's own
snapshots side by side (no port involved):

  enter -> bake   corners added by the split; the old corners' tangents kept?
  bake  -> baked  the weld and normal recompute: tangents touched?
  baked -> world  ConvertMeshForBuilder's rest: tangents touched?

and, per stage, the tangents' length and their dot with the corner's
normal (a re-orthogonalised tangent has |dot| ~ 0, a carried one not).
"""
import argparse
import json
import math
import struct
from collections import defaultdict


def floats(words):
    return [struct.unpack("<f", bytes.fromhex(w)[::-1])[0] for w in words] if words else []


def vecs(words, n):
    f = floats(words)
    return [tuple(f[i:i + n]) for i in range(0, len(f), n)]


def stats(normals, tangents):
    if not tangents:
        return "no tangents"
    lengths = [math.sqrt(sum(c * c for c in t[:3])) for t in tangents]
    dots = [abs(sum(a * b for a, b in zip(n, t[:3]))) for n, t in zip(normals, tangents)] if normals else []
    off_unit = sum(1 for x in lengths if abs(x - 1) > 1e-5)
    ortho = sum(1 for d in dots if d < 1e-5)
    return f"{len(tangents)} tangents, {off_unit} not unit, {ortho}/{len(dots)} orthogonal to the normal"


def same(a, b):
    """Corners whose tangent bits agree at the same index (over the shorter list)."""
    n = min(len(a), len(b))
    return sum(1 for i in range(n) if a[i] == b[i]), n


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("capture")
    p.add_argument("--mesh")
    p.add_argument("--limit", type=int, default=40)
    a = p.parse_args()
    meshes = defaultdict(dict)
    order = []
    for line in open(a.capture):
        d = json.loads(line)
        if "stage" not in d:
            continue
        key = d["mesh"]
        if d["stage"] == "enter":
            # A pointer can be reused by a later mesh; a new enter starts a new record.
            order.append(key + "#" + str(d["call"]))
            meshes[order[-1]] = {}
        rec = next((k for k in reversed(order) if k.startswith(key + "#")), None)
        if rec is not None and d["stage"] not in meshes[rec]:
            meshes[rec][d["stage"]] = d
    shown = 0
    totals = defaultdict(lambda: [0, 0])
    for rec in order:
        if a.mesh and not rec.startswith(a.mesh):
            continue
        s = meshes[rec]
        if not {"enter", "bake", "baked"} <= s.keys():
            continue
        t = {k: s[k]["tangent"] for k in s}
        n = {k: s[k]["normal"] for k in s}
        lines = [f"{rec}: corners " + " ".join(f"{k} {s[k]['corners']}" for k in ("enter", "bake", "baked", "world") if k in s)]
        for k in ("enter", "bake", "baked", "world"):
            if k in s:
                lines.append(f"  {k:5}: " + stats(vecs(n[k], 3), vecs(t[k], 4)))
        for x, y in (("enter", "bake"), ("bake", "baked"), ("baked", "world")):
            if x in s and y in s and t[x] and t[y]:
                eq, cmpd = same(vecs(t[x], 4), vecs(t[y], 4))
                totals[(x, y)][0] += eq
                totals[(x, y)][1] += cmpd
                lines.append(f"  {x} -> {y}: {eq} of {cmpd} index-aligned tangents unchanged")
        if shown < a.limit:
            print("\n".join(lines))
        shown += 1
    print(f"{shown} baked meshes")
    for (x, y), (eq, cmpd) in totals.items():
        print(f"all meshes {x} -> {y}: {eq} of {cmpd} unchanged")


if __name__ == "__main__":
    main()
