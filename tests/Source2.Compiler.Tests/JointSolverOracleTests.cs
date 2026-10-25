using System.Runtime.InteropServices;
using Source2.Compiler.Simulation;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// Rubikon's joint solver functions, ported, against vphysics2's own on the
/// same random joints and solver bodies; every output byte must match.
/// </summary>
public unsafe class JointSolverOracleTests
{
    private const int Trials = 20000;

    [Fact]
    public void TheWorldAnchorMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var point = (delegate* unmanaged<SolverBody*, Vec3*, Vec3*, Vec3*>)Vphysics2Oracle.At(module, 0x180209820);
        var random = new Random(41);
        var body = (SolverBody*)NativeMemory.AlignedAlloc((nuint)sizeof(SolverBody), 16);
        try
        {
            for (var i = 0; i < Trials; i++)
            {
                *body = Body(random);
                var p = V(random, 30);
                Vec3 theirs;
                point(body, &theirs, &p);
                Assert.Equal(Bytes(theirs), Bytes(JointRows.WorldPoint(*body, p)));
            }
        }
        finally
        {
            NativeMemory.AlignedFree(body);
        }
    }

    [Fact]
    public void ALimitRowMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var limit = (delegate* unmanaged<AngularRow*, Mat3*, Mat3*, float, Vec3*, float, float, float, void>)
            Vphysics2Oracle.At(module, 0x180229380);
        var random = new Random(42);
        for (var i = 0; i < Trials; i++)
        {
            var (ia, ib) = (Inertia(random), Inertia(random));
            var axis = random.Next(10) == 0 ? default : Unit(random);
            var (bias, impulse) = (F(random, -50, 50), F(random, -100, 100));
            var (min, max) = random.Next(2) == 0 ? (0f, float.MaxValue) : (-float.MaxValue, 0f);
            AngularRow theirs = default, ours = default;
            limit(&theirs, &ia, &ib, bias, &axis, impulse, min, max);
            JointRows.Limit(ref ours, ia, ib, bias, axis, impulse, min, max);
            Assert.Equal(Bytes(theirs), Bytes(ours));
        }
    }

    [Fact]
    public void AnAngularBlockMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var block = (delegate* unmanaged<AngularBlock*, Mat3*, Mat3*, Vec3*, Vec3*, Vec3*, float, void>)
            Vphysics2Oracle.At(module, 0x180206120);
        var random = new Random(43);
        for (var i = 0; i < Trials; i++)
        {
            var (ia, ib) = (Inertia(random), Inertia(random));
            var bias = V(random, 5);
            var soft = random.Next(3) == 0 ? default : V(random, 0.5);
            var impulse = V(random, 100);
            var max = F(random, 0, 1000);
            AngularBlock theirs = default, ours = default;
            block(&theirs, &ia, &ib, &bias, &soft, &impulse, max);
            JointRows.Angular(ref ours, ia, ib, bias, soft, impulse, max);
            Assert.Equal(Bytes(theirs), Bytes(ours));
        }
    }

    [Fact]
    public void APointBlockMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var block = (delegate* unmanaged<PointBlock*, float, Mat3*, Vec3*, float, Mat3*, Vec3*, Vec3*, void>)
            Vphysics2Oracle.At(module, 0x1802064a0);
        var random = new Random(44);
        for (var i = 0; i < Trials; i++)
        {
            var (ia, ib) = (Inertia(random), Inertia(random));
            var (ra, rb, impulse) = (V(random, 20), V(random, 20), V(random, 100));
            var (ma, mb) = (InvMass(random), InvMass(random));
            PointBlock theirs = default, ours = default;
            block(&theirs, ma, &ia, &ra, mb, &ib, &rb, &impulse);
            JointRows.Point(ref ours, ma, ia, ra, mb, ib, rb, impulse);
            Assert.Equal(Bytes(theirs), Bytes(ours));
        }
    }

    [Fact]
    public void TheConicalPrepareMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var prepare = (delegate* unmanaged<ConicalJointState*, byte**, SolverBody*, float, byte, int>)
            Vphysics2Oracle.At(module, 0x180229870);
        var random = new Random(45);
        var joint = (ConicalJointState*)NativeMemory.AlignedAlloc((nuint)sizeof(ConicalJointState), 16);
        var bodies = (SolverBody*)NativeMemory.AlignedAlloc((nuint)(2 * sizeof(SolverBody)), 16);
        var stream = (byte*)NativeMemory.AlignedAlloc(0x400, 16);
        try
        {
            for (var i = 0; i < Trials; i++)
            {
                var j = Conical(random);
                var a = Body(random);
                var b = Body(random);
                var header = new JointHeader
                {
                    Type = 0, BodyA = 0, BodyB = 1,
                    ScaleA = random.Next(6) == 0 ? 0f : 1f, ScaleB = random.Next(6) == 0 ? 0f : 1f,
                };
                var dt = random.Next(2) == 0 ? 1f / 90 : F(random, 0.001, 0.05);
                var warm = random.Next(4) != 0;

                *joint = j;
                bodies[0] = a;
                bodies[1] = b;
                NativeMemory.Clear(stream, 0x400);
                *(JointHeader*)stream = header;
                var cursor = stream;
                var theirType = prepare(joint, &cursor, bodies, dt, (byte)(warm ? 1 : 0));
                Assert.Equal(0x14 + 0x1d4, (int)(cursor - stream));

                var ours = j;
                var record = default(ConicalRecord);
                Span<SolverBody> mine = [a, b];
                var ourType = ConicalJoint.Prepare(ref ours, header, ref record, mine, dt, warm);
                Assert.Equal(theirType, ourType);
                Assert.Equal(Convert.ToHexString(new ReadOnlySpan<byte>(stream + 0x14, 0x1d4)), Convert.ToHexString(Bytes(record)));
                Assert.Equal(Bytes(bodies[0]), Bytes(mine[0]));
                Assert.Equal(Bytes(bodies[1]), Bytes(mine[1]));
                Assert.Equal(Bytes(*joint), Bytes(ours));
            }
        }
        finally
        {
            NativeMemory.AlignedFree(joint);
            NativeMemory.AlignedFree(bodies);
            NativeMemory.AlignedFree(stream);
        }
    }

    [Fact]
    public void TheConicalVelocityIterationAndStoreMatch()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var prepare = (delegate* unmanaged<ConicalJointState*, byte**, SolverBody*, float, byte, int>)
            Vphysics2Oracle.At(module, 0x180229870);
        var solve = (delegate* unmanaged<byte**, SolverBody*, void>)Vphysics2Oracle.At(module, 0x18022c860);
        var store = (delegate* unmanaged<ConicalJointState*, byte**, void>)Vphysics2Oracle.At(module, 0x18022c390);
        var random = new Random(46);
        var joint = (ConicalJointState*)NativeMemory.AlignedAlloc((nuint)sizeof(ConicalJointState), 16);
        var bodies = (SolverBody*)NativeMemory.AlignedAlloc((nuint)(2 * sizeof(SolverBody)), 16);
        var stream = (byte*)NativeMemory.AlignedAlloc(0x400, 16);
        var skipped = 0;
        try
        {
            for (var i = 0; i < Trials; i++)
            {
                *joint = Conical(random);
                bodies[0] = Body(random);
                bodies[1] = Body(random);
                NativeMemory.Clear(stream, 0x400);
                var header = new JointHeader { BodyA = 0, BodyB = 1, ScaleA = 1f, ScaleB = random.Next(6) == 0 ? 0f : 1f };
                *(JointHeader*)stream = header;
                var cursor = stream;
                ((JointHeader*)stream)->Type = prepare(joint, &cursor, bodies, 1f / 90, 1);
                // Different velocities for the iteration than the prepare saw.
                bodies[0].V = V(random, 200);
                bodies[0].W = V(random, 10);
                bodies[1].W = V(random, 10);

                var record = *(ConicalRecord*)(stream + 0x14);
                // A spring between two bodies with no inertia has infinite
                // softness and a NaN block (the wrapper never prepares such a
                // pair); a NaN's sign follows operand order, which the JIT
                // may swap, so such trials stop.
                if (HasNaN(Bytes(record)))
                {
                    skipped++;
                    continue;
                }
                Span<SolverBody> mine = [bodies[0], bodies[1]];
                var ours = *joint;
                for (var k = 0; k < 3; k++)
                {
                    cursor = stream;
                    solve(&cursor, bodies);
                    Assert.Equal(0x1e8, (int)(cursor - stream));
                    ConicalJoint.SolveVelocity(ref record, header, mine);
                    Assert.Equal(Convert.ToHexString(new ReadOnlySpan<byte>(stream + 0x14, 0x1d4)), Convert.ToHexString(Bytes(record)));
                    Assert.Equal(Bytes(bodies[0]), Bytes(mine[0]));
                    Assert.Equal(Bytes(bodies[1]), Bytes(mine[1]));
                }
                cursor = stream;
                store(joint, &cursor);
                ConicalJoint.StoreImpulses(ref ours, record);
                Assert.Equal(Bytes(*joint), Bytes(ours));
            }
        }
        finally
        {
            NativeMemory.AlignedFree(joint);
            NativeMemory.AlignedFree(bodies);
            NativeMemory.AlignedFree(stream);
        }
        Assert.True(skipped < Trials / 20, $"{skipped} prepared records held a NaN");
    }

    [Fact]
    public void TheConicalPositionIterationMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var solve = (delegate* unmanaged<ConicalJointState*, byte**, SolverBody*, void>)Vphysics2Oracle.At(module, 0x18022ab10);
        var random = new Random(47);
        var joint = (ConicalJointState*)NativeMemory.AlignedAlloc((nuint)sizeof(ConicalJointState), 16);
        var bodies = (SolverBody*)NativeMemory.AlignedAlloc((nuint)(2 * sizeof(SolverBody)), 16);
        var stream = (byte*)NativeMemory.AlignedAlloc(0x400, 16);
        int corrected = 0, skipped = 0;
        try
        {
            for (var i = 0; i < Trials; i++)
            {
                var j = Conical(random);
                // Keep the anchors close and the limits often violated, as a joint in a settle is.
                if (random.Next(2) == 0)
                    j.Anchor2 = V(random, 4);
                *joint = j;
                bodies[0] = Body(random);
                bodies[1] = Body(random);
                if (random.Next(2) == 0)
                    bodies[1].Position = new(bodies[0].Position.X + F(random, -10, 10), bodies[0].Position.Y + F(random, -10, 10), bodies[0].Position.Z + F(random, -10, 10));
                for (var k = 0; k < 2; k++)
                    if (random.Next(5) == 0)
                    {
                        bodies[k].InfiniteInertia = 1;
                        bodies[k].InfiniteMass = (byte)random.Next(2);
                    }
                NativeMemory.Clear(stream, 0x400);
                var header = new JointHeader { Type = ConicalJoint.RecordType, BodyA = 0, BodyB = 1, ScaleA = 1f, ScaleB = random.Next(6) == 0 ? 0f : 1f };
                *(JointHeader*)stream = header;
                Span<SolverBody> mine = [bodies[0], bodies[1]];
                var before = Bytes(bodies[0]);
                for (var k = 0; k < 2; k++)
                {
                    var cursor = stream;
                    solve(joint, &cursor, bodies);
                    Assert.Equal(0x1e8, (int)(cursor - stream));
                    // A NaN's sign follows operand order, which the JIT may
                    // swap, and atan2f's NaN path is not ported; a settle
                    // never carries one, so such trials stop.
                    if (HasNaN(bodies[0]) || HasNaN(bodies[1]))
                    {
                        skipped++;
                        break;
                    }
                    ConicalJoint.SolvePosition(ref j, header, mine);
                    Assert.Equal(Bytes(bodies[0]), Bytes(mine[0]));
                    Assert.Equal(Bytes(bodies[1]), Bytes(mine[1]));
                }
                if (!Bytes(bodies[0]).AsSpan().SequenceEqual(before))
                    corrected++;
            }
        }
        finally
        {
            NativeMemory.AlignedFree(joint);
            NativeMemory.AlignedFree(bodies);
            NativeMemory.AlignedFree(stream);
        }
        // NaN comes from a zero effective mass (no inertia on either side), which the wrapper never prepares.
        Assert.True(corrected > Trials / 2 && skipped < Trials / 10, $"{corrected} trials moved a body, {skipped} went NaN");
    }

    internal static ConicalJointState Conical(Random random)
    {
        var j = new ConicalJointState
        {
            WorldIndex = 0, Type = 10, Flags = (ushort)(random.Next(4) == 0 ? 4 : 0),
            Frame1 = UnitQuat(random), Frame2 = UnitQuat(random),
            Anchor1 = V(random, 15), Anchor2 = V(random, 15), Size = 0x1e8,
            Target = random.Next(4) == 0 ? Quat.Identity : UnitQuat(random), Mode = random.Next(4),
            Frequency = random.Next(5) == 0 ? 0f : F(random, 0, 20), DampingRatio = F(random, 0, 2),
            TargetVelocity = random.Next(3) == 0 ? default : V(random, 10),
            Plasticity = random.Next(2) == 0 ? 0f : F(random, 0, 5), PlasticThreshold = random.Next(4) == 0 ? 0f : F(random, 0, 0.5),
            MaxTorque = random.Next(5) == 0 ? 0f : F(random, 0, 50000),
            TwistLimited = (byte)random.Next(2), SwingLimited = (byte)random.Next(2),
        };
        j.TwistMin = F(random, -1.5, 0.2);
        j.TwistMax = F(random, j.TwistMin, 1.5);
        j.SwingMax = F(random, 0, 1);
        if (random.Next(3) != 0)
        {
            j.MotorImpulse = V(random, 100);
            j.TwistLowImpulse = F(random, 0, 100);
            j.TwistHighImpulse = F(random, -100, 0);
            j.SwingImpulse = F(random, -100, 0);
            j.LinearImpulse = V(random, 500);
        }
        return j;
    }

    internal static SolverBody Body(Random random)
    {
        var b = new SolverBody
        {
            V = V(random, 200), W = V(random, 10), WorldInvInertia = Inertia(random), LocalInvInertia = Inertia(random), Q = UnitQuat(random),
            LocalMassCenter = V(random, 5), Position = V(random, 500), InvMass = InvMass(random),
            Scale = random.Next(3) == 0 ? F(random, 0.5, 2) : 1f,
        };
        return b;
    }

    internal static float F(Random random, double lo, double hi) => (float)(lo + random.NextDouble() * (hi - lo));

    internal static Vec3 V(Random random, double r) => new(F(random, -r, r), F(random, -r, r), F(random, -r, r));

    private static Vec3 Unit(Random random)
    {
        var v = V(random, 1);
        var n = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return n < 1e-3f ? new(1, 0, 0) : new(v.X / n, v.Y / n, v.Z / n);
    }

    internal static Quat UnitQuat(Random random)
    {
        var q = new Quat(F(random, -1, 1), F(random, -1, 1), F(random, -1, 1), F(random, -1, 1));
        var n = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        return n < 1e-3f ? Quat.Identity : new(q.X / n, q.Y / n, q.Z / n, q.W / n);
    }

    internal static float InvMass(Random random) => random.Next(8) == 0 ? 0f : F(random, 0.001, 1);

    internal static Mat3 Inertia(Random random)
    {
        if (random.Next(8) == 0)
            return default;
        var m = new Mat3();
        for (var r = 0; r < 3; r++)
            for (var c = r; c < 3; c++)
            {
                var v = r == c ? F(random, 0.0001, 0.05) : F(random, -0.005, 0.005);
                m[3 * r + c] = v;
                m[3 * c + r] = random.Next(4) == 0 ? v * 1.0000001f : v;
            }
        return m;
    }

    internal static bool HasNaN(in SolverBody b) => HasNaN(Bytes(b).AsSpan(0, 0xb0));

    internal static bool HasNaN(ReadOnlySpan<byte> bytes)
    {
        var floats = MemoryMarshal.Cast<byte, float>(bytes);
        foreach (var f in floats)
            if (float.IsNaN(f))
                return true;
        return false;
    }

    internal static byte[] Bytes<T>(T value) where T : unmanaged
        => MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)).ToArray();
}
