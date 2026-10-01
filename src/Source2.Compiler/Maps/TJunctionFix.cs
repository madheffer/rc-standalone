using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// <c>CWorldRendererBuilderNode::FixTJunctionEdgeCracks</c> (resourcecompiler
/// 0923: 180259d30; on in CS2: csgo_core's FixTJunctionEdgeCracks 1), run on a
/// node's mesh entries before the weld. A vertex position is a vertex's
/// first three floats.
/// <list type="number">
/// <item>Edges (job 18025a280): of every entry that takes part (attribute bit
/// 0 clear, +0x1a4 clear), every triangle whose three corners differ and
/// every edge of it without a twin (vertex indices, 1812d91c0) gives two
/// records, one from each end: the point, the unit direction to the other
/// end, the entry's +0x40 (its source node) and the vertex. The records are
/// sorted byte-wise and duplicates dropped (a CVertexKDTree is built on them;
/// a box query over all of them is the same set).</item>
/// <item>Fix (job 18025b410), entry by entry, over its original triangles that
/// have an open edge and whose corners stay distinct snapped to 1/32: each
/// record inside the corners' box grown by 1/32 (callback 18025cf80) is
/// skipped when it is the triangle's own vertex in its own node (or any of
/// its node when +0x1a0 is set); a record whose snapped point is a snapped
/// corner flags that corner; otherwise, against each open edge it runs
/// nearly along (|dot| at least 0.99), its point is projected: strictly
/// inside the edge and within 1/1024 squared of it, it is a point to insert.
/// A flagged corner moves to its snapped position, the triangles on that
/// vertex whose turn would flip having their first two indices swapped. With
/// points to insert (sorted by edge, then by t), the face is rebuilt: each
/// corner, then its edge's points snapped, a point equal to a corner or an
/// earlier point skipped, a new vertex the edge's corners interpolated at t
/// with the snapped position; it is cut by <see cref="PolygonTriangulator"/>
/// (a fan when that gives up), the first triangle in the old one's place and
/// the rest after the index list.</item>
/// </list>
/// </summary>
internal static class TJunctionFix
{
    public sealed class Mesh
    {
        public required List<float> Vertices;
        public required int Stride;
        public required List<int> Indices;
        /// <summary>+0x40: the source node.</summary>
        public int Group;
        /// <summary>Attribute bit 0 or +0x1a4: the entry takes no part.</summary>
        public bool Excluded;
        /// <summary>+0x1a0: its own node's records are never used on it.</summary>
        public bool SkipOwnNode;
    }

    readonly record struct Record(Vector3 Point, Vector3 Dir, int Group, int Vertex);

    readonly record struct Insert(int Edge, float T, Vector3 Point);

    const float Grid = 0.03125f;
    const float Reach = 0.0009765625f;

    public static void Run(IReadOnlyList<Mesh> meshes)
    {
        var records = Collect(meshes);
        foreach (var mesh in meshes)
        {
            if (!mesh.Excluded)
                Fix(mesh, records);
        }
    }

    static Vector3 Pos(Mesh m, int v) => new(m.Vertices[v * m.Stride], m.Vertices[v * m.Stride + 1], m.Vertices[v * m.Stride + 2]);

    static void SetPos(Mesh m, int v, Vector3 p)
    {
        m.Vertices[v * m.Stride] = p.X;
        m.Vertices[v * m.Stride + 1] = p.Y;
        m.Vertices[v * m.Stride + 2] = p.Z;
    }

    static float Snap(float x) => MathF.Floor(x / Grid + 0.5f) * Grid;

    static Vector3 Snap(Vector3 p) => new(Snap(p.X), Snap(p.Y), Snap(p.Z));

    static bool Same(Vector3 a, Vector3 b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z;

    /// <summary>FUN_18013e340: squares summed y, z, x; outside 1e-17 to 1e17 the double path, zero stays zero.</summary>
    internal static Vector3 Normalise(Vector3 d)
    {
        var len = MathF.Sqrt(d.Y * d.Y + d.Z * d.Z + d.X * d.X);
        if (1e-17f <= len && len <= 1e17f)
        {
            var inv = 1f / len;
            return new Vector3(inv * d.X, inv * d.Y, inv * d.Z);
        }
        if (len == 0f)
            return Vector3.Zero;
        double x = d.X, y = d.Y, z = d.Z;
        var l = Math.Sqrt(y * y + x * x + z * z);
        return new Vector3((float)(x / l), (float)(y / l), (float)(z / l));
    }

    /// <summary>1812d91c0: each corner's edge (corner k to k + 1) twinned with the opposite edge on the same two vertices, -1 without.</summary>
    static int[] Twins(Mesh m)
    {
        var edges = new Dictionary<(int, int), int>();
        var count = m.Indices.Count / 3 * 3;
        for (var c = 0; c < count; c++)
        {
            var a = m.Indices[c];
            var b = m.Indices[c - c % 3 + (c % 3 + 1) % 3];
            edges.TryAdd((a, b), c);
        }
        var twin = new int[count];
        for (var c = 0; c < count; c++)
        {
            var a = m.Indices[c];
            var b = m.Indices[c - c % 3 + (c % 3 + 1) % 3];
            twin[c] = edges.TryGetValue((b, a), out var o) ? o : -1;
        }
        return twin;
    }

    static List<Record> Collect(IReadOnlyList<Mesh> meshes)
    {
        var records = new List<Record>();
        foreach (var m in meshes)
        {
            if (m.Excluded)
                continue;
            var twin = Twins(m);
            for (var t = 0; t + 2 < m.Indices.Count; t += 3)
            {
                int i0 = m.Indices[t], i1 = m.Indices[t + 1], i2 = m.Indices[t + 2];
                Vector3 p0 = Pos(m, i0), p1 = Pos(m, i1), p2 = Pos(m, i2);
                if (Same(p1, p0) || Same(p2, p1) || Same(p0, p2))
                    continue;
                int[] ix = [i0, i1, i2];
                Vector3[] p = [p0, p1, p2];
                for (var k = 0; k < 3; k++)
                {
                    if (twin[t + k] != -1)
                        continue;
                    var n = (k + 1) % 3;
                    records.Add(new Record(p[k], Normalise(p[n] - p[k]), m.Group, ix[k]));
                    records.Add(new Record(p[n], Normalise(p[k] - p[n]), m.Group, ix[n]));
                }
            }
        }
        // Sorted byte-wise and deduplicated (memcmp over the 32 bytes).
        static byte[] Bytes(Record r)
        {
            var b = new byte[32];
            BitConverter.TryWriteBytes(b.AsSpan(0), r.Point.X);
            BitConverter.TryWriteBytes(b.AsSpan(4), r.Point.Y);
            BitConverter.TryWriteBytes(b.AsSpan(8), r.Point.Z);
            BitConverter.TryWriteBytes(b.AsSpan(12), r.Dir.X);
            BitConverter.TryWriteBytes(b.AsSpan(16), r.Dir.Y);
            BitConverter.TryWriteBytes(b.AsSpan(20), r.Dir.Z);
            BitConverter.TryWriteBytes(b.AsSpan(24), r.Group);
            BitConverter.TryWriteBytes(b.AsSpan(28), r.Vertex);
            return b;
        }
        var keyed = records.Select(r => (Key: Bytes(r), R: r)).ToList();
        keyed.Sort((a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));
        var unique = new List<Record>();
        for (var i = 0; i < keyed.Count; i++)
        {
            if (i == 0 || !keyed[i].Key.AsSpan().SequenceEqual(keyed[i - 1].Key))
                unique.Add(keyed[i].R);
        }
        return unique;
    }

    static void Fix(Mesh m, List<Record> records)
    {
        var twin = Twins(m);
        var original = m.Indices.Count / 3 * 3;
        // 1812db3c0: the triangles on each vertex.
        var on = new Dictionary<int, List<int>>();
        for (var c = 0; c < original; c++)
        {
            if (!on.TryGetValue(m.Indices[c], out var list))
                on[m.Indices[c]] = list = [];
            list.Add(c / 3);
        }
        for (var t = 0; t < original; t += 3)
        {
            if (twin[t] != -1 && twin[t + 1] != -1 && twin[t + 2] != -1)
                continue;
            int[] ix = [m.Indices[t], m.Indices[t + 1], m.Indices[t + 2]];
            Vector3[] c = [Pos(m, ix[0]), Pos(m, ix[1]), Pos(m, ix[2])];
            Vector3[] s = [Snap(c[0]), Snap(c[1]), Snap(c[2])];
            if (Same(s[1], s[0]) || Same(s[2], s[1]) || Same(s[0], s[2]))
                continue;
            Vector3[] dirs = [Normalise(c[1] - c[0]), Normalise(c[2] - c[1]), Normalise(c[0] - c[2])];
            var lo = new Vector3(Min(c, 0), Min(c, 1), Min(c, 2)) - new Vector3(Grid);
            var hi = new Vector3(Max(c, 0), Max(c, 1), Max(c, 2)) + new Vector3(Grid);
            var flags = new bool[3];
            var inserts = new List<Insert>();
            foreach (var r in records)
            {
                if (r.Point.X < lo.X || hi.X < r.Point.X || r.Point.Y < lo.Y || hi.Y < r.Point.Y || r.Point.Z < lo.Z || hi.Z < r.Point.Z)
                    continue;
                Visit(m, r, ix, c, s, dirs, twin, t, flags, inserts);
            }
            for (var k = 0; k < 3; k++)
            {
                if (!flags[k])
                    continue;
                // The slot's vertex as it stands now (a swap for an earlier
                // corner may have moved it), with the slot's snapped position.
                var v = m.Indices[t + k];
                foreach (var tri in on.GetValueOrDefault(v) ?? [])
                    KeepTurn(m, tri, v, s[k]);
                SetPos(m, v, s[k]);
            }
            if (inserts.Count == 0)
                continue;
            var sorted = inserts.ToArray();
            MsvcSort.Sort(sorted, (a, b) => a.Edge == b.Edge ? a.T < b.T : a.Edge < b.Edge);
            var poly = new List<int>();
            var points = new List<Vector3>();
            var cursor = 0;
            for (var k = 0; k < 3; k++)
            {
                var a = m.Indices[t + k];
                var b = m.Indices[t + (k + 1) % 3];
                poly.Add(a);
                points.Add(Pos(m, a));
                for (; cursor < sorted.Length && sorted[cursor].Edge < k + 1; cursor++)
                {
                    var p = Snap(sorted[cursor].Point);
                    if (Same(Pos(m, m.Indices[t]), p) || Same(Pos(m, m.Indices[t + 1]), p) || Same(Pos(m, m.Indices[t + 2]), p))
                        continue;
                    if (points.Any(q => Same(q, p)))
                        continue;
                    var added = m.Vertices.Count / m.Stride;
                    var tt = sorted[cursor].T;
                    for (var f = 0; f < m.Stride; f++)
                    {
                        var va = m.Vertices[a * m.Stride + f];
                        var vb = m.Vertices[b * m.Stride + f];
                        m.Vertices.Add((vb - va) * tt + va);
                    }
                    SetPos(m, added, p);
                    poly.Add(added);
                    points.Add(p);
                }
            }
            var expected = (points.Count - 2) * 3;
            var cut = PolygonTriangulator.Triangulate([.. points]);
            if (cut.Length != expected)
            {
                cut = new int[expected];
                for (var i = 0; i < points.Count - 2; i++)
                    (cut[i * 3], cut[i * 3 + 1], cut[i * 3 + 2]) = (0, i + 1, i + 2);
            }
            for (var i = 0; i < cut.Length; i += 3)
            {
                if (i == 0)
                {
                    m.Indices[t] = poly[cut[0]];
                    m.Indices[t + 1] = poly[cut[1]];
                    m.Indices[t + 2] = poly[cut[2]];
                }
                else
                {
                    m.Indices.Add(poly[cut[i]]);
                    m.Indices.Add(poly[cut[i + 1]]);
                    m.Indices.Add(poly[cut[i + 2]]);
                }
            }
        }
    }

    static float Min(Vector3[] c, int axis)
    {
        var a = Axis(c[0], axis);
        var b = Axis(c[1], axis);
        var m = b <= a ? b : a;
        var z = Axis(c[2], axis);
        return z <= m ? z : m;
    }

    static float Max(Vector3[] c, int axis)
    {
        var a = Axis(c[0], axis);
        var b = Axis(c[1], axis);
        var m = a <= b ? b : a;
        var z = Axis(c[2], axis);
        return m <= z ? z : m;
    }

    static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    /// <summary>FUN_18025cf80, one record against the triangle.</summary>
    static void Visit(Mesh m, Record r, int[] ix, Vector3[] c, Vector3[] s, Vector3[] dirs, int[] twin, int t, bool[] flags, List<Insert> inserts)
    {
        if (r.Group == m.Group)
        {
            if (m.SkipOwnNode)
                return;
            if (r.Vertex == ix[0] || r.Vertex == ix[1] || r.Vertex == ix[2])
                return;
        }
        var p = Snap(r.Point);
        for (var k = 0; k < 3; k++)
        {
            if (Same(s[k], p))
            {
                flags[k] = true;
                return;
            }
        }
        for (var k = 0; k < 3; k++)
        {
            if (twin[t + k] != -1)
                continue;
            var e = dirs[k];
            if (!(0.99f <= MathF.Abs(e.Z * r.Dir.Z + r.Dir.Y * e.Y + e.X * r.Dir.X)))
                continue;
            var a = c[k];
            var b = c[(k + 1) % 3];
            var tp = ((r.Point.Z - a.Z) * (b.Z - a.Z) + (r.Point.Y - a.Y) * (b.Y - a.Y) + (r.Point.X - a.X) * (b.X - a.X))
                     / ((b.Z - a.Z) * (b.Z - a.Z) + (b.Y - a.Y) * (b.Y - a.Y) + (b.X - a.X) * (b.X - a.X));
            if (!(0f < tp && tp < 1f))
                continue;
            var qx = r.Point.X - ((b.X - a.X) * tp + a.X);
            var qy = r.Point.Y - ((b.Y - a.Y) * tp + a.Y);
            var qz = r.Point.Z - ((b.Z - a.Z) * tp + a.Z);
            if (qy * qy + qx * qx + qz * qz < Reach)
                inserts.Add(new Insert(k, tp, r.Point));
        }
    }

    /// <summary>
    /// A triangle on a vertex that moves: when the turn of its corners with the
    /// vertex at its new place opposes the old one, its first two indices swap.
    /// </summary>
    static void KeepTurn(Mesh m, int tri, int v, Vector3 moved)
    {
        int ia = m.Indices[tri * 3], ib = m.Indices[tri * 3 + 1], ic = m.Indices[tri * 3 + 2];
        Vector3 pa = Pos(m, ia), pb = Pos(m, ib), pc = Pos(m, ic);
        float e1x = pb.X - pa.X, e1y = pb.Y - pa.Y, e1z = pb.Z - pa.Z;
        float e2x = pc.X - pa.X, e2y = pc.Y - pa.Y, e2z = pc.Z - pa.Z;
        var na = ia == v ? moved : pa;
        var nb = ib == v ? moved : pb;
        var nc = ic == v ? moved : pc;
        var turn = (e2y * e1x - e1y * e2x) * ((nc.Y - na.Y) * (nb.X - na.X) - (nb.Y - na.Y) * (nc.X - na.X))
                   + ((nb.Z - na.Z) * (nc.X - na.X) - (nc.Z - na.Z) * (nb.X - na.X)) * (e1z * e2x - e2z * e1x)
                   + ((nc.Z - na.Z) * (nb.Y - na.Y) - (nb.Z - na.Z) * (nc.Y - na.Y)) * (e2z * e1y - e2y * e1z);
        if (turn < 0f)
        {
            m.Indices[tri * 3] = ib;
            m.Indices[tri * 3 + 1] = ia;
        }
    }
}
