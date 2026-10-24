using System.Numerics;

namespace Source2.Compiler.Physics;

/// <summary>
/// The GJK distance RnMesh_TrianglePairTouches (vphysics2 0924, 180165350)
/// measures two triangles with: FUN_180129800 makes each a polytope proxy
/// (FUN_180129c50 at the identity, FUN_180128cf0: scale 1, radius 0) and runs
/// FUN_18037a1c0 through FUN_18037ac70 for at most 32 iterations, the result
/// less both radii and clamped at 0. This is vphysics2's second GJK, apart
/// from the one the settle uses (FUN_1802ec220, Simulation.Gjk): its own
/// simplex solvers (FUN_18037b750, FUN_18037be20, the edge case inline),
/// triangle weights (FUN_180379ef0), search direction (FUN_18037acc0) and
/// support (FUN_18037af20), each sum in the DLL's order (most in z, y, x).
/// Only the tetrahedron weights (FUN_180315bb0) are shared. With identity
/// frames every transform is exact and is left out.
/// </summary>
internal static class MeshGjk
{
    private const float Tiny = 1.17549435e-35f;

    private struct Vertex
    {
        public int IndexA, IndexB;
        public Vector3 A, B, W;
    }

    // The simplex (0xb floats a vertex) and its weights at +0xb4, which stay put as vertices come and go.
    private struct Simplex
    {
        public int Count;
        public Vertex V0, V1, V2, V3;
        public float L0, L1, L2, L3;

        public void Set(int i, in Vertex v)
        {
            switch (i)
            {
                case 0: V0 = v; break;
                case 1: V1 = v; break;
                case 2: V2 = v; break;
                default: V3 = v; break;
            }
        }

        public readonly Vertex Get(int i) => i switch { 0 => V0, 1 => V1, 2 => V2, _ => V3 };
    }

    /// <summary>FUN_180129800 for two triangles at the identity: their GJK distance, 0 when they touch or overlap.</summary>
    public static float Distance(Vector3[] a, Vector3[] b)
    {
        // FUN_18037b4a0 with an empty cache: vertex 0 of each, weight 0.
        var s = new Simplex { Count = 1, V0 = new Vertex { A = a[0], B = b[0], W = Sub(b[0], a[0]) } };
        // The copy FUN_18037a1c0 falls back to (local_1c8): count 0 until the first vertex is added.
        var backup = new Simplex();
        var best = float.MaxValue;
        var overlap = false;
        var iteration = 0;
        Span<int> savedA = stackalloc int[4];
        Span<int> savedB = stackalloc int[4];
        for (;;)
        {
            var saved = s.Count;
            for (var k = 0; k < saved; k++)
            {
                savedA[k] = s.Get(k).IndexA;
                savedB[k] = s.Get(k).IndexB;
            }
            var solved = s.Count switch
            {
                1 => Keep1(ref s),
                2 => Solve2(ref s),
                3 => Solve3(ref s),
                _ => Solve4(ref s),
            };
            if (!solved)
            {
                s = backup;
                break;
            }
            if (s.Count == 4)
            {
                overlap = true;
                break;
            }
            var p = Closest(s);
            var d2 = (p.Y * p.Y + p.Z * p.Z) + p.X * p.X;
            if (best <= d2)
            {
                s = backup;
                break;
            }
            var d = Direction(s);
            if ((d.Y * d.Y + d.Z * d.Z) + d.X * d.X < Tiny)
            {
                overlap = true;
                break;
            }
            var ia = Support(a, new Vector3(-d.X, -d.Y, -d.Z));
            var ib = Support(b, d);
            backup = s;
            var duplicate = false;
            for (var k = 0; k < saved; k++)
                if (savedA[k] == ia && savedB[k] == ib)
                    duplicate = true;
            if (duplicate)
                break;
            iteration++;
            s.Set(s.Count, new Vertex { IndexA = ia, IndexB = ib, A = a[ia], B = b[ib], W = Sub(b[ib], a[ia]) });
            s.Count++;
            best = d2;
            if (32 <= iteration)
                break;
        }
        var (pa, pb) = Witness(s);
        if (overlap)
            pb = pa;
        var dz = pa.Z - pb.Z;
        var dy = pa.Y - pb.Y;
        var dx = pa.X - pb.X;
        var distance = MathF.Sqrt((dz * dz + dy * dy) + dx * dx);
        return distance <= 0f ? 0f : distance;
    }

    private static Vector3 Sub(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    // FUN_18037af20: the vertex furthest along d, measured from vertex 0, the first of equals winning.
    private static int Support(Vector3[] v, Vector3 d)
    {
        if (v.Length < 2)
            return 0;
        var best = 0f;
        var index = 0;
        for (var i = 1; i < v.Length; i++)
        {
            var dot = ((v[i].Y - v[0].Y) * d.Y + (v[i].Z - v[0].Z) * d.Z) + (v[i].X - v[0].X) * d.X;
            if (best < dot)
            {
                best = dot;
                index = i;
            }
        }
        return index;
    }

    // The closest point: the weighted w, the second vertex's term first.
    private static Vector3 Closest(in Simplex s) => s.Count switch
    {
        1 => new(s.V0.W.X * s.L0, s.V0.W.Y * s.L0, s.V0.W.Z * s.L0),
        2 => new(s.V1.W.X * s.L1 + s.V0.W.X * s.L0, s.V1.W.Y * s.L1 + s.V0.W.Y * s.L0, s.V1.W.Z * s.L1 + s.V0.W.Z * s.L0),
        3 => new((s.V1.W.X * s.L1 + s.V0.W.X * s.L0) + s.V2.W.X * s.L2, (s.V1.W.Y * s.L1 + s.V0.W.Y * s.L0) + s.V2.W.Y * s.L2,
                 (s.V1.W.Z * s.L1 + s.V0.W.Z * s.L0) + s.V2.W.Z * s.L2),
        _ => new(((s.V1.W.X * s.L1 + s.V0.W.X * s.L0) + s.V2.W.X * s.L2) + s.V3.W.X * s.L3,
                 ((s.V1.W.Y * s.L1 + s.V0.W.Y * s.L0) + s.V2.W.Y * s.L2) + s.V3.W.Y * s.L3,
                 ((s.V1.W.Z * s.L1 + s.V0.W.Z * s.L0) + s.V2.W.Z * s.L2) + s.V3.W.Z * s.L3),
    };

    // FUN_180316270: the witness points, the weighted A and B (with four, one point for both).
    private static (Vector3 A, Vector3 B) Witness(in Simplex s)
    {
        static Vector3 Sum(in Simplex s, Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3) => s.Count switch
        {
            1 => new(s.L0 * v0.X, v0.Y * s.L0, v0.Z * s.L0),
            2 => new(s.L1 * v1.X + s.L0 * v0.X, v1.Y * s.L1 + v0.Y * s.L0, v1.Z * s.L1 + v0.Z * s.L0),
            3 => new((s.L1 * v1.X + s.L0 * v0.X) + s.L2 * v2.X, (v1.Y * s.L1 + v0.Y * s.L0) + v2.Y * s.L2,
                     (v1.Z * s.L1 + v0.Z * s.L0) + v2.Z * s.L2),
            4 => new(((v1.X * s.L1 + s.L0 * v0.X) + s.L2 * v2.X) + s.L3 * v3.X, ((v1.Y * s.L1 + v0.Y * s.L0) + v2.Y * s.L2) + v3.Y * s.L3,
                     ((v1.Z * s.L1 + v0.Z * s.L0) + v2.Z * s.L2) + v3.Z * s.L3),
            _ => default,
        };
        var pa = Sum(s, s.V0.A, s.V1.A, s.V2.A, s.V3.A);
        return s.Count == 4 ? (pa, pa) : (pa, Sum(s, s.V0.B, s.V1.B, s.V2.B, s.V3.B));
    }

    // FUN_18037acc0: towards the origin from the simplex's feature.
    private static Vector3 Direction(in Simplex s)
    {
        var a = s.V0.W;
        switch (s.Count)
        {
            case 1:
                return new(-a.X, -a.Y, -a.Z);
            case 2:
            {
                var ex = s.V1.W.X - a.X;
                var ez = s.V1.W.Z - a.Z;
                var ey = s.V1.W.Y - a.Y;
                var tx = ey * -a.Z - -a.Y * ez;
                var ty = -a.Y * ex - -a.X * ey;
                var tz = -a.X * ez - ex * -a.Z;
                return new(tz * ez - ty * ey, ex * ty - tx * ez, tx * ey - tz * ex);
            }
            case 3:
            {
                var e1x = s.V1.W.X - a.X;
                var e1y = s.V1.W.Y - a.Y;
                var e1z = s.V1.W.Z - a.Z;
                var e2x = s.V2.W.X - a.X;
                var e2y = s.V2.W.Y - a.Y;
                var e2z = s.V2.W.Z - a.Z;
                var ny = e2x * e1z - e2z * e1x;
                var nx = e2z * e1y - e2y * e1z;
                var nz = e2y * e1x - e2x * e1y;
                if (0f <= (a.Z * nz + a.Y * ny) + nx * a.X)
                    return new(-nx, -ny, -nz);
                return new(nx, ny, nz);
            }
            default:
                return default;
        }
    }

    // An edge x to y: u = y . (y - x), v = -x . (y - x), |y - x|^2, each sum z, y, x.
    private static (float U, float V, float Length) Edge(Vector3 x, Vector3 y)
    {
        var ez = y.Z - x.Z;
        var ey = y.Y - x.Y;
        var ex = y.X - x.X;
        return ((y.Z * ez + y.Y * ey) + y.X * ex, -((x.Z * ez + x.Y * ey) + x.X * ex), (ez * ez + ey * ey) + ex * ex);
    }

    // FUN_18037be20's b-c edge, whose dot products run y, z, x.
    private static (float U, float V, float Length) EdgeBc(Vector3 b, Vector3 c)
    {
        var ex = c.X - b.X;
        var ez = c.Z - b.Z;
        var ey = c.Y - b.Y;
        return ((ey * c.Y + ez * c.Z) + ex * c.X, -((ey * b.Y + ez * b.Z) + ex * b.X), (ez * ez + ey * ey) + ex * ex);
    }

    // FUN_18037be20's c-d edge: u = (ez dz + ey dy) + ex dx, v = -((ey cy + ez cz) + ex cx).
    private static (float U, float V, float Length) EdgeCd(Vector3 c, Vector3 d)
    {
        var ey = d.Y - c.Y;
        var ex = d.X - c.X;
        var ez = d.Z - c.Z;
        return ((ez * d.Z + ey * d.Y) + ex * d.X, -((ey * c.Y + ez * c.Z) + ex * c.X), (ez * ez + ey * ey) + ex * ex);
    }

    // FUN_18037be20's d-b edge: u = (ey by + ez bz) + ex bx, v = -((ez dz + ey dy) + ex dx).
    private static (float U, float V, float Length) EdgeDb(Vector3 d, Vector3 b)
    {
        var ex = b.X - d.X;
        var ey = b.Y - d.Y;
        var ez = b.Z - d.Z;
        return ((ey * b.Y + ez * b.Z) + ex * b.X, -((ez * d.Z + ey * d.Y) + ex * d.X), (ez * ez + ey * ey) + ex * ex);
    }

    // FUN_180379ef0 with the origin: the weights of p, q, r and |n|^2.
    private static (float U, float V, float W, float D) Triangle(Vector3 p, Vector3 q, Vector3 r)
    {
        var qpz = q.Z - p.Z;
        var rpz = r.Z - p.Z;
        var rpx = r.X - p.X;
        var qpx = q.X - p.X;
        var qpy = q.Y - p.Y;
        var rpy = r.Y - p.Y;
        var ny = rpx * qpz - rpz * qpx;
        var nx = rpz * qpy - rpy * qpz;
        var nz = rpy * qpx - rpx * qpy;
        var u = ((r.Y * q.X - r.X * q.Y) * nz + (r.X * q.Z - r.Z * q.X) * ny) + (r.Z * q.Y - r.Y * q.Z) * nx;
        var v = ((p.X * r.Z - p.Z * r.X) * ny + (p.Y * r.X - p.X * r.Y) * nz) + (p.Z * r.Y - p.Y * r.Z) * nx;
        var w = ((p.Z * q.X - p.X * q.Z) * ny + (p.X * q.Y - p.Y * q.X) * nz) + (p.Y * q.Z - p.Z * q.Y) * nx;
        return (u, v, w, (nz * nz + ny * ny) + nx * nx);
    }

    private static bool Keep1(ref Simplex s)
    {
        s.L0 = 1f;
        return true;
    }

    private static bool Keep(ref Simplex s, in Vertex v)
    {
        s.Count = 1;
        s.V0 = v;
        s.L0 = 1f;
        return true;
    }

    // An edge kept; positive tells whether the length must be above 0 (a NaN fails) or only not at or below it.
    private static bool KeepEdge(ref Simplex s, in Vertex a, in Vertex b, (float U, float V, float Length) e, bool positive)
    {
        s.Count = 2;
        s.V0 = a;
        s.V1 = b;
        if (positive ? !(0f < e.Length) : e.Length <= 0f)
            return false;
        s.L0 = e.U / e.Length;
        s.L1 = e.V / e.Length;
        return true;
    }

    private static bool KeepFace(ref Simplex s, in Vertex a, in Vertex b, in Vertex c, (float U, float V, float W, float D) t)
    {
        s.Count = 3;
        s.V0 = a;
        s.V1 = b;
        s.V2 = c;
        if (t.D <= 0f)
            return false;
        s.L0 = t.U / t.D;
        s.L1 = t.V / t.D;
        s.L2 = t.W / t.D;
        return true;
    }

    // FUN_18037a1c0's inline edge case.
    private static bool Solve2(ref Simplex s)
    {
        var e = Edge(s.V0.W, s.V1.W);
        if (e.V <= 0f)
            return Keep(ref s, s.V0);
        if (e.U <= 0f)
            return Keep(ref s, s.V1);
        if (e.Length <= 0f)
            return false;
        s.L0 = e.U / e.Length;
        s.L1 = e.V / e.Length;
        return true;
    }

    // FUN_18037b750.
    private static bool Solve3(ref Simplex s)
    {
        Vertex a = s.V0, b = s.V1, c = s.V2;
        var ab = Edge(a.W, b.W);
        var bc = Edge(b.W, c.W);
        var ca = Edge(c.W, a.W);
        if (!(0f < ab.V) && !(0f < ca.U))
            return Keep(ref s, a);
        if (!(0f < bc.V) && !(0f < ab.U))
            return Keep(ref s, b);
        if (!(0f < ca.V) && !(0f < bc.U))
            return Keep(ref s, c);
        var t = Triangle(a.W, b.W, c.W);
        if (!(0f < t.W || ab.U <= 0f || ab.V <= 0f))
            return KeepEdge(ref s, a, b, ab, positive: true);
        if (!(0f < t.U || bc.U <= 0f || bc.V <= 0f))
            return KeepEdge(ref s, b, c, bc, positive: true);
        if (!(0f < t.V || ca.U <= 0f || ca.V <= 0f))
            return KeepEdge(ref s, c, a, ca, positive: true);
        if (!(0f < t.D))
            return false;
        s.L0 = t.U / t.D;
        s.L1 = t.V / t.D;
        s.L2 = t.W / t.D;
        return true;
    }

    // FUN_18037be20.
    private static bool Solve4(ref Simplex s)
    {
        Vertex a = s.V0, b = s.V1, c = s.V2, d = s.V3;
        var ab = Edge(a.W, b.W);
        var ac = Edge(a.W, c.W);
        var ad = Edge(a.W, d.W);
        var bc = EdgeBc(b.W, c.W);
        var cd = EdgeCd(c.W, d.W);
        var db = EdgeDb(d.W, b.W);
        if (ab.V <= 0f && ac.V <= 0f && ad.V <= 0f)
            return Keep(ref s, a);
        if (ab.U <= 0f && db.U <= 0f && bc.V <= 0f)
            return Keep(ref s, b);
        if (ac.U <= 0f && bc.U <= 0f && cd.V <= 0f)
            return Keep(ref s, c);
        if (ad.U <= 0f && cd.U <= 0f && db.V <= 0f)
            return Keep(ref s, d);
        var acb = Triangle(a.W, c.W, b.W);
        var abd = Triangle(a.W, b.W, d.W);
        var adc = Triangle(a.W, d.W, c.W);
        var bcd = Triangle(b.W, c.W, d.W);
        if (!(0f < abd.W || 0f < acb.V || ab.U <= 0f || ab.V <= 0f))
            return KeepEdge(ref s, a, b, ab, positive: true);
        if (acb.W <= 0f && adc.V <= 0f && 0f < ac.U && 0f < ac.V)
            return KeepEdge(ref s, a, c, ac, positive: false);
        if (adc.W <= 0f && abd.V <= 0f && 0f < ad.U && 0f < ad.V)
            return KeepEdge(ref s, a, d, ad, positive: false);
        if (!(0f < acb.U || 0f < bcd.W || bc.U <= 0f || bc.V <= 0f))
            return KeepEdge(ref s, b, c, bc, positive: true);
        if (!(0f < adc.U || 0f < bcd.U || cd.U <= 0f || cd.V <= 0f))
            return KeepEdge(ref s, c, d, cd, positive: true);
        if (!(0f < abd.U || 0f < bcd.V || db.U <= 0f || db.V <= 0f))
            return KeepEdge(ref s, d, b, db, positive: false);
        var t = Simulation.Gjk.Tetrahedron(V(a.W), V(b.W), V(c.W), V(d.W));
        if (t.D < 0f && 0f < acb.U && 0f < acb.V && 0f < acb.W)
            return KeepFace(ref s, a, c, b, acb);
        if (t.C < 0f && 0f < abd.U && 0f < abd.V && 0f < abd.W)
            return KeepFace(ref s, a, b, d, abd);
        if (t.B < 0f && 0f < adc.U && 0f < adc.V && 0f < adc.W)
            return KeepFace(ref s, a, d, c, adc);
        if (t.A < 0f && 0f < bcd.U && 0f < bcd.V && 0f < bcd.W)
            return KeepFace(ref s, b, c, d, bcd);
        if (t.Volume <= 0f)
            return false;
        s.L0 = t.A / t.Volume;
        s.L1 = t.B / t.Volume;
        s.L2 = t.C / t.Volume;
        s.L3 = t.D / t.Volume;
        return true;
    }

    private static Simulation.Vec3 V(Vector3 v) => new(v.X, v.Y, v.Z);
}
