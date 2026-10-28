using System.Numerics;
using Source2.Compiler.Physics;

namespace Source2.Compiler.Simulation;

/// <summary>
/// vphysics2's sphere cast against a mesh (the mesh shape's vfn 0x20, 180242750 -> 180247c30): the BVH
/// walk with the swept sphere, and per triangle the feature-walking sweep (1803157c0 / 180315820 and
/// the vertex, edge and face sweeps under it). Transcribed from vphysics2 20260924 (identical in
/// 20261015); sums keep Valve's association. The start and delta are in the shape's frame (the
/// broadphase 1801da640 moves them there) and the mesh is scaled by the shape's scale.
/// </summary>
public static class SphereCast
{
    /// <summary>
    /// The cast's result as 180247c30 writes it: the fraction (+0), the start-solid byte (+0x30) and
    /// the hit's normal in the shape's frame (+0x10 .. +0x18: the best triangle's sweep normal, its
    /// +0xc0). A start inside a closed mesh (18024ac40) gives fraction 0, start-solid and a normal from
    /// 18016f710, which is not ported (NaN here).
    /// </summary>
    public readonly record struct Hit(bool Found, float Fraction, bool StartSolid, Vector3 Normal = default);

    private const float Eps = 1.1920929e-07f;

    /// <summary>The triangle sweep's state (the 0xd8-byte object 1803157c0 fills).</summary>
    private sealed class Tri
    {
        // +0x00 start, +0x10 end (relative to v0), +0x20 e0 = v1 - v0, +0x30 e1 = v2 - v1, +0x40 e2 = v0 - v2
        public float[] F = new float[0x38];
        public byte Flags;      // +0xb8: features visited (1, 2, 4 vertices; 8, 0x10, 0x20 edges; 0x40 face)
        public bool StartSolid; // +0xd4
        public float Radius;    // +0xd0
    }

    // F indices (floats): start 0..3, end 4..7, e0 8..11, e1 12..15, e2 16..19, n 20..23, m0 24..27,
    // m1 28..31, m2 32..35, contact normal 36..39, contact distance 40, margin 41, e0.e0 42,
    // e1.e0 43, e2.e1 44, e2.e2 45; output normal 48..51.
    private const int N = 20, M0 = 24, M1 = 28, M2 = 32, CN = 36, CD = 40, Margin = 41, E00 = 42, E10 = 43, E21 = 44, E22 = 45, ON = 48;

    /// <summary>1803157c0 / 18033a740: the triangle (v0, v1, v2), the margin radius + 1/32.</summary>
    private static Tri Setup(float radius, Vector3 v0, Vector3 v1, Vector3 v2)
    {
        var t = new Tri { Radius = radius };
        var f = t.F;
        f[Margin] = radius + 0.03125f;
        f[8] = v1.X - v0.X;
        f[9] = v1.Y - v0.Y;
        f[10] = v1.Z - v0.Z;
        f[11] = 0f;
        float e1x = v2.X - v1.X, e1y = v2.Y - v1.Y, e1z = v2.Z - v1.Z;
        float e0x = f[8], e0y = f[9], e0z = f[10];
        f[12] = e1x;
        f[13] = e1y;
        f[14] = e1z;
        f[15] = 0f;
        f[16] = v0.X - v2.X;
        f[17] = v0.Y - v2.Y;
        f[18] = v0.Z - v2.Z;
        f[19] = 0f;
        var cx = e1z * e0y - e1y * e0z;
        var cy = e1x * e0z - e1z * e0x;
        var cz = e1y * e0x - e1x * e0y;
        var len = MathF.Sqrt(cx * cx + cy * cy + cz * cz);
        float nx = 0f, ny = 0f, nz = 0f;
        if (0f < len)
        {
            nx = cx / len;
            ny = cy / len;
            nz = cz / len;
        }
        f[N] = nx;
        f[N + 1] = ny;
        f[N + 2] = nz;
        f[N + 3] = 0f;
        f[M0] = nz * e0y - ny * e0z;
        f[M0 + 1] = nx * e0z - nz * e0x;
        f[M0 + 2] = ny * e0x - nx * e0y;
        f[M1] = nz * e1y - ny * e1z;
        f[M1 + 1] = nx * e1z - nz * e1x;
        f[M1 + 2] = ny * e1x - nx * e1y;
        float e2x = f[16], e2y = f[17], e2z = f[18];
        f[M2] = nz * e2y - ny * e2z;
        f[M2 + 1] = nx * e2z - nz * e2x;
        f[M2 + 2] = ny * e2x - nx * e2y;
        f[E00] = e0y * e0y + e0x * e0x + e0z * e0z;
        f[E10] = e1y * e0y + e1x * e0x + e1z * e0z;
        f[E22] = e2y * e2y + e2x * e2x + e2z * e2z;
        f[E21] = e2y * e1y + e2x * e1x + e2z * e1z;
        return t;
    }

    private static float Dot(float[] f, int a, float x, float y, float z) => y * f[a + 1] + x * f[a] + z * f[a + 2];

    /// <summary>180315820: the region of the start picks the feature to sweep from.</summary>
    private static float Sweep(Tri t, Vector4 p, Vector4 e)
    {
        var f = t.F;
        t.Flags = 0;
        f[0] = p.X;
        f[1] = p.Y;
        f[2] = p.Z;
        f[3] = p.W;
        f[4] = e.X;
        f[5] = e.Y;
        f[6] = e.Z;
        f[7] = e.W;
        float px = p.X, py = p.Y, pz = p.Z;
        if (0f < Dot(f, M0, px, py, pz))
        {
            var t0 = Dot(f, 8, px, py, pz);
            if (0f < t0)
            {
                if (t0 - f[E00] <= 0f)
                    return EntryE0(t);
                var t1 = Dot(f, 12, px, py, pz);
                if (t1 - f[E10] <= 0f)
                    return EntryV1(t);
                if (t1 + f[E21] <= 0f)
                    return EntryE1(t);
                return EntryV2(t);
            }
        }
        else
        {
            float qx = px - f[8], qy = py - f[9], qz = pz - f[10];
            if (0f < Dot(f, M1, qx, qy, qz))
            {
                var t1 = Dot(f, 12, px, py, pz);
                if (t1 - f[E10] <= 0f)
                    return EntryV1(t);
                if (t1 + f[E21] <= 0f)
                    return EntryE1(t);
            }
            else if (!(0f < Dot(f, M2, px, py, pz)))
                return EntryFace(t);
        }
        var t2 = Dot(f, 16, px, py, pz);
        if (t2 + f[E22] <= 0f)
            return EntryV2(t);
        if (t2 <= 0f)
            return EntryE2(t);
        return EntryV0(t);
    }

    private static void Advance(float[] f, float s)
    {
        var r = 1f - s;
        f[0] = s * f[4] + r * f[0];
        f[1] = s * f[5] + r * f[1];
        f[2] = s * f[6] + r * f[2];
        f[3] = s * f[7] + r * f[3];
    }

    private static bool Contact(Tri t, float s)
    {
        if (!(s == 0f && t.F[CD] <= t.Radius))
            return false;
        t.StartSolid = true;
        CopyNormal(t);
        return true;
    }

    private static void CopyNormal(Tri t)
    {
        var f = t.F;
        f[ON] = f[CN];
        f[ON + 1] = f[CN + 1];
        f[ON + 2] = f[CN + 2];
        f[ON + 3] = f[CN + 3];
    }

    /// <summary>The check the entry layer ends with: the tighter margin (radius + 0.028125) from the same feature must also hit.</summary>
    private static float Confirm(Tri t, byte clear, Func<Tri, float> plain, float s)
    {
        t.Flags &= (byte)~clear;
        CopyNormal(t);
        t.F[Margin] = t.Radius + 0.028125f;
        var r = plain(t);
        return r != 1f ? s : 1f;
    }

    private static float Combine(float inner, float s) => 1f - (1f - inner) * (1f - s);

    // ---- vertex and edge sweeps (the plain layer) ----

    /// <summary>180339a30: the swept sphere against the vertex at the origin of q; exits through the two adjacent edges' planes.</summary>
    private static (float S, int Code) VertexSweep(Tri t, Vector4 q, Vector4 qe, int ea, int eb, byte fa, byte fb)
    {
        var f = t.F;
        var m = f[Margin];
        var qq = q.X * q.X + q.Y * q.Y + q.Z * q.Z;
        var c = qq - m * m;
        if (c <= 0f)
        {
            var dist = MathF.Sqrt(qq);
            if (qq <= 1e-08f)
            {
                f[CN] = f[N];
                f[CN + 1] = f[N + 1];
                f[CN + 2] = f[N + 2];
                f[CN + 3] = f[N + 3];
            }
            else
            {
                f[CN] = q.X / dist;
                f[CN + 1] = q.Y / dist;
                f[CN + 2] = q.Z / dist;
                f[CN + 3] = q.W / dist;
            }
            f[CD] = dist;
            return (0f, 0);
        }
        float dx = f[4] - f[0], dy = f[5] - f[1], dz = f[6] - f[2], dw = f[7] - f[3];
        var dd = dx * dx + dy * dy + dz * dz;
        if (dd < 1e-10f)
            return (1f, 0);
        var s = 1f;
        var res = (1f, 0);
        var b = dy * q.Y + dx * q.X + dz * q.Z;
        if (b < 0f)
        {
            var disc = b * b - dd * c;
            if (0f <= disc)
            {
                var root = (-b - MathF.Sqrt(disc)) / dd;
                if (root < 0f)
                    return (1f, 0);
                if (root < 1f)
                {
                    float hx = root * dx + q.X, hy = root * dy + q.Y, hz = root * dz + q.Z, hw = root * dw + q.W;
                    var h2 = hx * hx + hy * hy + hz * hz;
                    var gap = m * m - h2;
                    s = root;
                    if (m * 0.001f < gap)
                    {
                        var r = b / dd + root;
                        r = r * r + gap / dd;
                        r = -(b / dd) - MathF.Sqrt(r);
                        if (0f < r && r < root)
                        {
                            hx = r * dx + q.X;
                            hy = r * dy + q.Y;
                            hz = r * dz + q.Z;
                            hw = r * dw + q.W;
                            h2 = hy * hy + hx * hx + hz * hz;
                            s = r;
                        }
                    }
                    var dist = MathF.Sqrt(h2);
                    if (dist <= Eps)
                    {
                        var l = MathF.Sqrt(dd);
                        f[CN] = dx / -l;
                        f[CN + 1] = dy / -l;
                        f[CN + 2] = dz / -l;
                        f[CN + 3] = dw / -l;
                    }
                    else
                    {
                        f[CN] = hx / dist;
                        f[CN + 1] = hy / dist;
                        f[CN + 2] = hz / dist;
                        f[CN + 3] = hw / dist;
                    }
                    f[CD] = dist;
                    res = (s, 0);
                }
            }
        }
        if ((fa & t.Flags) == 0)
        {
            var a = qe.X * f[ea] + f[ea + 1] * qe.Y + f[ea + 2] * qe.Z;
            if (a < -Eps)
            {
                var bq = q.X * f[ea] + f[ea + 1] * q.Y + f[ea + 2] * q.Z;
                var bb = 0f;
                if (0f <= bq)
                    bb = bq;
                var cc = bb - a;
                if (bb < cc * s)
                {
                    s = bb / cc;
                    res = (s, 1);
                }
            }
        }
        if ((fb & t.Flags) == 0)
        {
            var a = qe.X * f[eb] + qe.Y * f[eb + 1] + qe.Z * f[eb + 2];
            if (Eps < a)
            {
                var bb = q.X * f[eb] + q.Y * f[eb + 1] + q.Z * f[eb + 2];
                if (0f <= bb)
                    bb = 0f;
                var cc = bb - a;
                if (cc * s < bb)
                {
                    s = bb / cc;
                    res = (s, 2);
                }
            }
        }
        if (s * dd <= b)
            res = (1f, 0);
        return res;
    }

    /// <summary>180339fc0: the swept sphere against the edge from the origin of q along <paramref name="edge"/>.</summary>
    private static (float S, int Code) EdgeSweep(Tri t, Vector4 q, int edge, float lenSq, int plane, byte fa, byte fb)
    {
        var f = t.F;
        var len = MathF.Sqrt(lenSq);
        var m = f[Margin];
        float nx = f[N], ny = f[N + 1], nz = f[N + 2], nw = f[N + 3];
        float dx = f[4] - f[0], dy = f[5] - f[1], dz = f[6] - f[2];
        float ux = f[plane] / len, uy = f[plane + 1] / len, uz = f[plane + 2] / len, uw = f[plane + 3] / len;
        var dn = ny * dy + nx * dx + nz * dz;
        var du = uy * dy + ux * dx + uz * dz;
        var qn = ny * q.Y + nx * q.X + nz * q.Z;
        var qu = uy * q.Y + ux * q.X + uz * q.Z;
        var d2 = qu * qu + qn * qn;
        var c = d2 - m * m;
        if (c <= 0f)
        {
            float cx = nx * qn + qu * ux, cy = ny * qn + qu * uy, cz = nz * qn + qu * uz, cw = nw * qn + qu * uw;
            if (1e-12f < d2)
            {
                var dist = MathF.Sqrt(d2);
                f[CD] = dist;
                f[CN] = cx / dist;
                f[CN + 1] = cy / dist;
                f[CN + 2] = cz / dist;
                f[CN + 3] = cw / dist;
                return (0f, 0);
            }
            if (0f <= dn)
            {
                nx = 0f - nx;
                ny = 0f - ny;
                nz = 0f - nz;
                nw = 0f - nw;
            }
            f[CN] = nx;
            f[CN + 1] = ny;
            f[CN + 2] = nz;
            f[CN + 3] = nw;
            f[CD] = 0f;
            return (0f, 0);
        }
        var b = qu * du + qn * dn;
        float a2;
        if (0f <= b || (a2 = du * du + dn * dn) < Eps)
            return (1f, 0);
        float wx = f[edge] / len, wy = f[edge + 1] / len, wz = f[edge + 2] / len;
        var flags = t.Flags;
        var s = 1f;
        var res = (1f, 0);
        var dw = dx * wx + dy * wy + dz * wz;
        var qw = q.Y * wy + q.X * wx + q.Z * wz;
        float num;
        int code;
        if ((fa & flags) != 0 || -Eps <= dw + qw)
        {
            if ((fb & flags) != 0 || dw + qw <= len)
                goto Planes;
            code = 2;
            num = len - qw;
        }
        else
        {
            dw = -dw;
            code = 1;
            num = qw;
        }
        s = num / dw;
        res = (s, code);
        if (s <= Eps)
        {
            s = Eps;
            res = (Eps, code);
        }
    Planes:
        if ((flags & 0x40) == 0 && du * s + qu < -Eps)
        {
            s = -qu / du;
            res = (s, 3);
        }
        if (s <= Eps)
        {
            s = Eps;
            res = (Eps, res.Item2);
        }
        var disc = b * b - a2 * c;
        if (disc <= b * -1e-06f * b)
        {
            if (-b <= s * a2)
                res = (1f, 0);
        }
        else
        {
            var bh = b / a2;
            float root;
            if (disc <= 0f)
                root = -bh;
            else
            {
                root = (-b - MathF.Sqrt(disc)) / a2;
                if (root <= 0f)
                {
                    f[CD] = m;
                    f[CN] = nx * qn + ux * qu;
                    f[CN + 1] = ny * qn + uy * qu;
                    f[CN + 2] = nz * qn + uz * qu;
                    f[CN + 3] = nw * qn + uw * qu;
                    return (0f, 0);
                }
            }
            var hn = qn + dn * root;
            var hu = qu + du * root;
            var h2 = hu * hu + hn * hn;
            var gap = m * m - h2;
            var g = (root + bh) * (root + bh) + gap / a2;
            var sq = 0f;
            if (0f < g)
                sq = MathF.Sqrt(g);
            var r2 = -bh - sq;
            var best = root;
            if (0f < r2)
            {
                hn = qn + dn * r2;
                hu = qu + du * r2;
                h2 = hn * hn + hu * hu;
                best = r2;
            }
            if (best <= s + Eps)
            {
                float cx = nx * hn + ux * hu, cy = ny * hn + uy * hu, cz = nz * hn + uz * hu, cw = nw * hn + uw * hu;
                var dist = MathF.Sqrt(h2);
                if (0f <= gap || dist <= m * 1.0001f + Eps || (2e-05f <= disc && root <= s + 2e-05f && dist < m * 1.003f))
                {
                    if (Eps <= dist)
                    {
                        f[CN] = cx / dist;
                        f[CN + 1] = cy / dist;
                        f[CN + 2] = cz / dist;
                        f[CN + 3] = cw / dist;
                    }
                    else
                    {
                        f[CN] = ux;
                        f[CN + 1] = uy;
                        f[CN + 2] = uz;
                        f[CN + 3] = uw;
                    }
                    f[CD] = m;
                    return (best, 0);
                }
            }
        }
        return res;
    }

    private static Vector4 Start(float[] f) => new(f[0], f[1], f[2], f[3]);
    private static Vector4 End(float[] f) => new(f[4], f[5], f[6], f[7]);
    private static Vector4 Sub(Vector4 a, float[] f, int o) => new(a.X - f[o], a.Y - f[o + 1], a.Z - f[o + 2], a.W - f[o + 3]);
    private static Vector4 AddE(float[] f, int o, Vector4 a) => new(f[o] + a.X, f[o + 1] + a.Y, f[o + 2] + a.Z, f[o + 3] + a.W);

    private static (float, int) SweepV0(Tri t)
    {
        t.Flags |= 1;
        return VertexSweep(t, Start(t.F), End(t.F), 16, 8, 0x20, 8);
    }

    private static (float, int) SweepV1(Tri t)
    {
        t.Flags |= 2;
        return VertexSweep(t, Sub(Start(t.F), t.F, 8), Sub(End(t.F), t.F, 8), 8, 12, 8, 0x10);
    }

    private static (float, int) SweepV2(Tri t)
    {
        t.Flags |= 4;
        return VertexSweep(t, AddE(t.F, 16, Start(t.F)), AddE(t.F, 16, End(t.F)), 12, 16, 0x10, 0x20);
    }

    private static (float, int) SweepE0(Tri t)
    {
        t.Flags |= 8;
        return EdgeSweep(t, Start(t.F), 8, t.F[E00], M0, 1, 2);
    }

    private static (float, int) SweepE1(Tri t)
    {
        t.Flags |= 0x10;
        var f = t.F;
        var l2 = f[12] * f[12] + f[13] * f[13] + f[14] * f[14];
        return EdgeSweep(t, Sub(Start(f), f, 8), 12, l2, M1, 2, 4);
    }

    private static (float, int) SweepE2(Tri t)
    {
        t.Flags |= 0x20;
        return EdgeSweep(t, AddE(t.F, 16, Start(t.F)), 16, t.F[E22], M2, 4, 1);
    }

    // ---- the plain layer (180338ef0 and friends) ----

    private static float PV0(Tri t)
    {
        var (s, code) = SweepV0(t);
        if (code == 0)
            return s;
        Advance(t.F, s);
        var inner = code == 1 ? PE2(t) : PE0(t);
        return 1f - (1f - inner) * (1f - s);
    }

    private static float PV1(Tri t)
    {
        var (s, code) = SweepV1(t);
        if (code == 0)
            return s;
        Advance(t.F, s);
        var inner = code == 1 ? PE0(t) : PE1(t);
        return 1f - (1f - inner) * (1f - s);
    }

    private static float PV2(Tri t)
    {
        var (s, code) = SweepV2(t);
        if (code == 0)
            return s;
        Advance(t.F, s);
        var inner = code == 1 ? PE1(t) : PE2(t);
        return 1f - (1f - inner) * (1f - s);
    }

    private static float PEdge((float S, int Code) r, Tri t, Func<Tri, float> a, Func<Tri, float> b)
    {
        var (s, code) = r;
        if (code == 0)
            return s;
        Advance(t.F, s);
        var inner = code == 1 ? a(t) : code == 2 ? b(t) : PFace(t);
        return Combine(inner, s);
    }

    private static float PE0(Tri t) => PEdge(SweepE0(t), t, PV0, PV1);
    private static float PE1(Tri t) => PEdge(SweepE1(t), t, PV1, PV2);
    private static float PE2(Tri t) => PEdge(SweepE2(t), t, PV2, PV0);

    /// <summary>
    /// The face's slab test against the margin and the exits through the edge planes, shared by
    /// 180339120 (plain), 180314620 and 180314170 (entry): the crossing fraction and which edge plane
    /// the sweep leaves through first (3 edge 2, 1 edge 0, then edge 1 checked last), or -1 when the
    /// start already lies in the slab, or 2 for a miss.
    /// </summary>
    private static (float S, int Exit, bool InSlab) FacePlanes(Tri t)
    {
        var f = t.F;
        var flags = t.Flags;
        t.Flags = (byte)(flags | 0x40);
        float nx = f[N], ny = f[N + 1], nz = f[N + 2], nw = f[N + 3];
        float px = f[0], py = f[1], pz = f[2];
        float ex = f[4], ey = f[5], ez = f[6];
        var s = py * ny + px * nx + pz * nz;
        var m = f[Margin];
        var en = ey * ny + ex * nx + ez * nz;
        float t0;
        if (s <= m)
        {
            if (-m <= s)
            {
                if (s <= 0f)
                {
                    s = -s;
                    f[CN] = 0f - nx;
                    f[CN + 1] = 0f - ny;
                    f[CN + 2] = 0f - nz;
                    f[CN + 3] = 0f - nw;
                }
                else
                {
                    f[CN] = nx;
                    f[CN + 1] = ny;
                    f[CN + 2] = nz;
                    f[CN + 3] = nw;
                }
                f[CD] = s;
                return (0f, -1, true);
            }
            if (en <= -m)
                return (1f, 2, false);
            t0 = m + s;
            f[CN] = 0f - nx;
            f[CN + 1] = 0f - ny;
            f[CN + 2] = 0f - nz;
            f[CN + 3] = 0f - nw;
        }
        else
        {
            if (m <= en)
                return (1f, 2, false);
            t0 = s - m;
            f[CN] = nx;
            f[CN + 1] = ny;
            f[CN + 2] = nz;
            f[CN + 3] = nw;
        }
        f[CD] = m;
        var exit = 0;
        var fr = t0 / (s - en);
        if ((flags & 0x20) == 0)
        {
            var a = ey * f[M2 + 1] + ex * f[M2] + ez * f[M2 + 2];
            if (Eps < a)
            {
                var b = py * f[M2 + 1] + px * f[M2] + pz * f[M2 + 2];
                a = b - a;
                if (a * fr < b)
                {
                    exit = 3;
                    fr = b / a;
                }
            }
        }
        if ((flags & 8) == 0)
        {
            var a = ey * f[M0 + 1] + ex * f[M0] + ez * f[M0 + 2];
            if (Eps < a)
            {
                var b = py * f[M0 + 1] + px * f[M0] + pz * f[M0 + 2];
                a = b - a;
                if (a * fr < b)
                {
                    exit = 1;
                    fr = b / a;
                }
            }
        }
        if ((flags & 0x10) == 0)
        {
            var a = (ey - f[9]) * f[M1 + 1] + (ex - f[8]) * f[M1] + (ez - f[10]) * f[M1 + 2];
            if (Eps < a)
            {
                var b = (py - f[9]) * f[M1 + 1] + (px - f[8]) * f[M1] + (pz - f[10]) * f[M1 + 2];
                a = b - a;
                if (!(b <= a * fr))
                    return (b / a, 4, false);
            }
        }
        return (fr, exit, false);
    }

    private static void FaceAdvance(float[] f, float fr)
    {
        var r = 1f - fr;
        float px = f[0], py = f[1], pz = f[2], pw = f[3];
        f[0] = f[4] * fr + px * r;
        f[1] = f[5] * fr + py * r;
        f[2] = f[6] * fr + pz * r;
        f[3] = f[7] * fr + pw * r;
    }

    /// <summary>180339120.</summary>
    private static float PFace(Tri t)
    {
        var (fr, exit, inSlab) = FacePlanes(t);
        if (inSlab)
            return 0f;
        if (exit == 2)
            return 1f;
        if (exit == 0)
            return fr;
        var r = 1f - fr;
        FaceAdvance(t.F, fr);
        var inner = exit switch { 1 => PE0(t), 3 => PE2(t), _ => PE1(t) };
        return 1f - (1f - inner) * r;
    }

    // ---- the entry layer (margin radius + 1/32, with the start-contact checks) ----

    private static float EntryVertex((float S, int Code) r, Tri t, byte clear, Func<Tri, float> plain, Func<Tri, float> exitA,
                                     Func<Tri, float> exitB, bool contact)
    {
        var (s, code) = r;
        if (code != 0)
        {
            Advance(t.F, s);
            var inner = code == 1 ? exitA(t) : exitB(t);
            return 1f - (1f - inner) * (1f - s);
        }
        if (contact && Contact(t, s))
            return 0f;
        return Confirm(t, clear, plain, s);
    }

    private static float EntryEdge((float S, int Code) r, Tri t, byte clear, Func<Tri, float> plain, Func<Tri, float> a, Func<Tri, float> b,
                                   bool contact)
    {
        var (s, code) = r;
        if (code == 0)
        {
            if (contact && Contact(t, s))
                return 0f;
            return Confirm(t, clear, plain, s);
        }
        Advance(t.F, s);
        var inner = code == 1 ? a(t) : code == 2 ? b(t) : AFace(t);
        return Combine(inner, s);
    }

    private static float EntryV0(Tri t) => EntryVertex(SweepV0(t), t, 1, PV0, AE2, AE0, true);
    private static float EntryV1(Tri t) => EntryVertex(SweepV1(t), t, 2, PV1, AE0, AE1, true);
    private static float EntryV2(Tri t) => EntryVertex(SweepV2(t), t, 4, PV2, AE1, AE2, true);
    private static float EntryE0(Tri t) => EntryEdge(SweepE0(t), t, 8, PE0, AV0, AV1, true);
    private static float EntryE1(Tri t) => EntryEdge(SweepE1(t), t, 0x10, PE1, AV1, AV2, true);
    private static float EntryE2(Tri t) => EntryEdge(SweepE2(t), t, 0x20, PE2, AV2, AV0, true);

    // 180313cf0, 180314bf0, 180315290, 180313fe0, 180314f40, 1803155b0: the same without the contact check.
    private static float AV0(Tri t) => EntryVertex(SweepV0(t), t, 1, PV0, AE2, AE0, false);
    private static float AV1(Tri t) => EntryVertex(SweepV1(t), t, 2, PV1, AE0, AE1, false);
    private static float AV2(Tri t) => EntryVertex(SweepV2(t), t, 4, PV2, AE1, AE2, false);
    private static float AE0(Tri t) => EntryEdge(SweepE0(t), t, 8, PE0, AV0, AV1, false);
    private static float AE1(Tri t) => EntryEdge(SweepE1(t), t, 0x10, PE1, AV1, AV2, false);
    private static float AE2(Tri t) => EntryEdge(SweepE2(t), t, 0x20, PE2, AV2, AV0, false);

    /// <summary>180314170 (entry, with the contact check at the plain radius) and 180314620.</summary>
    private static float EntryFace(Tri t) => FaceEntry(t, true);

    private static float AFace(Tri t) => FaceEntry(t, false);

    private static float FaceEntry(Tri t, bool contact)
    {
        var f = t.F;
        if (contact)
        {
            var flags0 = t.Flags;
            t.Flags = (byte)(flags0 | 0x40);
            float nx = f[N], ny = f[N + 1], nz = f[N + 2], nw = f[N + 3];
            var s0 = f[1] * ny + f[0] * nx + f[2] * nz;
            if (MathF.Abs(s0) <= t.Radius)
            {
                if (s0 < 0f)
                {
                    nx = 0f - nx;
                    ny = 0f - ny;
                    nz = 0f - nz;
                    nw = 0f - nw;
                }
                f[ON] = nx;
                f[ON + 1] = ny;
                f[ON + 2] = nz;
                f[ON + 3] = nw;
                t.StartSolid = true;
                return 0f;
            }
            t.Flags = flags0;
        }
        var flags = t.Flags;
        var (fr, exit, inSlab) = FacePlanes(t);
        if (inSlab)
        {
            t.Flags = (byte)(flags & 0xbf);
            CopyNormal(t);
            f[Margin] = t.Radius + 0.028125f;
            return PFace(t) != 1f ? 0f : 1f;
        }
        if (exit == 2)
            return 1f;
        if (exit == 0)
        {
            // 180315750
            t.Flags = (byte)(t.Flags & 0xbf);
            CopyNormal(t);
            f[Margin] = t.Radius + 0.028125f;
            return PFace(t) == 1f ? 1f : fr;
        }
        var r = 1f - fr;
        FaceAdvance(f, fr);
        var inner = exit switch { 1 => AE0(t), 3 => AE2(t), _ => AE1(t) };
        return 1f - (1f - inner) * r;
    }

    // ---- the mesh ----

    /// <summary>
    /// 180247c30: the sphere of <paramref name="radius"/> from <paramref name="start"/> by
    /// <paramref name="delta"/> (shape frame) against the mesh at <paramref name="scale"/>, up to
    /// <paramref name="maxFraction"/>; the least fraction over the triangles the walk reaches.
    /// </summary>
    public static Hit Mesh(RnMesh mesh, Vector3 scale, Vector3 start, Vector3 delta, float radius, float maxFraction)
    {
        var best = maxFraction;
        var startSolid = false;
        var normal = Vector3.Zero;
        if (mesh.Nodes.Length == 0)
            return default;
        var dirLen = MathF.Sqrt(delta.Z * delta.Z + delta.Y * delta.Y + delta.X * delta.X);
        Vector3 dir;
        if (dirLen < 1e-17f || 1e+17f < dirLen)
        {
            if (dirLen == 0f)
                dir = new Vector3(1f, 0f, 0f);
            else
            {
                double x = delta.X, y = delta.Y, z = delta.Z;
                var l = Math.Sqrt(y * y + x * x + z * z);
                var inv = 1.0 / l;
                dir = new Vector3((float)(inv * x), (float)(inv * y), (float)(inv * z));
                if (l == 0.0)
                    dir = new Vector3(1f, 0f, 0f);
            }
        }
        else
        {
            var inv = 1f / dirLen;
            dir = new Vector3(delta.X * inv, delta.Y * inv, delta.Z * inv);
        }
        var r2 = radius * radius;
        var startUnscaled = new Vector3(start.X / scale.X, start.Y / scale.Y, start.Z / scale.Z);
        var end0 = new Vector3(maxFraction * delta.X + start.X, maxFraction * delta.Y + start.Y, maxFraction * delta.Z + start.Z);
        var lo = Vector3.Min(start, end0);
        var hi = Vector3.Max(start, end0);
        var stack = new List<int>();
        var node = 0;
        while (true)
        {
            var n = mesh.Nodes[node];
            float mxs = n.Max.X * scale.X, mys = n.Max.Y * scale.Y, mzs = n.Max.Z * scale.Z;
            float cx = (n.Min.X * scale.X + mxs) * 0.5f, cy = (n.Min.Y * scale.Y + mys) * 0.5f, cz = (n.Min.Z * scale.Z + mzs) * 0.5f;
            float ex = mxs - cx, ey = mys - cy, ez = mzs - cz;
            var clx = MathF.Max(lo.X - ex, MathF.Min(ex + hi.X, cx)) - cx;
            var cly = MathF.Max(lo.Y - ey, MathF.Min(ey + hi.Y, cy)) - cy;
            var clz = MathF.Max(lo.Z - ez, MathF.Min(ez + hi.Z, cz)) - cz;
            var visit = false;
            if (clx * clx + cly * cly + clz * clz <= r2)
            {
                var rx = start.X - cx;
                var ry = start.Y - cy;
                var rz = start.Z - cz;
                var s1 = MathF.Abs(rx * dir.Z - rz * dir.X) - (ex * MathF.Abs(dir.Z) + ez * MathF.Abs(dir.X)) <= radius;
                var s0 = MathF.Abs(rz * dir.Y - ry * dir.Z) - (ez * MathF.Abs(dir.Y) + ey * MathF.Abs(dir.Z)) <= radius;
                var s2 = MathF.Abs(ry * dir.X - rx * dir.Y) - (ey * MathF.Abs(dir.X) + ex * MathF.Abs(dir.Y)) <= radius;
                visit = s0 && s1 && s2;
            }
            if (visit)
            {
                var type = n.Children >> 30;
                var count = (int)(n.Children & 0x3fffffff);
                if (type != 3)
                {
                    var child1Max = mesh.Nodes[node + 1].Max;
                    var axisValue = type switch
                    {
                        0 => child1Max.X - startUnscaled.X,
                        1 => child1Max.Y - startUnscaled.Y,
                        _ => child1Max.Z - startUnscaled.Z,
                    };
                    if (axisValue <= 0f)
                    {
                        stack.Add(node + 1);
                        node += count;
                    }
                    else
                    {
                        stack.Add(node + count);
                        node += 1;
                    }
                    continue;
                }
                for (var k = 0; k < count; k++)
                {
                    var (ia, ib, ic) = mesh.Triangles[(int)n.TriangleOffset + k];
                    Vector3 V(int i) => new(mesh.Vertices[i].X * scale.X, mesh.Vertices[i].Y * scale.Y, mesh.Vertices[i].Z * scale.Z);
                    var v0 = V(ia);
                    var v1 = V(ib);
                    var v2 = V(ic);
                    var tmin = Vector3.Min(v0, Vector3.Min(v1, v2));
                    var tmax = Vector3.Max(v0, Vector3.Max(v1, v2));
                    if (hi.X < tmin.X - 0.03125f - radius || hi.Y < tmin.Y - 0.03125f - radius || hi.Z < tmin.Z - 0.03125f - radius
                        || tmax.X + 0.03125f + radius < lo.X || tmax.Y + 0.03125f + radius < lo.Y || tmax.Z + 0.03125f + radius < lo.Z)
                        continue;
                    var tri = Setup(radius, v0, v1, v2);
                    var p = new Vector4(start.X - v0.X, start.Y - v0.Y, start.Z - v0.Z, 0f);
                    var e = new Vector4(delta.X + p.X, delta.Y + p.Y, delta.Z + p.Z, 0f);
                    var fr = Sweep(tri, p, e);
                    if (fr < best)
                    {
                        best = fr;
                        startSolid = tri.StartSolid;
                        normal = new Vector3(tri.F[ON], tri.F[ON + 1], tri.F[ON + 2]);
                        if (fr == 0f && tri.StartSolid)
                            goto Done;
                        var at = new Vector3(fr * delta.X + start.X, fr * delta.Y + start.Y, fr * delta.Z + start.Z);
                        lo = Vector3.Min(start, at);
                        hi = Vector3.Max(start, at);
                    }
                }
            }
            if (stack.Count == 0)
                break;
            node = stack[^1];
            stack.RemoveAt(stack.Count - 1);
        }
    Done:
        if ((mesh.Flags & 1) != 0 && best != 0f && BoxCast.InsideClosed(mesh, scale, start))
            return new Hit(true, 0f, true, new Vector3(float.NaN));
        if (!(best < maxFraction))
            return default;
        return new Hit(true, best, startSolid, normal);
    }
}
