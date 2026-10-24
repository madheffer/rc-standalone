"""path_deformer_map4.py <s2c_deformpath.vmap> <out_nest2.vmap> <out_prefabs.vmap>: path deformer copies in a host
that does not hold the prefab maps' own nodes. path_deformer_map3.py builds its maps on copies of s2c_deformpath, so a
prefab's deformer finds the host's copy of its target by the old referenceID while the collapse remaps it (a recentre
onto the host's path and back); here both maps start from s2c_deformpath with its cables and deformers removed.

<out_nest2.vmap> (s2c_deformnest2), deformers whose target path is their own child, with 2, 3 and 4 path nodes in the
world, the 3-node one also in a group placed by an instance moved (-4096 0 64) and turned (0 30 0), the 4-node one by
an instance moved (-4096 0 0) and turned (10 -20 5).

<out_prefabs.vmap> (s2c_deformpath4), CMapPrefabs of maps/s2c_deformpath.vmap at (8192 0 0) unturned and at
(8192 -4096 128) turned (10 45 0), of maps/s2c_deformpath2.vmap (a nested prefab) at (-8192 0 0) turned (0 180 0), of
maps/s2c_deformnest.vmap at (0 9000 0) turned (0 60 0) and of maps/s2c_deformnest2.vmap at (0 -9000 0) turned
(0 -30 0)."""
import os, sys, tempfile

sys.path.insert(0, os.path.dirname(__file__))
import path_deformer_map3 as m3  # noqa: E402


def clean(b):
    """The base's world without its cables and deformers (and what they hold)."""
    kept = []
    for c in b.world.get("children"):
        e = c
        if isinstance(c, tuple):
            e = next((x for t in b.tops for x in m3.pm.walk(t) if x.get("id") == c[1]), None)
        if isinstance(e, m3.pm.Element) and (e.type == "CMapCable" or e.type.startswith("CMapDeformer")):
            continue
        kept.append(c)
    b.world.set("children", "element_array", kept)


def self_child(b, y, count, settings=()):
    """A deformer at (3000 y -300) whose target path, of <count> nodes, is its own child."""
    points = [(2900 + 400 * i / (count - 1), y + (100 if i % 2 else 0), -300 + 10 * i) for i in range(count)]
    path = b.path(points)
    return b.deformer((3000, y, -300), (0, 0, 0), (128, 32, 32), path, settings, [path])


def nest2_map(base, out, work):
    b = m3.Builder(base, work, 0x8300)
    clean(b)
    world = b.world.get("children")
    world.append(self_child(b, -2400, 2))
    world.append(self_child(b, -3200, 3, [("start", 0.2)]))
    world.append(self_child(b, -4000, 4, [("end", 0.7)]))
    g1 = b.group((3000, -4800, -300), [self_child(b, -4800, 3, [("start", 0.2)])])
    i1 = b.instance(g1, m3.add((3000, -4800, -300), (-4096, 0, 64)), (0, 30, 0))
    g2 = b.group((3000, -5600, -300), [self_child(b, -5600, 4)])
    i2 = b.instance(g2, m3.add((3000, -5600, -300), (-4096, 0, 0)), (10, -20, 5))
    world.extend([g1, i1, g2, i2])
    b.write(work, out)
    print("wrote", out, "instances", i1.get("nodeID"), i2.get("nodeID"), "next", b.node)


def prefab_map(base, out, work):
    b = m3.Builder(base, work, 0x8400)
    clean(b)
    b.world.get("children").extend([
        b.prefab("maps/s2c_deformpath.vmap", (8192, 0, 0), (0, 0, 0), 1799009851),
        b.prefab("maps/s2c_deformpath.vmap", (8192, -4096, 128), (10, 45, 0), 1799009852),
        b.prefab("maps/s2c_deformpath2.vmap", (-8192, 0, 0), (0, 180, 0), 1799009853),
        b.prefab("maps/s2c_deformnest.vmap", (0, 9000, 0), (0, 60, 0), 1799009854),
        b.prefab("maps/s2c_deformnest2.vmap", (0, -9000, 0), (0, -30, 0), 1799009855),
    ])
    b.write(work, out)
    print("wrote", out, "next", b.node)


def main(base, out_nest2, out_prefabs):
    work = tempfile.mkdtemp(prefix="path_deformer4_")
    nest2_map(base, out_nest2, work)
    prefab_map(base, out_prefabs, work)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3])
