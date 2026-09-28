using System.Runtime.InteropServices;
using Source2.Compiler.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="RoundCollision"/> against vphysics2's own cores on random
/// spheres, capsules and frames, with and without an old manifold: the
/// return value and the whole 0xe0-byte manifold must match byte for byte.
/// </summary>
public unsafe class RoundCollisionOracleTests(ITestOutputHelper output)
{
    private const int Cases = 20000;

    private static float F(Random r, double lo, double hi) => (float)(lo + r.NextDouble() * (hi - lo));

    private static Vec3 V(Random r, double k) => new(F(r, -k, k), F(r, -k, k), F(r, -k, k));

    private static float Gauss(Random r)
        => (float)(Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble()));

    private static RnTransform Frame(Random r, Vec3 t)
    {
        var q = r.Next(5) == 0 ? Quat.Identity : RnMath.Normalize(new Quat(Gauss(r), Gauss(r), Gauss(r), Gauss(r)));
        return new RnTransform { R = RnMath.Matrix(q), T = t };
    }

    private static CachedManifold Old(Random r)
    {
        var m = new CachedManifold
        {
            PointCount = 1,
            Normal = V(r, 1),
            T1 = V(r, 1),
            T2 = V(r, 1),
            Impulse1 = F(r, -5, 5),
            Impulse2 = F(r, -5, 5),
            TwistImpulse = F(r, -5, 5),
        };
        m.P0.Impulse = r.Next(4) == 0 ? -1f : F(r, 0, 50);
        return m;
    }

    private delegate bool Port<TA, TB>(ReadOnlySpan<CachedManifold> old, ref CachedManifold result,
                                       in RnTransform xfA, in TA a, in RnTransform xfB, in TB b);

    private void Run<TA, TB>(ulong va, int seed, Func<Random, TA> makeA, Func<Random, TB> makeB, Port<TA, TB> port)
        where TA : unmanaged where TB : unmanaged
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var fn = (delegate* unmanaged<CachedManifold*, CachedManifold*, RnTransform*, TA*, RnTransform*, TB*, byte>)Vphysics2Oracle.At(module, va);
        var random = new Random(seed);
        int hits = 0, bad = 0;
        var mem = (byte*)NativeMemory.AlignedAlloc(0x400, 16);
        try
        {
            for (var i = 0; i < Cases; i++)
            {
                var a = makeA(random);
                var b = makeB(random);
                var xfA = Frame(random, V(random, 20));
                // Near A often, so the shapes touch about half the time.
                var xfB = Frame(random, random.Next(3) == 0 ? V(random, 40) : new Vec3(xfA.T.X + F(random, -12, 12), xfA.T.Y + F(random, -12, 12), xfA.T.Z + F(random, -12, 12)));
                var withOld = random.Next(2) == 0;
                var old = Old(random);
                var fill = (byte)random.Next(256);
                var pOld = (CachedManifold*)mem;
                var pOut = (CachedManifold*)(mem + 0x100);
                var pXa = (RnTransform*)(mem + 0x200);
                var pXb = (RnTransform*)(mem + 0x240);
                var pA = (TA*)(mem + 0x280);
                var pB = (TB*)(mem + 0x2c0);
                *pOld = old;
                NativeMemory.Fill(pOut, 0xe0, fill);
                *pXa = xfA;
                *pXb = xfB;
                *pA = a;
                *pB = b;
                var valveHit = fn(withOld ? pOld : null, pOut, pXa, pA, pXb, pB) != 0;
                var mine = default(CachedManifold);
                NativeMemory.Fill(&mine, 0xe0, fill);
                var mineHit = port(withOld ? new ReadOnlySpan<CachedManifold>(in old) : default, ref mine, xfA, a, xfB, b);
                if (valveHit)
                    hits++;
                if (valveHit != mineHit || !new ReadOnlySpan<byte>(pOut, 0xe0).SequenceEqual(new ReadOnlySpan<byte>(&mine, 0xe0)))
                {
                    if (bad++ < 5)
                    {
                        var v = new ReadOnlySpan<byte>(pOut, 0xe0);
                        var m = new ReadOnlySpan<byte>(&mine, 0xe0);
                        var at = 0;
                        while (at < 0xe0 && v[at] == m[at])
                            at++;
                        output.WriteLine($"case {i}: hit {valveHit}/{mineHit}, first byte difference at 0x{at:x}");
                    }
                }
            }
        }
        finally
        {
            NativeMemory.AlignedFree(mem);
        }
        output.WriteLine($"{Cases} cases, {hits} touching, {bad} different");
        Assert.Equal(0, bad);
    }

    /// <summary>Whether the sphere's centre lies behind every face plane of the scaled hull (roughly: for counting).</summary>
    private static bool Inside(RoundCollision.Sphere s, in RnTransform xfA, Physics.RnHull hull, float scale, in RnTransform xfB)
    {
        var c = s.Centre;
        var w = new System.Numerics.Vector3(c.X * xfA.R.M0 + c.Y * xfA.R.M3 + c.Z * xfA.R.M6 + xfA.T.X,
                                            c.X * xfA.R.M1 + c.Y * xfA.R.M4 + c.Z * xfA.R.M7 + xfA.T.Y,
                                            c.X * xfA.R.M2 + c.Y * xfA.R.M5 + c.Z * xfA.R.M8 + xfA.T.Z);
        var u = w - new System.Numerics.Vector3(xfB.T.X, xfB.T.Y, xfB.T.Z);
        var l = new System.Numerics.Vector3(xfB.R.M0 * u.X + xfB.R.M1 * u.Y + xfB.R.M2 * u.Z,
                                            xfB.R.M3 * u.X + xfB.R.M4 * u.Y + xfB.R.M5 * u.Z,
                                            xfB.R.M6 * u.X + xfB.R.M7 * u.Y + xfB.R.M8 * u.Z);
        return hull.Planes.All(p => System.Numerics.Vector3.Dot(p.Normal, l) - scale * p.Offset < 0f);
    }

    private static RoundCollision.Sphere RandomSphere(Random r) => new(V(r, 4), F(r, 0.5, 12));

    private static RoundCollision.Capsule RandomCapsule(Random r)
        => r.Next(10) == 0 ? new(new Vec3(1, 2, 3), new Vec3(1, 2, 3), F(r, 0.5, 8)) : new(V(r, 10), V(r, 10), F(r, 0.5, 8));

    [Fact]
    public void SphereSphereIsValves() => Run(0x1802f5240, 81, RandomSphere, RandomSphere, RoundCollision.SphereSphere);

    [Fact]
    public void SphereCapsuleIsValves() => Run(0x1802f38c0, 82, RandomSphere, RandomCapsule, RoundCollision.SphereCapsule);

    [Fact]
    public void CapsuleCapsuleIsValves() => Run(0x1802f0cf0, 83, RandomCapsule, RandomCapsule, RoundCollision.CapsuleCapsule);

    /// <summary>
    /// FUN_1802f3f30 on the hull oracle's random hulls: the manifold, the
    /// return value and the GJK cache after the call must all be Valve's.
    /// </summary>
    [Fact]
    public void SphereHullIsValves()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var fn = (delegate* unmanaged<CachedManifold*, CachedManifold*, RnTransform*, RoundCollision.Sphere*, RnTransform*,
                                      HullCollisionOracleTests.NativeRef*, GjkCache*, byte>)Vphysics2Oracle.At(module, 0x1802f3f30);
        var pool = HullCollisionOracleTests.Pool();
        var random = new Random(84);
        int hits = 0, bad = 0, inside = 0;
        var mem = (byte*)NativeMemory.AlignedAlloc(0x400, 16);
        try
        {
            for (var i = 0; i < 8000; i++)
            {
                var hull = pool[random.Next(pool.Length)];
                var scale = random.Next(3) == 0 ? F(random, 0.25, 2) : 1f;
                var sphere = RandomSphere(random);
                var xfB = Frame(random, V(random, 20));
                // The sphere's centre near the hull's centroid, inside it at times.
                var c = hull.Hull.Centroid * scale;
                var world = new Vec3(((c.X * xfB.R.M0) + (c.Y * xfB.R.M3)) + (c.Z * xfB.R.M6) + xfB.T.X,
                                     ((c.X * xfB.R.M1) + (c.Y * xfB.R.M4)) + (c.Z * xfB.R.M7) + xfB.T.Y,
                                     ((c.X * xfB.R.M2) + (c.Y * xfB.R.M5)) + (c.Z * xfB.R.M8) + xfB.T.Z);
                var reach = random.Next(4) == 0 ? 2.0 : 40.0;
                var xfA = Frame(random, new Vec3(world.X + F(random, -reach, reach), world.Y + F(random, -reach, reach), world.Z + F(random, -reach, reach)));
                var withOld = random.Next(2) == 0;
                var old = Old(random);
                var fill = (byte)random.Next(256);
                var pOld = (CachedManifold*)mem;
                var pOut = (CachedManifold*)(mem + 0x100);
                var pXa = (RnTransform*)(mem + 0x200);
                var pXb = (RnTransform*)(mem + 0x240);
                var pA = (RoundCollision.Sphere*)(mem + 0x280);
                var pRef = (HullCollisionOracleTests.NativeRef*)(mem + 0x2a0);
                var pCache = (GjkCache*)(mem + 0x2c0);
                *pOld = old;
                NativeMemory.Fill(pOut, 0xe0, fill);
                *pXa = xfA;
                *pXb = xfB;
                *pA = sphere;
                pRef->Hull = hull.Ptr;
                pRef->Scale = scale;
                *pCache = default;
                var valveHit = fn(withOld ? pOld : null, pOut, pXa, pA, pXb, pRef, pCache) != 0;
                var mine = default(CachedManifold);
                NativeMemory.Fill(&mine, 0xe0, fill);
                var cache = default(GjkCache);
                var mineHit = RoundCollision.SphereHull(withOld ? new ReadOnlySpan<CachedManifold>(in old) : default, ref mine,
                                                        xfA, sphere, xfB, new HullRef(hull.Hull, scale), ref cache);
                if (valveHit)
                    hits++;
                var same = valveHit == mineHit
                           && new ReadOnlySpan<byte>(pOut, 0xe0).SequenceEqual(new ReadOnlySpan<byte>(&mine, 0xe0))
                           && new ReadOnlySpan<byte>(pCache, 0x2c).SequenceEqual(new ReadOnlySpan<byte>(&cache, 0x2c));
                if (!same && bad++ < 5)
                {
                    var v = new ReadOnlySpan<byte>(pOut, 0xe0);
                    var m = new ReadOnlySpan<byte>(&mine, 0xe0);
                    var at = 0;
                    while (at < 0xe0 && v[at] == m[at])
                        at++;
                    output.WriteLine($"case {i}: hit {valveHit}/{mineHit}, manifold first difference 0x{at:x}");
                }
                if (valveHit && Inside(sphere, xfA, hull.Hull, scale, xfB))
                    inside++;
            }
        }
        finally
        {
            NativeMemory.AlignedFree(mem);
        }
        output.WriteLine($"8000 cases, {hits} touching ({inside} with the centre inside), {bad} different");
        Assert.Equal(0, bad);
    }

    /// <summary>
    /// FUN_1802efbc0 (capsule against hull, GJK then the manifold or the
    /// separating-axis path) on the hull oracle's hulls, the capsule often
    /// through the hull: the manifold and the GJK cache must be Valve's. The
    /// function leaves its callee's answer in AL, compared where it made one.
    /// </summary>
    [Fact]
    public void CapsuleHullIsValves()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var fn = (delegate* unmanaged<CachedManifold*, CachedManifold*, RnTransform*, RoundCollision.Capsule*, RnTransform*,
                                      HullCollisionOracleTests.NativeRef*, GjkCache*, byte>)Vphysics2Oracle.At(module, 0x1802efbc0);
        var pool = HullCollisionOracleTests.Pool();
        var random = new Random(85);
        int hits = 0, bad = 0, flagsDiffer = 0;
        var mem = (byte*)NativeMemory.AlignedAlloc(0x400, 16);
        try
        {
            for (var i = 0; i < 8000; i++)
            {
                var hull = pool[random.Next(pool.Length)];
                var scale = random.Next(3) == 0 ? F(random, 0.25, 2) : 1f;
                var capsule = RandomCapsule(random);
                var xfB = Frame(random, V(random, 20));
                var c = hull.Hull.Centroid * scale;
                var world = new Vec3(((c.X * xfB.R.M0) + (c.Y * xfB.R.M3)) + (c.Z * xfB.R.M6) + xfB.T.X,
                                     ((c.X * xfB.R.M1) + (c.Y * xfB.R.M4)) + (c.Z * xfB.R.M7) + xfB.T.Y,
                                     ((c.X * xfB.R.M2) + (c.Y * xfB.R.M5)) + (c.Z * xfB.R.M8) + xfB.T.Z);
                var reach = random.Next(3) == 0 ? 3.0 : 30.0;
                var xfA = Frame(random, new Vec3(world.X + F(random, -reach, reach), world.Y + F(random, -reach, reach), world.Z + F(random, -reach, reach)));
                var withOld = random.Next(2) == 0;
                var old = Old(random);
                if (withOld && random.Next(2) == 0)
                {
                    old.PointCount = 2;
                    old.P1.Feature = 0x01000100;
                    old.P1.Impulse = F(random, 0, 9);
                }
                var fill = (byte)random.Next(256);
                var pOld = (CachedManifold*)mem;
                var pOut = (CachedManifold*)(mem + 0x100);
                var pXa = (RnTransform*)(mem + 0x200);
                var pXb = (RnTransform*)(mem + 0x240);
                var pA = (RoundCollision.Capsule*)(mem + 0x280);
                var pRef = (HullCollisionOracleTests.NativeRef*)(mem + 0x2a0);
                var pCache = (GjkCache*)(mem + 0x2c0);
                *pOld = old;
                NativeMemory.Fill(pOut, 0xe0, fill);
                *pXa = xfA;
                *pXb = xfB;
                *pA = capsule;
                pRef->Hull = hull.Ptr;
                pRef->Scale = scale;
                *pCache = default;
                var valveHit = fn(withOld ? pOld : null, pOut, pXa, pA, pXb, pRef, pCache) != 0;
                var mine = default(CachedManifold);
                NativeMemory.Fill(&mine, 0xe0, fill);
                var cache = default(GjkCache);
                var mineHit = RoundCollision.CapsuleHull(withOld ? new ReadOnlySpan<CachedManifold>(in old) : default, ref mine,
                                                         xfA, capsule, xfB, new HullRef(hull.Hull, scale), ref cache);
                if (mineHit)
                    hits++;
                // Bytes 0x26 and 0x27 of each point are padding: where the edge
                // contact replaces the face contact (FUN_1802f13a0) Valve copies
                // a whole manifold from an uninitialised stack temporary.
                // The copy brings the unused point slots along too.
                for (var k = 0; k < 4; k++)
                {
                    ((byte*)pOut)[0x66 + (0x28 * k)] = ((byte*)&mine)[0x66 + (0x28 * k)];
                    ((byte*)pOut)[0x67 + (0x28 * k)] = ((byte*)&mine)[0x67 + (0x28 * k)];
                    if (k >= mine.PointCount && pOut->PointCount == mine.PointCount)
                        new ReadOnlySpan<byte>((byte*)&mine + 0x40 + (0x28 * k), 0x28).CopyTo(new Span<byte>((byte*)pOut + 0x40 + (0x28 * k), 0x28));
                }
                var same = new ReadOnlySpan<byte>(pOut, 0xe0).SequenceEqual(new ReadOnlySpan<byte>(&mine, 0xe0))
                           && new ReadOnlySpan<byte>(pCache, 0x2c).SequenceEqual(new ReadOnlySpan<byte>(&cache, 0x2c));
                if (mineHit && valveHit != mineHit)
                    flagsDiffer++;
                if (!same && bad++ < 8)
                {
                    var v = new ReadOnlySpan<byte>(pOut, 0xe0);
                    var m = new ReadOnlySpan<byte>(&mine, 0xe0);
                    var at = 0;
                    while (at < 0xe0 && v[at] == m[at])
                        at++;
                    output.WriteLine($"case {i}: hit {valveHit}/{mineHit}, points {pOut->PointCount}/{mine.PointCount}, manifold first difference 0x{at:x}");
                }
            }
        }
        finally
        {
            NativeMemory.AlignedFree(mem);
        }
        output.WriteLine($"8000 cases, {hits} touching, {bad} different, {flagsDiffer} answers different");
        Assert.Equal(0, bad);
        Assert.Equal(0, flagsDiffer);
    }

    /// <summary>A triangle as the mesh contact hands it over: v0 at the origin, or anywhere.</summary>
    private static (Vec3, Vec3, Vec3) RandomTriangle(Random r)
    {
        var v0 = r.Next(2) == 0 ? default : V(r, 10);
        return (v0, Add(v0, V(r, 40)), Add(v0, V(r, 40)));
    }

    /// <summary>A point about the triangle in B's frame: on it, over it, or off an edge or corner.</summary>
    private static Vec3 Near(Random r, Vec3 v0, Vec3 v1, Vec3 v2, double reach)
    {
        var u = F(r, -0.3, 1.2);
        var v = F(r, -0.3, 1.2);
        var p = Add(Add(v0, Mul(Sub(v1, v0), u)), Mul(Sub(v2, v0), v));
        return r.Next(8) == 0 ? p : Add(p, V(r, reach));
    }

    private static Vec3 Add(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    private static Vec3 Sub(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    private static Vec3 Mul(Vec3 a, float k) => new(a.X * k, a.Y * k, a.Z * k);

    private static Vec3 Through(in RnTransform xf, Vec3 p)
        => new(((p.X * xf.R.M0) + (p.Y * xf.R.M3)) + (p.Z * xf.R.M6) + xf.T.X,
               ((p.X * xf.R.M1) + (p.Y * xf.R.M4)) + (p.Z * xf.R.M7) + xf.T.Y,
               ((p.X * xf.R.M2) + (p.Y * xf.R.M5)) + (p.Z * xf.R.M8) + xf.T.Z);

    private delegate bool TrianglePort<T>(ref CachedManifold result, in RnTransform xfA, in T a, in RnTransform xfB,
                                          Vec3 v0, Vec3 v1, Vec3 v2, ref MeshTriangleCache cache, int triangle);

    /// <summary>
    /// A triangle core against Valve's: the answer, the whole manifold, the
    /// 0x3c-byte triangle cache. Shape A sits about the triangle, sometimes
    /// right on it, sometimes with the cache left from the previous case.
    /// </summary>
    private void RunTriangle<T>(ulong va, int seed, Func<Random, Vec3, T> make, TrianglePort<T> port, bool maskEdgeCopy) where T : unmanaged
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var fn = (delegate* unmanaged<CachedManifold*, RnTransform*, T*, RnTransform*, Vec3*, MeshTriangleCache*, int, byte>)
            Vphysics2Oracle.At(module, va);
        var random = new Random(seed);
        int hits = 0, bad = 0, flagsDiffer = 0;
        var mem = (byte*)NativeMemory.AlignedAlloc(0x400, 16);
        var cache = default(MeshTriangleCache);
        var valveCache = default(MeshTriangleCache);
        try
        {
            for (var i = 0; i < Cases; i++)
            {
                var (v0, v1, v2) = RandomTriangle(random);
                var xfB = Frame(random, V(random, 20));
                var world = Through(xfB, Near(random, v0, v1, v2, random.Next(3) == 0 ? 2.0 : 12.0));
                var local = V(random, 3);
                var a = make(random, local);
                var xfA = Frame(random, default);
                xfA.T = Sub(world, Through(xfA, local));
                if (random.Next(3) != 0)
                {
                    cache = default;
                    valveCache = default;
                }
                var triangle = random.Next(1000);
                var fill = (byte)random.Next(256);
                var pOut = (CachedManifold*)mem;
                var pXa = (RnTransform*)(mem + 0x100);
                var pXb = (RnTransform*)(mem + 0x140);
                var pA = (T*)(mem + 0x180);
                var pV = (Vec3*)(mem + 0x1c0);
                var pCache = (MeshTriangleCache*)(mem + 0x200);
                NativeMemory.Fill(pOut, 0xe0, fill);
                *pXa = xfA;
                *pXb = xfB;
                *pA = a;
                pV[0] = v0;
                pV[1] = v1;
                pV[2] = v2;
                *pCache = valveCache;
                var valveHit = fn(pOut, pXa, pA, pXb, pV, pCache, triangle) != 0;
                valveCache = *pCache;
                var mine = default(CachedManifold);
                NativeMemory.Fill(&mine, 0xe0, fill);
                var mineHit = port(ref mine, xfA, a, xfB, v0, v1, v2, ref cache, triangle);
                if (mineHit)
                    hits++;
                if (maskEdgeCopy)
                {
                    for (var k = 0; k < 4; k++)
                    {
                        ((byte*)pOut)[0x66 + (0x28 * k)] = ((byte*)&mine)[0x66 + (0x28 * k)];
                        ((byte*)pOut)[0x67 + (0x28 * k)] = ((byte*)&mine)[0x67 + (0x28 * k)];
                        if (k >= mine.PointCount && pOut->PointCount == mine.PointCount)
                            new ReadOnlySpan<byte>((byte*)&mine + 0x40 + (0x28 * k), 0x28).CopyTo(new Span<byte>((byte*)pOut + 0x40 + (0x28 * k), 0x28));
                    }
                }
                var same = new ReadOnlySpan<byte>(pOut, 0xe0).SequenceEqual(new ReadOnlySpan<byte>(&mine, 0xe0))
                           && new ReadOnlySpan<byte>(pCache, 0x3c).SequenceEqual(new ReadOnlySpan<byte>(&cache, 0x3c));
                if (valveHit != mineHit)
                    flagsDiffer++;
                if (!same && bad++ < 8)
                {
                    var v = new ReadOnlySpan<byte>(pOut, 0xe0);
                    var m = new ReadOnlySpan<byte>(&mine, 0xe0);
                    var at = 0;
                    while (at < 0xe0 && v[at] == m[at])
                        at++;
                    output.WriteLine($"case {i}: hit {valveHit}/{mineHit}, points {pOut->PointCount}/{mine.PointCount}, manifold first difference 0x{at:x}");
                }
                cache = valveCache;
            }
        }
        finally
        {
            NativeMemory.AlignedFree(mem);
        }
        output.WriteLine($"{Cases} cases, {hits} touching, {bad} different, {flagsDiffer} answers different");
        Assert.Equal(0, bad);
        Assert.Equal(0, flagsDiffer);
    }

    [Fact]
    public void SphereTriangleIsValves()
        => RunTriangle(0x1802f49d0, 86, (r, p) => new RoundCollision.Sphere(p, F(r, 0.5, 12)), RoundCollision.SphereTriangle, false);

    [Fact]
    public void CapsuleTriangleIsValves()
        => RunTriangle(0x1802efd30, 87, (r, p) =>
        {
            var d = V(r, 20);
            return new RoundCollision.Capsule(Sub(p, Mul(d, 0.5f)), Add(p, Mul(d, 0.5f)), F(r, 0.5, 12));
        }, RoundCollision.CapsuleTriangle, true);
}
