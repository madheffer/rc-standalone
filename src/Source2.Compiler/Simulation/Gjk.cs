using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Source2.Compiler.Simulation;

/// <summary>
/// A convex shape as GJK sees it (FUN_18012e550, 0x40 bytes in Valve): a
/// point cloud scaled uniformly, or a box given by its six scaled bounds
/// (a hull flagged as a box, whose support is picked by the sign bits of the
/// direction), and a radius (1/16 for a hull, FUN_180251480).
/// </summary>
public sealed class GjkProxy
{
    public Vec3[] Vertices = [];
    public int Count;
    public float Scale = 1f;
    public bool Box;

    /// <summary>+0x18: max.x, min.x, max.y, min.y, max.z, min.z, all scaled.</summary>
    public readonly float[] Bounds = new float[6];

    public float Radius;

    /// <summary>FUN_18012e550 with the hull radius FUN_180251480 gives.</summary>
    public static GjkProxy OfHull(Vec3[] vertices, uint flags, Vec3 min, Vec3 max, float scale, float radius = 0.0625f)
    {
        var p = new GjkProxy { Vertices = vertices, Count = vertices.Length, Scale = scale, Radius = radius };
        if ((flags & 3) == 3)
        {
            p.Box = true;
            p.Count = 8;
            p.Bounds[0] = max.X * scale;
            p.Bounds[1] = min.X * scale;
            p.Bounds[2] = max.Y * scale;
            p.Bounds[3] = min.Y * scale;
            p.Bounds[4] = max.Z * scale;
            p.Bounds[5] = min.Z * scale;
        }
        return p;
    }

    /// <summary>The proxy's vertex <paramref name="i"/> in its own frame.</summary>
    public Vec3 Vertex(int i)
    {
        if (Box)
            return new(Bounds[i & 1], Bounds[2 + ((i >> 1) & 1)], Bounds[4 + ((i >> 2) & 1)]);
        var v = Vertices[i];
        return new(Scale * v.X, v.Y * Scale, v.Z * Scale);
    }

    /// <summary>
    /// FUN_1802eb9e0: the vertex furthest along <paramref name="d"/>. A box
    /// takes the corner of the direction's signs; a point cloud is searched
    /// in order, measured from vertex 0, the first of equals winning.
    /// </summary>
    public int Support(Vec3 d)
    {
        if (Box)
        {
            var sx = BitConverter.SingleToUInt32Bits(d.X) >> 31;
            var sy = BitConverter.SingleToUInt32Bits(d.Y) >> 31;
            var sz = BitConverter.SingleToUInt32Bits(d.Z) >> 31;
            return (int)((sz * 2 | sy) * 2 | sx);
        }
        var best = 0;
        var max = 0f;
        var v0 = Vertices[0];
        for (var i = 1; i < Count; i++)
        {
            var v = Vertices[i];
            var dot = ((v.Z - v0.Z) * d.Z + (v.Y - v0.Y) * d.Y) + (v.X - v0.X) * d.X;
            if (dot > max)
            {
                max = dot;
                best = i;
            }
        }
        return best;
    }
}

/// <summary>What GJK leaves between calls (0x2C bytes): the simplex's vertex indices and weights, its size measure, the last direction.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x2c)]
public unsafe struct GjkCache
{
    [FieldOffset(0x00)] public float Metric;
    [FieldOffset(0x04)] public int Count;
    [FieldOffset(0x08)] public fixed byte IndexA[4];
    [FieldOffset(0x0c)] public fixed byte IndexB[4];
    [FieldOffset(0x10)] public fixed float Lambda[4];
    [FieldOffset(0x20)] public Vec3 Direction;
}

/// <summary>GJK's result: the distance and the closest points.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct GjkOutput
{
    public float Distance;
    public Vec3 PointA;
    public Vec3 PointB;
}

/// <summary>A simplex vertex (0x2C bytes): the proxy vertex indices, both points and w = b - a.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x2c)]
public struct SimplexVertex
{
    [FieldOffset(0x00)] public int IndexA;
    [FieldOffset(0x04)] public int IndexB;
    [FieldOffset(0x08)] public Vec3 A;
    [FieldOffset(0x14)] public Vec3 B;
    [FieldOffset(0x20)] public Vec3 W;
}

/// <summary>The GJK simplex in vphysics2's layout (0xF0 bytes).</summary>
[StructLayout(LayoutKind.Explicit, Size = 0xf0)]
public unsafe struct Simplex
{
    [FieldOffset(0x00)] public int Count;
    [FieldOffset(0x04)] public SimplexVertex V0;
    [FieldOffset(0x30)] public SimplexVertex V1;
    [FieldOffset(0x5c)] public SimplexVertex V2;
    [FieldOffset(0x88)] public SimplexVertex V3;
    [FieldOffset(0xb4)] public fixed float Lambda[4];

    /// <summary>+0xC8: the count before the last solve; +0xCC/+0xD0 its vertex indices (bytes), for the duplicate test.</summary>
    [FieldOffset(0xc8)] public int SavedCount;
    [FieldOffset(0xcc)] public fixed byte SavedA[4];
    [FieldOffset(0xd0)] public fixed byte SavedB[4];

    [UnscopedRef]
    public ref SimplexVertex this[int i]
    {
        get
        {
            fixed (SimplexVertex* p = &V0)
                return ref *(SimplexVertex*)((byte*)p + i * 0x2c);
        }
    }
}

/// <summary>
/// vphysics2's GJK distance (FUN_1802ec220) between two convex proxies,
/// Voronoi-region simplex solver included, each float operation in the DLL's
/// order.
/// </summary>
public static unsafe class Gjk
{
    private const float Tiny = 1.17549435e-35f;

    /// <summary>A point through a frame: ((x m0 + y m3) + z m6) + t.</summary>
    private static Vec3 Apply(in RnTransform xf, Vec3 v)
    {
        ref readonly var r = ref xf.R;
        return new(
            ((v.X * r.M0 + v.Y * r.M3) + v.Z * r.M6) + xf.T.X,
            ((v.X * r.M1 + v.Y * r.M4) + v.Z * r.M7) + xf.T.Y,
            ((v.X * r.M2 + v.Y * r.M5) + v.Z * r.M8) + xf.T.Z);
    }

    /// <summary>A direction into a frame: R^T d.</summary>
    private static Vec3 ApplyInverse(in RnTransform xf, Vec3 d)
    {
        ref readonly var r = ref xf.R;
        return new(
            (r.M0 * d.X + r.M1 * d.Y) + r.M2 * d.Z,
            (r.M3 * d.X + r.M4 * d.Y) + r.M5 * d.Z,
            (r.M6 * d.X + r.M7 * d.Y) + r.M8 * d.Z);
    }

    private static Vec3 Sub(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    private static float Sqrt(float f) => MathF.Sqrt(f);

    /// <summary>FUN_1802ebd20: the simplex from the cache, or vertex 0 of each proxy when the cache no longer fits.</summary>
    public static void ReadCache(ref Simplex s, in RnTransform xfA, GjkProxy a, in RnTransform xfB, GjkProxy b, in GjkCache cache)
    {
        s.Count = cache.Count;
        var ok = true;
        for (var k = 0; k < s.Count; k++)
        {
            int ia = cache.IndexA[k], ib = cache.IndexB[k];
            if (a.Count <= ia || b.Count <= ib)
            {
                s.Count = 0;
                ok = false;
                break;
            }
            ref var v = ref s[k];
            v.IndexA = ia;
            v.IndexB = ib;
            v.A = Apply(xfA, a.Vertex(ia));
            v.B = Apply(xfB, b.Vertex(ib));
            v.W = Sub(v.B, v.A);
            s.Lambda[k] = 0f;
        }
        if (ok && s.Count > 1)
        {
            var old = cache.Metric;
            var metric = Metric(s);
            if (old + old < metric || metric < old * 0.5f || metric < 1.1920929e-07f)
                s.Count = 0;
        }
        if (s.Count != 0)
            return;
        ref var v0 = ref s.V0;
        v0.A = Apply(xfA, a.Vertex(0));
        v0.B = Apply(xfB, b.Vertex(0));
        s.Count = 1;
        v0.IndexA = 0;
        v0.IndexB = 0;
        s.Lambda[0] = 0f;
        v0.W = Sub(v0.B, v0.A);
    }

    /// <summary>FUN_180315ab0: adds a vertex, refusing one the last solve already had when asked.</summary>
    public static bool Add(ref Simplex s, int ia, Vec3 a, int ib, Vec3 b, bool checkDuplicate)
    {
        if (checkDuplicate)
            for (var k = 0; k < s.SavedCount; k++)
                if (s.SavedA[k] == (byte)ia && s.SavedB[k] == (byte)ib)
                    return false;
        ref var v = ref s[s.Count];
        v.IndexA = ia;
        v.IndexB = ib;
        v.A = a;
        v.B = b;
        v.W = Sub(b, a);
        s.Count++;
        return true;
    }

    /// <summary>
    /// FUN_180318460: remembers the simplex's indices, then reduces it to the
    /// feature closest to the origin with its barycentric weights. False for a
    /// degenerate feature.
    /// </summary>
    public static bool Solve(ref Simplex s)
    {
        s.SavedCount = s.Count;
        for (var k = 0; k < s.Count; k++)
        {
            s.SavedA[k] = (byte)s[k].IndexA;
            s.SavedB[k] = (byte)s[k].IndexB;
        }
        switch (s.Count)
        {
            case 1:
                s.Lambda[0] = 1f;
                return true;
            case 2:
                return Solve2(ref s);
            case 3:
                return Solve3(ref s);
            case 4:
                return Solve4(ref s);
            default:
                return false;
        }
    }

    /// <summary>The edge weights of X to Y: u = Y . (Y - X), v = -X . (Y - X), and |Y - X|^2.</summary>
    private static (float U, float V, float Length) Edge(Vec3 x, Vec3 y)
    {
        var ex = y.X - x.X;
        var ey = y.Y - x.Y;
        var ez = y.Z - x.Z;
        var u = (y.Y * ey + y.X * ex) + y.Z * ez;
        var v = -((x.Y * ey + x.X * ex) + x.Z * ez);
        var length = (ey * ey + ex * ex) + ez * ez;
        return (u, v, length);
    }

    private static void Keep(ref Simplex s, in SimplexVertex a)
    {
        s.Count = 1;
        s.V0 = a;
        s.Lambda[0] = 1f;
    }

    private static bool KeepEdge(ref Simplex s, in SimplexVertex a, in SimplexVertex b, float u, float v, float length)
    {
        s.Count = 2;
        s.V0 = a;
        s.V1 = b;
        if (length <= 0f)
            return false;
        s.Lambda[0] = u / length;
        s.Lambda[1] = v / length;
        return true;
    }

    private static bool Solve2(ref Simplex s)
    {
        var a = s.V0;
        var b = s.V1;
        var (u, v, length) = Edge(a.W, b.W);
        if (v <= 0f)
        {
            Keep(ref s, a);
            return true;
        }
        if (u <= 0f)
        {
            Keep(ref s, b);
            return true;
        }
        if (length <= 0f)
            return false;
        s.Lambda[0] = u / length;
        s.Lambda[1] = v / length;
        return true;
    }

    /// <summary>
    /// FUN_180315fa0 with the origin: the weights of a, b, c (twice the signed
    /// sub-areas times the normal) and |n|^2.
    /// </summary>
    private static (float U, float V, float W, float D) Triangle(Vec3 a, Vec3 b, Vec3 c)
    {
        var abx = b.X - a.X;
        var aby = b.Y - a.Y;
        var abz = b.Z - a.Z;
        var acx = c.X - a.X;
        var acy = c.Y - a.Y;
        var acz = c.Z - a.Z;
        var nx = (acz * aby) - (acy * abz);
        var ny = (acx * abz) - (acz * abx);
        var nz = (acy * abx) - (acx * aby);
        var u = ((((c.Z * b.Y) - (c.Y * b.Z)) * nx) + (((c.X * b.Z) - (c.Z * b.X)) * ny)) + (((c.Y * b.X) - (c.X * b.Y)) * nz);
        var v = ((((a.X * c.Z) - (a.Z * c.X)) * ny) + (((a.Z * c.Y) - (a.Y * c.Z)) * nx)) + (((a.Y * c.X) - (a.X * c.Y)) * nz);
        var w = ((((a.Y * b.Z) - (a.Z * b.Y)) * nx) + (((a.Z * b.X) - (a.X * b.Z)) * ny)) + (((a.X * b.Y) - (a.Y * b.X)) * nz);
        var d = (nx * nx + ny * ny) + nz * nz;
        return (u, v, w, d);
    }

    private static bool Solve3(ref Simplex s)
    {
        var a = s.V0;
        var b = s.V1;
        var c = s.V2;
        var ab = Edge(a.W, b.W);
        var bc = Edge(b.W, c.W);
        var ca = Edge(c.W, a.W);
        if (!(0f < ab.V) && !(0f < ca.U))
        {
            Keep(ref s, a);
            return true;
        }
        if (!(0f < bc.V) && !(0f < ab.U))
        {
            Keep(ref s, b);
            return true;
        }
        if (!(0f < ca.V) && !(0f < bc.U))
        {
            Keep(ref s, c);
            return true;
        }
        var t = Triangle(a.W, b.W, c.W);
        if (!(0f < t.W) && ab.U > 0f && ab.V > 0f)
            return KeepEdge(ref s, a, b, ab.U, ab.V, ab.Length);
        if (!(0f < t.U) && bc.U > 0f && bc.V > 0f)
            return KeepEdge(ref s, b, c, bc.U, bc.V, bc.Length);
        if (!(0f < t.V) && ca.U > 0f && ca.V > 0f)
            return KeepEdge(ref s, c, a, ca.U, ca.V, ca.Length);
        if (!(0f < t.D))
            return false;
        s.Lambda[0] = t.U / t.D;
        s.Lambda[1] = t.V / t.D;
        s.Lambda[2] = t.W / t.D;
        return true;
    }

    /// <summary>FUN_180315bb0 with the origin: the weights of a, b, c, d and the signed volume, all made positive by its sign.</summary>
    private static (float A, float B, float C, float D, float Volume) Tetrahedron(Vec3 a, Vec3 b, Vec3 c, Vec3 d)
    {
        var volume = ((((d.Z - a.Z) * (c.Y - a.Y)) - ((d.Y - a.Y) * (c.Z - a.Z))) * (b.X - a.X)
                      - (((d.Z - a.Z) * (b.Y - a.Y)) - ((d.Y - a.Y) * (b.Z - a.Z))) * (c.X - a.X))
                     + (((c.Z - a.Z) * (b.Y - a.Y)) - ((c.Y - a.Y) * (b.Z - a.Z))) * (d.X - a.X);
        var sign = volume < 0f || float.IsNaN(volume) ? -1f : 1f;
        var wa = ((((d.Z * c.Y) - (d.Y * c.Z)) * b.X - ((d.Z * b.Y) - (d.Y * b.Z)) * c.X) + ((c.Z * b.Y) - (c.Y * b.Z)) * d.X) * sign;
        var wb = ((((d.Y * c.Z) - (d.Z * c.Y)) * a.X - d.X * ((c.Z * a.Y) - (c.Y * a.Z))) + c.X * ((d.Z * a.Y) - (d.Y * a.Z))) * sign;
        var wc = ((a.X * ((d.Z * b.Y) - (d.Y * b.Z)) - b.X * ((d.Z * a.Y) - (d.Y * a.Z))) + d.X * ((b.Z * a.Y) - (b.Y * a.Z))) * sign;
        var wd = ((((c.Y * b.Z) - (c.Z * b.Y)) * a.X - c.X * ((b.Z * a.Y) - (b.Y * a.Z))) + b.X * ((c.Z * a.Y) - (c.Y * a.Z))) * sign;
        return (wa, wb, wc, wd, sign * volume);
    }

    private static bool KeepFace(ref Simplex s, in SimplexVertex a, in SimplexVertex b, in SimplexVertex c,
                                 (float U, float V, float W, float D) t)
    {
        s.Count = 3;
        s.V0 = a;
        s.V1 = b;
        s.V2 = c;
        if (t.D <= 0f)
            return false;
        s.Lambda[0] = t.U / t.D;
        s.Lambda[1] = t.V / t.D;
        s.Lambda[2] = t.W / t.D;
        return true;
    }

    private static bool Solve4(ref Simplex s)
    {
        var a = s.V0;
        var b = s.V1;
        var c = s.V2;
        var d = s.V3;
        var ab = Edge(a.W, b.W);
        var ac = Edge(a.W, c.W);
        var ad = Edge(a.W, d.W);
        var bc = Edge(b.W, c.W);
        var cd = Edge(c.W, d.W);
        var db = Edge(d.W, b.W);
        if (!(0f < ab.V) && !(0f < ac.V) && !(0f < ad.V))
        {
            Keep(ref s, a);
            return true;
        }
        if (!(0f < ab.U) && !(0f < db.U) && !(0f < bc.V))
        {
            Keep(ref s, b);
            return true;
        }
        if (!(0f < ac.U) && !(0f < bc.U) && !(0f < cd.V))
        {
            Keep(ref s, c);
            return true;
        }
        if (!(0f < ad.U) && !(0f < cd.U) && !(0f < db.V))
        {
            Keep(ref s, d);
            return true;
        }
        var acb = Triangle(a.W, c.W, b.W);
        var abd = Triangle(a.W, b.W, d.W);
        var adc = Triangle(a.W, d.W, c.W);
        var bcd = Triangle(b.W, c.W, d.W);
        if (!(0f < abd.W) && !(0f < acb.V) && !(ab.U <= 0f) && !(ab.V <= 0f))
            return KeepEdge(ref s, a, b, ab.U, ab.V, ab.Length);
        if (!(0f < acb.W) && !(0f < adc.V) && !(ac.U <= 0f) && !(ac.V <= 0f))
            return KeepEdge(ref s, a, c, ac.U, ac.V, ac.Length);
        if (!(0f < adc.W) && !(0f < abd.V) && !(ad.U <= 0f) && !(ad.V <= 0f))
            return KeepEdge(ref s, a, d, ad.U, ad.V, ad.Length);
        if (!(0f < acb.U) && !(0f < bcd.W) && !(bc.U <= 0f) && !(bc.V <= 0f))
            return KeepEdge(ref s, b, c, bc.U, bc.V, bc.Length);
        if (!(0f < adc.U) && !(0f < bcd.U) && !(cd.U <= 0f) && !(cd.V <= 0f))
            return KeepEdge(ref s, c, d, cd.U, cd.V, cd.Length);
        if (!(0f < abd.U) && !(0f < bcd.V) && !(db.U <= 0f) && !(db.V <= 0f))
            return KeepEdge(ref s, d, b, db.U, db.V, db.Length);
        var t = Tetrahedron(a.W, b.W, c.W, d.W);
        if (!(0f <= t.D) && !(acb.U <= 0f) && !(acb.V <= 0f) && !(acb.W <= 0f))
            return KeepFace(ref s, a, c, b, acb);
        if (!(0f <= t.C) && !(abd.U <= 0f) && !(abd.V <= 0f) && !(abd.W <= 0f))
            return KeepFace(ref s, a, b, d, abd);
        if (!(0f <= t.B) && !(adc.U <= 0f) && !(adc.V <= 0f) && !(adc.W <= 0f))
            return KeepFace(ref s, a, d, c, adc);
        if (!(0f <= t.A) && !(bcd.U <= 0f) && !(bcd.V <= 0f) && !(bcd.W <= 0f))
            return KeepFace(ref s, b, c, d, bcd);
        if (t.Volume <= 0f)
            return false;
        s.Lambda[0] = t.A / t.Volume;
        s.Lambda[1] = t.B / t.Volume;
        s.Lambda[2] = t.C / t.Volume;
        s.Lambda[3] = t.D / t.Volume;
        return true;
    }

    /// <summary>FUN_180316670: the simplex point closest to the origin, sum of weighted w.</summary>
    public static Vec3 ClosestPoint(in Simplex s)
    {
        switch (s.Count)
        {
            case 1:
                return new(s.V0.W.X * s.Lambda[0], s.V0.W.Y * s.Lambda[0], s.V0.W.Z * s.Lambda[0]);
            case 2:
                return Combine(s, 2, v => v.W);
            case 3:
                return Combine(s, 3, v => v.W);
            case 4:
                return Combine(s, 4, v => v.W);
            default:
                return default;
        }
    }

    /// <summary>(l1 p1 + l0 p0) + l2 p2 + l3 p3, the order every weighted sum here takes.</summary>
    private static Vec3 Combine(in Simplex s, int n, Func<SimplexVertex, Vec3> pick)
    {
        var p0 = pick(s.V0);
        var p1 = pick(s.V1);
        var l0 = s.Lambda[0];
        var l1 = s.Lambda[1];
        var x = l1 * p1.X + l0 * p0.X;
        var y = l1 * p1.Y + l0 * p0.Y;
        var z = l1 * p1.Z + l0 * p0.Z;
        if (n > 2)
        {
            var p2 = pick(s.V2);
            var l2 = s.Lambda[2];
            x += l2 * p2.X;
            y += l2 * p2.Y;
            z += l2 * p2.Z;
        }
        if (n > 3)
        {
            var p3 = pick(s.V3);
            var l3 = s.Lambda[3];
            x += l3 * p3.X;
            y += l3 * p3.Y;
            z += l3 * p3.Z;
        }
        return new(x, y, z);
    }

    /// <summary>FUN_180316270: the closest points on A and B; inside (4 vertices) both are A's.</summary>
    public static (Vec3 A, Vec3 B) Witness(in Simplex s)
    {
        switch (s.Count)
        {
            case 1:
                return (new(s.Lambda[0] * s.V0.A.X, s.V0.A.Y * s.Lambda[0], s.V0.A.Z * s.Lambda[0]),
                        new(s.Lambda[0] * s.V0.B.X, s.V0.B.Y * s.Lambda[0], s.V0.B.Z * s.Lambda[0]));
            case 2:
            case 3:
                return (Combine(s, s.Count, v => v.A), Combine(s, s.Count, v => v.B));
            case 4:
                var p = Combine(s, 4, v => v.A);
                return (p, p);
            default:
                return default;
        }
    }

    /// <summary>FUN_180316b70: the simplex's size: 0, its length, its area or its volume (x6).</summary>
    public static float Metric(in Simplex s)
    {
        var w0 = s.V0.W;
        var w1 = s.V1.W;
        switch (s.Count)
        {
            case 2:
            {
                var dx = w0.X - w1.X;
                var dy = w0.Y - w1.Y;
                var dz = w0.Z - w1.Z;
                return Sqrt((dx * dx + dy * dy) + dz * dz);
            }
            case 3:
            {
                var w2 = s.V2.W;
                var ax = w1.X - w0.X;
                var ay = w1.Y - w0.Y;
                var az = w1.Z - w0.Z;
                var bx = w2.X - w0.X;
                var by = w2.Y - w0.Y;
                var bz = w2.Z - w0.Z;
                var cx = ay * bz - az * by;
                var cy = az * bx - ax * bz;
                var cz = ax * by - ay * bx;
                return Sqrt((cy * cy + cx * cx) + cz * cz) * 0.5f;
            }
            case 4:
            {
                var w2 = s.V2.W;
                var w3 = s.V3.W;
                return MathF.Abs(((((w2.Y - w0.Y) * (w3.Z - w0.Z)) - ((w2.Z - w0.Z) * (w3.Y - w0.Y))) * (w1.X - w0.X)
                                  - (((w1.Y - w0.Y) * (w3.Z - w0.Z)) - ((w1.Z - w0.Z) * (w3.Y - w0.Y))) * (w2.X - w0.X))
                                 + (((w1.Y - w0.Y) * (w2.Z - w0.Z)) - ((w1.Z - w0.Z) * (w2.Y - w0.Y))) * (w3.X - w0.X));
            }
            default:
                return 0f;
        }
    }

    /// <summary>FUN_180316930: the direction to search next, towards the origin from the simplex's feature.</summary>
    public static Vec3 SearchDirection(in Simplex s)
    {
        var w0 = s.V0.W;
        switch (s.Count)
        {
            case 1:
                return new(-w0.X, -w0.Y, -w0.Z);
            case 2:
            {
                var w1 = s.V1.W;
                var ex = w1.X - w0.X;
                var ez = w1.Z - w0.Z;
                var ey = w1.Y - w0.Y;
                var tx = -w0.X * ez - -w0.Z * ex;
                var ty = -w0.Z * ey - -w0.Y * ez;
                var tz = -w0.Y * ex - -w0.X * ey;
                return new(ez * tx - ey * tz, ex * tz - ez * ty, ey * ty - ex * tx);
            }
            case 3:
            {
                var w1 = s.V1.W;
                var w2 = s.V2.W;
                var bx = w2.X - w0.X;
                var ax = w1.X - w0.X;
                var ay = w1.Y - w0.Y;
                var by = w2.Y - w0.Y;
                var ny = bx * (w1.Z - w0.Z) - (w2.Z - w0.Z) * ax;
                var nx = (w2.Z - w0.Z) * ay - by * (w1.Z - w0.Z);
                var nz = by * ax - bx * ay;
                if (0f <= (ny * w0.Y + nx * w0.X) + nz * w0.Z)
                    return new(-nx, -ny, -nz);
                return new(nx, ny, nz);
            }
            default:
                return default;
        }
    }

    /// <summary>
    /// FUN_1802ec220: the distance between two proxies at their frames,
    /// warm started from and written back to <paramref name="cache"/>. A step
    /// that fails to shrink the distance falls back to the simplex before it.
    /// </summary>
    public static GjkOutput Distance(in RnTransform xfA, GjkProxy a, in RnTransform xfB, GjkProxy b, ref GjkCache cache, int maxIterations)
        => Distance(xfA, a, xfB, b, ref cache, maxIterations, out _);

    /// <summary>
    /// As above; <paramref name="unsetWeight"/> tells that the iteration limit
    /// came right after a vertex was added. Valve then reads that vertex's
    /// weight from uninitialised stack; here it is 0.
    /// </summary>
    public static GjkOutput Distance(in RnTransform xfA, GjkProxy a, in RnTransform xfB, GjkProxy b, ref GjkCache cache,
                                     int maxIterations, out bool unsetWeight)
    {
        unsetWeight = false;
        var s = new Simplex();
        var backup = new Simplex();
        ReadCache(ref s, xfA, a, xfB, b, cache);
        var best = float.MaxValue;
        var iteration = 0;
        if (0 < maxIterations)
        {
            for (;;)
            {
                if (!Solve(ref s))
                {
                    s = backup;
                    break;
                }
                if (s.Count == 4)
                    break;
                var p = ClosestPoint(s);
                var d2 = (p.Y * p.Y + p.X * p.X) + p.Z * p.Z;
                if (best <= d2)
                {
                    s = backup;
                    break;
                }
                var d = SearchDirection(s);
                cache.Direction = d;
                if ((d.Y * d.Y + d.X * d.X) + d.Z * d.Z < Tiny)
                    break;
                var ia = a.Support(ApplyInverse(xfA, new Vec3(-d.X, -d.Y, -d.Z)));
                var pa = Apply(xfA, a.Vertex(ia));
                var ib = b.Support(ApplyInverse(xfB, d));
                var pb = Apply(xfB, b.Vertex(ib));
                backup = s;
                if (!Add(ref s, ia, pa, ib, pb, true))
                    break;
                iteration++;
                best = d2;
                if (maxIterations <= iteration)
                {
                    unsetWeight = true;
                    break;
                }
            }
        }
        var (wa, wb) = Witness(s);
        cache.Metric = Metric(s);
        cache.Count = s.Count;
        for (var k = 0; k < s.Count; k++)
        {
            cache.IndexA[k] = (byte)s[k].IndexA;
            cache.IndexB[k] = (byte)s[k].IndexB;
            cache.Lambda[k] = s.Lambda[k];
        }
        var dx = wa.X - wb.X;
        var dy = wa.Y - wb.Y;
        var dz = wa.Z - wb.Z;
        return new GjkOutput { Distance = Sqrt((dx * dx + dy * dy) + dz * dz), PointA = wa, PointB = wb };
    }
}
