using System.Numerics;

namespace Source2.Compiler.Physics;

/// <summary>
/// How the resource compiler cuts a mesh face of four or more corners into
/// triangles when it builds a physics mesh (<c>FUN_18136f7a0</c>): the face's
/// Newell normal (<c>FUN_18125b510</c>), then a quad rule or an ear clipper
/// (<c>FUN_18136f810</c>). A face it cannot cut yields no triangles, and the
/// caller then skips the face altogether.
/// </summary>
internal static class FaceTriangulator
{
    /// <summary>Triangles as corner indices, three per triangle; empty on failure.</summary>
    public static int[] Triangulate(Vector3[] p)
    {
        var n = p.Length;
        if (n < 3)
            return [];
        var normal = Normal(p);
        if (n == 4)
            return Quad(p, normal);
        return Clip(p, normal);
    }

    // FUN_18125b510
    private static Vector3 Normal(Vector3[] p)
    {
        float nx = 0f, ny = 0f, nz = 0f;
        for (var i = 0; i < p.Length; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Length];
            nx = nx + ((a.Y - b.Y) * (a.Z + b.Z));
            ny = ny + ((a.X + b.X) * (a.Z - b.Z));
            nz = nz + ((a.X - b.X) * (a.Y + b.Y));
        }
        var len = MathF.Sqrt(((ny * ny) + (nz * nz)) + (nx * nx));
        var inv = 1f / (len + 1.1920929e-07f);
        return new Vector3(nx * inv, ny * inv, nz * inv);
    }

    private static readonly int[] DiagonalA = [0, 1, 2, 0, 2, 3];
    private static readonly int[] DiagonalB = [0, 1, 3, 1, 2, 3];

    // The n == 4 branch of FUN_18136f810: the (0,2) diagonal unless it is the
    // worse fit or more than 1.01 times the other one squared.
    private static int[] Quad(Vector3[] p, Vector3 normal)
    {
        var (lenA, flagA) = Split(p, DiagonalA, 0, 2, normal);
        var (lenB, flagB) = Split(p, DiagonalB, 1, 3, normal);
        if ((flagA & 2) == 0 && (flagB & 2) == 0)
            return [];
        if (((flagA & 2) == 0) != ((flagB & 2) == 0))
        {
            // Only one split works: every corner must stand clear of the lines
            // through its neighbours (FUN_181256f30).
            for (var u = 2; u <= 5; u++)
            {
                var q = p[u - 2];
                if (SegmentDistance(q, p[(u - 1) & 3], p[u & 3]) < 1e-07f
                    || SegmentDistance(q, p[u & 3], p[(u + 1) & 3]) < 1e-07f)
                    return [];
            }
        }
        if (flagB <= flagA && lenA <= lenB * 1.01f)
            return [0, 1, 2, 0, 2, 3];
        return [0, 1, 3, 1, 2, 3];
    }

    // FUN_18136c790: the diagonal's squared length, and 3 when both triangles
    // face along the normal, 2 when one does not, 0 when either is degenerate.
    private static (float Length, int Flag) Split(Vector3[] p, int[] t, int d0, int d1, Vector3 n)
    {
        Vector3 a = p[t[0]], b = p[t[1]], c = p[t[2]];
        float e1x = b.X - a.X, e1y = b.Y - a.Y, e1z = b.Z - a.Z;
        float e2x = c.X - a.X, e2y = c.Y - a.Y, e2z = c.Z - a.Z;
        var c1y = (e2x * e1z) - (e2z * e1x);
        var c1x = (e2z * e1y) - (e2y * e1z);
        var c1z = (e2y * e1x) - (e2x * e1y);
        Vector3 d = p[t[3]], e = p[t[4]], f = p[t[5]];
        float g1x = e.X - d.X, g1y = e.Y - d.Y, g1z = e.Z - d.Z;
        float g2x = f.X - d.X, g2y = f.Y - d.Y, g2z = f.Z - d.Z;
        var c2x = (g2z * g1y) - (g2y * g1z);
        var c2y = (g2x * g1z) - (g2z * g1x);
        var c2z = (g2y * g1x) - (g2x * g1y);
        var dx = p[d0].X - p[d1].X;
        var dz = p[d0].Z - p[d1].Z;
        var dy = p[d0].Y - p[d1].Y;
        var len = ((dy * dy) + (dx * dx)) + (dz * dz);
        if (((c1y * c1y) + (c1x * c1x)) + (c1z * c1z) < 1e-14f || ((c2y * c2y) + (c2x * c2x)) + (c2z * c2z) < 1e-14f || len < 1e-14f)
            return (0f, 0);
        var flag = 3;
        if (((n.Z * c1z) + (n.Y * c1y)) + (n.X * c1x) <= 0f || ((n.Z * c2z) + (n.Y * c2y)) + (n.X * c2x) <= 0f)
            flag = 2;
        return (len, flag);
    }

    // FUN_181256f30 over FUN_181256960 is not ported exactly; the projection
    // of FUN_181256bf0 stands in, as this only guards a rare concave quad.
    private static float SegmentDistance(Vector3 q, Vector3 a, Vector3 b)
        => MathF.Sqrt(Project(q, a, b, out _));

    // FUN_181256bf0: squared distance from q to the line a-b, and the parameter t.
    private static float Project(Vector3 q, Vector3 a, Vector3 b, out float t)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var dz = b.Z - a.Z;
        var l = ((dz * dz) + (dy * dy)) + (dx * dx);
        t = 1e-05f <= l ? ((((dz * q.Z) + (dy * q.Y)) + (dx * q.X)) - (((dz * a.Z) + (dy * a.Y)) + (dx * a.X))) / l : 0f;
        var ey = q.Y - ((dy * t) + a.Y);
        var ez = q.Z - ((dz * t) + a.Z);
        var ex = q.X - ((dx * t) + a.X);
        return ((ey * ey) + (ex * ex)) + (ez * ez);
    }

    // The general branch of FUN_18136f810: cut the best ear, rescore its two
    // neighbours, repeat. No ear scoring above 0 fails the whole face.
    private static int[] Clip(Vector3[] p, Vector3 normal)
    {
        var n = p.Length;
        var idx = Enumerable.Range(0, n).ToList();
        var scores = new List<float>(n);
        for (var i = 0; i < n; i++)
            scores.Add(Score(p, i, idx, n, normal));
        var tris = new List<int>();
        while (true)
        {
            var best = -1;
            var bestScore = 0f;
            for (var k = 0; k < n; k++)
            {
                if (bestScore < scores[k])
                {
                    bestScore = scores[k];
                    best = k;
                }
            }
            if (best == -1)
                return [];
            tris.Add(idx[(n - 1 + best) % n]);
            tris.Add(idx[best]);
            tris.Add(idx[(best + 1) % n]);
            idx.RemoveAt(best);
            scores.RemoveAt(best);
            var m = n - 1;
            var k1 = (m - 1 + best) % m;
            scores[k1] = Score(p, k1, idx, m, normal);
            var k2 = best % m;
            scores[k2] = Score(p, k2, idx, m, normal);
            n = m;
            if (n < 3)
                return [.. tris];
        }
    }

    private static float Cross(Vector3 a, Vector3 b, Vector3 c, Vector3 n)
    {
        // ((v.x u.z - v.z u.x) n.y + (v.y u.x - v.x u.y) n.z) + (v.z u.y - v.y u.z) n.x, u = b - a, v = c - a
        float ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
        float vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
        return ((((vx * uz) - (vz * ux)) * n.Y) + (((vy * ux) - (vx * uy)) * n.Z)) + (((vz * uy) - (vy * uz)) * n.X);
    }

    // FUN_181370790: -1 when corner i is no ear.
    private static float Score(Vector3[] p, int i, List<int> idx, int n, Vector3 normal)
    {
        var prevI = (i + n - 1) % n;
        var curI = i % n;
        var nextI = (i + 1) % n;
        var a = p[idx[prevI]];
        var b = p[idx[curI]];
        var c = p[idx[nextI]];
        var area = Cross(a, b, c, normal);
        if (area < 1e-07f)
            return -1f;
        for (var j = 0; j < n; j++)
        {
            if (j == prevI || j == curI || j == nextI)
                continue;
            var q = p[idx[j]];
            if (!Inside(a, b, c, q, normal))
                continue;
            if (q == a || q == b || q == c)
                continue;
            return -1f;
        }
        var r = area;
        if (n > 3)
        {
            var d = p[idx[(nextI + 1) % n]];
            // (b - d, c - d) around d
            float ax = b.X - d.X, ay = b.Y - d.Y, az = b.Z - d.Z;
            float bx = c.X - d.X, by = c.Y - d.Y, bz = c.Z - d.Z;
            var v = ((((az * bx) - (ax * bz)) * normal.Y) + (((ax * by) - (ay * bx)) * normal.Z)) + (((ay * bz) - (az * by)) * normal.X);
            if (v <= r)
                r = v;
        }
        if (n > 4)
        {
            var e = p[idx[(i + n - 2) % n]];
            float az = a.Z - e.Z, bz = b.Z - e.Z, ax = a.X - e.X, bx = b.X - e.X, ay = a.Y - e.Y, by = b.Y - e.Y;
            var v = ((((az * bx) - (ax * bz)) * normal.Y) + (((ax * by) - (ay * bx)) * normal.Z)) + (((ay * bz) - (az * by)) * normal.X);
            if (v <= r)
                r = v;
        }
        var w = Unit(new Vector3(c.X - b.X, c.Y - b.Y, c.Z - b.Z));
        var u = Unit(new Vector3(b.X - a.X, b.Y - a.Y, b.Z - a.Z));
        var vv = UnitV(new Vector3(c.X - a.X, c.Y - a.Y, c.Z - a.Z));
        var c1 = ((vv.Y * u.Y) + (vv.Z * u.Z)) + (vv.X * u.X);
        if (r <= 1f)
            r = 1f;
        var c2 = ((w.Z * u.Z) + (w.Y * u.Y)) + (u.X * w.X);
        var c3 = ((w.Z * vv.Z) + (w.Y * vv.Y)) + (vv.X * w.X);
        if (c2 <= c3)
            c2 = c3;
        if (c1 <= c2)
            c1 = c2;
        return ((1f / r) + (1f - c1)) + (1f - c1);
    }

    // Normalised as the ear score does it for the first two directions: length (z² + y²) + x².
    private static Vector3 Unit(Vector3 v)
    {
        var len = MathF.Sqrt(((v.Z * v.Z) + (v.Y * v.Y)) + (v.X * v.X));
        return Scale(v, len);
    }

    // ... and for the third: (y² + z²) + x².
    private static Vector3 UnitV(Vector3 v)
    {
        var len = MathF.Sqrt(((v.Y * v.Y) + (v.Z * v.Z)) + (v.X * v.X));
        return Scale(v, len);
    }

    private static Vector3 Scale(Vector3 v, float len)
    {
        if (len < 1e-17f || 1e+17f < len)
        {
            if (len == 0f)
                return Vector3.Zero;
            // FUN_18125d000: in double.
            double x = v.X, y = v.Y, z = v.Z;
            var l = Math.Sqrt(((y * y) + (x * x)) + (z * z));
            var d = 1.0 / l;
            return new Vector3((float)(d * x), (float)(d * y), (float)(d * z));
        }
        var inv = 1f / len;
        return new Vector3(inv * v.X, inv * v.Y, inv * v.Z);
    }

    // FUN_18136dcb0: q inside triangle abc (or within 1e-14 of an edge).
    private static bool Inside(Vector3 a, Vector3 b, Vector3 c, Vector3 q, Vector3 n)
    {
        var e1 = Edge(b, c, q, n);
        var e2 = Edge(c, a, q, n);
        var e3 = Edge(a, b, q, n);
        if (!(e1 <= -1e-07f || e2 <= -1e-07f || e3 <= -1e-07f))
            return true;
        if (!(1e-14f <= Project(q, a, b, out var t1) || t1 < 0f || 1f < t1))
            return true;
        if (!(1e-14f <= Project(q, b, c, out var t2) || t2 < 0f || 1f < t2))
            return true;
        return Project(q, c, a, out var t3) < 1e-14f && 0f <= t3 && t3 <= 1f;
    }

    private static float Edge(Vector3 from, Vector3 to, Vector3 q, Vector3 n)
    {
        float ex = to.X - from.X, ey = to.Y - from.Y, ez = to.Z - from.Z;
        float qx = q.X - from.X, qy = q.Y - from.Y, qz = q.Z - from.Z;
        return ((((ez * qx) - (ex * qz)) * n.Y) + (((ex * qy) - (ey * qx)) * n.Z)) + (((ey * qz) - (ez * qy)) * n.X);
    }
}
