using System.Runtime.CompilerServices;

namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// navsystem's GJK distance between two point clouds (20261015, 1800eff20 through 1800f09d0 with an
/// empty cache), as IslandRemoval calls it through 1800a3060: both shapes with the identity transform
/// (180139ed0) and scale 1, so every support point is the input point itself. The simplex keeps each
/// vertex's indices, its point on A and on B and w = B - A (0x2c bytes); 1800f1200 starts it from
/// both first points, 1800f0a20 picks the search direction, 1800f0c80 the support, 1800f14b0 and
/// 1800f1b80 solve three and four vertices (barycentrics 1800ef850 and 1800ef460), 1800efb20 gives
/// the witness points. Float order is the disassembly's (tools/re/symsse.py: every sum left-nested).
/// vphysics2's GJK (1802ec220, through CVPhysics2Interface slot 0x190) is the same algorithm and layout
/// with every three-term dot product ending on z where navsystem's ends on x (edges, lengths, triangle
/// barycentrics, |v|^2, |d|^2, the face direction's sign test, the final distance), and no forced zero
/// when the direction vanishes; the support's dot and the weighted sums are the same in both.
/// </summary>
public static class NavGjk
{
    private interface IOrder
    {
        /// <summary>A dot product's three per-axis terms summed in the build's order.</summary>
        static abstract float Dot(float x, float y, float z);

        /// <summary>Whether a vanishing search direction counts as overlap (distance 0).</summary>
        static abstract bool ZeroOnTinyDirection { get; }
    }

    private readonly struct NavOrder : IOrder
    {
        public static float Dot(float x, float y, float z) => y + z + x;
        public static bool ZeroOnTinyDirection => true;
    }

    private readonly struct PhysicsOrder : IOrder
    {
        public static float Dot(float x, float y, float z) => x + y + z;
        public static bool ZeroOnTinyDirection => false;
    }

    private struct Vertex
    {
        public int IA, IB;
        public float Ax, Ay, Az, Bx, By, Bz, Wx, Wy, Wz;
    }

    [InlineArray(4)]
    private struct Vertices
    {
        private Vertex _e;
    }

    [InlineArray(4)]
    private struct Weights
    {
        private float _e;
    }

    private struct Simplex
    {
        public int Count;
        public Vertices V;
        public Weights L;
    }

    /// <summary>navsystem's distance between the hulls of a and b (0 when they overlap), at most 32 iterations.</summary>
    public static float Distance(ReadOnlySpan<(float X, float Y, float Z)> a, ReadOnlySpan<(float X, float Y, float Z)> b, int maxIterations = 32)
        => Run<NavOrder>(a, b, maxIterations);

    /// <summary>
    /// vphysics2's distance (18029b6f0: point lists, scale 1, 32 iterations) as CVPhysics2Interface slot
    /// 0x190 returns it with identity matrices: 0 when at most 0.125.
    /// </summary>
    public static float PhysicsDistance(ReadOnlySpan<(float X, float Y, float Z)> a, ReadOnlySpan<(float X, float Y, float Z)> b)
    {
        var d = Run<PhysicsOrder>(a, b, 32);
        return 0.125f >= d ? 0f : d;
    }

    private static float Run<T>(ReadOnlySpan<(float X, float Y, float Z)> a, ReadOnlySpan<(float X, float Y, float Z)> b, int maxIterations)
        where T : struct, IOrder
    {
        var s = new Simplex { Count = 1 };
        s.V[0] = Make(a, b, 0, 0);
        s.L[0] = 0f;
        var backup = new Simplex();
        Span<byte> saveA = stackalloc byte[4], saveB = stackalloc byte[4];
        var prev = float.MaxValue;
        var iter = 0;
        var overlap = false;
        while (true)
        {
            var saveCount = s.Count;
            for (var i = 0; i < saveCount; i++)
            {
                saveA[i] = (byte)s.V[i].IA;
                saveB[i] = (byte)s.V[i].IB;
            }
            var ok = true;
            switch (s.Count)
            {
                case 1:
                    s.L[0] = 1f;
                    break;
                case 2:
                    ok = Solve2<T>(ref s);
                    break;
                case 3:
                    ok = Solve3<T>(ref s);
                    break;
                case 4:
                    ok = Solve4<T>(ref s);
                    break;
            }
            if (!ok)
            {
                s = backup;
                break;
            }
            if (s.Count == 4)
            {
                overlap = true;
                break;
            }
            float vx, vy, vz;
            ref var v0 = ref s.V[0];
            if (s.Count == 1)
            {
                vx = v0.Wx * s.L[0];
                vy = v0.Wy * s.L[0];
                vz = v0.Wz * s.L[0];
            }
            else if (s.Count == 2)
            {
                ref var v1 = ref s.V[1];
                vx = v1.Wx * s.L[1] + v0.Wx * s.L[0];
                vy = v1.Wy * s.L[1] + v0.Wy * s.L[0];
                vz = v1.Wz * s.L[1] + v0.Wz * s.L[0];
            }
            else
            {
                ref var v1 = ref s.V[1];
                ref var v2 = ref s.V[2];
                vx = v1.Wx * s.L[1] + v0.Wx * s.L[0] + v2.Wx * s.L[2];
                vy = v1.Wy * s.L[1] + v0.Wy * s.L[0] + v2.Wy * s.L[2];
                vz = v1.Wz * s.L[1] + v0.Wz * s.L[0] + v2.Wz * s.L[2];
            }
            var distSq = T.Dot(vx * vx, vy * vy, vz * vz);
            if (prev <= distSq)
            {
                s = backup;
                break;
            }
            var (dx, dy, dz) = SearchDirection<T>(ref s);
            if (T.Dot(dx * dx, dy * dy, dz * dz) < 1.17549435e-35f)
            {
                overlap = T.ZeroOnTinyDirection;
                break;
            }
            var ia = Support(a, -dx, -dy, -dz);
            var ib = Support(b, dx, dy, dz);
            backup = s;
            var duplicate = false;
            for (var i = 0; i < saveCount; i++)
                if (saveA[i] == ia && saveB[i] == ib)
                {
                    duplicate = true;
                    break;
                }
            if (duplicate)
                break;
            iter++;
            s.V[s.Count] = Make(a, b, ia, ib);
            s.Count++;
            prev = distSq;
            if (iter >= maxIterations)
                break;
        }
        var (pa, pb) = Witness(ref s);
        if (overlap)
            pb = pa;
        var ex = pa.X - pb.X;
        var ey = pa.Y - pb.Y;
        var ez = pa.Z - pb.Z;
        return MathF.Sqrt(T.Dot(ex * ex, ey * ey, ez * ez));
    }

    private static Vertex Make(ReadOnlySpan<(float X, float Y, float Z)> a, ReadOnlySpan<(float X, float Y, float Z)> b, int ia, int ib)
    {
        var p = a[ia];
        var q = b[ib];
        return new Vertex
        {
            IA = ia, IB = ib,
            Ax = p.X, Ay = p.Y, Az = p.Z,
            Bx = q.X, By = q.Y, Bz = q.Z,
            Wx = q.X - p.X, Wy = q.Y - p.Y, Wz = q.Z - p.Z,
        };
    }

    // 1800f0c80: the point farthest along d measured from the first (ties keep the earlier).
    private static int Support(ReadOnlySpan<(float X, float Y, float Z)> points, float dx, float dy, float dz)
    {
        if (points.Length < 2)
            return 0;
        var p0 = points[0];
        var best = 0f;
        var index = 0;
        for (var i = 1; i < points.Length; i++)
        {
            var p = points[i];
            var dot = (p.Y - p0.Y) * dy + (p.Z - p0.Z) * dz + (p.X - p0.X) * dx;
            if (best < dot)
            {
                best = dot;
                index = i;
            }
        }
        return index;
    }

    // 1800f0a20: towards the origin from the point, the segment or the triangle.
    private static (float X, float Y, float Z) SearchDirection<T>(ref Simplex s) where T : struct, IOrder
    {
        ref var v0 = ref s.V[0];
        switch (s.Count)
        {
            case 1:
                return (-v0.Wx, -v0.Wy, -v0.Wz);
            case 2:
            {
                ref var v1 = ref s.V[1];
                var ex = v1.Wx - v0.Wx;
                var ez = v1.Wz - v0.Wz;
                var ey = v1.Wy - v0.Wy;
                var tx = ey * -v0.Wz - -v0.Wy * ez;
                var tz = -v0.Wy * ex - -v0.Wx * ey;
                var ty = -v0.Wx * ez - ex * -v0.Wz;
                return (ty * ez - tz * ey, ex * tz - tx * ez, tx * ey - ty * ex);
            }
            case 3:
            {
                ref var v1 = ref s.V[1];
                ref var v2 = ref s.V[2];
                var e2x = v2.Wx - v0.Wx;
                var e1x = v1.Wx - v0.Wx;
                var e1y = v1.Wy - v0.Wy;
                var e2y = v2.Wy - v0.Wy;
                var ny = e2x * (v1.Wz - v0.Wz) - (v2.Wz - v0.Wz) * e1x;
                var nx = (v2.Wz - v0.Wz) * e1y - e2y * (v1.Wz - v0.Wz);
                var nz = e2y * e1x - e2x * e1y;
                if (0f <= T.Dot(nx * v0.Wx, v0.Wy * ny, v0.Wz * nz))
                    return (-nx, -ny, -nz);
                return (nx, ny, nz);
            }
            default:
                return (0f, 0f, 0f);
        }
    }

    // The inline two-vertex solve in 1800eff20.
    private static bool Solve2<T>(ref Simplex s) where T : struct, IOrder
    {
        var (u, v, len) = Edge<T>(s.V[0], s.V[1]);
        if (!(0f < v))
        {
            s.Count = 1;
            s.L[0] = 1f;
            return true;
        }
        if (!(0f < u))
        {
            s.V[0] = s.V[1];
            s.Count = 1;
            s.L[0] = 1f;
            return true;
        }
        if (len <= 0f)
            return false;
        s.L[0] = u / len;
        s.L[1] = v / len;
        return true;
    }

    // The edge terms every solve shares: u (dot(b, b - a)), v (-dot(a, b - a)) and |b - a|^2.
    private static (float U, float V, float Len) Edge<T>(in Vertex a, in Vertex b) where T : struct, IOrder
    {
        var ex = b.Wx - a.Wx;
        var ey = b.Wy - a.Wy;
        var ez = b.Wz - a.Wz;
        var u = T.Dot((b.Wx - 0f) * ex, (b.Wy - 0f) * ey, (b.Wz - 0f) * ez);
        var v = -T.Dot((a.Wx - 0f) * ex, (a.Wy - 0f) * ey, (a.Wz - 0f) * ez);
        return (u, v, T.Dot(ex * ex, ey * ey, ez * ez));
    }

    // 1800f14b0 (vphysics2 180316df0).
    private static bool Solve3<T>(ref Simplex s) where T : struct, IOrder
    {
        var w0 = s.V[0];
        var w1 = s.V[1];
        var w2 = s.V[2];
        var (u01, v01, len01) = Edge<T>(w0, w1);
        var (u12, v12, len12) = Edge<T>(w1, w2);
        var (u20, v20, len20) = Edge<T>(w2, w0);
        if (!(0f < v01) && !(0f < u20))
        {
            s.Count = 1;
            s.L[0] = 1f;
            return true;
        }
        if (!(0f < v12) && !(0f < u01))
        {
            s.Count = 1;
            s.V[0] = w1;
            s.L[0] = 1f;
            return true;
        }
        if (!(0f < v20) && !(0f < u12))
        {
            s.Count = 1;
            s.V[0] = w2;
            s.L[0] = 1f;
            return true;
        }
        var (t0, t1, t2, t3) = Triangle<T>(w0, w1, w2);
        if (!(0f < t2) && !(u01 <= 0f) && !(v01 <= 0f))
            return Pair(ref s, w0, w1, u01, v01, len01);
        if (!(0f < t0) && !(u12 <= 0f) && !(v12 <= 0f))
            return Pair(ref s, w1, w2, u12, v12, len12);
        if (!(0f < t1) && !(u20 <= 0f) && !(v20 <= 0f))
            return Pair(ref s, w2, w0, u20, v20, len20);
        if (0f < t3)
        {
            s.L[0] = t0 / t3;
            s.L[1] = t1 / t3;
            s.L[2] = t2 / t3;
            return true;
        }
        return false;
    }

    private static bool Pair(ref Simplex s, in Vertex a, in Vertex b, float u, float v, float len)
    {
        s.Count = 2;
        s.V[0] = a;
        s.V[1] = b;
        if (!(0f < len))
            return false;
        s.L[0] = u / len;
        s.L[1] = v / len;
        return true;
    }

    private static bool Face(ref Simplex s, in Vertex a, in Vertex b, in Vertex c, (float A, float B, float C, float D) t)
    {
        s.Count = 3;
        s.V[0] = a;
        s.V[1] = b;
        s.V[2] = c;
        if (!(0f < t.D))
            return false;
        s.L[0] = t.A / t.D;
        s.L[1] = t.B / t.D;
        s.L[2] = t.C / t.D;
        return true;
    }

    // 1800f1b80 (vphysics2 1803174c0).
    private static bool Solve4<T>(ref Simplex s) where T : struct, IOrder
    {
        var w0 = s.V[0];
        var w1 = s.V[1];
        var w2 = s.V[2];
        var w3 = s.V[3];
        var (u01, v01, len01) = Edge<T>(w0, w1);
        var (u02, v02, len02) = Edge<T>(w0, w2);
        var (u03, v03, len03) = Edge<T>(w0, w3);
        var (u12, v12, len12) = Edge<T>(w1, w2);
        var (u23, v23, len23) = Edge<T>(w2, w3);
        var (u31, v31, len31) = Edge<T>(w3, w1);
        if (v01 <= 0f && v02 <= 0f && v03 <= 0f)
            return Single(ref s, w0);
        if (u01 <= 0f && u31 <= 0f && v12 <= 0f)
            return Single(ref s, w1);
        if (u02 <= 0f && u12 <= 0f && v23 <= 0f)
            return Single(ref s, w2);
        if (u03 <= 0f && u23 <= 0f && v31 <= 0f)
            return Single(ref s, w3);
        var ta = Triangle<T>(w0, w2, w1);
        var tb = Triangle<T>(w0, w1, w3);
        var tc = Triangle<T>(w0, w3, w2);
        var td = Triangle<T>(w1, w2, w3);
        if (!(0f < tb.C) && !(0f < ta.B) && !(u01 <= 0f) && !(v01 <= 0f))
            return Pair(ref s, w0, w1, u01, v01, len01);
        if (ta.C <= 0f && tc.B <= 0f && 0f < u02 && 0f < v02)
            return Pair(ref s, w0, w2, u02, v02, len02);
        if (tc.C <= 0f && tb.B <= 0f && 0f < u03 && 0f < v03)
            return Pair(ref s, w0, w3, u03, v03, len03);
        if (!(0f < ta.A) && !(0f < td.C) && !(u12 <= 0f) && !(v12 <= 0f))
            return Pair(ref s, w1, w2, u12, v12, len12);
        if (!(0f < tc.A) && !(0f < td.A) && !(u23 <= 0f) && !(v23 <= 0f))
            return Pair(ref s, w2, w3, u23, v23, len23);
        if (!(0f < tb.A) && !(0f < td.B) && !(u31 <= 0f) && !(v31 <= 0f))
            return Pair(ref s, w3, w1, u31, v31, len31);
        var (q0, q1, q2, q3, q4) = Tetrahedron(w0, w1, w2, w3);
        if (!(0f <= q3) && !(ta.A <= 0f) && !(ta.B <= 0f) && !(ta.C <= 0f))
            return Face(ref s, w0, w2, w1, ta);
        if (!(0f <= q2) && !(tb.A <= 0f) && !(tb.B <= 0f) && !(tb.C <= 0f))
            return Face(ref s, w0, w1, w3, tb);
        if (!(0f <= q1) && !(tc.A <= 0f) && !(tc.B <= 0f) && !(tc.C <= 0f))
            return Face(ref s, w0, w3, w2, tc);
        if (!(0f <= q0) && !(td.A <= 0f) && !(td.B <= 0f) && !(td.C <= 0f))
            return Face(ref s, w1, w2, w3, td);
        if (0f < q4)
        {
            s.L[0] = q0 / q4;
            s.L[1] = q1 / q4;
            s.L[2] = q2 / q4;
            s.L[3] = q3 / q4;
            return true;
        }
        return false;
    }

    private static bool Single(ref Simplex s, in Vertex a)
    {
        s.Count = 1;
        s.V[0] = a;
        s.L[0] = 1f;
        return true;
    }

    // 1800ef850 (vphysics2 180315fa0) about the origin: the signed areas opposite a, b and c, and the
    // squared normal.
    private static (float A, float B, float C, float D) Triangle<T>(in Vertex a, in Vertex b, in Vertex c) where T : struct, IOrder
    {
        var f1 = a.Wx - 0f;
        var f2 = a.Wz - 0f;
        var f3 = a.Wy - 0f;
        var f5 = b.Wz - a.Wz;
        var f13 = b.Wz - 0f;
        var f11 = c.Wx - 0f;
        var f16 = b.Wy - 0f;
        var f14 = b.Wx - 0f;
        var f4 = c.Wz - a.Wz;
        var f10 = c.Wz - 0f;
        var f6 = c.Wx - a.Wx;
        var f12 = c.Wy - 0f;
        var f8 = b.Wx - a.Wx;
        var f7 = b.Wy - a.Wy;
        var f15 = c.Wy - a.Wy;
        var ny = f6 * f5 - f4 * f8;
        var nx = f4 * f7 - f15 * f5;
        var nz = f15 * f8 - f6 * f7;
        return (T.Dot((f10 * f16 - f12 * f13) * nx, (f11 * f13 - f10 * f14) * ny, (f12 * f14 - f11 * f16) * nz),
                T.Dot((f2 * f12 - f3 * f10) * nx, (f1 * f10 - f2 * f11) * ny, (f3 * f11 - f1 * f12) * nz),
                T.Dot((f3 * f13 - f2 * f16) * nx, (f2 * f14 - f1 * f13) * ny, (f1 * f16 - f3 * f14) * nz),
                T.Dot(nx * nx, ny * ny, nz * nz));
    }

    // 1800ef460 (vphysics2 180315bb0, the same) about the origin: the signed volumes opposite each corner, made positive with the
    // whole volume's sign, and that volume.
    private static (float A, float B, float C, float D, float V) Tetrahedron(in Vertex a, in Vertex b, in Vertex c, in Vertex d)
    {
        var f1 = a.Wx - 0f;
        var f5 = a.Wz - 0f;
        var f6 = a.Wy - 0f;
        var f19 = b.Wz - 0f;
        var f11 = d.Wz - a.Wz;
        var f17 = c.Wz - 0f;
        var f18 = d.Wz - 0f;
        var f15 = b.Wz - a.Wz;
        var f13 = c.Wz - a.Wz;
        var f2 = b.Wx - 0f;
        var f7 = b.Wy - 0f;
        var f3 = c.Wx - 0f;
        var f8 = c.Wy - 0f;
        var f4 = d.Wx - 0f;
        var f9 = d.Wy - 0f;
        var f16 = b.Wy - a.Wy;
        var f14 = c.Wy - a.Wy;
        var f12 = d.Wy - a.Wy;
        var vol = (f11 * f14 - f12 * f13) * (b.Wx - a.Wx) - (f11 * f16 - f12 * f15) * (c.Wx - a.Wx) + (f13 * f16 - f14 * f15) * (d.Wx - a.Wx);
        var sign = vol < 0f ? -1f : 1f;
        var g14 = f18 * f6 - f9 * f5;
        var g13 = f17 * f6 - f8 * f5;
        var g11 = f18 * f7 - f9 * f19;
        var g5 = f19 * f6 - f7 * f5;
        return (((f18 * f8 - f9 * f17) * f2 - g11 * f3 + (f17 * f7 - f8 * f19) * f4) * sign,
                ((f9 * f17 - f18 * f8) * f1 - f4 * g13 + f3 * g14) * sign,
                (f1 * g11 - f2 * g14 + f4 * g5) * sign,
                ((f8 * f19 - f17 * f7) * f1 - f3 * g5 + f2 * g13) * sign,
                sign * vol);
    }

    // 1800efb20: the closest points on A and on B (the same point with four vertices).
    private static ((float X, float Y, float Z) A, (float X, float Y, float Z) B) Witness(ref Simplex s)
    {
        ref var v0 = ref s.V[0];
        var l0 = s.L[0];
        switch (s.Count)
        {
            case 1:
                return ((l0 * v0.Ax, v0.Ay * l0, v0.Az * l0), (l0 * v0.Bx, v0.By * l0, v0.Bz * l0));
            case 2:
            {
                ref var v1 = ref s.V[1];
                var l1 = s.L[1];
                return ((l1 * v1.Ax + l0 * v0.Ax, v1.Ay * l1 + v0.Ay * l0, v1.Az * l1 + v0.Az * l0),
                        (v1.Bx * l1 + l0 * v0.Bx, v1.By * l1 + v0.By * l0, v1.Bz * l1 + v0.Bz * l0));
            }
            case 3:
            {
                ref var v1 = ref s.V[1];
                ref var v2 = ref s.V[2];
                var l1 = s.L[1];
                var l2 = s.L[2];
                return ((l1 * v1.Ax + l0 * v0.Ax + l2 * v2.Ax, v1.Ay * l1 + v0.Ay * l0 + v2.Ay * l2, v1.Az * l1 + v0.Az * l0 + v2.Az * l2),
                        (l1 * v1.Bx + l0 * v0.Bx + l2 * v2.Bx, v1.By * l1 + v0.By * l0 + v2.By * l2, v1.Bz * l1 + v0.Bz * l0 + v2.Bz * l2));
            }
            case 4:
            {
                ref var v1 = ref s.V[1];
                ref var v2 = ref s.V[2];
                ref var v3 = ref s.V[3];
                var l1 = s.L[1];
                var l2 = s.L[2];
                var l3 = s.L[3];
                var p = (v1.Ax * l1 + l0 * v0.Ax + l2 * v2.Ax + l3 * v3.Ax,
                         v1.Ay * l1 + v0.Ay * l0 + v2.Ay * l2 + v3.Ay * l3,
                         v1.Az * l1 + v0.Az * l0 + v2.Az * l2 + v3.Az * l3);
                return (p, p);
            }
            default:
                return ((0f, 0f, 0f), (0f, 0f, 0f));
        }
    }
}
