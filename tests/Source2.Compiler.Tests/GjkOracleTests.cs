using System.Runtime.InteropServices;
using Source2.Compiler.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// vphysics2's GJK distance and its simplex solver, called directly in the
/// DLL on random simplices, proxies and frames, against <see cref="Gjk"/>.
/// </summary>
public unsafe class GjkOracleTests(ITestOutputHelper output)
{
    private const ulong SolveVa = 0x180318460;
    private const ulong ClosestPointVa = 0x180316670;
    private const ulong SearchDirectionVa = 0x180316930;
    private const ulong WitnessVa = 0x180316270;
    private const ulong MetricVa = 0x180316b70;
    private const ulong SupportVa = 0x1802eb9e0;
    private const ulong DistanceVa = 0x1802ec220;

    /// <summary>Valve's proxy (0x40 bytes), as FUN_18012e550 fills it.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x40)]
    public struct NativeProxy
    {
        [FieldOffset(0x00)] public int Count;
        [FieldOffset(0x08)] public Vec3* Vertices;
        [FieldOffset(0x10)] public float Scale;
        [FieldOffset(0x14)] public byte Box;
        [FieldOffset(0x18)] public fixed float Bounds[6];
        [FieldOffset(0x38)] public float Radius;
    }

    private static float F(Random r, double lo, double hi) => (float)(lo + r.NextDouble() * (hi - lo));

    private static Vec3 V(Random r, double k) => new(F(r, -k, k), F(r, -k, k), F(r, -k, k));

    /// <summary>A random simplex of 1 to 4 vertices, sometimes near the origin, sometimes degenerate.</summary>
    private static Simplex RandomSimplex(Random r)
    {
        var s = new Simplex { Count = 1 + r.Next(4) };
        var spread = r.Next(3) switch { 0 => 1.0, 1 => 20.0, _ => 200.0 };
        var shift = r.Next(2) == 0 ? default : V(r, spread);
        for (var k = 0; k < s.Count; k++)
        {
            ref var v = ref s[k];
            v.IndexA = r.Next(8);
            v.IndexB = r.Next(8);
            v.A = V(r, spread);
            if (k > 0 && r.Next(10) == 0)
                v.W = s[r.Next(k)].W;
            else
            {
                var w = V(r, spread);
                v.W = new(w.X + shift.X, w.Y + shift.Y, w.Z + shift.Z);
            }
            if (k > 1 && r.Next(10) == 0)
            {
                var t = F(r, -1, 2);
                var p = s[0].W;
                var q = s[1].W;
                v.W = new(p.X + (q.X - p.X) * t, p.Y + (q.Y - p.Y) * t, p.Z + (q.Z - p.Z) * t);
            }
            v.B = new(v.A.X + v.W.X, v.A.Y + v.W.Y, v.A.Z + v.W.Z);
            s.Lambda[k] = F(r, 0, 1);
        }
        return s;
    }

    private static string Hex<T>(in T value) where T : unmanaged
    {
        fixed (T* p = &value)
            return Convert.ToHexString(new ReadOnlySpan<byte>(p, sizeof(T)));
    }

    /// <summary>The live part of a simplex: count, vertices and weights, saved indices.</summary>
    private static string Live(in Simplex s)
    {
        var text = $"n{s.Count} saved{s.SavedCount}";
        for (var k = 0; k < Math.Min(s.Count, 4); k++)
            text += $" [{Hex(s[k])} {BitConverter.SingleToUInt32Bits(s.Lambda[k]):X8}]";
        for (var k = 0; k < Math.Min(s.SavedCount, 4); k++)
            text += $" {s.SavedA[k]}/{s.SavedB[k]}";
        return text;
    }

    [Fact]
    public void TheSimplexSolverMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var solve = (delegate* unmanaged<Simplex*, byte>)Vphysics2Oracle.At(module, SolveVa);
        var closest = (delegate* unmanaged<Simplex*, Vec3*, Vec3*>)Vphysics2Oracle.At(module, ClosestPointVa);
        var direction = (delegate* unmanaged<Simplex*, Vec3*, Vec3*>)Vphysics2Oracle.At(module, SearchDirectionVa);
        var witness = (delegate* unmanaged<Simplex*, Vec3*, Vec3*, void>)Vphysics2Oracle.At(module, WitnessVa);
        var metric = (delegate* unmanaged<Simplex*, float>)Vphysics2Oracle.At(module, MetricVa);
        var random = new Random(71);
        var counts = new int[5];
        var failed = 0;
        for (var i = 0; i < 400000; i++)
        {
            var valve = RandomSimplex(random);
            var ours = valve;
            var before = valve.Count;
            var a = solve(&valve) != 0;
            var b = Gjk.Solve(ref ours);
            if (a != b || Live(valve) != Live(ours))
                Assert.Fail($"solve trial {i} (count {before}): valve {a} {Live(valve)} ours {b} {Live(ours)}");
            if (!a)
            {
                failed++;
                continue;
            }
            counts[ours.Count]++;
            Vec3 p, q, wa, wb;
            closest(&valve, &p);
            Assert.True(Hex(p) == Hex(Gjk.ClosestPoint(ours)), $"closest point trial {i} count {ours.Count}");
            if (ours.Count < 4)
            {
                direction(&valve, &q);
                Assert.True(Hex(q) == Hex(Gjk.SearchDirection(ours)), $"search direction trial {i} count {ours.Count}");
            }
            witness(&valve, &wa, &wb);
            var (oa, ob) = Gjk.Witness(ours);
            Assert.True(Hex(wa) == Hex(oa) && Hex(wb) == Hex(ob), $"witness trial {i} count {ours.Count}");
            Assert.True(BitConverter.SingleToUInt32Bits(metric(&valve)) == BitConverter.SingleToUInt32Bits(Gjk.Metric(ours)),
                        $"metric trial {i} count {ours.Count}");
        }
        output.WriteLine($"400000 simplices: kept 1/2/3/4 vertices {counts[1]}/{counts[2]}/{counts[3]}/{counts[4]}, degenerate {failed}");
    }

    /// <summary>A random proxy: a point cloud, or a box hull.</summary>
    private static (GjkProxy Ours, NativeProxy Valve, Vec3[] Pinned) RandomProxy(Random r)
    {
        var box = r.Next(3) == 0;
        var scale = r.Next(2) == 0 ? 1f : F(r, 0.25, 3);
        Vec3[] points;
        GjkProxy ours;
        if (box)
        {
            var min = V(r, 20);
            var max = new Vec3(min.X + F(r, 0.5, 30), min.Y + F(r, 0.5, 30), min.Z + F(r, 0.5, 30));
            points = new Vec3[8];
            for (var k = 0; k < 8; k++)
                points[k] = new((k & 1) == 0 ? max.X : min.X, (k & 2) == 0 ? max.Y : min.Y, (k & 4) == 0 ? max.Z : min.Z);
            ours = GjkProxy.OfHull(points, 3, min, max, scale);
        }
        else
        {
            points = new Vec3[1 + r.Next(40)];
            var c = V(r, 10);
            var k = F(r, 1, 25);
            for (var j = 0; j < points.Length; j++)
            {
                var p = V(r, k);
                points[j] = new(c.X + p.X, c.Y + p.Y, c.Z + p.Z);
            }
            ours = GjkProxy.OfHull(points, 0, default, default, scale);
        }
        var valve = new NativeProxy { Count = ours.Count, Scale = scale, Box = (byte)(box ? 1 : 0), Radius = ours.Radius };
        for (var j = 0; j < 6; j++)
            valve.Bounds[j] = ours.Bounds[j];
        return (ours, valve, points);
    }

    private static RnTransform RandomFrame(Random r, double spread)
    {
        var q = new Quat(F(r, -1, 1), F(r, -1, 1), F(r, -1, 1), F(r, -1, 1));
        if (r.Next(5) == 0)
            q = Quat.Identity;
        q = RnMath.Normalize(q);
        return new RnTransform { R = RnMath.Matrix(q), T = V(r, spread) };
    }

    [Fact]
    public void TheSupportFunctionMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var support = (delegate* unmanaged<NativeProxy*, int*, Vec3*, int*>)Vphysics2Oracle.At(module, SupportVa);
        var random = new Random(72);
        var result = stackalloc int[2];
        for (var i = 0; i < 100000; i++)
        {
            var (ours, valve, points) = RandomProxy(random);
            fixed (Vec3* p = points)
            {
                valve.Vertices = p;
                var d = random.Next(10) == 0 ? new Vec3(random.Next(3) - 1, random.Next(3) - 1, 0f) : V(random, 1);
                support(&valve, result, &d);
                Assert.True(result[0] == ours.Support(d), $"trial {i}: valve {result[0]} ours {ours.Support(d)}");
            }
        }
    }

    [Fact]
    public void TheDistanceMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var distance = (delegate* unmanaged<GjkOutput*, RnTransform*, NativeProxy*, RnTransform*, NativeProxy*, GjkCache*, int, void>)
            Vphysics2Oracle.At(module, DistanceVa);
        var random = new Random(73);
        var cache = new GjkCache();
        var overlapping = 0;
        var garbage = 0;
        for (var i = 0; i < 100000; i++)
        {
            var (oa, va, pa) = RandomProxy(random);
            var (ob, vb, pb) = RandomProxy(random);
            var spread = random.Next(3) switch { 0 => 5.0, 1 => 30.0, _ => 120.0 };
            var xa = RandomFrame(random, spread);
            var xb = RandomFrame(random, spread);
            if (random.Next(3) != 0)
                cache = new GjkCache();
            else if (random.Next(2) == 0)
                cache.Count = Math.Min(cache.Count, 1 + random.Next(4));
            var iterations = random.Next(4) == 0 ? 1 + random.Next(6) : 32;
            fixed (Vec3* a = pa)
            fixed (Vec3* b = pb)
            {
                va.Vertices = a;
                vb.Vertices = b;
                var valveCache = cache;
                var ourCache = cache;
                GjkOutput valve;
                distance(&valve, &xa, &va, &xb, &vb, &valveCache, iterations);
                var ours = Gjk.Distance(xa, oa, xb, ob, ref ourCache, iterations, out var unset);
                // An iteration limit reached right after adding a vertex leaves
                // that vertex's weight unset: Valve's is stack garbage, and so
                // are the witness points and distance made with it.
                if (unset)
                {
                    garbage++;
                    valveCache.Lambda[valveCache.Count - 1] = ourCache.Lambda[ourCache.Count - 1];
                    Assert.True(Hex(valveCache) == Hex(ourCache), $"trial {i}: valve {Hex(valveCache)} ours {Hex(ourCache)}");
                    cache = ourCache;
                    continue;
                }
                if (Hex(valve) != Hex(ours) || Hex(valveCache) != Hex(ourCache))
                    Assert.Fail($"trial {i}: valve {Hex(valve)} {Hex(valveCache)} ours {Hex(ours)} {Hex(ourCache)}");
                if (ours.Distance == 0f)
                    overlapping++;
                cache = ourCache;
            }
        }
        output.WriteLine($"100000 pairs, {overlapping} overlapping, {garbage} with an unset last weight");
    }

    private const ulong SepInitVa = 0x1802d85c0;
    private const ulong SepFindMinVa = 0x1802dbcf0;
    private const ulong SepEvaluateVa = 0x1802db190;
    private const ulong SepFreezeVa = 0x1802dccb0;
    private const ulong ToiVa = 0x1802dddd0;

    private static Quat RandomQuat(Random r) => RnMath.Normalize(new Quat(F(r, -1, 1), F(r, -1, 1), F(r, -1, 1), F(r, -1, 1)));

    /// <summary>A random sweep: moving and turning, only moving, or still.</summary>
    private static Sweep RandomSweep(Random r, double spread, double travel)
    {
        var s = new Sweep
        {
            Q0 = RandomQuat(r),
            LocalCenter = r.Next(3) == 0 ? default : V(r, 3),
            C0 = V(r, spread),
            Alpha0 = r.Next(2) == 0 ? 0f : F(r, 0, 0.9),
        };
        var kind = r.Next(4);
        s.Q = kind switch
        {
            0 => s.Q0,
            1 => RandomQuat(r),
            _ => RnMath.Normalize(new Quat(s.Q0.X + F(r, -0.2, 0.2), s.Q0.Y + F(r, -0.2, 0.2), s.Q0.Z + F(r, -0.2, 0.2), s.Q0.W + F(r, -0.2, 0.2))),
        };
        var d = kind == 0 && r.Next(2) == 0 ? default : V(r, travel);
        s.C = new(s.C0.X + d.X, s.C0.Y + d.Y, s.C0.Z + d.Z);
        return s;
    }

    private static string Fn(byte* f) =>
        $"type {*(int*)f} axis {Convert.ToHexString(new ReadOnlySpan<byte>(f + 0xc8, 12))} point {(*(int*)f == 1 ? "-" : Convert.ToHexString(new ReadOnlySpan<byte>(f + 0xd4, 12)))}";

    private static string Fn(SeparationFunction f) =>
        $"type {f.Type} axis {Hex(f.Axis)} point {(f.Type == 1 ? "-" : Hex(f.Point))}";

    [Fact]
    public void TheSeparationFunctionMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var distance = (delegate* unmanaged<GjkOutput*, RnTransform*, NativeProxy*, RnTransform*, NativeProxy*, GjkCache*, int, void>)
            Vphysics2Oracle.At(module, DistanceVa);
        var init = (delegate* unmanaged<byte*, Sweep*, NativeProxy*, Sweep*, NativeProxy*, GjkOutput*, GjkCache*, float, byte*>)
            Vphysics2Oracle.At(module, SepInitVa);
        var findMin = (delegate* unmanaged<byte*, int*, int*, float, float>)Vphysics2Oracle.At(module, SepFindMinVa);
        var evaluate = (delegate* unmanaged<byte*, int, int, float, float>)Vphysics2Oracle.At(module, SepEvaluateVa);
        var freeze = (delegate* unmanaged<byte*, float, void>)Vphysics2Oracle.At(module, SepFreezeVa);
        var random = new Random(74);
        var types = new int[5];
        var f = (byte*)NativeMemory.AllocZeroed(0x200);
        try
        {
            for (var i = 0; i < 100000; i++)
            {
                var (oa, va, pa) = RandomProxy(random);
                var (ob, vb, pb) = RandomProxy(random);
                var sa = RandomSweep(random, 40, 60);
                var sb = RandomSweep(random, 40, 60);
                var t = random.Next(3) == 0 ? 0f : F(random, 0, 1);
                fixed (Vec3* a = pa)
                fixed (Vec3* b = pb)
                {
                    va.Vertices = a;
                    vb.Vertices = b;
                    var cache = new GjkCache();
                    var xa = Continuous.At(sa, t);
                    var xb = Continuous.At(sb, t);
                    GjkOutput output;
                    distance(&output, &xa, &va, &xb, &vb, &cache, 0x20);
                    if (output.Distance <= 0f || cache.Count > 3)
                        continue;
                    var aligned = (Sweep*)NativeMemory.AlignedAlloc(0xa0, 16);
                    aligned[0] = sa;
                    aligned[1] = sb;
                    init(f, &aligned[0], &va, &aligned[1], &vb, &output, &cache, t);
                    NativeMemory.AlignedFree(aligned);
                    var ours = SeparationFunction.Create(sa, oa, sb, ob, cache, t);
                    if (Fn(f) != Fn(ours))
                        Assert.Fail($"init trial {i} count {cache.Count}: valve {Fn(f)} ours {Fn(ours)}");
                    types[ours.Type]++;
                    for (var k = 0; k < 4; k++)
                    {
                        var u = F(random, 0, 1);
                        int ia, ib;
                        var s = findMin(f, &ia, &ib, u);
                        var s2 = ours.FindMinSeparation(out var ja, out var jb, u);
                        Assert.True(ia == ja && ib == jb && Bits(s) == Bits(s2), $"find min trial {i} type {ours.Type}: valve {ia}/{ib} {s:R} ours {ja}/{jb} {s2:R}");
                        ia = ia < 0 ? random.Next(oa.Count) : ia;
                        ib = ib < 0 ? random.Next(ob.Count) : ib;
                        var e = evaluate(f, ia, ib, u);
                        var e2 = ours.Evaluate(ia, ib, u);
                        Assert.True(Bits(e) == Bits(e2), $"evaluate trial {i} type {ours.Type}: valve {e:R} ours {e2:R}");
                    }
                    if (ours.Type == 2)
                    {
                        var u = F(random, 0, 1);
                        freeze(f, u);
                        ours.Freeze(u);
                        Assert.True(Fn(f) == Fn(ours), $"freeze trial {i}: valve {Fn(f)} ours {Fn(ours)}");
                    }
                }
            }
        }
        finally
        {
            NativeMemory.Free(f);
        }
        output.WriteLine($"separation functions by type 1/2/3/4: {types[1]}/{types[2]}/{types[3]}/{types[4]}");
    }

    private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);

    [Fact]
    public void TheTimeOfImpactMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var toi = (delegate* unmanaged<Sweep*, NativeProxy*, Sweep*, NativeProxy*, float, int, ulong>)Vphysics2Oracle.At(module, ToiVa);
        var random = new Random(75);
        var states = new int[5];
        for (var i = 0; i < 300000; i++)
        {
            var (oa, va, pa) = RandomProxy(random);
            var (ob, vb, pb) = RandomProxy(random);
            var sa = RandomSweep(random, 40, 80);
            var sb = random.Next(3) == 0 ? new Sweep { Q0 = Quat.Identity, Q = Quat.Identity } : RandomSweep(random, 40, 80);
            if (random.Next(2) == 0)
            {
                // Thrown through B: from one side of it to the other.
                var o = V(random, 60);
                var m = new Vec3(sb.C0.X + F(random, -10, 10), sb.C0.Y + F(random, -10, 10), sb.C0.Z + F(random, -10, 10));
                sa.C0 = new(m.X + o.X, m.Y + o.Y, m.Z + o.Z);
                sa.C = new(m.X - o.X, m.Y - o.Y, m.Z - o.Z);
            }
            var tMax = random.Next(3) == 0 ? F(random, 0.1, 1) : 1f;
            var iterations = random.Next(5) == 0 ? 1 + random.Next(4) : 20;
            fixed (Vec3* a = pa)
            fixed (Vec3* b = pb)
            {
                va.Vertices = a;
                vb.Vertices = b;
                var aligned = (Sweep*)NativeMemory.AlignedAlloc(0xa0, 16);
                aligned[0] = sa;
                aligned[1] = sb;
                var valve = toi(&aligned[0], &va, &aligned[1], &vb, tMax, iterations);
                NativeMemory.AlignedFree(aligned);
                var (state, t) = TimeOfImpact.Compute(sa, oa, sb, ob, tMax, iterations);
                var vs = (int)(uint)valve;
                var vt = BitConverter.UInt32BitsToSingle((uint)(valve >> 32));
                Assert.True(vs == (int)state && Bits(vt) == Bits(t), $"trial {i}: valve {vs} {vt:R} ours {(int)state} {t:R}");
                states[vs]++;
            }
        }
        output.WriteLine($"time of impact states failed/overlapped/touching/separated: {states[0]}/{states[2]}/{states[3]}/{states[4]}");
    }
}
