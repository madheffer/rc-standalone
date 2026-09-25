"""subdiv_sim.py <face.json> -- search the builder's subdivision order model against one face.

The map builder bakes subdivision (BakeSubdivisionForFaces, resourcecompiler
0923: 1813baa40) with half-edge operations on a mesh whose faces live in a
dense array: a new face is appended, a removed one is replaced by the last.
The export walks that array, each face from its first half-edge. Per face
(1813ca560, recursive):
  1. AddVertexToEdge at 0.5 on each edge (mid[j] between corner j and j+1)
  2. for each corner k: AddEdgeToFace(mid[k-1], mid[k]), then AddVertexToEdge
     at 0.5 on that new edge (e[k])
  3. for each k: AddEdgeToFace(e[k-1], e[k])
  4. CollapseFace on the inner face (all e[k] become the centre)
  5. recurse into each corner's quad [corner, mid[k], centre, mid[k-1]]:
     corners in order at the top level, 0, 1, 3, 2 below it
Then (1813baa40, triangulate flag) each top-level patch's grid cells are cut
along the diagonal, patch by patch, row by row.

Choices the code leaves to details not yet read are parameters; this finds
the ones that reproduce a face's exported triangles, order and corners.
face.json: {"corners": [[x,y,z]...], "tris": [[[x,y,z]x3]...]} (export order,
the face's own slot first), from SubdivisionOrderProbe (SUBDIVCMP_FACE).
"""
import itertools
import json
import sys


def key(p):
    return tuple(round(c, 3) for c in p)


class Mesh:
    def __init__(self, corners, opts):
        self.o = opts
        self.pos = []
        self.faces = []          # dense array: list of vertex-id cycles, starting at the first half-edge
        ids = [self.vert(p) for p in corners]
        self.faces.append(ids)

    def vert(self, p):
        self.pos.append(tuple(p))
        return len(self.pos) - 1

    def find_face(self, a, b):
        for i, f in enumerate(self.faces):
            if a in f and b in f:
                return i
        raise KeyError((a, b))

    def add_vertex_to_edge(self, a, b, t=0.5):
        pa, pb = self.pos[a], self.pos[b]
        v = self.vert([pa[i] + (pb[i] - pa[i]) * t for i in range(3)])
        for f in self.faces:
            n = len(f)
            for i in range(n):
                u, w = f[i], f[(i + 1) % n]
                if (u, w) in ((a, b), (b, a)):
                    f.insert(i + 1, v)
                    break
        return v

    def split_between(self, fi, a, b, t=0.5):
        """1813ba570: the point at t between corners a and b of face fi. A
        vertex already there on the face's path from a to b (a neighbour's
        split) is reused; otherwise the sub-edge holding it is split."""
        f = self.faces[fi]
        n = len(f)
        ia = f.index(a)
        path = [a]
        j = ia
        while f[j] != b:
            j = (j + 1) % n
            path.append(f[j])
        pa, pb = self.pos[a], self.pos[b]
        target = key([pa[i] + (pb[i] - pa[i]) * t for i in range(3)])
        for v in path[1:-1]:
            if key(self.pos[v]) == target:
                return v
        for u, w in zip(path, path[1:]):
            pu, pw = self.pos[u], self.pos[w]
            # the sub-edge whose span holds the target along a -> b
            length = sum((pb[i] - pa[i]) ** 2 for i in range(3)) ** 0.5
            su = sum((pu[i] - pa[i]) * (pb[i] - pa[i]) for i in range(3)) / length
            sw = sum((pw[i] - pa[i]) * (pb[i] - pa[i]) for i in range(3)) / length
            if su < t * length < sw:
                return self.add_vertex_to_edge(u, w, (t * length - su) / (sw - su))
        raise KeyError((a, b))

    def add_edge_to_face(self, fi, a, b, site=0):
        f = self.faces[fi]
        n = len(f)
        ia, ib = f.index(a), f.index(b)
        # loop a -> b -> (boundary after b) ... back to a
        side_ab = [a] + [f[(ib + j) % n] for j in range((ia - ib) % n)]
        side_ab = [a, b] + [f[(ib + j) % n] for j in range(1, (ia - ib) % n)]
        side_ba = [b, a] + [f[(ia + j) % n] for j in range(1, (ib - ia) % n)]
        keep, new = (side_ab, side_ba) if self.o["keep_ab"][site] else (side_ba, side_ab)
        self.faces[fi] = keep
        self.faces.append(new)

    def remove(self, fi):
        last = self.faces.pop()
        if fi < len(self.faces):
            self.faces[fi] = last

    def collapse(self, fi):
        inner = self.faces[fi]
        c = self.vert([sum(self.pos[v][i] for v in inner) / len(inner) for i in range(3)])
        gone = set(inner)
        if self.o["collapse_face_first"]:
            self.remove(fi)
        # every other face: inner vertices become the centre, runs merged
        dead = []
        for i, f in enumerate(self.faces):
            if self.o["collapse_face_first"] is False and i == fi:
                continue
            if not gone.intersection(f):
                continue
            g = [c if v in gone else v for v in f]
            h = [v for j, v in enumerate(g) if v != g[j - 1]] if len(g) > 1 else g
            if len(h) > 1 and h[0] == h[-1]:
                h = h[:-1]
            if len(set(h)) < 3:
                dead.append(i)
            else:
                # the loop keeps its first half-edge where it can
                self.faces[i] = h
        if not self.o["collapse_face_first"]:
            dead.append(fi)
            dead = sorted(set(dead), key=lambda d: dead.index(d))
        order = sorted(dead, reverse=self.o["dead_desc"]) if self.o["dead_sorted"] else (dead[::-1] if self.o["dead_desc"] else dead)
        # removals shift indices: remove by identity
        victims = [self.faces[d] for d in order]
        for vf in victims:
            self.remove(next(i for i, f in enumerate(self.faces) if f is vf))
        return c

    def subdivide(self, fi, corners, level, max_level, patches):
        n = len(corners)
        if not (n > 2 and level < max_level and (level == 0 or n == 4)):
            return
        mid = [None] * n
        for j in range(n):
            prev = (j - 1) % n
            mid[prev] = self.split_between(self.find_face(corners[prev], corners[j]), corners[prev], corners[j])
        e = [None] * n
        for k in range(n):
            a, b = mid[(k - 1) % n], mid[k]
            self.add_edge_to_face(self.find_face(a, b), a, b, 0)
            e[k] = self.add_vertex_to_edge(a, b)
        for k in range(n):
            a, b = e[(k - 1) % n], e[k]
            self.add_edge_to_face(self.find_face(a, b), a, b, 1)
        inner = next(i for i, f in enumerate(self.faces) if set(f) == set(e))
        c = self.collapse(inner)
        kids = [[corners[k], mid[k], c, mid[(k - 1) % n]] for k in range(n)]
        if level == 0:
            patches.extend(kids)
            order = range(n)
        else:
            order = [0, 1, 3, 2]
        for k in order:
            q = kids[k]
            self.subdivide(self.find_face(q[0], q[2]), q, level + 1, max_level, patches)


def grid_of(m, patch, side, orient):
    """Patch vertices by (row, col): origin corner and axis order from orient."""
    q = [m.pos[v] for v in patch]
    o, a1, a2 = orient
    p0 = q[o]
    ax = [q[(o + 1) % 4][i] - p0[i] for i in range(3)]
    ay = [q[(o + 3) % 4][i] - p0[i] for i in range(3)]
    if a1:
        ax, ay = ay, ax
    index = {key(m.pos[v]): v for f in m.faces for v in f}

    def at(r, c):
        p = [p0[i] + ax[i] * c / side + ay[i] * r / side for i in range(3)]
        return index[key(p)]
    return at


def run(corners, level, opts):
    m = Mesh(corners, opts)
    patches = []
    m.subdivide(0, m.faces[0][:], 0, level, patches)
    side = 1 << (level - 1)
    for patch in patches:
        at = grid_of(m, patch, side, opts["orient"])
        for r in range(side):
            for c in range(side):
                a, b = at(r, c), at(r + 1, c + 1)
                if opts["diag_rev"]:
                    a, b = b, a
                m.add_edge_to_face(m.find_face(a, b), a, b, 2)
    return [[key(m.pos[v]) for v in f] for f in m.faces]


def main():
    d = json.load(open(sys.argv[1]))
    corners = d["corners"]
    want = [[key(p) for p in t] for t in d["tris"]]
    level = int(sys.argv[2]) if len(sys.argv) > 2 else 2
    best = None
    for keep_ab, cff, dsorted, ddesc, o, a1, rev in itertools.product(
            list(itertools.product([True, False], repeat=3)), [True, False], [True, False], [True, False], range(4), [False, True], [False, True]):
        opts = {"keep_ab": keep_ab, "collapse_face_first": cff, "dead_sorted": dsorted, "dead_desc": ddesc,
                "orient": (o, a1, None), "diag_rev": rev}
        try:
            got = run(corners, level, opts)
        except (KeyError, ValueError, StopIteration):
            continue
        same_order = sum(1 for x, y in zip(got, want) if set(x) == set(y))
        same_exact = sum(1 for x, y in zip(got, want) if x == y)
        score = (same_order, same_exact)
        if best is None or score > best[0]:
            best = (score, opts, got)
    (so, se), opts, got = best
    print(f"best: {so}/{len(want)} triangles in order, {se} with exact corners; {opts}")
    for i, (x, y) in enumerate(zip(got, want)):
        if x != y:
            print(f"  first difference at {i}: model {x} valve {y}")
            break


if __name__ == "__main__":
    main()
