namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// The fan split's fallback triangulation (navsystem 20261015, 1800f3780): floats throughout, the
/// polygon's corners and normal as floats. Four corners choose a diagonal (1800f30b0); more corners
/// are ear-clipped by score (1800f3d70). Sums follow the disassembly, which Ghidra's printing does
/// not always group as the code does.
/// </summary>
public static class NavGenTriangulate
{
    private readonly record struct V(float X, float Y, float Z)
    {
        public static V operator -(V a, V b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    }

    /// <summary>
    /// Triangles over corner indices into <paramref name="tris"/> (three per triangle), at most
    /// <paramref name="cap"/> indices; returns the index count, 0 on failure.
    /// </summary>
    public static int Triangulate(IReadOnlyList<(float X, float Y, float Z)> corners, (float X, float Y, float Z) normal, List<int> tris, int cap)
    {
        tris.Clear();
        var n = corners.Count;
        if (n < 3)
            return 0;
        var p = corners.Select(c => new V(c.X, c.Y, c.Z)).ToArray();
        var nrm = new V(normal.X, normal.Y, normal.Z);
        if (n == 4)
        {
            if (cap < 6)
                return 0;
            var (len02, q02) = Diagonal(p, [0, 1, 2, 0, 2, 3], 0, 2, nrm);
            var (len13, q13) = Diagonal(p, [0, 1, 3, 1, 2, 3], 1, 3, nrm);
            var valid02 = (q02 & 2) != 0;
            var valid13 = (q13 & 2) != 0;
            if (!valid02 && !valid13)
                return 0;
            if (!(valid02 && valid13))
            {
                // Some corner within 1e-7 of either edge from the next corner fails the quad.
                for (var k = 2; k <= 5; k++)
                {
                    var o = p[k - 2];
                    if (SegmentDistance(o, p[(k - 1) & 3], p[k & 3]) < 1e-7f)
                        return 0;
                    if (SegmentDistance(o, p[k & 3], p[(k + 1) & 3]) < 1e-7f)
                        return 0;
                }
            }
            if (q13 <= q02 && len02 <= len13 * 1.01f)
                tris.AddRange([0, 1, 2, 0, 2, 3]);
            else
                tris.AddRange([0, 1, 3, 1, 2, 3]);
            return 6;
        }
        var idx = Enumerable.Range(0, n).ToList();
        var score = new List<float>(n);
        for (var i = 0; i < n; i++)
            score.Add(Score(p, i, idx, nrm));
        var written = 2;
        var total = 0;
        while (true)
        {
            if (cap <= written)
                return total;
            var count = idx.Count;
            var best = -1;
            var top = 0f;
            for (var i = 0; i < count; i++)
                if (top < score[i])
                {
                    top = score[i];
                    best = i;
                }
            if (best == -1)
                return 0;
            total += 3;
            written += 3;
            tris.Add(idx[(count - 1 + best) % count]);
            tris.Add(idx[best]);
            tris.Add(idx[(best + 1) % count]);
            idx.RemoveAt(best);
            score.RemoveAt(best);
            var m = count - 1;
            var before = ((count - 2) + best) % m;
            score[before] = Score(p, before, idx, nrm);
            var after = best % m;
            score[after] = Score(p, after, idx, nrm);
            if (m < 3)
                return total;
        }
    }

    // 1800f30b0: the two triangles of a split (t0..t5) and its diagonal i-j: (0, 0) when a triangle or
    // the diagonal is shorter than 1e-7 (squares below 1e-14), else the diagonal's squared length with 3
    // when both triangles face the normal, 2 when not.
    private static (float Len2, int Quality) Diagonal(V[] p, int[] t, int i, int j, V n)
    {
        var a = p[t[0]];
        float e1x = p[t[1]].X - a.X, e1y = p[t[1]].Y - a.Y, e1z = p[t[1]].Z - a.Z;
        float e2x = p[t[2]].X - a.X, e2y = p[t[2]].Y - a.Y, e2z = p[t[2]].Z - a.Z;
        var c1y = e2x * e1z - e2z * e1x;
        var c1x = e2z * e1y - e2y * e1z;
        var c1z = e2y * e1x - e2x * e1y;
        var b = p[t[3]];
        float f1x = p[t[4]].X - b.X, f1y = p[t[4]].Y - b.Y, f1z = p[t[4]].Z - b.Z;
        float f2x = p[t[5]].X - b.X, f2y = p[t[5]].Y - b.Y, f2z = p[t[5]].Z - b.Z;
        var c2x = f2z * f1y - f2y * f1z;
        var c2y = f2x * f1z - f2z * f1x;
        var c2z = f2y * f1x - f2x * f1y;
        var d = p[i] - p[j];
        var len2 = d.Y * d.Y + d.X * d.X + d.Z * d.Z;
        if (c1y * c1y + c1x * c1x + c1z * c1z < 1e-14f || c2y * c2y + c2x * c2x + c2z * c2z < 1e-14f || len2 < 1e-14f)
            return (0f, 0);
        var q = n.Z * c1z + n.Y * c1y + n.X * c1x <= 0f || n.Z * c2z + n.Y * c2y + n.X * c2x <= 0f ? 2 : 3;
        return (len2, q);
    }

    // 1800ebd60: the squared distance from q to the line a-b at its parameter t (t 0 under 1e-5).
    private static float LineDistance2(V q, V a, V b, out float t)
    {
        var d = b - a;
        var len2 = d.Z * d.Z + d.Y * d.Y + d.X * d.X;
        t = 1e-5f > len2 ? 0f : ((d.Z * q.Z + d.Y * q.Y + d.X * q.X) - (d.Z * a.Z + d.Y * a.Y + d.X * a.X)) / len2;
        var y = q.Y - (d.Y * t + a.Y);
        var z = q.Z - (d.Z * t + a.Z);
        var x = q.X - (d.X * t + a.X);
        return y * y + x * x + z * z;
    }

    // 1800ec0f0 / 1800ebad0: the distance from q to the segment a-b (t clamped to [0, 1]).
    private static float SegmentDistance(V q, V a, V b)
    {
        var d = b - a;
        var len2 = d.Z * d.Z + d.Y * d.Y + d.X * d.X;
        var t = 1e-5f > len2 ? 0f : ((d.Z * q.Z + d.Y * q.Y + d.X * q.X) - (d.Z * a.Z + d.Y * a.Y + d.X * a.X)) / len2;
        var c = 0f > t ? 0f : (1f < t ? 1f : t);
        var px = d.X * c + a.X;
        var py = d.Y * c + a.Y;
        var pz = d.Z * c + a.Z;
        float dy = q.Y - py, dz = q.Z - pz, dx = q.X - px;
        var s = dz * dz + dy * dy + dx * dx;
        return MathF.Sqrt(s);
    }

    // 1800f33c0: q inside triangle a, b, c about the normal (each edge's side above -1e-7), or within
    // 1e-7 of an edge (squared 1e-14, parameter in [0, 1]).
    private static bool InTriangle(V a, V b, V c, V q, V n)
    {
        static float Side(V from, V to, V q, V n)
        {
            float ex = to.X - from.X, ey = to.Y - from.Y, ez = to.Z - from.Z;
            float qx = q.X - from.X, qy = q.Y - from.Y, qz = q.Z - from.Z;
            var ty = ez * qx - ex * qz;
            var tz = ex * qy - ey * qx;
            var tx = ey * qz - ez * qy;
            return ty * n.Y + tz * n.Z + tx * n.X;
        }
        if (!(Side(b, c, q, n) <= -1e-7f) && !(Side(c, a, q, n) <= -1e-7f) && !(Side(a, b, q, n) <= -1e-7f))
            return true;
        foreach (var (s0, s1) in new[] { (a, b), (b, c), (c, a) })
            if (LineDistance2(q, s0, s1, out var t) < 1e-14f && 0f <= t && t <= 1f)
                return true;
        return false;
    }

    private static V Unit(V v, float len)
    {
        if (len < 1e-17f || 1e17f < len)
        {
            if (len == 0f)
                return new V(0, 0, 0);
            // 1800eda30: in doubles, (y² + x²) + z².
            double x = v.X, y = v.Y, z = v.Z;
            var inv = 1.0 / Math.Sqrt(y * y + x * x + z * z);
            return new V((float)(inv * x), (float)(inv * y), (float)(inv * z));
        }
        var f = 1f / len;
        return new V(v.X * f, v.Y * f, v.Z * f);
    }

    // 1800f3d70: the ear at idx[i]: -1 when its area about the normal is under 1e-7 or another corner
    // (not one of its own positions) lies in it; else 1 / max(smallest of its area and its neighbours'
    // ears, 1) + 2 (1 - the largest cosine between its edges).
    private static float Score(V[] p, int i, List<int> idx, V n)
    {
        var count = idx.Count;
        var prevAt = (i + count - 1) % count;
        var at = i % count;
        var nextAt = (i + 1) % count;
        var P = p[idx[prevAt]];
        var C = p[idx[at]];
        var N = p[idx[nextAt]];
        var a = C - P;
        var b = N - P;
        var area = (b.X * a.Z - b.Z * a.X) * n.Y + (b.Y * a.X - b.X * a.Y) * n.Z + (b.Z * a.Y - b.Y * a.Z) * n.X;
        if (area < 1e-7f)
            return -1f;
        for (var j = 0; j < count; j++)
        {
            if (j == prevAt || j == at || j == nextAt)
                continue;
            var q = p[idx[j]];
            if (!InTriangle(P, C, N, q, n))
                continue;
            if (q == P || q == C || q == N)
                continue;
            return -1f;
        }
        var least = area;
        if (3 < count)
        {
            var NN = p[idx[(nextAt + 1) % count]];
            float cz = C.Z - NN.Z, nz = N.Z - NN.Z, cx = C.X - NN.X, cy = C.Y - NN.Y, ny = N.Y - NN.Y, nx = N.X - NN.X;
            var e = (cz * nx - cx * nz) * n.Y + (cx * ny - cy * nx) * n.Z + (cy * nz - cz * ny) * n.X;
            if (e <= least)
                least = e;
        }
        if (4 < count)
        {
            var PP = p[idx[(i + count - 2) % count]];
            float pz = P.Z - PP.Z, cz = C.Z - PP.Z, px = P.X - PP.X, cx = C.X - PP.X, py = P.Y - PP.Y, cy = C.Y - PP.Y;
            var e = (pz * cx - px * cz) * n.Y + (px * cy - py * cx) * n.Z + (py * cz - pz * cy) * n.X;
            if (e <= least)
                least = e;
        }
        var nc = N - C;
        var u = Unit(nc, MathF.Sqrt(nc.Z * nc.Z + nc.Y * nc.Y + nc.X * nc.X));
        var v = Unit(a, MathF.Sqrt(a.Z * a.Z + a.Y * a.Y + a.X * a.X));
        var w = Unit(b, MathF.Sqrt(b.Y * b.Y + b.Z * b.Z + b.X * b.X));
        var vw = v.Z * w.Z + v.Y * w.Y + w.X * v.X;
        var uv = v.Z * u.Z + v.Y * u.Y + u.X * v.X;
        var uw = w.Y * u.Y + w.Z * u.Z + w.X * u.X;
        var inv = 1f / (least <= 1f ? 1f : least);
        var m = uv > vw ? uv : vw;
        m = uw > m ? uw : m;
        var one = 1f - m;
        return inv + (one + one);
    }
}
