"""material_order.py <out.txt> <run dir>=<map.vpk>...: the materials the engine creates before a map loads, in creation order.

Reads each run's frida.jsonl from run_probe.py --frida tools/cubehost/layer_sort.js (its "material" records:
every CMaterial2 as materialsystem2 names it, in call order) and the run's map package. The cut is the earliest
creation, over all runs, of a material the map's world node models reference that is not created in every run
(the map's own); kept are the materials created in every run with their median creation index below it, one per
line in that order (ties by name). The runs must cover at least two maps.

Valve's layer sort draws materials by address (docs/CUBEMAPS.md "The draw list", GROUND_TRUTH 156); measured,
a material created before the map loads always sorts before the map's own, while the map's materials follow the
order their loads complete, which is not data.
"""
import json
import os
import statistics
import struct
import sys


def created(run):
    first = {}
    for line in open(os.path.join(run, "frida.jsonl"), encoding="utf-8"):
        r = json.loads(line)
        if r.get("kind") == "material" and r.get("name") and r["name"] not in first:
            first[r["name"]] = r["seq"]
    return first


def rerl(data):
    """A resource's external references (its RERL block)."""
    _, _, _, block_off, count = struct.unpack_from("<IHHII", data, 0)
    for i in range(count):
        p = 8 + block_off + i * 12
        if data[p:p + 4] != b"RERL":
            continue
        start = p + 4 + struct.unpack_from("<I", data, p + 4)[0]
        entries, n = struct.unpack_from("<II", data, start)
        out = []
        for k in range(n):
            q = start + entries + k * 16
            s = q + 8 + struct.unpack_from("<I", data, q + 8)[0]
            out.append(data[s:data.index(b"\0", s)].decode())
        return out
    return []


def world_materials(path):
    import vpk
    pak = vpk.open(path)
    out = set()
    for node in (p for p in pak if p.endswith(".vwnod_c")):
        for model in rerl(pak[node].read()):
            if model.endswith(".vmdl") and model + "_c" in pak:
                out.update(r for r in rerl(pak[model + "_c"].read()) if r.endswith(".vmat"))
    return out


def main():
    if len(sys.argv) < 4:
        sys.exit(__doc__)
    out = sys.argv[1]
    runs = [a.split("=", 1) for a in sys.argv[2:]]
    seqs = [created(r) for r, _ in runs]
    common = set.intersection(*(set(s) for s in seqs))
    cut = min(min((s[m] for m in world_materials(p) if m in s and m not in common), default=1 << 30)
              for s, (_, p) in zip(seqs, runs))
    order = sorted((n for n in common if statistics.median(s[n] for s in seqs) < cut),
                   key=lambda n: (statistics.median(s[n] for s in seqs), n))
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        f.write(f"# materials CMaterial2 creates in every run before a map's own (creation index below {cut}), by median "
                f"creation index: {len(order)} from " + ", ".join(os.path.basename(os.path.normpath(r)) for r, _ in runs)
                + " (tools/cubehost/material_order.py)\n")
        for n in order:
            f.write(n + "\n")
    print(f"cut {cut}: {len(order)} of {len(common)} common materials")


if __name__ == "__main__":
    main()
