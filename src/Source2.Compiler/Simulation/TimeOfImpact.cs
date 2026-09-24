namespace Source2.Compiler.Simulation;

/// <summary>
/// The separation function of vphysics2's time of impact (0xE0 bytes in
/// Valve): how far apart two swept proxies are along one feature pair's axis.
/// Type 1 is a world axis between points, 2 an edge pair (local edge
/// directions in <see cref="Axis"/> and <see cref="Point"/>), 3 a face of A and
/// 4 a face of B (local normal in <see cref="Axis"/>, local centroid in
/// <see cref="Point"/>).
/// </summary>
public sealed class SeparationFunction
{
    public int Type;
    public Sweep SweepA, SweepB;
    public GjkProxy ProxyA = null!, ProxyB = null!;
    public Vec3 Axis, Point;

    private const float Tiny = 1.17549435e-35f;

    internal static Vec3 Mul(in RnTransform xf, Vec3 v)
    {
        ref readonly var r = ref xf.R;
        return new(
            ((v.X * r.M0 + v.Y * r.M3) + v.Z * r.M6) + xf.T.X,
            ((v.X * r.M1 + v.Y * r.M4) + v.Z * r.M7) + xf.T.Y,
            ((v.X * r.M2 + v.Y * r.M5) + v.Z * r.M8) + xf.T.Z);
    }

    internal static Vec3 Rot(in RnTransform xf, Vec3 v)
    {
        ref readonly var r = ref xf.R;
        return new(
            (v.X * r.M0 + v.Y * r.M3) + v.Z * r.M6,
            (v.X * r.M1 + v.Y * r.M4) + v.Z * r.M7,
            (v.X * r.M2 + v.Y * r.M5) + v.Z * r.M8);
    }

    internal static Vec3 RotT(in RnTransform xf, Vec3 v)
    {
        ref readonly var r = ref xf.R;
        return new(
            (r.M0 * v.X + r.M1 * v.Y) + r.M2 * v.Z,
            (r.M3 * v.X + r.M4 * v.Y) + r.M5 * v.Z,
            (r.M6 * v.X + r.M7 * v.Y) + r.M8 * v.Z);
    }

    internal static Vec3 Sub(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    internal static Vec3 Neg(Vec3 a) => new(-a.X, -a.Y, -a.Z);

    internal static float Dot(Vec3 a, Vec3 b) => (a.X * b.X + a.Y * b.Y) + a.Z * b.Z;

    internal static Vec3 Cross(Vec3 a, Vec3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    /// <summary>Unit vector, or zero at or below 1.17549435e-35 squared length.</summary>
    internal static Vec3 Normalize(Vec3 v)
    {
        var length = (v.X * v.X + v.Y * v.Y) + v.Z * v.Z;
        if (length <= Tiny)
            return default;
        var inverse = 1f / MathF.Sqrt(length);
        return new(inverse * v.X, inverse * v.Y, inverse * v.Z);
    }

    /// <summary>
    /// FUN_1802d85c0: the function for the GJK simplex in
    /// <paramref name="cache"/> at time <paramref name="t"/>: one vertex on each
    /// side gives a point axis; a vertex and an edge the edge's normal towards
    /// the vertex; two edges their cross product (or, when that fails the
    /// FUN_1802daf90 test, the edge pair itself; nearly parallel edges fall back
    /// to their closest points); a triangle on one side that face.
    /// </summary>
    public static SeparationFunction Create(in Sweep sweepA, GjkProxy a, in Sweep sweepB, GjkProxy b, in GjkCache cache, float t)
    {
        var f = new SeparationFunction { SweepA = sweepA, SweepB = sweepB, ProxyA = a, ProxyB = b };
        f.Init(cache, t);
        return f;
    }

    private unsafe void Init(in GjkCache cache, float t)
    {
        var xfA = Continuous.At(SweepA, t);
        var xfB = Continuous.At(SweepB, t);
        int ia0 = cache.IndexA[0], ia1 = cache.IndexA[1], ia2 = cache.IndexA[2];
        int ib0 = cache.IndexB[0], ib1 = cache.IndexB[1], ib2 = cache.IndexB[2];
        switch (cache.Count)
        {
            case 1:
                Type = 1;
                Axis = Normalize(Sub(Mul(xfB, ProxyB.Vertex(ib0)), Mul(xfA, ProxyA.Vertex(ia0))));
                return;
            case 2:
            {
                var uniqueA = ia0 != ia1 ? 2 : 1;
                var uniqueB = ib0 != ib1 ? 2 : 1;
                if (uniqueA != 2)
                {
                    PointAndEdge(xfA, xfB, ia0, ib0, ib1);
                    return;
                }
                if (uniqueB != uniqueA)
                {
                    EdgeAndPoint(xfA, xfB, ia0, ia1, ib0);
                    return;
                }
                EdgePair(xfA, xfB, ia0, ia1, ib0, ib1);
                return;
            }
            case 3:
            {
                var uniqueA = Unique(ia0, ia1, ia2);
                var uniqueB = Unique(ib0, ib1, ib2);
                if (uniqueA == 3)
                {
                    FaceOfA(xfA, cache, ia0, ia1, ia2);
                    return;
                }
                if (uniqueB == 3)
                {
                    FaceOfB(xfB, cache, ib0, ib1, ib2);
                    return;
                }
                EdgePair(xfA, xfB, ia0, ia0 == ia1 ? ia2 : ia1, ib0, ib0 == ib1 ? ib2 : ib1);
                return;
            }
        }
    }

    private static int Unique(int a, int b, int c)
    {
        if (a == b)
            return a == c && b == c ? 1 : 2;
        return a == c || b == c ? 2 : 3;
    }

    /// <summary>A vertex of A against an edge of B: -((e x r) x e), e the edge, r from its start to the vertex.</summary>
    private void PointAndEdge(in RnTransform xfA, in RnTransform xfB, int ia, int ib0, int ib1)
    {
        var b0 = Mul(xfB, ProxyB.Vertex(ib0));
        var e = Sub(Mul(xfB, ProxyB.Vertex(ib1)), b0);
        var r = Sub(Mul(xfA, ProxyA.Vertex(ia)), b0);
        var c = new Vec3(r.Z * e.Y - r.Y * e.Z, r.X * e.Z - r.Z * e.X, r.Y * e.X - r.X * e.Y);
        Type = 1;
        Axis = Normalize(new Vec3(-(c.Y * e.Z - c.Z * e.Y), -(c.Z * e.X - c.X * e.Z), -(c.X * e.Y - c.Y * e.X)));
    }

    /// <summary>An edge of A against a vertex of B: (e x r) x e.</summary>
    private void EdgeAndPoint(in RnTransform xfA, in RnTransform xfB, int ia0, int ia1, int ib)
    {
        var a0 = Mul(xfA, ProxyA.Vertex(ia0));
        var e = Sub(Mul(xfA, ProxyA.Vertex(ia1)), a0);
        var r = Sub(Mul(xfB, ProxyB.Vertex(ib)), a0);
        var c = new Vec3(r.Z * e.Y - r.Y * e.Z, r.X * e.Z - r.Z * e.X, r.Y * e.X - r.X * e.Y);
        Type = 1;
        Axis = Normalize(new Vec3(c.Y * e.Z - c.Z * e.Y, c.Z * e.X - c.X * e.Z, c.X * e.Y - c.Y * e.X));
    }

    /// <summary>Two edges (the shared tail of the two- and three-vertex cases).</summary>
    private void EdgePair(in RnTransform xfA, in RnTransform xfB, int ia0, int ia1, int ib0, int ib1)
    {
        var a0 = ProxyA.Vertex(ia0);
        var a1 = ProxyA.Vertex(ia1);
        var b0 = ProxyB.Vertex(ib0);
        var b1 = ProxyB.Vertex(ib1);
        var edgeA = Sub(a1, a0);
        var edgeB = Sub(b1, b0);
        var worldA = Normalize(Rot(xfA, edgeA));
        var worldB = Normalize(Rot(xfB, edgeB));
        var n = Cross(worldA, worldB);
        var length = MathF.Sqrt((n.X * n.X + n.Y * n.Y) + n.Z * n.Z);
        if (length < 0.005f)
        {
            var (pa, pb) = ClosestPointsOnSegments(Mul(xfA, a0), Mul(xfA, a1), Mul(xfB, b0), Mul(xfB, b1));
            Type = 1;
            Axis = Normalize(Sub(pb, pa));
            return;
        }
        var inverse = 1f / length;
        n = new(inverse * n.X, inverse * n.Y, inverse * n.Z);
        if (Dot(Sub(Mul(xfB, b0), Mul(xfA, a0)), n) < 0f)
        {
            n = Neg(n);
            worldB = Neg(worldB);
            edgeB = Neg(edgeB);
        }
        var localB = RotT(xfB, worldB);
        var localA = RotT(xfA, worldA);
        if (!Crossing(Continuous.At(SweepA, 1f), localA, Continuous.At(SweepB, 1f), localB, n))
        {
            Type = 2;
            Axis = edgeA;
            Point = edgeB;
            return;
        }
        Type = 1;
        Axis = n;
        Point = default;
    }

    /// <summary>FUN_1802daf90: whether the edges' cross product at the end of the step points against <paramref name="n"/>.</summary>
    private static bool Crossing(in RnTransform xfA, Vec3 edgeA, in RnTransform xfB, Vec3 edgeB, Vec3 n)
    {
        var c = Normalize(Cross(Rot(xfA, edgeA), Rot(xfB, edgeB)));
        return Dot(c, n) < 0f;
    }

    private void FaceOfA(in RnTransform xfA, in GjkCache cache, int i0, int i1, int i2)
    {
        var (normal, centroid) = Face(ProxyA, i0, i1, i2);
        Type = 3;
        Axis = normal;
        Point = centroid;
        var w = Rot(xfA, normal);
        var d = cache.Direction;
        if (((w.X * -d.X) - w.Y * d.Y) - w.Z * d.Z < 0f)
            Axis = Neg(Axis);
    }

    private void FaceOfB(in RnTransform xfB, in GjkCache cache, int i0, int i1, int i2)
    {
        var (normal, centroid) = Face(ProxyB, i0, i1, i2);
        Type = 4;
        Axis = normal;
        Point = centroid;
        if (Dot(Rot(xfB, normal), cache.Direction) < 0f)
            Axis = Neg(Axis);
    }

    /// <summary>A triangle's unit normal (v1 - v0) x (v2 - v0) and its centroid, in the proxy's frame.</summary>
    private static (Vec3 Normal, Vec3 Centroid) Face(GjkProxy p, int i0, int i1, int i2)
    {
        var v0 = p.Vertex(i0);
        var v1 = p.Vertex(i1);
        var v2 = p.Vertex(i2);
        var u = Sub(v1, v0);
        var w = Sub(v2, v0);
        var n = Normalize(new Vec3(w.Z * u.Y - w.Y * u.Z, w.X * u.Z - w.Z * u.X, w.Y * u.X - w.X * u.Y));
        const float third = 0.33333334f;
        var c = new Vec3(((v0.X + v1.X) + v2.X) * third, ((v0.Y + v1.Y) + v2.Y) * third, ((v0.Z + v1.Z) + v2.Z) * third);
        return (n, c);
    }

    /// <summary>
    /// FUN_180321560: the closest points of segments p1-q1 and p2-q2
    /// (Ericson's ClosestPtSegmentSegment).
    /// </summary>
    public static (Vec3 A, Vec3 B) ClosestPointsOnSegments(Vec3 p1, Vec3 q1, Vec3 p2, Vec3 q2)
    {
        var d1 = Sub(q1, p1);
        var d2 = Sub(q2, p2);
        var r = Sub(p1, p2);
        var a = (d1.X * d1.X + d1.Y * d1.Y) + d1.Z * d1.Z;
        var e = (d2.X * d2.X + d2.Y * d2.Y) + d2.Z * d2.Z;
        var f = (d2.X * r.X + d2.Y * r.Y) + d2.Z * r.Z;
        float s, t;
        if (a < Tiny)
        {
            if (e < Tiny)
                return (p1, p2);
            t = Clamp01(f / e);
            return (p1, new(d2.X * t + p2.X, d2.Y * t + p2.Y, d2.Z * t + p2.Z));
        }
        var c = (d1.X * r.X + d1.Y * r.Y) + d1.Z * r.Z;
        if (e < Tiny)
        {
            s = Clamp01(-c / a);
            return (new(d1.X * s + p1.X, d1.Y * s + p1.Y, d1.Z * s + p1.Z), p2);
        }
        var b = (d2.X * d1.X + d2.Y * d1.Y) + d2.Z * d1.Z;
        var denominator = e * a - b * b;
        s = Tiny <= denominator * denominator ? Clamp01((b * f - c * e) / denominator) : 0f;
        t = (s * b + f) / e;
        if (0f <= t)
        {
            if (1f < t)
            {
                t = 1f;
                s = Clamp01((b - c) / a);
            }
        }
        else
        {
            t = 0f;
            s = Clamp01(-c / a);
        }
        return (new(d1.X * s + p1.X, d1.Y * s + p1.Y, d1.Z * s + p1.Z), new(d2.X * t + p2.X, d2.Y * t + p2.Y, d2.Z * t + p2.Z));
    }

    /// <summary>"x &lt;= 0 gives 0, then 1 &lt;= x gives 1": NaN passes through.</summary>
    private static float Clamp01(float x)
    {
        if (x <= 0f)
            x = 0f;
        if (1f <= x)
            x = 1f;
        return x;
    }

    /// <summary>The world edge-pair normal of a type 2 function at a frame pair (unoriented).</summary>
    private Vec3 EdgeNormal(in RnTransform xfA, in RnTransform xfB) => Normalize(Cross(Rot(xfA, Axis), Rot(xfB, Point)));

    /// <summary>FUN_1802dbcf0: the deepest vertex pair along the function at <paramref name="t"/> and its separation.</summary>
    public float FindMinSeparation(out int indexA, out int indexB, float t)
    {
        var xfA = Continuous.At(SweepA, t);
        var xfB = Continuous.At(SweepB, t);
        switch (Type)
        {
            case 1:
            case 2:
            {
                var axis = Type == 1 ? Axis : EdgeNormal(xfA, xfB);
                indexA = ProxyA.Support(RotT(xfA, axis));
                indexB = ProxyB.Support(Neg(RotT(xfB, axis)));
                return Dot(Sub(Mul(xfB, ProxyB.Vertex(indexB)), Mul(xfA, ProxyA.Vertex(indexA))), axis);
            }
            case 3:
            {
                var n = Rot(xfA, Axis);
                var p = Mul(xfA, Point);
                indexA = -1;
                indexB = ProxyB.Support(RotT(xfB, Neg(n)));
                return Dot(Sub(Mul(xfB, ProxyB.Vertex(indexB)), p), n);
            }
            case 4:
            {
                var n = Rot(xfB, Axis);
                var p = Mul(xfB, Point);
                indexB = -1;
                indexA = ProxyA.Support(RotT(xfA, Neg(n)));
                return Dot(Sub(Mul(xfA, ProxyA.Vertex(indexA)), p), n);
            }
            default:
                indexA = -1;
                indexB = -1;
                return 0f;
        }
    }

    /// <summary>FUN_1802db190: the separation of a vertex pair along the function at <paramref name="t"/>.</summary>
    public float Evaluate(int indexA, int indexB, float t)
    {
        var xfA = Continuous.At(SweepA, t);
        var xfB = Continuous.At(SweepB, t);
        switch (Type)
        {
            case 1:
                return Dot(Sub(Mul(xfB, ProxyB.Vertex(indexB)), Mul(xfA, ProxyA.Vertex(indexA))), Axis);
            case 2:
                return Dot(Sub(Mul(xfB, ProxyB.Vertex(indexB)), Mul(xfA, ProxyA.Vertex(indexA))), EdgeNormal(xfA, xfB));
            case 3:
                return Dot(Sub(Mul(xfB, ProxyB.Vertex(indexB)), Mul(xfA, Point)), Rot(xfA, Axis));
            case 4:
                return Dot(Sub(Mul(xfA, ProxyA.Vertex(indexA)), Mul(xfB, Point)), Rot(xfB, Axis));
            default:
                return 0f;
        }
    }

    /// <summary>FUN_1802dccb0: an edge pair frozen into its world normal at <paramref name="t"/>.</summary>
    public void Freeze(float t)
    {
        Axis = EdgeNormal(Continuous.At(SweepA, t), Continuous.At(SweepB, t));
        Type = 1;
    }
}

/// <summary>What the time of impact search ends in.</summary>
public enum ToiState
{
    Failed = 0,
    Overlapped = 2,
    Touching = 3,
    Separated = 4,
}

/// <summary>
/// FUN_1802dddd0: vphysics2's time of impact, conservative advancement in
/// Box2D's form (GJK distance, a separation function, a bisection and secant
/// root search) with Valve's changes: the target is the radii less 3/32 (at
/// least 1/32), and the first time the start is found too deep the function
/// is replaced by the last GJK direction and the search starts over.
/// </summary>
public static class TimeOfImpact
{
    private const float Tolerance = 0.0078125f;
    private const float RootTolerance = 0.00078125f;

    public static (ToiState State, float T) Compute(in Sweep sweepA, GjkProxy a, in Sweep sweepB, GjkProxy b, float tMax, int maxIterations)
    {
        var target = (a.Radius + b.Radius) - 0.09375f;
        target = 0.03125f > target ? 0.03125f : target;
        var t1 = 0f;
        var cache = new GjkCache();
        if (maxIterations < 1)
            return (ToiState.Failed, t1);
        for (var iteration = 0; ;)
        {
            var xfA = Continuous.At(sweepA, t1);
            var xfB = Continuous.At(sweepB, t1);
            var distance = Gjk.Distance(xfA, a, xfB, b, ref cache, 0x20).Distance;
            if (distance <= 0f)
                return (ToiState.Overlapped, 0f);
            if (distance <= target + Tolerance)
                return (ToiState.Touching, t1);
            var f = SeparationFunction.Create(sweepA, a, sweepB, b, cache, t1);
            var done = false;
            var t2 = tMax;
            var pushBack = 0;
            for (;;)
            {
                var s2 = f.FindMinSeparation(out var indexA, out var indexB, t2);
                if (s2 > target + Tolerance || t1 == tMax)
                    return (ToiState.Separated, tMax);
                if (s2 > target - Tolerance)
                {
                    t1 = t2;
                    break;
                }
                var s1 = f.Evaluate(indexA, indexB, t1);
                if (s1 < target - Tolerance && !done)
                {
                    done = true;
                    f.Type = 1;
                    f.Axis = SeparationFunction.Normalize(SeparationFunction.Neg(cache.Direction));
                    t2 = tMax;
                    pushBack = 0;
                    continue;
                }
                if (s1 <= target + Tolerance)
                    return (ToiState.Touching, t1);
                float lo = t1, hi = t2;
                for (var root = 0; root < 64; root++)
                {
                    var t = (root & 1) != 0 ? ((hi - lo) * (target - s1)) / (s2 - s1) + lo : (hi + lo) * 0.5f;
                    var s = f.Evaluate(indexA, indexB, t);
                    if (MathF.Abs(s - target) <= RootTolerance)
                    {
                        t2 = t;
                        break;
                    }
                    if (s > target)
                    {
                        lo = t;
                        s1 = s;
                    }
                    else
                    {
                        hi = t;
                        s2 = s;
                    }
                }
                if (pushBack == maxIterations - 1 && f.Type == 2)
                {
                    pushBack = 0;
                    f.Freeze(t1);
                    t2 = tMax;
                }
                pushBack++;
                if (pushBack >= maxIterations)
                    break;
            }
            if (pushBack == maxIterations)
                return (ToiState.Failed, t1);
            iteration++;
            if (iteration >= maxIterations)
                return (ToiState.Failed, t1);
        }
    }
}
