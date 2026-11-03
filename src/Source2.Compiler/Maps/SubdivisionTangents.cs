using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The subdivision bake's tangent stream (ledger 38). The bake's patch evaluator (resourcecompiler 0923 1813c1cc0,
/// from the patch builders 1813b7750 and 1813b82f0) gives each face-vertex stream by its mode: 0 the corners lerped,
/// 1 the position grid, 2 the normal grid, 3 the tangent grid. The tangent stream is mode 3 (captured 2026-11-03:
/// 35,796 of atixref's 38,166 baked tangents are tangent grid outputs, the rest unbaked corners).
/// </summary>
internal static class SubdivisionTangents
{
    /// <summary>
    /// 1813bdf80: the patch's four tangents from the face loop walked from the patch's corner. 0 is the corner's own,
    /// 1 the next corner's and 2 the previous corner's (the last one walked) each added to it and normalised, 3 the sum
    /// of every corner in walk order normalised by 18013e340.
    /// </summary>
    public static Vector3[] Gather(IReadOnlyList<Vector3> loopFromCorner)
    {
        var slots = new Vector3[4];
        float sx = 0f, sy = 0f, sz = 0f;
        for (var k = 0; k < loopFromCorner.Count; k++)
        {
            var t = loopFromCorner[k];
            slots[Math.Min(k, 2)] = t;
            sx = t.X + sx;
            sy = t.Y + sy;
            sz = t.Z + sz;
        }
        var c = slots[0];
        slots[1] = Normalise(new Vector3(slots[1].X + c.X, slots[1].Y + c.Y, slots[1].Z + c.Z));
        slots[2] = Normalise(new Vector3(slots[2].X + c.X, slots[2].Y + c.Y, slots[2].Z + c.Z));
        slots[3] = Normalise(new Vector3(sx, sy, sz));
        return slots;
    }

    /// <summary>
    /// 1813c2090: per grid point (u, v) with the normal grid's n, A = |lerp(t0, t2, v)| and B = |lerp(t1, t3, v)|, the
    /// tangent T = lerp(A, B, u), then |(n x T) x n| with the corner's own w. A lerp is (b - a) t + a per component.
    /// </summary>
    public static Vector4[] Grid(Vector3[] slots, float w, IReadOnlyList<Vector3> normals, IReadOnlyList<Vector2> uv)
    {
        var result = new Vector4[normals.Count];
        var (t0, t1, t2, t3) = (slots[0], slots[1], slots[2], slots[3]);
        for (var i = 0; i < result.Length; i++)
        {
            var u = uv[i].X;
            var v = uv[i].Y;
            var a = Normalise(new Vector3((t2.X - t0.X) * v + t0.X, (t2.Y - t0.Y) * v + t0.Y, (t2.Z - t0.Z) * v + t0.Z));
            var b = Normalise(new Vector3((t3.X - t1.X) * v + t1.X, (t3.Y - t1.Y) * v + t1.Y, (t3.Z - t1.Z) * v + t1.Z));
            var tx = (b.X - a.X) * u + a.X;
            var ty = (b.Y - a.Y) * u + a.Y;
            var tz = (b.Z - a.Z) * u + a.Z;
            var n = normals[i];
            var cx = n.Y * tz - ty * n.Z;
            var cz = ty * n.X - tx * n.Y;
            var cy = tx * n.Z - n.X * tz;
            var r = Normalise(new Vector3(cy * n.Z - cz * n.Y, cz * n.X - cx * n.Z, cx * n.Y - cy * n.X));
            result[i] = new Vector4(r, w);
        }
        return result;
    }

    /// <summary>
    /// 1813be620: a patch's position grid (side g = n + 1, row-major) to its normals. Each corner takes one triangle,
    /// every other point the sum of two unit triangle normals made unit, each a cross product of differences from the
    /// point P: (0, 0) (right - P) x (diagonal - P), through 18013e340; (0, n) (below - P) x (below-left - P);
    /// (n, 0) (above - P) x (above-right - P); (n, n) (left - P) x (above-left - P); row 0 (right - P) x (below - P)
    /// and (P - left) x (below - P); column 0 (right - P) x (below - P) and (right - P) x (P - above); inside
    /// (right - P) x (below - P) and (P - left) x (P - above); column n (P - left) x (below - P) and
    /// (P - left) x (P - above); row n (right - P) x (P - above) and (P - left) x (P - above).
    /// </summary>
    public static Vector3[] PositionNormals(IReadOnlyList<Vector3> grid, int n)
    {
        var g = n + 1;
        var normals = new Vector3[g * g];
        Vector3 P(int r, int c) => grid[r * g + c];
        static Vector3 Cross(Vector3 a, Vector3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        static Vector3 Sub(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        static Vector3 Two(Vector3 a, Vector3 b)
        {
            var x = Normalise(a);
            var y = Normalise(b);
            return Normalise(new Vector3(x.X + y.X, x.Y + y.Y, x.Z + y.Z));
        }
        for (var r = 0; r <= n; r++)
            for (var c = 0; c <= n; c++)
            {
                var p = P(r, c);
                Vector3 Right() => Sub(P(r, c + 1), p);
                Vector3 Below() => Sub(P(r + 1, c), p);
                Vector3 FromLeft() => Sub(p, P(r, c - 1));
                Vector3 FromAbove() => Sub(p, P(r - 1, c));
                normals[r * g + c] = (r, c) switch
                {
                    (0, 0) => Normalise(Cross(Right(), Sub(P(1, 1), p))),
                    (0, _) when c == n => Normalise(Cross(Below(), Sub(P(1, n - 1), p))),
                    (_, 0) when r == n => Normalise(Cross(Sub(P(n - 1, 0), p), Sub(P(n - 1, 1), p))),
                    _ when r == n && c == n => Normalise(Cross(Sub(P(n, n - 1), p), Sub(P(n - 1, n - 1), p))),
                    (0, _) => Two(Cross(Right(), Below()), Cross(FromLeft(), Below())),
                    (_, 0) => Two(Cross(Right(), Below()), Cross(Right(), FromAbove())),
                    _ when r == n => Two(Cross(Right(), FromAbove()), Cross(FromLeft(), FromAbove())),
                    _ when c == n => Two(Cross(FromLeft(), Below()), Cross(FromLeft(), FromAbove())),
                    _ => Two(Cross(Right(), Below()), Cross(FromLeft(), FromAbove())),
                };
            }
        return normals;
    }

    // mathlib's VectorNormalize as these functions inline it (and 18013e340): the squared length (z z + y y) + x x,
    // times the reciprocal of its root within 1e-17 .. 1e17, zero at zero, else VectorNormalize_Slow (18125d000).
    internal static Vector3 Normalise(Vector3 d)
    {
        var len = MathF.Sqrt(d.Z * d.Z + d.Y * d.Y + d.X * d.X);
        if (len >= 1e-17f && len <= 1e17f)
        {
            var inv = 1f / len;
            return new Vector3(d.X * inv, d.Y * inv, d.Z * inv);
        }
        return len == 0f ? Vector3.Zero : VectorNormalizeSlow.Normalise(d);
    }
}
