using System.Numerics;

namespace Source2.Compiler.Simulation;

/// <summary>
/// vphysics2's box cast against a convex hull (the hull shape's vfn 0x40, 180250540 -> 180251800): a
/// GJK ray cast of the box swept along the ray against the hull (1802e8060 / 1802eacc0), its time of
/// impact (1802e7120) and the contact depth that turns into the fraction (1802e5ab0 and the edge,
/// face and vertex refinements under it). Transcribed function by function from vphysics2 20260924
/// (code identical in 20261015); every float sum keeps Valve's association, since a + b + c in the
/// decompile is (a + b) + c. Function addresses are on each method.
/// </summary>
public static class HullCast
{
    private const float Tiny = 1.17549435e-35f;
    private const float Eps8 = 1e-08f;
    private const float FltEpsilon = 1.1920929e-07f;
    private const float Far = 1e+24f;

    private struct V(float x, float y, float z)
    {
        public float X = x, Y = y, Z = z;
        public static V operator -(V a) => new(-a.X, -a.Y, -a.Z);
        public readonly bool SameAs(V o) => X == o.X && Y == o.Y && Z == o.Z;
    }

    /// <summary>The ray the box sweeps along (the 0x40-byte struct 1802e2c30 builds).</summary>
    private sealed class Ray
    {
        public V Start, End, Delta, Dir;
        public float Len, Len0, Inv;
    }

    /// <summary>The cast state (1802e2ce0): +0 v, +0xc normal, +0x28 box, +0x30 hull, +0x38 ray.</summary>
    private sealed class Gjk
    {
        public V V0;
        public V Normal;
        public float Dist, Frac = 1f, Margin, Tol = 0.03125f;
        public bool StartSolid, Shrunk, Sep;
        public Ray Ray = null!;
        public V Half;
        public bool Point;
        public V[] Points = null!;

        /// <summary>B is a capsule's segment (its two centres): its support takes the second on a tie (18006d070).</summary>
        public bool Segment;
    }

    /// <summary>The simplex: up to four points and a count (+0x30).</summary>
    private sealed class Smp
    {
        public readonly float[] S = new float[12];
        public int N;
        public V P(int i) => new(S[i * 3], S[i * 3 + 1], S[i * 3 + 2]);
        public void Set(int i, V v) { S[i * 3] = v.X; S[i * 3 + 1] = v.Y; S[i * 3 + 2] = v.Z; }
    }

    /// <summary>What the hull cast reports: whether it hit and the fraction (180251800's out[0]).</summary>
    public readonly record struct Hit(bool Found, float Fraction, bool StartSolid);

    /// <summary>
    /// 180251800: the box (<paramref name="half"/> extents) cast from <paramref name="start"/> by
    /// <paramref name="delta"/> against the hull with reference point <paramref name="reference"/> and
    /// <paramref name="points"/> at <paramref name="scale"/>, placed by <paramref name="t"/> (3x3 rows
    /// then the translation). The hull is moved to the frame of its transformed reference point.
    /// </summary>
    public static Hit Cast(Vector3 reference, Vector3[] points, float scale, float[] t, Vector3 start, Vector3 delta, Vector3 half)
    {
        if (points.Length <= 0)
            return default;
        float rx = reference.X, ry = reference.Y, rz = reference.Z;
        var wx = rx * t[0] + ry * t[3] + rz * t[6] + t[9];
        var wy = rx * t[1] + ry * t[4] + rz * t[7] + t[10];
        var wz = rx * t[2] + ry * t[5] + rz * t[8] + t[11];
        var pts = new V[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            float px = points[i].X * scale, py = points[i].Y * scale, pz = points[i].Z * scale;
            pts[i] = new V(t[3] * py + t[0] * px + t[6] * pz + (t[9] - wx),
                           t[4] * py + t[1] * px + t[7] * pz + (t[10] - wy),
                           t[5] * py + t[2] * px + t[8] * pz + (t[11] - wz));
        }
        var rel = new V(start.X - wx, start.Y - wy, start.Z - wz);
        var ray = new Ray { Start = rel, Delta = new V(delta.X, delta.Y, delta.Z), Dir = new V(delta.X, delta.Y, delta.Z) };
        ray.End = new V(delta.X + rel.X, delta.Y + rel.Y, delta.Z + rel.Z);
        var len = Normalize(ref ray.Dir);
        ray.Len = ray.Len0 = len;
        ray.Inv = 0f < len ? 1f / len : 0f;
        var g = new Gjk { V0 = rel, Ray = ray, Half = new V(half.X, half.Y, half.Z), Points = pts };
        if (!CastRay(g))
            return default;
        return new Hit(true, g.Frac, g.StartSolid);
    }

    /// <summary>
    /// 180253ac0 at scale 1 (the hull shape's vfn 0x20, 180250b20, for a hull with no region SVM): a
    /// sphere of <paramref name="radius"/> cast from <paramref name="start"/> by <paramref name="delta"/>
    /// in the hull's frame. The ray (1802e2c30) is cut by 1802e6e60 to min(1 / length * 0.3125 +
    /// <paramref name="limit"/>, 1) of the delta (its length and end; the delta stays), then the GJK cast
    /// (1802e2ce0, 1802e7fc0 -> 1802e8060) runs on the point (0, 0, 0) with the radius as the margin
    /// against the hull's points as stored.
    /// </summary>
    public static Hit CastSphere(Vector3[] points, Vector3 start, Vector3 delta, float radius, float limit)
    {
        var pts = new V[points.Length];
        for (var i = 0; i < points.Length; i++)
            pts[i] = new V(points[i].X, points[i].Y, points[i].Z);
        var s = new V(start.X, start.Y, start.Z);
        var ray = new Ray { Start = s, Delta = new V(delta.X, delta.Y, delta.Z), Dir = new V(delta.X, delta.Y, delta.Z) };
        var len = Normalize(ref ray.Dir);
        ray.Len = ray.Len0 = len;
        ray.Inv = 0f < len ? 1f / len : 0f;
        var f = ray.Inv * 0.3125f + limit;
        f = f < 1f ? f : 1f;
        ray.Len = f * ray.Len0;
        ray.End = new V(delta.X * f + s.X, delta.Y * f + s.Y, delta.Z * f + s.Z);
        var g = new Gjk { V0 = s, Ray = ray, Point = true, Points = pts, Margin = radius };
        if (!CastRay(g))
            return default;
        return new Hit(true, g.Frac, g.StartSolid);
    }

    /// <summary>
    /// The sphere and capsule shapes' box casts (vfn 0x40: CRnSphereShape 18024bc00, CRnCapsuleShape
    /// 18024e6b0): the ray (1802e2c30) cut by 1802e6e60 to min(1 / length * 0.3125 + <paramref name="limit"/>, 1)
    /// of the delta, then the GJK cast (1802e2ce0, 1802e7fc0) of the box (its half extents passed negated,
    /// as the hull cast passes them) against the sphere's centre (18006d110) or the capsule's segment
    /// (18006d070), placed in the world, the radius as the margin, starting from the ray's start.
    /// <paramref name="centres"/> are the world centres: one for a sphere, two for a capsule.
    /// </summary>
    public static Hit CastBoxRound(Vector3[] centres, float radius, Vector3 start, Vector3 delta, Vector3 half, float limit)
    {
        var pts = new V[centres.Length];
        for (var i = 0; i < centres.Length; i++)
            pts[i] = new V(centres[i].X, centres[i].Y, centres[i].Z);
        var s = new V(start.X, start.Y, start.Z);
        var ray = new Ray { Start = s, Delta = new V(delta.X, delta.Y, delta.Z), Dir = new V(delta.X, delta.Y, delta.Z) };
        var len = Normalize(ref ray.Dir);
        ray.Len = ray.Len0 = len;
        ray.Inv = 0f < len ? 1f / len : 0f;
        var f = ray.Inv * 0.3125f + limit;
        f = f < 1f ? f : 1f;
        ray.Len = f * ray.Len0;
        ray.End = new V(delta.X * f + s.X, delta.Y * f + s.Y, delta.Z * f + s.Z);
        var g = new Gjk { V0 = s, Ray = ray, Half = new V(half.X, half.Y, half.Z), Points = pts, Segment = pts.Length == 2, Margin = radius };
        if (!CastRay(g))
            return default;
        return new Hit(true, g.Frac, g.StartSolid);
    }

    // ---- supports ----

    /// <summary>1802ea210: the box's corner by the sign bit of each component; 18006d110 for a sphere's point: its centre, (0, 0, 0).</summary>
    private static V SupA(Gjk g, V d) => g.Point ? default : new(BitConverter.SingleToInt32Bits(d.X) < 0 ? -g.Half.X : g.Half.X,
                                             BitConverter.SingleToInt32Bits(d.Y) < 0 ? -g.Half.Y : g.Half.Y,
                                             BitConverter.SingleToInt32Bits(d.Z) < 0 ? -g.Half.Z : g.Half.Z);

    /// <summary>1802eaa40: the first hull point with the greatest d.z p.z + p.y d.y + d.x p.x.</summary>
    private static V SupB(Gjk g, V d)
    {
        var pts = g.Points;
        if (g.Segment)
        {
            // 18006d070: the second centre unless the first is strictly farther along d.
            var a = pts[0];
            var b = pts[1];
            return (d.Z * a.Z + a.Y * d.Y) + d.X * a.X <= (d.Z * b.Z + b.Y * d.Y) + d.X * b.X ? b : a;
        }
        var best = 0;
        var bd = d.Z * pts[0].Z + pts[0].Y * d.Y + d.X * pts[0].X;
        for (var i = 1; i < pts.Length; i++)
        {
            var v = d.Z * pts[i].Z + pts[i].Y * d.Y + d.X * pts[i].X;
            if (bd < v)
            {
                bd = v;
                best = i;
            }
        }
        return pts[best];
    }

    /// <summary>The ray segment's end along d: the start when delta . d is not above 0.</summary>
    private static V SupSeg(Gjk g, V d)
    {
        var r = g.Ray;
        return r.Delta.Z * d.Z + r.Delta.Y * d.Y + r.Delta.X * d.X <= 0f ? r.Start : r.End;
    }

    /// <summary>The swept box's support minus the hull's: (box(d) + segment(d)) - hull(-d).</summary>
    private static V W(Gjk g, V d)
    {
        var s = SupSeg(g, d);
        var a = SupA(g, d);
        var b = SupB(g, -d);
        return new V(a.X + s.X - b.X, a.Y + s.Y - b.Y, a.Z + s.Z - b.Z);
    }

    // ---- normalisation ----

    /// <summary>18008d4e0: normalise in double; returns the length (at most FLT_MAX).</summary>
    private static float NormalizeDouble(ref V v)
    {
        double y = v.Y, x = v.X, z = v.Z;
        var l = Math.Sqrt(y * y + x * x + z * z);
        var inv = 1.0 / l;
        if (3.4028234663852886e+38 <= l)
            l = 3.4028234663852886e+38;
        v = new V((float)(inv * x), (float)(inv * y), (float)(inv * z));
        return (float)l;
    }

    /// <summary>180088f50: normalise in place (length (y^2 + z^2) + x^2); returns the length.</summary>
    private static float Normalize(ref V v)
    {
        var l = MathF.Sqrt(v.Y * v.Y + v.Z * v.Z + v.X * v.X);
        if (1e-17f <= l && l <= 1e+17f)
        {
            var inv = 1f / l;
            v = new V(inv * v.X, inv * v.Y, inv * v.Z);
            return l;
        }
        if (l == 0f)
        {
            v = default;
            return 0f;
        }
        return NormalizeDouble(ref v);
    }

    /// <summary>The inlined form of 180088f50 with the squared length summed by the caller.</summary>
    private static V Norm17(V v, float lenSq)
    {
        var l = MathF.Sqrt(lenSq);
        if (l < 1e-17f || 1e+17f < l)
        {
            if (l != 0f)
                NormalizeDouble(ref v);
            else
                v = default;
            return v;
        }
        var inv = 1f / l;
        return new V(v.X * inv, v.Y * inv, v.Z * inv);
    }

    /// <summary>The inlined "zero below 1.17549435e-35" normalisation.</summary>
    private static V NormTiny(V v, float lenSq)
    {
        if (lenSq <= Tiny)
            return default;
        var inv = 1f / MathF.Sqrt(lenSq);
        return new V(v.X * inv, v.Y * inv, v.Z * inv);
    }

    private static float Dzyx(V a, V b) => a.Z * b.Z + a.Y * b.Y + a.X * b.X;

    private static bool Dup(Smp s, V w)
    {
        for (var i = 0; i < s.N; i++)
        {
            float dx = s.S[i * 3] - w.X, dy = s.S[i * 3 + 1] - w.Y, dz = s.S[i * 3 + 2] - w.Z;
            if (dy * dy + dx * dx + dz * dz <= FltEpsilon)
                return true;
        }
        return false;
    }

    // ---- the cast ----

    /// <summary>1802e8060: the cast; rays longer than 512 are first clipped to the hull's slab along the ray.</summary>
    private static bool CastRay(Gjk g)
    {
        var ray = g.Ray;
        if (ray.Len0 <= 512f)
        {
            if (!Run(g))
                return false;
            g.Frac = ray.Inv * g.Dist;
            FixNormal(g);
            return true;
        }
        var n = ray.Dir;
        var l = MathF.Sqrt(n.Z * n.Z + n.Y * n.Y + n.X * n.X);
        if (l < 1e-17f || 1e+17f < l)
        {
            if (l != 0f)
                NormalizeDouble(ref n);
            else
                n = default;
        }
        else
        {
            var inv = 1f / l;
            n = new V(n.X * inv, n.Y * inv, n.Z * inv);
        }
        var aBack = SupA(g, -n);
        var s0 = ray.Start;
        var aFwd = SupA(g, n);
        var bBack = SupB(g, -n);
        var bFwd = SupB(g, n);
        var m8 = g.Tol * 8f + g.Margin;
        var t0 = ((bBack.Y - (aFwd.Y + s0.Y)) * n.Y + (bBack.Z - (aFwd.Z + s0.Z)) * n.Z + (bBack.X - (s0.X + aFwd.X)) * n.X - m8) * ray.Inv;
        var t1 = ((bFwd.Y - (aBack.Y + s0.Y)) * n.Y + (bFwd.Z - (s0.Z + aBack.Z)) * n.Z + (bFwd.X - (s0.X + aBack.X)) * n.X + m8) * ray.Inv;
        if (!(t0 < 1f || t0 == 1f))
            return false;
        var c0 = 0f <= t0 ? (t0 <= 1f ? t0 : 1f) : 0f;
        var c1 = 0f <= t1 ? (t1 <= 1f ? t1 : 1f) : 0f;
        var d = ray.Delta;
        var st = new V(c0 * d.X + s0.X, d.Y * c0 + s0.Y, d.Z * c0 + s0.Z);
        var dl = new V(d.X * c1 + s0.X - st.X, d.Y * c1 + s0.Y - st.Y, d.Z * c1 + s0.Z - st.Z);
        var sub = new Ray { Start = st, Delta = dl, End = new V(dl.X + st.X, st.Y + dl.Y, st.Z + dl.Z), Dir = dl };
        var sl = Normalize(ref sub.Dir);
        sub.Len = sub.Len0 = sl;
        sub.Inv = 0f < sl ? 1f / sl : 0f;
        var h = new Gjk { V0 = g.V0, Ray = sub, Half = g.Half, Point = g.Point, Points = g.Points, Margin = g.Margin, Segment = g.Segment };
        if (!Run(h))
            return false;
        g.Frac = sub.Inv * h.Dist * (c1 - c0) + c0;
        g.Normal = h.Normal;
        g.StartSolid = h.StartSolid;
        if (h.StartSolid)
            g.Frac = 0f;
        FixNormal(g);
        return true;
    }

    private static void FixNormal(Gjk g)
    {
        if (!g.StartSolid || !(g.Normal.X * g.Normal.X + g.Normal.Y * g.Normal.Y + g.Normal.Z * g.Normal.Z < 0.001f))
            return;
        g.Normal = -g.Ray.Dir;
        if (g.Normal.X * g.Normal.X + g.Normal.Y * g.Normal.Y + g.Normal.Z * g.Normal.Z < 0.001f)
            g.Normal = new V(1f, 0f, 0f);
    }

    private static bool Solid(Gjk g)
    {
        g.StartSolid = true;
        g.Dist = 0f;
        return true;
    }

    /// <summary>1802eacc0: GJK on the swept box minus the hull.</summary>
    private static bool Run(Gjk g)
    {
        var smp = new Smp();
        var iter = 0;
        var v = g.V0;
        if (v.X * v.X + v.Y * v.Y + v.Z * v.Z < 1.1754944e-38f)
            g.V0 = v = new V(1f, 0f, 0f);
        var d = NormTiny(-v, (-v.Y) * (-v.Y) + (-v.X) * (-v.X) + (-v.Z) * (-v.Z));
        float toi;
        while (true)
        {
            var w = W(g, d);
            var dw = d.Z * w.Z + d.Y * w.Y + w.X * d.X;
            if (w.Y * w.Y + w.X * w.X + w.Z * w.Z < 1e-08f)
            {
                var rd = g.Ray.Delta;
                if (!g.Shrunk && 0f < rd.Z * d.Z + rd.Y * d.Y + rd.X * d.X)
                {
                    var r = g.Ray;
                    var nl = r.Len - g.Tol * 0.05f;
                    if (nl <= 0f)
                        nl = 0f;
                    r.Len = nl;
                    r.End = new V(nl * r.Dir.X + r.Start.X, nl * r.Dir.Y + r.Start.Y, nl * r.Dir.Z + r.Start.Z);
                    g.Shrunk = true;
                    return Run(g);
                }
                return Solid(g);
            }
            if (dw < 0f)
            {
                var sep = MathF.Abs(dw);
                if (g.Margin + g.Tol <= sep)
                    return false;
                if (g.Ray.Len <= 0f && g.Margin <= 0f)
                    return false;
                g.Sep = true;
                if (0 < smp.N)
                    return Separated(g, smp, d, sep, w);
            }
            if (0 < smp.N && Dup(smp, w))
            {
                if (smp.N != 3)
                    return false;
                var s = smp.S;
                float nx = (s[7] - s[1]) * (s[5] - s[2]) - (s[8] - s[2]) * (s[4] - s[1]);
                float nz = (s[6] - s[0]) * (s[4] - s[1]) - (s[7] - s[1]) * (s[3] - s[0]);
                float ny = (s[8] - s[2]) * (s[3] - s[0]) - (s[6] - s[0]) * (s[5] - s[2]);
                var n = Norm17(new V(nx, ny, nz), nz * nz + ny * ny + nx * nx);
                toi = s[2] * n.Z + s[1] * n.Y + s[0] * n.X;
                if (0.001f <= MathF.Abs(toi))
                    return false;
                d = n.Z * d.Z + n.Y * d.Y + n.X * d.X <= 0f ? n : -n;
                var w2 = W(g, d);
                if (!Dup(smp, w2))
                {
                    smp.Set(smp.N, w2);
                    smp.N++;
                }
                return Finish(g, smp, toi);
            }
            var v0 = g.V0;
            var done = SimplexAdd(smp, w, ref v0);
            g.V0 = v0;
            var lsq = v0.X * v0.X + v0.Y * v0.Y + v0.Z * v0.Z;
            if (lsq < 1e-08f || done)
                break;
            var nv = NormTiny(v0, lsq);
            d = -nv;
            iter++;
            if (99 < iter)
                return false;
        }
        if (g.Ray.Len == 0f)
            return Solid(g);
        return Finish(g, smp, 0f);
    }

    private static bool Finish(Gjk g, Smp smp, float toi)
    {
        if (Toi(g, smp, ref toi) && toi < g.Ray.Len)
        {
            Contact(g, smp, toi);
            return true;
        }
        return Solid(g);
    }

    /// <summary>1802e7840: GJK advancement once a separating direction was seen.</summary>
    private static bool Separated(Gjk g, Smp smp, V dir, float sep, V w)
    {
        var margin = g.Margin;
        var tol = g.Tol;
        var hitMargin = false;
        if (0f < sep)
        {
            if (!Dup(smp, w))
            {
                for (var iter = 0; iter < 20; iter++)
                {
                    var old = w;
                    var v0 = g.V0;
                    SimplexAdd(smp, w, ref v0);
                    g.V0 = v0;
                    float x = -v0.X, y = -v0.Y, z = -v0.Z;
                    dir = NormTiny(new V(x, y, z), y * y + x * x + z * z);
                    w = W(g, dir);
                    var f = -(dir.Z * w.Z + dir.Y * w.Y + dir.X * w.X);
                    if (sep <= f)
                        sep = f;
                    if (margin <= f)
                    {
                        hitMargin = true;
                        if (margin + tol <= f)
                            return false;
                        if (g.Ray.Len <= 0f)
                            return false;
                    }
                    if (MathF.Abs(f - -(old.Z * dir.Z + old.Y * dir.Y + old.X * dir.X)) < 0.0001f)
                        break;
                    if (Dup(smp, w))
                        break;
                }
            }
        }
        if (margin <= sep)
            hitMargin = true;
        var ray = g.Ray;
        if (smp.N == 4)
        {
            g.Sep = false;
            if (ray.Len != 0f)
            {
                var toi = 0f;
                if (Toi(g, smp, ref toi) && toi < ray.Len)
                {
                    Contact(g, smp, toi);
                    return true;
                }
            }
            if (!hitMargin)
            {
                g.StartSolid = true;
                g.Dist = 0f;
            }
            return true;
        }
        var v1 = g.V0;
        var n = Norm17(v1, v1.Z * v1.Z + v1.Y * v1.Y + v1.X * v1.X);
        if (g.Sep && 0f <= smp.S[2] * n.Z + smp.S[1] * n.Y + n.X * smp.S[0])
            n = -n;
        var rdn = ray.Dir.Z * n.Z + ray.Dir.Y * n.Y + ray.Dir.X * n.X;
        if (hitMargin)
        {
            if (ray.Len <= 0f || rdn <= 0f)
                return false;
        }
        else if (rdn <= -0.001f)
        {
            g.StartSolid = true;
            g.Dist = 0f;
            g.Normal = ray.Dir;
            return true;
        }
        if (ray.Len <= 0f)
        {
            g.Dist = 0f;
            g.Frac = 0f;
            g.StartSolid = true;
            g.Normal = ray.Dir;
            return true;
        }
        var r = Contact(g, smp, 0f);
        if (hitMargin)
            g.StartSolid = false;
        return r;
    }

    // ---- simplex ----

    /// <summary>The closest point of the edge from w to w + e: (e x w) x e and its squared length.</summary>
    private static float EdgeV(V e, V w, out V v)
    {
        var cx = w.Z * e.Y - w.Y * e.Z;
        var cy = w.X * e.Z - w.Z * e.X;
        var cz = w.Y * e.X - w.X * e.Y;
        v = new V(cy * e.Z - cz * e.Y, cz * e.X - cx * e.Z, cx * e.Y - cy * e.X);
        return cx * cx + cy * cy + cz * cz;
    }

    /// <summary>The degenerate-edge fallback at the end of 1802e8750 (also 1802ea0c9).</summary>
    private static bool Degenerate(Smp s, float dz, V u, ref V v)
    {
        var fx = s.S[0] - u.X;
        var fy = s.S[1] - u.Y;
        var f8 = dz * 0f;
        var f7 = fx * 0f;
        v = new V(fy - f8, f8 - fx, f7 - fy * 0f);
        if (v.X * v.X + v.Y * v.Y + v.Z * v.Z <= Eps8)
            v = new V(fy * 0f - f8, dz - f7, f7 - fy);
        return false;
    }

    /// <summary>1802e6ed0: add w to the simplex and set v; true ends the GJK loop.</summary>
    private static bool SimplexAdd(Smp s, V w, ref V v)
    {
        switch (s.N)
        {
            case 0:
                s.N = 1;
                s.Set(0, w);
                v = w;
                return false;
            case 1:
            {
                var e = new V(s.S[0] - w.X, s.S[1] - w.Y, s.S[2] - w.Z);
                if (0f <= e.Y * w.Y + e.Z * w.Z + e.X * w.X)
                {
                    s.N = 1;
                    s.Set(0, w);
                    v = w;
                    return false;
                }
                s.N = 2;
                s.Set(1, w);
                var cx = e.Y * w.Z - e.Z * w.Y;
                var cy = e.Z * w.X - e.X * w.Z;
                var cz = e.X * w.Y - e.Y * w.X;
                v = new V(cy * e.Z - cz * e.Y, cz * e.X - cx * e.Z, cx * e.Y - cy * e.X);
                if (cy * cy + cx * cx + cz * cz <= Eps8)
                {
                    v = new V(e.Y - e.Z * 0f, e.Z - e.X, e.X * 0f - e.Y);
                    return true;
                }
                return false;
            }
            case 2:
                return Triangle(s, w, ref v);
            case 3:
                return Tetra(s, w, ref v);
            default:
                return true;
        }
    }

    /// <summary>1802e8750: the triangle step (P0, P1, w).</summary>
    private static bool Triangle(Smp s, V w, ref V v)
    {
        var a = new V(s.S[0] - w.X, s.S[1] - w.Y, s.S[2] - w.Z);
        var b = new V(s.S[3] - w.X, s.S[4] - w.Y, s.S[5] - w.Z);
        var f3 = b.Z * a.Y - b.Y * a.Z;
        var f4 = b.X * a.Z - b.Z * a.X;
        var f6 = b.Y * a.X - b.X * a.Y;
        bool aEdge;
        if (0f <= (f6 * b.X - f3 * b.Z) * w.Y + (f3 * b.Y - f4 * b.X) * w.Z + (f4 * b.Z - f6 * b.Y) * w.X)
        {
            if (0f < (f3 * a.Z - f6 * a.X) * w.Y + (f4 * a.X - f3 * a.Y) * w.Z + (f6 * a.Y - f4 * a.Z) * w.X)
            {
                s.N = 3;
                if (0f <= w.Y * f4 + w.Z * f6 + f3 * w.X)
                {
                    s.Set(2, s.P(1));
                    s.Set(1, w);
                    v = new V(f3, f4, f6);
                }
                else
                {
                    s.Set(2, w);
                    v = new V(-f3, -f4, -f6);
                }
                return false;
            }
            aEdge = true;
        }
        else
            aEdge = 0f <= w.Y * b.Y + w.Z * b.Z + b.X * w.X;
        if (aEdge)
        {
            if (0f <= w.Z * a.Z + w.Y * a.Y + w.X * a.X)
            {
                s.N = 1;
                s.Set(0, w);
                v = w;
                return false;
            }
            var l = EdgeV(a, w, out v);
            s.N = 2;
            s.Set(1, w);
            if (Eps8 < l)
                return false;
            return Degenerate(s, s.S[2] - w.Z, w, ref v);
        }
        var lb = EdgeV(b, w, out v);
        s.N = 2;
        s.Set(0, w);
        if (Eps8 < lb)
            return false;
        return Degenerate(s, s.S[2] - s.S[5], s.P(1), ref v);
    }

    /// <summary>1802e8cc0: the tetrahedron step (P0, P1, P2, w); its eight cases from the jump table at 1802ea1f0.</summary>
    private static bool Tetra(Smp s, V w, ref V v)
    {
        var a = new V(s.S[0] - w.X, s.S[1] - w.Y, s.S[2] - w.Z);
        var b = new V(s.S[3] - w.X, s.S[4] - w.Y, s.S[5] - w.Z);
        var c = new V(s.S[6] - w.X, s.S[7] - w.Y, s.S[8] - w.Z);
        // n_ab = a x b, n_bc = b x c, n_ca = c x a
        var f19 = b.Z * a.Y - b.Y * a.Z;
        var f6 = b.X * a.Z - b.Z * a.X;
        var f7 = b.Y * a.X - b.X * a.Y;
        var f17 = c.Z * b.Y - c.Y * b.Z;
        var f12 = c.X * b.Z - c.Z * b.X;
        var f15 = c.Y * b.X - c.X * b.Y;
        var f8 = c.Y * a.Z - c.Z * a.Y;
        var f9 = c.Z * a.X - c.X * a.Z;
        var f10 = c.X * a.Y - c.Y * a.X;
        var bits = (w.Z * f7 + w.Y * f6 + w.X * f19 < 0f ? 1 : 0)
                   | (0f <= w.Z * f15 + w.Y * f12 + w.X * f17 ? 0 : 2)
                   | (0f <= w.Z * f10 + w.Y * f9 + w.X * f8 ? 0 : 4);
        var vv = v;

        // The planes through w of each edge with each face normal.
        float AbOutA() => (a.Z * f19 - a.X * f7) * w.Y + (a.X * f6 - a.Y * f19) * w.Z + (a.Y * f7 - a.Z * f6) * w.X;
        float AbOutB() => (b.Y * f19 - b.X * f6) * w.Z + (b.X * f7 - b.Z * f19) * w.Y + (b.Z * f6 - b.Y * f7) * w.X;
        float BcOutB() => (b.X * f12 - b.Y * f17) * w.Z + (b.Z * f17 - b.X * f15) * w.Y + (b.Y * f15 - b.Z * f12) * w.X;
        float BcOutC() => (c.X * f15 - c.Z * f17) * w.Y + (c.Y * f17 - c.X * f12) * w.Z + (c.Z * f12 - c.Y * f15) * w.X;
        float CaOutC() => (c.X * f9 - c.Y * f8) * w.Z + (c.Z * f8 - c.X * f10) * w.Y + (c.Y * f10 - c.Z * f9) * w.X;
        float CaOutA() => (a.X * f10 - a.Z * f8) * w.Y + (a.Y * f8 - a.X * f9) * w.Z + (a.Z * f9 - a.Y * f10) * w.X;

        bool Vertex()
        {
            s.N = 1;
            s.Set(0, w);
            vv = w;
            return false;
        }
        bool FaceAb()
        {
            s.N = 3;
            s.Set(2, w);
            vv = new V(-f19, -f6, -f7);
            return false;
        }
        bool FaceBc()
        {
            s.N = 3;
            s.Set(0, w);
            vv = new V(-f17, -f12, -f15);
            return false;
        }
        bool FaceCa()
        {
            s.N = 3;
            s.Set(1, w);
            vv = new V(-f8, -f9, -f10);
            return false;
        }
        // Edge (P0, w): ea09b / 1802ea0c9.
        bool EdgeA()
        {
            var l = EdgeV(a, w, out vv);
            s.N = 2;
            s.Set(1, w);
            if (Eps8 < l)
                return false;
            return Degenerate(s, s.S[2] - w.Z, w, ref vv);
        }
        // Edge (w, P1): e9162.
        bool EdgeB()
        {
            var l = EdgeV(b, w, out vv);
            s.N = 2;
            s.Set(0, w);
            if (Eps8 < l)
                return false;
            return Degenerate(s, s.S[2] - s.S[5], s.P(1), ref vv);
        }
        // Edge (P2, w): e9fca, P0 <- P2 then as EdgeA.
        bool EdgeC()
        {
            s.Set(0, s.P(2));
            var l = EdgeV(c, w, out vv);
            s.N = 2;
            s.Set(1, w);
            if (Eps8 < l)
                return false;
            return Degenerate(s, s.S[2] - w.Z, w, ref vv);
        }
        bool EdgeCOrVertex() => w.Z * c.Z + w.Y * c.Y + w.X * c.X < 0f ? EdgeC() : Vertex();      // e9f96
        bool EdgeAOrNext(Func<bool> next) => 0f <= w.Z * a.Z + w.Y * a.Y + w.X * a.X ? next() : EdgeA();
        bool E92b0() => EdgeAOrNext(Vertex);
        bool E9ddc()
        {
            if (0f <= BcOutB())
            {
                if (0f <= BcOutC())
                    return FaceBc();
            }
            else if (b.Z * w.Z + b.Y * w.Y + b.X * w.X < 0f)
                return EdgeB();
            return EdgeCOrVertex();
        }

        bool r;
        switch (bits)
        {
            case 0:
                s.Set(3, w);
                s.N = 4;
                v = vv;
                return true;
            case 1:
                r = Triangle(s, w, ref vv);
                break;
            case 2:
                s.Set(0, s.P(2));
                r = Triangle(s, w, ref vv);
                break;
            case 4:
                s.Set(1, s.P(2));
                r = Triangle(s, w, ref vv);
                break;
            case 3:
                if (0f <= AbOutB())
                    r = 0f <= AbOutA() ? FaceAb() : E92b0();
                else if (BcOutB() < 0f)
                    r = 0f <= b.Z * w.Z + b.Y * w.Y + w.X * b.X ? Vertex() : EdgeB();
                else
                    r = 0f <= BcOutC() ? FaceBc() : EdgeC();
                break;
            case 5:
                if (0f <= CaOutA())
                    r = 0f <= CaOutC() ? FaceCa() : EdgeCOrVertex();
                else if (AbOutA() < 0f)
                    r = E92b0();
                else
                    r = 0f <= AbOutB() ? FaceAb() : EdgeB();
                break;
            case 6:
                if (0f <= BcOutC())
                {
                    if (0f <= BcOutB())
                        r = FaceBc();
                    else
                        r = w.Z * b.Z + w.Y * b.Y + w.X * b.X < 0f ? EdgeB() : Vertex();
                }
                else if (0f <= CaOutC())
                    r = 0f <= CaOutA() ? FaceCa() : EdgeA();
                else
                    r = EdgeCOrVertex();
                break;
            default: // 7
                if (CaOutA() < 0f || CaOutC() < 0f)
                {
                    if (AbOutB() < 0f)
                        r = E9ddc();
                    else if (AbOutA() < 0f)
                        r = EdgeAOrNext(E9ddc);
                    else
                        r = FaceAb();
                }
                else
                    r = FaceCa();
                break;
        }
        v = vv;
        return r;
    }

    // ---- time of impact ----

    /// <summary>1802e7120: the distance along the ray at which the swept box first touches the hull.</summary>
    private static bool Toi(Gjk g, Smp smp, ref float toi)
    {
        var dir = g.Ray.Dir;
        if (smp.N < 4)
        {
            if (smp.N == 2)
            {
                var v = g.V0;
                if (v.X * v.X + v.Y * v.Y + v.Z * v.Z <= 0f)
                    return false;
                var w = W(g, -v);
                if (!Dup(smp, w))
                {
                    smp.Set(2, w);
                    smp.N = 3;
                }
            }
            if (smp.N != 3)
                return false;
        }
        else
        {
            var o = default(V);
            if (ToiInit(smp, dir, ref o))
            {
                var iter = 0;
                while (true)
                {
                    o = NormTiny(o, o.Y * o.Y + o.X * o.X + o.Z * o.Z);
                    var w = W(g, o);
                    var s = smp.S;
                    if (o.Z * w.Z + o.Y * w.Y + w.X * o.X <= s[2] * o.Z + s[1] * o.Y + s[0] * o.X + 0.00390625f)
                        break;
                    smp.Set(smp.N, w);
                    smp.N++;
                    o = ToiStep(smp, dir);
                    iter++;
                    if (99 < iter)
                        return false;
                }
                var t = smp.S;
                var e2x = t[6] - t[0];
                var e1z = t[5] - t[2];
                var e1x = t[3] - t[0];
                var e1y = t[4] - t[1];
                var e2y = t[7] - t[1];
                var ny = e2x * e1z - (t[8] - t[2]) * e1x;
                var nx = (t[8] - t[2]) * e1y - e2y * e1z;
                var nz = e2y * e1x - e2x * e1y;
                var den = dir.Z * nz + dir.Y * ny + nx * dir.X;
                float r;
                if (den <= 0f)
                    r = Far;
                else
                {
                    var num = t[2] * nz + t[1] * ny + nx * t[0];
                    r = (0f <= num ? num : 0f) / den;
                }
                toi = r;
                if (smp.N != 3)
                    return true;
                if (t[2] * nz + t[1] * ny + nx * t[0] <= nz * 0f + ny * 0f + nx * 0f)
                    return true;
                var p1 = smp.P(1);
                smp.Set(1, smp.P(2));
                smp.Set(2, p1);
                return true;
            }
            smp.N = 3;
        }
        toi = ToiTriangle(smp, dir, g.Tol);
        return true;
    }

    private static float Ratio(float den, float num)
    {
        if (den <= 0f)
            return Far;
        return (0f <= num ? num : 0f) / den;
    }

    /// <summary>1802e4900: replace the vertex of the face the ray leaves through; returns that face's normal.</summary>
    private static V ToiStep(Smp smp, V d)
    {
        var s = smp.S;
        float s9 = s[9], s10 = s[10], s11 = s[11];
        var a = new V(s[0] - s9, s[1] - s10, s[2] - s11);
        var b = new V(s[3] - s9, s[4] - s10, s[5] - s11);
        var c = new V(s[6] - s9, s[7] - s10, s[8] - s11);
        var f15 = b.Z * a.Y - b.Y * a.Z;
        var f2 = b.X * a.Z - b.Z * a.X;
        var f12 = b.Y * a.X - b.X * a.Y;
        var f13 = c.Z * b.Y - c.Y * b.Z;
        var f14 = c.X * b.Z - c.Z * b.X;
        var f5 = c.Y * b.X - c.X * b.Y;
        var f4 = c.Y * a.Z - c.Z * a.Y;
        var f3 = c.Z * a.X - c.X * a.Z;
        var f6 = c.X * a.Y - c.Y * a.X;
        var t0 = Ratio(d.Z * f12 + d.Y * f2 + f15 * d.X, s11 * f12 + s10 * f2 + f15 * s9);
        var t1 = Ratio(d.Z * f5 + d.Y * f14 + f13 * d.X, s11 * f5 + s10 * f14 + f13 * s9);
        var t2 = Ratio(d.Z * f6 + d.Y * f3 + d.X * f4, s11 * f6 + s10 * f3 + s9 * f4);
        var best = t0 < t2 ? t0 : t2;
        var idx = t2 <= t0 ? 2 : 0;
        smp.N = 3;
        var p3 = smp.P(3);
        if (best <= t1)
        {
            if (idx == 0)
            {
                smp.Set(2, p3);
                return new V(f15, f2, f12);
            }
            smp.Set(1, p3);
            return new V(f4, f3, f6);
        }
        smp.Set(0, p3);
        return new V(f13, f14, f5);
    }

    /// <summary>1802e4cd0: the face of the tetrahedron the ray leaves through; false when the ray runs in its plane.</summary>
    private static bool ToiInit(Smp smp, V d, ref V o)
    {
        var s = smp.S;
        float s9 = s[9], s10 = s[10], s11 = s[11];
        var a = new V(s[0] - s9, s[1] - s10, s[2] - s11);
        var b = new V(s[3] - s9, s[4] - s10, s[5] - s11);
        var c = new V(s[6] - s9, s[7] - s10, s[8] - s11);
        var e1 = new V(s[3] - s[0], s[4] - s[1], s[5] - s[2]);
        var e2 = new V(s[6] - s[0], s[7] - s[1], s[8] - s[2]);
        var f4 = b.Z * a.Y - b.Y * a.Z;
        var f5 = b.X * a.Z - b.Z * a.X;
        var f6 = b.Y * a.X - b.X * a.Y;
        var f7 = c.Z * b.Y - c.Y * b.Z;
        var f8 = c.X * b.Z - c.Z * b.X;
        var f11 = c.Y * b.X - c.X * b.Y;
        var f9 = c.Y * a.Z - c.Z * a.Y;
        var f21 = c.Z * a.X - c.X * a.Z;
        var f3 = c.X * a.Y - c.Y * a.X;
        var f10 = e2.Y * e1.Z - e2.Z * e1.Y;
        var f13 = e2.Z * e1.X - e2.X * e1.Z;
        var f12 = e2.X * e1.Y - e2.Y * e1.X;
        var t0 = Ratio(d.Z * f6 + d.Y * f5 + d.X * f4, s11 * f6 + s10 * f5 + s9 * f4);
        var t1 = Ratio(d.Z * f11 + d.Y * f8 + d.X * f7, s11 * f11 + s10 * f8 + s9 * f7);
        var t2 = Ratio(d.Z * f3 + d.Y * f21 + d.X * f9, s11 * f3 + s10 * f21 + s9 * f9);
        var t3 = Ratio(d.Z * f12 + d.Y * f13 + d.X * f10, s[2] * f12 + s[1] * f13 + s[0] * f10);
        var best = t0 < t3 ? t0 : t3;
        var idx = t3 <= t0 ? 3 : 0;
        if (t1 < best)
        {
            idx = 1;
            best = t1;
        }
        if (t2 < best)
        {
            idx = 2;
            best = t2;
        }
        if (Far <= best && MathF.Abs(s11 * f12 + s10 * f13 + f10 * s9
                                     - (s[2] * f12 + s[1] * f13 + f10 * s[0] + s[5] * f12 + s[4] * f13 + f10 * s[3]) * 0.5f) < 1e-06f)
            return false;
        smp.N = 3;
        var p3 = smp.P(3);
        switch (idx)
        {
            case 0:
                smp.Set(2, p3);
                o = new V(f4, f5, f6);
                break;
            case 1:
                smp.Set(0, p3);
                o = new V(f7, f8, f11);
                break;
            case 2:
                smp.Set(1, p3);
                o = new V(f9, f21, f3);
                break;
            default:
                var p1 = smp.P(1);
                smp.Set(1, smp.P(2));
                smp.Set(2, p1);
                smp.Set(3, p1);
                o = new V(f10, f13, f12);
                break;
        }
        return true;
    }

    /// <summary>1802e52c0: the distance to the triangle's edge planes along the ray, plus the tolerance over the slope.</summary>
    private static float ToiTriangle(Smp smp, V d, float margin)
    {
        var s = smp.S;
        var a = new V(s[0] - s[6], s[1] - s[7], s[2] - s[8]);
        var b = new V(s[3] - s[6], s[4] - s[7], s[5] - s[8]);
        var e = new V(s[3] - s[0], s[4] - s[1], s[5] - s[2]);
        var f13 = b.Z * a.Y - b.Y * a.Z;
        var f6 = b.X * a.Z - b.Z * a.X;
        var f12 = b.Y * a.X - b.X * a.Y;
        var n = Norm17(new V(f13, f6, f12), f12 * f12 + f6 * f6 + f13 * f13);
        var m0 = new V(n.Z * a.Y - n.Y * a.Z, a.Z * n.X - n.Z * a.X, n.Y * a.X - a.Y * n.X);
        var m1 = new V(n.Y * b.Z - n.Z * b.Y, n.Z * b.X - n.X * b.Z, n.X * b.Y - n.Y * b.X);
        var m2 = new V(n.Z * e.Y - n.Y * e.Z, e.Z * n.X - n.Z * e.X, n.Y * e.X - e.Y * n.X);
        m0 = Norm17(m0, m0.Z * m0.Z + m0.Y * m0.Y + m0.X * m0.X);
        m1 = Norm17(m1, m1.Z * m1.Z + m1.Y * m1.Y + m1.X * m1.X);
        m2 = Norm17(m2, m2.Z * m2.Z + m2.Y * m2.Y + m2.X * m2.X);
        var t0 = Ratio(d.Z * m0.Z + d.Y * m0.Y + d.X * m0.X, s[2] * m0.Z + s[1] * m0.Y + s[0] * m0.X);
        var t1 = Ratio(d.Z * m1.Z + d.Y * m1.Y + d.X * m1.X, s[5] * m1.Z + s[4] * m1.Y + s[3] * m1.X);
        var t2 = Ratio(d.Z * m2.Z + d.Y * m2.Y + d.X * m2.X, s[5] * m2.Z + s[4] * m2.Y + s[3] * m2.X);
        var best = t1 < t0 ? t1 : t0;
        var m = t1 < t0 ? m1 : m0;
        if (t2 < best)
        {
            m = m2;
            best = t2;
        }
        var k = d.Z * m.Z + m.Y * d.Y + d.X * m.X;
        return 0f < k ? margin / k + best : Far;
    }

    // ---- contact depth ----

    /// <summary>1802e5ab0: the depth at the contact, turned into the distance along the ray.</summary>
    private static bool Contact(Gjk g, Smp smp, float toi)
    {
        var m = g.Margin + g.Tol;
        if (m <= 0f)
            throw new NotSupportedException("hull cast without margin (1802e4570)");
        var f = Depth(g, smp, toi, m);
        g.V0 = g.Normal;
        var ray = g.Ray;
        var dist = ray.Len - f;
        g.Dist = dist;
        if (dist < 0f)
        {
            var q = g.Tol * 0.25f;
            g.StartSolid = true;
            g.Dist = 0f;
            var mm = g.Margin <= q ? q : g.Margin;
            if (Depth(g, smp, toi, mm) < ray.Len)
                g.StartSolid = false;
        }
        return g.Dist <= ray.Len && ray.Len != g.Dist;
    }

    private static float Depth(Gjk g, Smp smp, float dist, float margin) => smp.N < 3
        ? smp.N == 2 ? EdgeDepth(g, smp.P(0), smp.P(1), dist, margin, true) : VertexDepth(g, smp.P(0), dist, margin)
        : FaceDepth(g, smp.P(0), smp.P(1), smp.P(2), dist, margin, true);

    /// <summary>1802e4200: one simplex point.</summary>
    private static float VertexDepth(Gjk g, V pt, float dist, float margin)
    {
        var d = g.Ray.Dir;
        var p = new V(dist * d.X, dist * d.Y, dist * d.Z);
        var f7 = d.Y * pt.Y + d.Z * pt.Z + pt.X * d.X + dist + margin + margin;
        var dl = new V(f7 * d.X + p.X - p.X, f7 * d.Y + p.Y - p.Y, f7 * d.Z + p.Z - p.Z);
        float t0 = 0f, t1 = 1f;
        if (!SegmentSphereClip(p, dl, pt, margin, ref t0, ref t1))
        {
            g.Normal = -d;
            return dist + margin;
        }
        var t = t0 <= t1 ? t1 : t0;
        var qy = dl.Y * t + p.Y - pt.Y;
        var qz = dl.Z * t + p.Z - pt.Z;
        var qx = dl.X * t + p.X - pt.X;
        var n = NormTiny(new V(qx, qy, qz), qx * qx + qy * qy + qz * qz);
        g.Normal = -n;
        var dlen = MathF.Sqrt(dl.Z * dl.Z + dl.Y * dl.Y + dl.X * dl.X);
        var f = dlen * t;
        if (f <= dist)
            f = dist;
        return RefineVertex(g, pt, f, margin, n);
    }

    /// <summary>18037d090 / 18037cd90: the segment start + t delta against the sphere, clipped to [0, 1].</summary>
    private static bool SegmentSphereClip(V s, V d, V c, float r, ref float t0, ref float t1)
    {
        var ox = s.X - c.X;
        var oz = s.Z - c.Z;
        var oy = s.Y - c.Y;
        var dd = d.Z * d.Z + d.Y * d.Y + d.X * d.X;
        bool ok;
        if (dd != 0f)
        {
            var inv = 1f / dd;
            var b = oz * d.Z + oy * d.Y + ox * d.X;
            var m = -(b + b) * inv * 0.5f;
            var px = d.X * m + ox;
            var py = d.Y * m + oy;
            var pz = d.Z * m + oz;
            var h = r * r - (px * px + py * py + pz * pz);
            if (0f <= h)
            {
                h = MathF.Sqrt(h * inv);
                t0 = m - h;
                t1 = h + m;
                ok = true;
            }
            else
                ok = false;
        }
        else
        {
            t1 = 0f;
            t0 = 0f;
            ok = ox * ox + oy * oy + oz * oz <= r * r;
        }
        if (ok && t0 <= 1f && 0f <= t1)
        {
            if (t0 < 0f)
                t0 = 0f;
            if (1f < t1)
                t1 = 1f;
            return true;
        }
        return false;
    }

    /// <summary>The ray-capsule result (16caf0's out): fraction, point, normal, started inside.</summary>
    private struct CapsuleHit
    {
        public float Frac;
        public V Pt, N;
        public bool Flag;
    }

    /// <summary>1802e2e80: two simplex points.</summary>
    private static float EdgeDepth(Gjk g, V p0, V p1, float dist, float margin, bool flag)
    {
        var d = g.Ray.Dir;
        var ex = p1.X - p0.X;
        var ey = p1.Y - p0.Y;
        var ez = p1.Z - p0.Z;
        var len = MathF.Sqrt(ez * ez + ey * ey + ex * ex);
        var l = len + margin + dist;
        var st = new V(d.X * l, d.Y * l, d.Z * l);
        var dl = new V(d.X * dist - st.X, d.Y * dist - st.Y, d.Z * dist - st.Z);
        var res = new CapsuleHit { Frac = 1f };
        RayCapsule(ref res, st, dl, p0, p1, margin);
        var f = res.Flag ? 0f : res.Frac;
        f = 0f <= f ? (f <= 1f ? f : 1f) : 0f;
        if (!(f < 1f))
        {
            g.Normal = -d;
            return dist;
        }
        g.Normal = -res.N;
        var inv = 1f / len;
        var nd = (1f - f) * (len + margin) + dist;
        var uz = ez * inv;
        var ux = ex * inv;
        var uy = ey * inv;
        var at = uz * res.Pt.Z + uy * res.Pt.Y + ux * res.Pt.X;
        if (uz * p0.Z + uy * p0.Y + ux * p0.X <= at)
        {
            if (at <= uy * p1.Y + uz * p1.Z + ux * p1.X)
            {
                var cx = d.Y * ez - d.Z * ey;
                var cy = d.Z * ex - ez * d.X;
                var cz = ey * d.X - d.Y * ex;
                var kx = cy * ez - ey * cz;
                var kz = ey * cx - cy * ex;
                var ky = cz * ex - cx * ez;
                var k = NormTiny(new V(kx, ky, kz), ky * ky + kx * kx + kz * kz);
                var kd = d.Z * k.Z + d.Y * k.Y + k.X * d.X;
                if (kd != 0f)
                {
                    if (kd < 0f)
                        return RefineEdge(g, p1, p0, nd, margin, -k);
                    return RefineEdge(g, p0, p1, nd, margin, k);
                }
                return nd;
            }
            return RefineVertex(g, p1, nd, margin, res.N);
        }
        if (flag)
            return RefineVertex(g, p0, nd, margin, res.N);
        return dist;
    }

    /// <summary>1802e3460: three simplex points.</summary>
    private static float FaceDepth(Gjk g, V p0, V p1, V p2, float dist, float margin, bool flag)
    {
        var d = g.Ray.Dir;
        var e01 = new V(p1.X - p0.X, p1.Y - p0.Y, p1.Z - p0.Z);
        var e12 = new V(p2.X - p1.X, p2.Y - p1.Y, p2.Z - p1.Z);
        var e20 = new V(p0.X - p2.X, p0.Y - p2.Y, p0.Z - p2.Z);
        var pd = new V(d.X * dist, d.Y * dist, d.Z * dist);
        var f14 = e20.Y * e01.Z - e20.Z * e01.Y;
        var f15 = e20.Z * e01.X - e20.X * e01.Z;
        var f12 = e20.X * e01.Y - e20.Y * e01.X;
        var n = Norm17(new V(f14, f15, f12), f12 * f12 + f15 * f15 + f14 * f14);
        if (!(0.5f <= n.X * n.X + n.Y * n.Y + n.Z * n.Z))
        {
            f14 = e12.Z * e01.Y - e12.Y * e01.Z;
            f15 = e12.X * e01.Z - e01.X * e12.Z;
            f12 = e01.X * e12.Y - e12.X * e01.Y;
            n = Norm17(new V(f14, f15, f12), f15 * f15 + f12 * f12 + f14 * f14);
        }
        var bFlag = false;
        var proj = p0.Z * n.Z + p0.Y * n.Y + n.X * p0.X;
        var slope = n.Y * d.Y + n.Z * d.Z + n.X * d.X;
        float ret = dist, f5;
        var far = false;
        if (MathF.Abs(slope) <= 0f)
            far = true;
        else
        {
            var t = (0f <= slope ? proj + margin : proj - margin) / slope;
            var rl = g.Ray.Len;
            if (rl + rl < t && MathF.Abs(slope) <= 0.001f)
                far = true;
            else
            {
                f5 = t - dist;
                if (!flag)
                    bFlag = true;
                else
                    g.Normal = 0f <= slope ? -n : n;
                ret = t;
                if (f5 < 0f)
                    far = true;
                else
                    goto Edges;
            }
        }
        {
            var m = g.Ray.Len;
            var e = p0.Z * d.Z + p0.Y * d.Y + p0.X * d.X;
            if (m <= e)
                m = e;
            e = p1.Z * d.Z + p1.Y * d.Y + p1.X * d.X;
            if (m <= e)
                m = e;
            e = p2.Z * d.Z + p2.Y * d.Y + p2.X * d.X;
            if (m <= e)
                m = e;
            f5 = m + margin - dist;
        }
    Edges:
        _ = far;
        if (ret <= dist)
            ret = dist;
        var which = -1;
        var q = (0f <= f5 ? f5 : 0f) + dist;
        var qv = new V(d.X * q, d.Y * q, d.Z * q);
        var best = 2f;
        // edge p0 p1
        {
            var mx = n.Z * e01.Y - n.Y * e01.Z;
            var mz = n.Y * e01.X - e01.Y * n.X;
            var my = e01.Z * n.X - n.Z * e01.X;
            var m = Norm17(new V(mx, my, mz), mz * mz + my * my + mx * mx);
            var a = p0.Z * m.Z + p0.Y * m.Y + m.X * p0.X;
            var bq = qv.Z * m.Z + qv.Y * m.Y + qv.X * m.X;
            if (a < bq)
            {
                var r = 0f;
                var c = m.Z * pd.Z + m.Y * pd.Y + pd.X * m.X;
                if (a <= c || (r = (a - c) / (bq - c)) < 2f)
                {
                    if (r <= 0f)
                        r = 0f;
                    which = 0;
                    best = r;
                }
            }
        }
        // edge p1 p2
        {
            var mx = n.Z * e12.Y - n.Y * e12.Z;
            var mz = n.Y * e12.X - e12.Y * n.X;
            var my = e12.Z * n.X - n.Z * e12.X;
            var m = Norm17(new V(mx, my, mz), mz * mz + my * my + mx * mx);
            var a = p1.Z * m.Z + p1.Y * m.Y + m.X * p1.X;
            var bq = qv.Z * m.Z + qv.Y * m.Y + qv.X * m.X;
            if (a < bq)
            {
                var r = 0f;
                var c = m.Z * pd.Z + m.Y * pd.Y + pd.X * m.X;
                if (c < a)
                    r = (a - c) / (bq - c);
                if (r < best)
                {
                    which = 1;
                    best = r;
                    if (r <= 0f)
                        best = 0f;
                }
            }
        }
        // edge p2 p0
        {
            var mx = n.Z * e20.Y - n.Y * e20.Z;
            var mz = n.Y * e20.X - e20.Y * n.X;
            var my = e20.Z * n.X - n.Z * e20.X;
            var m = Norm17(new V(mx, my, mz), my * my + mz * mz + mx * mx);
            var a = p2.Z * m.Z + p2.Y * m.Y + m.X * p2.X;
            var bq = qv.Z * m.Z + qv.Y * m.Y + qv.X * m.X;
            if (a < bq)
            {
                var c = m.Z * pd.Z + m.Y * pd.Y + pd.X * m.X;
                var r = 0f;
                if (c < a)
                    r = (a - c) / (bq - c);
                if (r < best)
                    return EdgeCapsule(g, p2, p0, qv, dist, margin, e20);
            }
        }
        if (which == 0)
            return EdgeCapsule(g, p0, p1, qv, dist, margin, e01);
        if (which == 1)
            return EdgeCapsule(g, p1, p2, qv, dist, margin, e12);
        if (bFlag)
            g.Normal = 0f <= slope ? -n : n;
        return ret;
    }

    /// <summary>1802eb5e0: the ray back from v against the capsule round an edge.</summary>
    private static float EdgeCapsule(Gjk g, V a, V b, V v, float dist, float margin, V edge)
    {
        var res = new CapsuleHit { Frac = 1f };
        RayCapsule(ref res, v, -v, a, b, margin);
        var f = res.Flag ? 0f : res.Frac;
        f = 0f <= f ? (f <= 1f ? f : 1f) : 0f;
        if (!(f < 1f))
            return dist;
        var len = MathF.Sqrt(v.Y * v.Y + v.Z * v.Z + v.X * v.X);
        var nd = len * (1f - f);
        if (!(dist < nd))
            return dist;
        g.Normal = -res.N;
        var e = Norm17(edge, edge.Z * edge.Z + edge.Y * edge.Y + edge.X * edge.X);
        var at = res.Pt.Z * e.Z + res.Pt.Y * e.Y + res.Pt.X * e.X;
        if (at < a.Y * e.Y + e.Z * a.Z + a.X * e.X)
            return RefineVertex(g, a, nd, margin, res.N);
        if (b.Y * e.Y + e.Z * b.Z + b.X * e.X < at)
            return RefineVertex(g, b, nd, margin, res.N);
        return RefineEdge(g, a, b, nd, margin, res.N);
    }

    /// <summary>1802e5c80: grow a contact at one point into an edge or a face.</summary>
    private static float RefineVertex(Gjk g, V p, float dist, float margin, V dir)
    {
        var w = W(g, dir);
        if (!(0.0001f < w.Z * dir.Z + w.Y * dir.Y + w.X * dir.X - (p.Y * dir.Y + dir.Z * p.Z + p.X * dir.X)))
            return dist;
        var rd = g.Ray.Dir;
        var e1 = new V(w.X - p.X, w.Y - p.Y, w.Z - p.Z);
        var r = new V(dist * rd.X, dist * rd.Y, dist * rd.Z);
        var rpx = r.X - p.X;
        var rpy = r.Y - p.Y;
        var rpz = r.Z - p.Z;
        var mx = rpy * e1.Z - e1.Y * rpz;
        var my = e1.X * rpz - e1.Z * rpx;
        var mz = e1.Y * rpx - e1.X * rpy;
        var m = NormTiny(new V(mx, my, mz), my * my + mx * mx + mz * mz);
        var kx = e1.Z * m.Y - e1.Y * m.Z;
        var ky = e1.X * m.Z - e1.Z * m.X;
        var kz = e1.Y * m.X - e1.X * m.Y;
        var n = NormTiny(new V(kx, ky, kz), ky * ky + kx * kx + kz * kz);
        if (n.Y * r.Y + n.Z * r.Z + n.X * r.X < 0f)
            n = -n;
        var w2 = W(g, n);
        if (!(0.0001f <= MathF.Abs(n.Z * w2.Z + n.Y * w2.Y + n.X * w2.X - (n.Z * p.Z + n.Y * p.Y + n.X * p.X))))
            return EdgeDepth(g, p, w, dist, margin, false);
        for (var iter = 0; ;)
        {
            var dy = w2.Y - p.Y;
            var dx = w2.X - p.X;
            var dz = w2.Z - p.Z;
            var cx = e1.Z * dy - e1.Y * dz;
            var cy = e1.X * dz - e1.Z * dx;
            var cz = e1.Y * dx - e1.X * dy;
            var m2 = Norm17(new V(cx, cy, cz), cz * cz + cy * cy + cx * cx);
            if (r.Z * m2.Z + r.Y * m2.Y + m2.X * r.X < 0f)
                m2 = -m2;
            var w3 = W(g, m2);
            var before = w2.Z * m2.Z + w2.Y * m2.Y + w2.X * m2.X;
            var after = m2.Z * w3.Z + m2.Y * w3.Y + m2.X * w3.X;
            if (!(0.0001f <= MathF.Abs(after - before) && before <= after && 0f <= dir.Z * m2.Z + dir.Y * m2.Y + dir.X * m2.X))
                break;
            iter++;
            w2 = w3;
            if (!(iter < 32))
                break;
        }
        var ny = (w2.Z - p.Z) * (w.Y - p.Y) - (w2.Y - p.Y) * (w.Z - p.Z);
        var nyy = (w2.X - p.X) * (w.Z - p.Z) - (w2.Z - p.Z) * (w.X - p.X);
        var nz = (w2.Y - p.Y) * (w.X - p.X) - (w2.X - p.X) * (w.Y - p.Y);
        if (dist * rd.Z * nz + dist * rd.Y * nyy + dist * rd.X * ny < p.Z * nz + p.Y * nyy + p.X * ny)
            return FaceDepth(g, p, w2, w, dist, margin, false);
        return FaceDepth(g, p, w, w2, dist, margin, false);
    }

    /// <summary>1802e6780: grow a contact on an edge into a face.</summary>
    private static float RefineEdge(Gjk g, V a, V b, float dist, float margin, V dir)
    {
        var found = false;
        var iter = 0;
        var cur = dir;
        var best = default(V);
        var ex = a.X - b.X;
        var ey = a.Y - b.Y;
        var dot0 = dir.Z * a.Z + dir.Y * a.Y + dir.X * a.X;
        var rd = g.Ray.Dir;
        var r = new V(rd.X * dist, rd.Y * dist, rd.Z * dist);
        var base0 = dot0;
        while (true)
        {
            var w = W(g, cur);
            if (cur.Z * w.Z + cur.Y * w.Y + w.X * cur.X - base0 <= 0.0001f || w.SameAs(b) || w.SameAs(a))
                break;
            if (dir.Y * w.Y + dir.Z * w.Z + dir.X * w.X - dot0 < -0.0001f)
            {
                if (found)
                    goto Face;
            }
            else
            {
                best = w;
                found = true;
            }
            var fx = w.X - b.X;
            var fy = w.Y - b.Y;
            var c13 = fx * (a.Z - b.Z) - (w.Z - b.Z) * ex;
            var c12 = (w.Z - b.Z) * ey - fy * (a.Z - b.Z);
            var c11 = fy * ex - fx * ey;
            cur = NormTiny(new V(c12, c13, c11), c13 * c13 + c12 * c12 + c11 * c11);
            base0 = b.Z * cur.Z + b.Y * cur.Y + cur.X * b.X;
            if (r.Z * cur.Z + r.Y * cur.Y + r.X * cur.X < base0)
            {
                base0 = -base0;
                cur = new V(cur.X * -1f, cur.Y * -1f, cur.Z * -1f);
            }
            if (dir.Z * w.Z + dir.Y * w.Y + dir.X * w.X < dot0)
                break;
            iter++;
            if (31 < iter)
                break;
        }
        if (!found)
            return dist;
    Face:
        var f8 = best.X - b.X;
        var ax = a.X - b.X;
        var ay = a.Y - b.Y;
        var f14 = best.Y - b.Y;
        var n9 = f8 * (a.Z - b.Z) - (best.Z - b.Z) * ax;
        var n15 = (best.Z - b.Z) * ay - f14 * (a.Z - b.Z);
        var n14 = f14 * ax - f8 * ay;
        if (dist * rd.Z * n14 + dist * rd.Y * n9 + dist * rd.X * n15 < b.Z * n14 + b.Y * n9 + n15 * b.X)
            return FaceDepth(g, b, best, a, dist, margin, false);
        return FaceDepth(g, b, a, best, dist, margin, false);
    }

    // ---- ray against capsule / sphere ----

    /// <summary>18016caf0: the ray from <paramref name="s"/> by <paramref name="d"/> against the capsule a-b of radius <paramref name="r"/>.</summary>
    private static void RayCapsule(ref CapsuleHit res, V s, V d, V a, V b, float r)
    {
        var ex = b.X - a.X;
        var rx = s.X - a.X;
        var ry = s.Y - a.Y;
        var ez = b.Z - a.Z;
        var rz = s.Z - a.Z;
        var ey = b.Y - a.Y;
        var l = MathF.Sqrt(ey * ey + ex * ex + ez * ez);
        if (r < 1e-05f)
            return;
        if (l < 0.001f)
        {
            RaySphere(ref res, new V(rx, ry, rz), s, d, r);
            return;
        }
        var inv = 1f / l;
        var dsq = d.X * d.X + d.Y * d.Y + d.Z * d.Z;
        var u = new V(ex * inv, ey * inv, inv * ez);
        var dlen = MathF.Sqrt(dsq);
        if (dlen <= 0.0001f)
        {
            var t = rz * u.Z + ry * u.Y + rx * u.X;
            if (0f <= t)
            {
                if (t <= l)
                {
                    rz -= t * u.Z;
                    rx -= t * u.X;
                    ry -= t * u.Y;
                }
                else
                {
                    rz = s.Z - b.Z;
                    rx = s.X - b.X;
                    ry = s.Y - b.Y;
                }
            }
            var qq = ry * ry + rx * rx + rz * rz;
            if (r * r < qq)
            {
                res.Flag = false;
                res.Frac = 1f;
                return;
            }
            res.Flag = true;
            res.Frac = 0f;
            if (qq <= 1e-08f)
                res.N = Perp(u);
            else
            {
                var i = 1f / MathF.Sqrt(qq);
                res.N = new V(i * rx, i * ry, i * rz);
            }
            res.Pt = s;
            return;
        }
        var hit = float.MaxValue;
        var id = 1f / dlen;
        var cx = id * d.Z * u.Y - id * d.Y * u.Z;
        var cy = id * d.X * u.Z - id * d.Z * u.X;
        var cz = id * d.Y * u.X - id * d.X * u.Y;
        var cl = cy * cy + cx * cx + cz * cz;
        var du = d.Z * u.Z + d.Y * u.Y + d.X * u.X;
        float axis;
        if (9.313226e-10f < cl)
        {
            var ic = 1f / MathF.Sqrt(cl);
            cy = ic * cy;
            cx = ic * cx;
            var c6 = ic * cz;
            var h = rz * c6 + ry * cy + rx * cx;
            h = r * r - h * h;
            if (h <= 0f)
            {
                res.Frac = float.MaxValue;
                return;
            }
            var kx = cy * u.Z - c6 * u.Y;
            var ky = c6 * u.X - cx * u.Z;
            var kz = cx * u.Y - cy * u.X;
            var ru = rz * u.Z + ry * u.Y + rx * u.X;
            var rk = rz * kz + ry * ky + rx * kx;
            axis = ru;
            if (ru <= 0f)
                axis = 0f;
            if (l <= axis)
                axis = l;
            var dk = kz * d.Z + ky * d.Y + d.X * kx;
            if (h <= (ru - axis) * (ru - axis) + rk * rk)
            {
                var iq = 1f / (dk * dk + du * du);
                var t = (-MathF.Sqrt(h) - rk) / dk;
                axis = 0f <= t ? t : 0f;
                axis = axis * du + ru;
                var ll = l;
                if (0f <= axis)
                {
                    if (axis < l)
                    {
                        if (0f <= t)
                            hit = t;
                        goto End;
                    }
                    ru -= l;
                }
                else
                    ll = 0f;
                var t2 = (ru * du + dk * rk) * iq;
                var disc = t2 * t2 - (ru * ru + rk * rk - h) * iq;
                axis = ll;
                if (0f <= disc)
                {
                    disc = MathF.Sqrt(disc);
                    if (0f <= -t2 - disc)
                        hit = -t2 - disc;
                }
            }
            else
                hit = 0f;
            goto End;
        }
        {
            var t = rz * u.Z + ry * u.Y + rx * u.X;
            var px = rx - u.X * t;
            var pz = rz - u.Z * t;
            var py = ry - u.Y * t;
            var pp = py * py + px * px + pz * pz;
            float rr;
            if (1.4210855e-14f <= pp)
            {
                var ip = 1f / MathF.Sqrt(pp);
                var tw = (pz * ip * u.X - ip * px * u.Z) * ry + (ip * px * u.Y - py * ip * u.X) * rz + (py * ip * u.Z - pz * ip * u.Y) * rx;
                rr = r * r - pp - tw * tw;
                axis = u.Z;
                if (rr <= 0f)
                    goto End;
                rr = MathF.Sqrt(rr);
            }
            else
                rr = r;
            if (t + rr < 0f)
            {
                var back = -(t + rr);
                axis = 0f;
                if (back <= du)
                    hit = back / du;
                goto End;
            }
            rr = l + rr;
            if (t < rr)
            {
                hit = 0f;
                if (t <= 0f)
                    t = 0f;
                axis = t;
                if (l <= t)
                    axis = l;
                goto End;
            }
            axis = l;
            if (t - rr <= -du)
                hit = (t - rr) / -du;
        }
    End:
        var f = hit <= 1f ? hit : 1f;
        res.Frac = f;
        res.Flag = hit <= 0f;
        var pz2 = f * d.Z + s.Z;
        res.Pt = new V(f * d.X + s.X, f * d.Y + s.Y, pz2);
        if (hit != float.MaxValue)
        {
            var nx = res.Pt.X - (axis * u.X + a.X);
            var nz = pz2 - (axis * u.Z + a.Z);
            var ny = res.Pt.Y - (axis * u.Y + a.Y);
            res.N = Norm17(new V(nx, ny, nz), nz * nz + ny * ny + nx * nx);
        }
        else
            res.N = new V(0f, 0f, 1f);
    }

    /// <summary>18016b0e0 / 18016b2f0: the ray against a sphere (a capsule shorter than 0.001).</summary>
    private static void RaySphere(ref CapsuleHit res, V rel, V s, V d, float r)
    {
        var dd = d.Z * d.Z + d.Y * d.Y + d.X * d.X;
        var q = rel.Y * rel.Y + rel.Z * rel.Z + rel.X * rel.X;
        var c = q - r * r;
        if (1.4210855e-14f <= dd)
        {
            var b = rel.Z * d.Z + d.Y * rel.Y + rel.X * d.X;
            var disc = b * b - c * dd;
            if (disc < 0f)
            {
                res.Frac = float.MaxValue;
                return;
            }
            var t = (-b - MathF.Sqrt(disc)) / dd;
            if (0f <= t)
            {
                res.Frac = t;
                res.Pt = new V(t * d.X + s.X, t * d.Y + s.Y, t * d.Z + s.Z);
                var ir = 1f / r;
                res.N = new V((t * d.X + rel.X) * ir, (t * d.Y + rel.Y) * ir, (t * d.Z + rel.Z) * ir);
                return;
            }
        }
        if (0f < c)
        {
            res.Frac = float.MaxValue;
            return;
        }
        res.Frac = 0f;
        res.Pt = s;
        res.Flag = true;
        if (1.1920929e-07f < q)
        {
            var i = 1f / MathF.Sqrt(q);
            res.N = new V(i * rel.X, i * rel.Y, i * rel.Z);
            return;
        }
        res.N = new V(0f, 0f, 1f);
    }

    /// <summary>18008d5b0: a unit vector perpendicular to u.</summary>
    private static V Perp(V u)
    {
        var o = new V((1f - u.Z) * (u.Y * u.Y - 0f) + u.Z, 0f, -u.X);
        Normalize(ref o);
        var k = o.Z * u.Z + u.Y * o.Y + o.X * u.X;
        o = new V(o.X - k * u.X, o.Y - u.Y * k, o.Z - u.Z * k);
        Normalize(ref o);
        return o;
    }
}
