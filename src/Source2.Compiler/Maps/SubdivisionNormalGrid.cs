using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The subdivision bake's patch normal grid (resourcecompiler 0923 1813c3cf0, ledger 38), the normals the tangent
/// grid (<see cref="SubdivisionTangents.Grid"/>) reads, on the .vmap's own arrays before anything is split. Half-edge
/// steps as the mesh's accessors take them: next (18139eee0, record +0x20), twin (18139efc0, +0x10), previous
/// (181396350, next walked to the start), the turn about a vertex twin(next) (18139ed20), a face (+0x30).
/// </summary>
internal sealed class SubdivisionNormalGrid(FaceArrays a, float cos)
{
    // 1813c3cf0's fVar36: the mesh's +0x3470 (the smoothing angle's cosine) plus 1e-5.
    private readonly float threshold = cos + 1e-5f;

    private int Twin(int h) => a.Opposite[h];
    private int Face(int h) => h >= 0 && h < a.EdgeFace.Length ? a.EdgeFace[h] : -1;

    private int Prev(int h)
    {
        var p = h;
        while (a.Next[p] != h)
            p = a.Next[p];
        return p;
    }

    private int Turn(int h) => Twin(a.Next[h]);

    // A face's loop from half-edge h (1813bda20 walks the patch's corners from its own).
    private (List<int> Loop, int At) Corner(int h)
    {
        var loop = a.Loop(Face(h));
        return (loop, loop.IndexOf(h));
    }

    private static Vector3 N(Vector3 v) => SubdivisionTangents.Normalise(v);

    private static Vector3 Add(Vector3 x, Vector3 y) => new(x.X + y.X, x.Y + y.Y, x.Z + y.Z);

    // The dot every comparison here makes: (z z' + y y') + x x'.
    private static float Dot(Vector3 x, Vector3 y) => x.Z * y.Z + x.Y * y.Y + x.X * y.X;

    private static Vector3 Cross(Vector3 x, Vector3 y) => new(x.Y * y.Z - x.Z * y.Y, x.Z * y.X - x.X * y.Z, x.X * y.Y - x.Y * y.X);

    private static Vector3 Sub(Vector3 x, Vector3 y) => new(x.X - y.X, x.Y - y.Y, x.Z - y.Z);

    // The patch's point from its lerped base and displacement d through the frame (B with d.x, T with d.y, N with
    // d.z), summed left to right as 1813bd320 and 1813c0960 do.
    private static Vector3 Displaced(Vector3 baseP, Vector3 d, (Vector3 N, Vector3 T, Vector3 B) f)
        => new(baseP.X + f.B.X * d.X + f.T.X * d.Y + f.N.X * d.Z,
               baseP.Y + f.B.Y * d.X + f.T.Y * d.Y + f.N.Y * d.Z,
               baseP.Z + f.B.Z * d.X + f.T.Z * d.Y + f.N.Z * d.Z);

    // 18243e8a0: per patch corner k, its grid corner (row, column in units of n), then three (control point, row
    // step, column step) triples; the first two are a neighbour's, the third the diagonal's.
    private static readonly int[][] CornerTable =
    [
        [0, 0, 1, 0, 1, 2, 1, 0, 3, 1, 1],
        [0, 1, 3, 1, 0, 0, 0, -1, 2, 1, -1],
        [1, 0, 0, -1, 0, 3, 0, 1, 1, -1, 1],
        [1, 1, 2, 0, -1, 1, -1, 0, 0, -1, -1],
    ];

    /// <summary>
    /// 1813bd320: the normal at corner k of the patch at half-edge h, from the corner point P, the point one grid
    /// step towards control point A (displaced), and the diagonal one (lerped towards B, then from A towards C):
    /// |(Q1 - P) x (Q2 - P)|. A face-less half-edge gives zero; an unsubdivided face its patch frame's normal.
    /// </summary>
    public Vector3 CornerPatchNormal(int h, int k)
    {
        if (Face(h) < 0)
            return Vector3.Zero;
        var (loop, at) = Corner(h);
        var frame = a.PatchFrame(loop, at, Face(h));
        var level = a.Level(h);
        if (level == 0)
            return frame.N;
        var n = 1 << Math.Max(level - 1, 0);
        var g = n + 1;
        var inv = 1f / n;
        var c = a.PatchControls(loop, at);
        var t = CornerTable[k];
        var i0 = (g * t[0] + t[1]) * n;
        var i1 = g * t[3] + t[4] + i0;
        var i2 = i0 + g * t[9] + t[10];
        var b = c[k];
        Vector3 D(int i) => a.DisplacementAt(h, i);
        var d0 = D(i0);
        var p = new Vector3(d0.X * frame.B.X + b.X + d0.Y * frame.T.X + d0.Z * frame.N.X,
                            d0.X * frame.B.Y + b.Y + d0.Y * frame.T.Y + d0.Z * frame.N.Y,
                            frame.B.Z * d0.X + b.Z + frame.T.Z * d0.Y + frame.N.Z * d0.Z);
        var ca = c[t[2]];
        var cb = c[t[5]];
        var cc = c[t[8]];
        var d1 = D(i1);
        var q1 = new Vector3(((ca.X - b.X) * inv + b.X + d1.X * frame.B.X + d1.Y * frame.T.X + d1.Z * frame.N.X) - p.X,
                             ((ca.Y - b.Y) * inv + b.Y + d1.X * frame.B.Y + d1.Y * frame.T.Y + d1.Z * frame.N.Y) - p.Y,
                             ((ca.Z - b.Z) * inv + b.Z + d1.X * frame.B.Z + d1.Y * frame.T.Z + d1.Z * frame.N.Z) - p.Z);
        var e = new Vector3((cb.X - b.X) * inv + b.X, (cb.Y - b.Y) * inv + b.Y, (cb.Z - b.Z) * inv + b.Z);
        var d2 = D(i2);
        var q2 = new Vector3(((((cc.X - ca.X) * inv + ca.X) - e.X) * inv + e.X + d2.X * frame.B.X + d2.Y * frame.T.X + d2.Z * frame.N.X) - p.X,
                             ((((cc.Y - ca.Y) * inv + ca.Y) - e.Y) * inv + e.Y + d2.X * frame.B.Y + d2.Y * frame.T.Y + d2.Z * frame.N.Y) - p.Y,
                             ((((cc.Z - ca.Z) * inv + ca.Z) - e.Z) * inv + e.Z + d2.X * frame.B.Z + d2.Y * frame.T.Z + d2.Z * frame.N.Z) - p.Z);
        return N(Cross(q1, q2));
    }

    /// <summary>
    /// 1813c8830: whether the edge of <paramref name="next"/> joins the two corners smoothly for the patches: its flags
    /// first (bit 0 hard, bit 1 soft), then the cosine (above 0.99999 hard, under 1e-5 smooth), else the cosine + 1e-5
    /// under the dot of the two patches' corner normals.
    /// </summary>
    private bool PatchSmooth(int cur, int next)
    {
        var flags = a.EdgeFlags(next);
        if ((flags & 1) != 0)
            return false;
        if ((flags & 2) != 0)
            return true;
        if (!(cos <= 0.99999f))
            return false;
        if (cos < 1e-5f)
            return true;
        var x = CornerPatchNormal(cur, 0);
        var y = CornerPatchNormal(next, 0);
        return cos + 1e-5f < x.Y * y.Y + x.Z * y.Z + x.X * y.X;
    }

    /// <summary>
    /// 1813c6140: the patch's vertex corner (0, 0): the corners about the vertex from the turn after the last crease
    /// (1813c8830) up to the first, their patch corner normals (1813bd320, corner 0) summed and normalised; a corner
    /// whose face is not subdivided or missing gives the half-edge corner normal instead (1813850e0), which goes
    /// through the same normalise again.
    /// </summary>
    public Vector3 VertexCorner(int h)
    {
        int start = h, end = h, cur = h;
        var crease = false;
        int t;
        do
        {
            t = Turn(cur);
            if (!crease)
                end = t;
            if (!PatchSmooth(cur, t))
            {
                crease = true;
                start = t;
            }
            cur = t;
        }
        while (t != h);
        var sum = Vector3.Zero;
        var k = start;
        do
        {
            if (Face(k) < 0 || a.Level(k) == 0)
            {
                sum = CornerNormal(h);
                break;
            }
            var v = CornerPatchNormal(k, 0);
            sum = new Vector3(sum.X + v.X, sum.Y + v.Y, sum.Z + v.Z);
            k = Turn(k);
        }
        while (k != end);
        return N(sum);
    }

    // 181384e40: a face's Newell normal over its loop from its first half-edge.
    private Vector3 FaceNormal(int face) => PolygonTriangulator.Newell([.. a.Loop(face).Select(h => a.Pos(a.To[h]))]);

    // 1813a5ce0 on half-edge h's edge: open hard, then the flags, then cos + 1e-5 under the faces' normals' dot.
    private bool EdgeSmooth(int h)
    {
        var t = Twin(h);
        if (Face(h) < 0 || Face(t) < 0)
            return false;
        var flags = a.EdgeFlags(h);
        if ((flags & 1) != 0)
            return false;
        if ((flags & 2) != 0)
            return true;
        if (!(cos <= 0.99999f))
            return false;
        if (cos < 1e-5f)
            return true;
        var e = Math.Min(h, t);
        var (n1, n2) = (FaceNormal(Face(e)), FaceNormal(Face(Twin(e))));
        return cos + 1e-5f < (n1.Y * n2.Y) + (n1.Z * n2.Z) + (n1.X * n2.X);
    }

    /// <summary>
    /// 1813850e0: corner c0's normal, its faces' normals summed over its smooth fan (corners turn about the vertex,
    /// the fan from the turn after the last hard edge up to the first) and normalised, as the bake's recompute
    /// (SubdivisionBake.RecomputeNormals) takes it on the baked mesh.
    /// </summary>
    public Vector3 CornerNormal(int c0)
    {
        int s = c0, end = c0, c = c0;
        var hard = false;
        do
        {
            c = Turn(c);
            if (!hard)
                end = c;
            if (!EdgeSmooth(c))
            {
                hard = true;
                s = c;
            }
        }
        while (c != c0);
        float x = 0f, y = 0f, z = 0f;
        var k = s;
        do
        {
            if (Face(k) >= 0)
            {
                var n = FaceNormal(Face(k));
                x += n.X;
                y += n.Y;
                z += n.Z;
            }
            k = Turn(k);
        }
        while (k != end);
        return NodeMeshEntries.Normalise(new Vector3(x, y, z));
    }

    /// <summary>
    /// 1813c0960: the normals along one border of the patch at half-edge h, side 0 a row and 1 a column, at index 0
    /// or n, from that line and the one beside it rebuilt from the control points (lerp(lerp(c0, c1, u),
    /// lerp(c2, c3, u), v)) and the displacement. The ends take one triangle, the points between two; the points
    /// between are signed by the side (-1 at index n). Null when the face is not subdivided.
    /// </summary>
    public Vector3[]? BorderNormals(int h, int side, int index)
    {
        if (Face(h) < 0 || a.Level(h) == 0)
            return null;
        var (loop, at) = Corner(h);
        var frame = a.PatchFrame(loop, at, Face(h));
        var c = a.PatchControls(loop, at);
        var n = 1 << Math.Max(a.Level(h) - 1, 0);
        var g = n + 1;
        var inv = 1f / n;
        bool b3 = side == 0 && index == 0, b4 = side == 0 && index >= n, b5 = side == 1 && index == 0, b6 = side == 1 && index >= n;
        int first = index, other;
        float sign;
        if (index < n)
        {
            other = index + 1;
            sign = 1f;
        }
        else
        {
            first = n;
            other = n - 1;
            sign = -1f;
        }
        // The column branch takes the sign negated (local_2d8 = -fVar25).
        if (side == 1)
            sign = -sign;
        var line = new Vector3[2 * g];
        int[] lines = [first, other];
        for (var j = 0; j < 2; j++)
            for (var q = 0; q <= n; q++)
            {
                var (r, col) = side == 0 ? (lines[j], q) : (q, lines[j]);
                var u = col * inv;
                var v = r * inv;
                var ax = (c[1].X - c[0].X) * u + c[0].X;
                var ay = (c[1].Y - c[0].Y) * u + c[0].Y;
                var az = (c[1].Z - c[0].Z) * u + c[0].Z;
                var bx = (c[3].X - c[2].X) * u + c[2].X;
                var by = (c[3].Y - c[2].Y) * u + c[2].Y;
                var bz = (c[3].Z - c[2].Z) * u + c[2].Z;
                line[j * g + q] = Displaced(new Vector3((bx - ax) * v + ax, (by - ay) * v + ay, (bz - az) * v + az), a.DisplacementAt(h, r * g + col), frame);
            }
        var o = new Vector3[g];
        Vector3 L(int i) => line[i];
        var l0 = L(0);
        if (b3 || b6)
        {
            o[0] = N(Cross(Sub(L(1), l0), Sub(L(g + 1), l0)));
            o[n] = N(Cross(Sub(L(g + n), L(n)), Sub(L(g + n - 1), L(n))));
        }
        else if (b4 || b5)
        {
            o[0] = N(Cross(Sub(L(g), l0), Sub(L(g + 1), l0)));
            o[n] = N(Cross(Sub(L(n - 1), L(n)), Sub(L(g + n - 1), L(n))));
        }
        for (var q = 1; q < n; q++)
        {
            var p = L(q);
            var s = Sub(L(g + q), p);
            var n1 = N(Cross(Sub(L(q + 1), p), s));
            var n2 = N(Cross(Sub(p, L(q - 1)), s));
            var m = N(new Vector3(n2.X + n1.X, n2.Y + n1.Y, n2.Z + n1.Z));
            o[q] = new Vector3(m.X * sign, m.Y * sign, m.Z * sign);
        }
        return o;
    }

    /// <summary>
    /// 1813c3cf0: the patch at half-edge h's normal grid from its position grid. The position normals (1813be620),
    /// then the inner seams averaged with the face's neighbouring patches (the previous corner's column n, the next
    /// corner's row n), the centre the sum of every corner patch's centre normal (1813bd320, corner 3), the vertex
    /// corner (1813c6140), then each border on a mesh edge that is neither open nor hard: against the patch across
    /// it (its column or row 0), or, when that face is not subdivided, the two ends' corner normals (1813850e0)
    /// lerped; a soft edge takes them always, any other where the dot passes the threshold. Null when the face is
    /// not subdivided or the mesh has no displacement grids (Valve's function then writes nothing).
    /// </summary>
    public Vector3[]? Grid(int h, IReadOnlyList<Vector3> positions)
    {
        var level = a.Level(h);
        if (level == 0 || !a.HasDisplacement)
            return null;
        var n = 1 << Math.Max(level - 1, 0);
        var g = n + 1;
        var o = SubdivisionTangents.PositionNormals(positions, n);
        var next = a.Next[h];
        var prev = Prev(h);
        if (BorderNormals(prev, 1, n) is { } seamPrev)
            for (var k = 0; k < n; k++)
                o[n * g + k] = N(Add(seamPrev[k], o[n * g + k]));
        if (BorderNormals(next, 0, n) is { } seamNext)
            for (var k = 0; k < n; k++)
                o[k * g + n] = N(Add(seamNext[k], o[k * g + n]));
        var sum = Vector3.Zero;
        var cur = h;
        do
        {
            var v = CornerPatchNormal(cur, 3);
            sum = new Vector3(sum.X + v.X, sum.Y + v.Y, sum.Z + v.Z);
            cur = a.Next[cur];
        }
        while (cur != h);
        o[n * g + n] = N(sum);
        o[0] = VertexCorner(h);

        // The border along the next side: row 0 against the patch across the edge of `next`.
        Border(o, n, g, next, i => i, nbTwin: true);
        // The border along the previous side: column 0 against the patch across the edge of `h`.
        Border(o, n, g, h, i => i * g, nbTwin: false);
        return o;
    }

    // One mesh-edge border of 1813c3cf0. For the next side the neighbour is twin(next)'s patch, its column 0
    // (c0960 side 1, index 0), whose far end is its patch corner 1 (1813bd320) and whose unsubdivided ends are
    // twin(next) and previous(twin(next)); for the previous side it is previous(twin(h))'s patch, its row 0
    // (side 0, index 0), its far end twin(h)'s corner 2, its unsubdivided ends previous(twin(h)) and twin(h).
    private void Border(Vector3[] o, int n, int g, int edge, Func<int, int> at, bool nbTwin)
    {
        var t = Twin(edge);
        if (Face(edge) < 0 || Face(t) < 0)
            return;
        var flags = a.EdgeFlags(edge);
        if ((flags & 1) != 0)
            return;
        var soft = (flags & 2) != 0;
        var pt = Prev(t);
        var nb = nbTwin ? BorderNormals(t, 1, 0) : BorderNormals(pt, 0, 0);
        var n2 = (nb?.Length ?? 0) - 1;
        if (n2 < 1)
        {
            var (cn0, cn1) = nbTwin ? (CornerNormal(t), CornerNormal(pt)) : (CornerNormal(pt), CornerNormal(t));
            var step = 0.5f / n;
            for (var i = 1; i <= n; i++)
            {
                var s = i * step;
                if (!soft)
                {
                    var l = new Vector3((cn1.X - cn0.X) * s + cn0.X, (cn1.Y - cn0.Y) * s + cn0.Y, (cn1.Z - cn0.Z) * s + cn0.Z);
                    if (threshold < Dot(o[at(i)], N(l)))
                        o[at(i)] = l;
                }
                else
                    o[at(i)] = new Vector3(s * (cn1.X - cn0.X) + cn0.X, s * (cn1.Y - cn0.Y) + cn0.Y, s * (cn1.Z - cn0.Z) + cn0.Z);
            }
            return;
        }
        var g2 = n2 + 1;
        var far = nbTwin ? CornerPatchNormal(pt, 1) : CornerPatchNormal(t, 2);
        var c = N(Add(far, nb![g2 - 1]));
        if (soft || threshold < Dot(o[at(n)], c))
        {
            var v = N(Add(c, o[at(n)]));
            o[at(n)] = v;
            nb[g2 - 1] = v;
        }
        nb[0] = o[0];
        if (n2 < n)
        {
            var ratio = n / n2;
            var inv = 1f / ratio;
            for (var j = 0; j < n2; j++)
            {
                var i0 = j * ratio;
                var A = o[at(i0)];
                if (soft || threshold < Dot(nb[j], A))
                    A = N(Add(A, nb[j]));
                var B = o[at(i0 + ratio)];
                if (soft || threshold < Dot(nb[j + 1], B))
                    B = N(Add(B, nb[j + 1]));
                for (var k = 0; k < ratio; k++)
                {
                    var s = k * inv;
                    o[at(i0 + k)] = new Vector3(s * (B.X - A.X) + A.X, s * (B.Y - A.Y) + A.Y, s * (B.Z - A.Z) + A.Z);
                }
            }
        }
        else
        {
            var ratio = n2 / n;
            for (var k = 1; k < n; k++)
            {
                var m = nb[k * ratio];
                if (soft || threshold < Dot(m, o[at(k)]))
                    o[at(k)] = N(Add(o[at(k)], m));
            }
        }
    }
}
