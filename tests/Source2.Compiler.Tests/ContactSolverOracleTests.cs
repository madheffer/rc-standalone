using System.Runtime.InteropServices;
using Source2.Compiler.Simulation;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// Rubikon's contact solver, ported, against vphysics2's own functions on the
/// same random rows and bodies; every output byte must match.
/// </summary>
public unsafe class ContactSolverOracleTests
{
    private const int Trials = 20000;

    [Fact]
    public void MaterialMixingMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var mix = (delegate* unmanaged<float, float, float, ContactSolver.Material*, ContactSolver.Material*,
                                       float*, float*, float*, float*, void>)Vphysics2Oracle.At(module, 0x1801d17c0);
        var random = new Random(3);
        for (var i = 0; i < Trials; i++)
        {
            var a = RandomMaterial(random);
            var b = RandomMaterial(random);
            var fa = Pick(random, 0f, 1f, F(random, 0, 2));
            var fb = Pick(random, 0f, 1f, F(random, 0, 2));
            var e = random.Next(2);
            float mu, rest, freq, zeta;
            mix(fa, fb, e, &a, &b, &mu, &rest, &freq, &zeta);
            var ours = ContactSolver.MixMaterials(fa, fb, e, a, b);
            Assert.Equal((Bits(mu), Bits(rest), Bits(freq), Bits(zeta)),
                         (Bits(ours.Friction), Bits(ours.Restitution), Bits(ours.Frequency), Bits(ours.DampingRatio)));
        }
    }

    [Fact]
    public void TheEffectiveMassMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var mass = (delegate* unmanaged<float, Mat3*, Vec3*, float, Mat3*, Vec3*, float, float>)
            Vphysics2Oracle.At(module, 0x1801d1610);
        var random = new Random(4);
        for (var i = 0; i < Trials; i++)
        {
            var (ia, ib) = (Inertia(random), Inertia(random));
            var (a, b) = (V(random, 50), V(random, 50));
            var (ma, mb) = (InvMass(random), InvMass(random));
            var extra = random.Next(2) == 0 ? 0f : F(random, 0, 0.1);
            Assert.Equal(Bits(mass(ma, &ia, &a, mb, &ib, &b, extra)),
                         Bits(ContactSolver.EffectiveMass(ma, ia, a, mb, ib, b, extra)));
        }
    }

    [Fact]
    public void PreparingAPointMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var prepare = (delegate* unmanaged<PointRow*, float, Mat3*, Vec3*, float, Mat3*, Vec3*, Vec3*,
                                           float, float, float, float, void>)Vphysics2Oracle.At(module, 0x1801d2860);
        var random = new Random(5);
        for (var i = 0; i < Trials; i++)
        {
            var (ia, ib) = (Inertia(random), Inertia(random));
            var (ra, rb, n) = (V(random, 40), V(random, 40), Unit(random));
            var (ma, mb) = (InvMass(random), InvMass(random));
            var (bias, soft, lambda, mu) = (F(random, -50, 50), random.Next(2) == 0 ? 0f : F(random, 0, 1),
                                            F(random, 0, 100), F(random, 0, 1));
            PointRow theirs = default, ours = default;
            prepare(&theirs, ma, &ia, &ra, mb, &ib, &rb, &n, bias, soft, lambda, mu);
            ContactSolver.PreparePoint(ref ours, ma, ia, ra, mb, ib, rb, n, bias, soft, lambda, mu);
            AssertSame(theirs, ours, i);
        }
    }

    [Fact]
    public void PreparingFrictionMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var prepare = (delegate* unmanaged<FrictionRow*, float, Mat3*, Vec3*, float, Mat3*, Vec3*, Vec3*, Vec3*,
                                           float*, float*, float, void>)Vphysics2Oracle.At(module, 0x1801d2030);
        var random = new Random(6);
        for (var i = 0; i < Trials; i++)
        {
            var (ia, ib) = (Inertia(random), Inertia(random));
            var (ra, rb) = (V(random, 40), V(random, 40));
            var t1 = Unit(random);
            var t2 = Unit(random);
            var (ma, mb) = (InvMass(random), InvMass(random));
            var bias = stackalloc float[] { F(random, -5, 5), F(random, -5, 5) };
            var lambda = stackalloc float[] { F(random, -50, 50), F(random, -50, 50) };
            var limit = F(random, 0, 100);
            FrictionRow theirs = default, ours = default;
            prepare(&theirs, ma, &ia, &ra, mb, &ib, &rb, &t1, &t2, bias, lambda, limit);
            ContactSolver.PrepareFriction(ref ours, ma, ia, ra, mb, ib, rb, t1, t2, bias[0], bias[1],
                                          lambda[0], lambda[1], limit);
            AssertSame(theirs, ours, i);
        }
    }

    [Fact]
    public void AVelocityIterationMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var solve = (delegate* unmanaged<byte**, SolverBody*, void>)Vphysics2Oracle.At(module, 0x1801d5e60);
        var random = new Random(7);
        const int size = 0x1000;
        var buffers = (byte*)NativeMemory.AlignedAlloc(2 * size, 16);
        var bodies = (SolverBody*)NativeMemory.AlignedAlloc((nuint)(8 * sizeof(SolverBody)), 16);
        try
        {
            for (var i = 0; i < Trials; i++)
            {
                NativeMemory.Clear(buffers, 2 * size);
                var stream = new Span<byte>(buffers, size);
                var length = RandomRows(random, stream);
                stream.CopyTo(new Span<byte>(buffers + size, size));
                for (var k = 0; k < 4; k++)
                {
                    bodies[k] = default;
                    bodies[k].V = V(random, 300);
                    bodies[k].W = V(random, 20);
                    bodies[k + 4] = bodies[k];
                }
                var cursor = buffers;
                solve(&cursor, bodies);
                ContactSolver.SolveVelocity(new Span<byte>(buffers + size, size), new Span<SolverBody>(bodies + 4, 4));
                var theirs = new ReadOnlySpan<byte>(buffers, length);
                var ours = new ReadOnlySpan<byte>(buffers + size, length);
                Assert.True(First(theirs, ours) < 0, $"trial {i}: rows differ at 0x{First(theirs, ours):x}");
                for (var k = 0; k < 4; k++)
                    Assert.True(First(new ReadOnlySpan<byte>(bodies + k, sizeof(SolverBody)),
                                      new ReadOnlySpan<byte>(bodies + 4 + k, sizeof(SolverBody))) < 0,
                                $"trial {i}: body {k} differs: valve v={bodies[k].V} w={bodies[k].W}"
                                + $" ours v={bodies[4 + k].V} w={bodies[4 + k].W}");
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buffers);
            NativeMemory.AlignedFree(bodies);
        }
    }

    /// <summary>A contact's rows: 1 to 3 manifolds of 1 to 4 points, random but finite.</summary>
    private static int RandomRows(Random r, Span<byte> stream)
    {
        ref var header = ref MemoryMarshal.AsRef<ContactHeader>(stream);
        header.BodyA = r.Next(4);
        header.BodyB = (header.BodyA + 1 + r.Next(3)) % 4;
        header.MassScaleA = 1f;
        header.MassScaleB = 1f;
        header.ManifoldCount = 1 + r.Next(3);
        var at = 0x18;
        for (var m = 0; m < header.ManifoldCount; m++)
        {
            ref var row = ref MemoryMarshal.AsRef<ManifoldRow>(stream[at..]);
            var (ia, ib) = (Inertia(r), Inertia(r));
            var (ma, mb) = (InvMass(r), InvMass(r));
            var n = Unit(r);
            row.Normal = n;
            row.TwistMass = F(r, 0, 1e4);
            row.TwistBias = r.Next(3) == 0 ? F(r, -1, 1) : 0f;
            row.TwistImpulse = F(r, -5, 5);
            row.TwistLimit = F(r, 0, 10);
            row.TwistA = V(r, 1e-3);
            row.TwistB = V(r, 1e-3);
            ContactSolver.PrepareFriction(ref row.Friction, ma, ia, V(r, 40), mb, ib, V(r, 40), Unit(r), Unit(r),
                                          r.Next(3) == 0 ? F(r, -1, 1) : 0f, 0f, F(r, -20, 20), F(r, -20, 20),
                                          F(r, 0, 60));
            row.PointCount = 1 + r.Next(4);
            row.Size = 0xc8 + row.PointCount * 0x5c;
            var points = MemoryMarshal.Cast<byte, PointRow>(stream.Slice(at + 0xc8, row.PointCount * 0x5c));
            for (var i = 0; i < points.Length; i++)
            {
                ContactSolver.PreparePoint(ref points[i], ma, ia, V(r, 40), mb, ib, V(r, 40), n,
                                           F(r, -30, 30), r.Next(2) == 0 ? 0f : F(r, 0, 0.5),
                                           F(r, 0, 50), F(r, 0, 1));
                points[i].TwistArm = F(r, 0.03125, 20);
            }
            at += row.Size;
        }
        return at;
    }

    private static void AssertSame<T>(T theirs, T ours, int trial) where T : unmanaged
    {
        var a = MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in theirs));
        var b = MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in ours));
        Assert.True(First(a, b) < 0, $"trial {trial}: first difference at +0x{First(a, b):x}");
    }

    /// <summary>
    /// The first differing byte offset, or -1. Words that are NaN on both sides
    /// count as equal: which operand's payload a NaN carries depends on how the
    /// JIT orders a commutative instruction, and means nothing for the port.
    /// </summary>
    private static int First(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        for (var i = 0; i < a.Length; i += 4)
        {
            if (a.Slice(i, Math.Min(4, a.Length - i)).SequenceEqual(b.Slice(i, Math.Min(4, a.Length - i))))
                continue;
            if (a.Length - i >= 4 && float.IsNaN(BitConverter.ToSingle(a[i..])) && float.IsNaN(BitConverter.ToSingle(b[i..])))
                continue;
            return i;
        }
        return -1;
    }

    private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);

    private static float F(Random r, double lo, double hi) => (float)(lo + r.NextDouble() * (hi - lo));

    private static float Pick(Random r, params float[] values) => values[r.Next(values.Length)];

    private static Vec3 V(Random r, double s) => new(F(r, -s, s), F(r, -s, s), F(r, -s, s));

    private static Vec3 Unit(Random r)
    {
        var v = V(r, 1);
        var n = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return new(v.X / n, v.Y / n, v.Z / n);
    }

    private static float InvMass(Random r) => r.Next(10) == 0 ? 0f : F(r, 1e-4, 0.2);

    private static Mat3 Inertia(Random r)
    {
        if (r.Next(10) == 0)
            return default;
        var m = new Mat3();
        for (var i = 0; i < 3; i++)
            for (var j = i; j < 3; j++)
            {
                var v = F(r, 0, 1e-3) * (i == j ? 1 : 0.2f);
                m[3 * i + j] = v;
                m[3 * j + i] = v;
            }
        return m;
    }

    private static ContactSolver.Material RandomMaterial(Random r) => new()
    {
        Friction = F(r, 0, 1.5),
        Restitution = r.Next(2) == 0 ? 0f : F(r, 0, 1.2),
        Frequency = r.Next(2) == 0 ? 0f : F(r, -1, 30),
        DampingRatio = F(r, 0, 2),
    };
}
