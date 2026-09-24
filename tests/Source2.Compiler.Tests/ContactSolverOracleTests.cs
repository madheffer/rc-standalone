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

    [Fact]
    public void PreparingAContactMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var prepare = (delegate* unmanaged<byte*, byte**, SolverBody*, float, byte, int>)
            Vphysics2Oracle.At(module, 0x1801d3fc0);
        // A fake CRnContact: its anchor hook (vtable slot 7) is the DLL's own no-op.
        var memory = (byte*)NativeMemory.AlignedAlloc(0x4000, 16);
        var bodies = (SolverBody*)NativeMemory.AlignedAlloc((nuint)(8 * sizeof(SolverBody)), 16);
        try
        {
            var random = new Random(8);
            for (var i = 0; i < Trials / 4; i++)
            {
                NativeMemory.Clear(memory, 0x4000);
                var vtable = (nint*)memory;
                vtable[7] = Vphysics2Oracle.At(module, 0x180009030);
                var contact = memory + 0x100;
                var shapeA = memory + 0x200;
                var shapeB = memory + 0x400;
                var body = memory + 0x600;
                var cacheBytes = memory + 0x800;
                var streams = memory + 0x1800;
                *(nint*)contact = (nint)vtable;
                *(nint*)(contact + 8) = (nint)shapeA;
                *(nint*)(contact + 0x10) = (nint)shapeB;
                *(int*)(contact + 0x18) = -1;
                *(int*)(contact + 0x1c) = -1;
                *(nint*)(contact + 0x80) = (nint)cacheBytes;
                var setup = new ContactSolver.ContactSetup(RandomMaterial(random), RandomMaterial(random),
                                                           Pick(random, 0.03125f, 0f, F(random, 0, 0.1)),
                                                           F(random, 0, 4));
                *(float*)(contact + 0x8c) = setup.SoftCap;
                *(float*)(contact + 0x94) = setup.Slop;
                foreach (var shape in new[] { shapeA, shapeB })
                {
                    *(nint*)(shape + 0x10) = (nint)body;
                    *(int*)(shape + 0x18) = 2;
                }
                *(ContactSolver.Material*)(shapeA + 0x20) = setup.MaterialA;
                *(ContactSolver.Material*)(shapeB + 0x20) = setup.MaterialB;
                *(int*)(body + 0x54) = 2;

                var count = 1 + random.Next(3);
                *(int*)(cacheBytes + 4) = count;
                var cache = new Span<CachedManifold>(cacheBytes + 8, count);
                for (var k = 0; k < count; k++)
                    cache[k] = RandomManifold(random);

                for (var k = 0; k < 4; k++)
                {
                    bodies[k] = RandomSolverBody(random);
                    bodies[k + 4] = bodies[k];
                }
                ref var header = ref *(ContactHeader*)streams;
                header.BodyA = random.Next(4);
                header.BodyB = (header.BodyA + 1 + random.Next(3)) % 4;
                header.MassScaleA = random.Next(4) == 0 ? 0f : 1f;
                header.MassScaleB = 1f;
                new Span<byte>(streams, 0x800).CopyTo(new Span<byte>(streams + 0x800, 0x800));

                var warm = (byte)(random.Next(5) == 0 ? 0 : 1);
                var dt = 1f / 90f;
                var cursor = streams;
                prepare(contact, &cursor, bodies, dt, warm);
                var length = ContactSolver.Prepare(new Span<byte>(streams + 0x800, 0x800),
                                                   new Span<SolverBody>(bodies + 4, 4), cache, setup, dt, warm != 0);
                Assert.Equal((int)(cursor - streams), length);
                var theirs = new ReadOnlySpan<byte>(streams, length);
                var ours = new ReadOnlySpan<byte>(streams + 0x800, length);
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
            NativeMemory.AlignedFree(memory);
            NativeMemory.AlignedFree(bodies);
        }
    }

    [Fact]
    public void APositionImpulseMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var apply = (delegate* unmanaged<SolverBody*, float, Mat3*, Vec3*, Vec3*, void>)
            Vphysics2Oracle.At(module, 0x1801b1750);
        var bodies = (SolverBody*)NativeMemory.AlignedAlloc((nuint)(2 * sizeof(SolverBody)), 16);
        try
        {
            var random = new Random(9);
            for (var i = 0; i < Trials; i++)
            {
                bodies[0] = RandomSolverBody(random);
                bodies[0].LocalInvInertia = Inertia(random);
                bodies[0].InfiniteMass = (byte)(random.Next(8) == 0 ? 1 : 0);
                bodies[0].InfiniteInertia = (byte)(random.Next(8) == 0 ? 1 : 0);
                bodies[1] = bodies[0];
                var inertia = Inertia(random);
                var (p, r) = (V(random, 5), V(random, 40));
                var mass = InvMass(random);
                apply(&bodies[0], mass, &inertia, &p, &r);
                ContactSolver.ApplyPositionImpulse(ref bodies[1], mass, inertia, p, r);
                Assert.True(First(new ReadOnlySpan<byte>(bodies, sizeof(SolverBody)),
                                  new ReadOnlySpan<byte>(bodies + 1, sizeof(SolverBody))) < 0,
                            $"trial {i}: q {bodies[0].Q} vs {bodies[1].Q}, p {bodies[0].Position} vs {bodies[1].Position}");
            }
        }
        finally
        {
            NativeMemory.AlignedFree(bodies);
        }
    }

    [Fact]
    public void APositionIterationMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var solve = (delegate* unmanaged<byte*, byte**, SolverBody*, float, float, float, byte>)
            Vphysics2Oracle.At(module, 0x1801d4c10);
        var memory = (byte*)NativeMemory.AlignedAlloc(0x4000, 16);
        var bodies = (SolverBody*)NativeMemory.AlignedAlloc((nuint)(8 * sizeof(SolverBody)), 16);
        try
        {
            var random = new Random(10);
            for (var i = 0; i < Trials / 4; i++)
            {
                NativeMemory.Clear(memory, 0x4000);
                var contact = memory + 0x100;
                var shapeA = memory + 0x200;
                var shapeB = memory + 0x400;
                var body = memory + 0x600;
                var cacheBytes = memory + 0x800;
                var stream = memory + 0x1800;
                *(nint*)(contact + 8) = (nint)shapeA;
                *(nint*)(contact + 0x10) = (nint)shapeB;
                *(int*)(contact + 0x18) = -1;
                *(int*)(contact + 0x1c) = -1;
                *(nint*)(contact + 0x80) = (nint)cacheBytes;
                *(int*)(contact + 0x98) = 0x18;
                var soft = random.Next(4) == 0;
                var setup = new ContactSolver.ContactSetup(
                    RigidOrSoft(random, soft && random.Next(2) == 0), RigidOrSoft(random, soft),
                    Pick(random, 0.03125f, 0f, F(random, 0, 0.1)), 4f);
                *(float*)(contact + 0x94) = setup.Slop;
                foreach (var shape in new[] { shapeA, shapeB })
                {
                    *(nint*)(shape + 0x10) = (nint)body;
                    *(int*)(shape + 0x18) = 2;
                }
                *(ContactSolver.Material*)(shapeA + 0x20) = setup.MaterialA;
                *(ContactSolver.Material*)(shapeB + 0x20) = setup.MaterialB;
                *(int*)(body + 0x54) = 2;

                var count = 1 + random.Next(3);
                *(int*)(cacheBytes + 4) = count;
                var cache = new Span<CachedManifold>(cacheBytes + 8, count);
                for (var k = 0; k < count; k++)
                    cache[k] = RandomManifold(random);
                for (var k = 0; k < 4; k++)
                {
                    bodies[k] = RandomSolverBody(random);
                    bodies[k].LocalInvInertia = Inertia(random);
                    bodies[k + 4] = bodies[k];
                }
                ref var header = ref *(ContactHeader*)stream;
                header.BodyA = random.Next(4);
                header.BodyB = (header.BodyA + 1 + random.Next(3)) % 4;
                header.MassScaleA = random.Next(4) == 0 ? 0f : 1f;
                header.MassScaleB = 1f;

                var cursor = stream;
                solve(contact, &cursor, bodies, 0.1f, -0.09375f, 1f / 90f);
                ContactSolver.SolvePosition(header, new Span<SolverBody>(bodies + 4, 4), cache, setup);
                for (var k = 0; k < 4; k++)
                    Assert.True(First(new ReadOnlySpan<byte>(bodies + k, sizeof(SolverBody)),
                                      new ReadOnlySpan<byte>(bodies + 4 + k, sizeof(SolverBody))) < 0,
                                $"trial {i}: body {k} differs: valve p={bodies[k].Position} q={bodies[k].Q}"
                                + $" ours p={bodies[4 + k].Position} q={bodies[4 + k].Q}");
            }
        }
        finally
        {
            NativeMemory.AlignedFree(memory);
            NativeMemory.AlignedFree(bodies);
        }
    }

    private static ContactSolver.Material RigidOrSoft(Random r, bool soft)
    {
        var m = RandomMaterial(r);
        m.Frequency = soft ? F(r, 0.5, 20) : 0f;
        m.DampingRatio = soft ? F(r, 0.1, 2) : m.DampingRatio;
        return m;
    }

    private static CachedManifold RandomManifold(Random r)
    {
        var m = new CachedManifold
        {
            PointCount = 1 + r.Next(4),
            Centre = V(r, 500),
            Normal = Unit(r),
            TwistImpulse = F(r, -3, 3),
            T1 = Unit(r),
            Impulse1 = F(r, -30, 30),
            T2 = Unit(r),
            Impulse2 = F(r, -30, 30),
        };
        for (var i = 0; i < m.PointCount; i++)
            m.Points[i] = new CachedPoint
            {
                LocalA = V(r, 30),
                LocalB = V(r, 30),
                Impulse = r.Next(4) == 0 ? 0f : F(r, 0, 60),
                SubShape = -1,
            };
        return m;
    }

    private static SolverBody RandomSolverBody(Random r)
    {
        var q = new Quat(F(r, -1, 1), F(r, -1, 1), F(r, -1, 1), F(r, -1, 1));
        var sb = new SolverBody
        {
            V = V(r, 300),
            W = V(r, 20),
            WorldInvInertia = Inertia(r),
            Q = RnMath.Normalize(q),
            LocalMassCenter = V(r, 10),
            Position = V(r, 500),
            InvMass = InvMass(r),
            V0 = V(r, 300),
            W0 = V(r, 20),
            FrictionScale = r.Next(3) == 0 ? F(r, 0, 1) : 1f,
            NoDynamicContact = (byte)r.Next(2),
            TimeScale = 1f,
        };
        return sb;
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
