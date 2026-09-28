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
}
