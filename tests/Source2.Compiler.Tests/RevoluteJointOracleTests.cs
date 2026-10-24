using System.Runtime.InteropServices;
using Source2.Compiler.Simulation;
using Xunit;
using static Source2.Compiler.Tests.JointSolverOracleTests;

namespace Source2.Compiler.Tests;

/// <summary>
/// CRnRevoluteJoint's solver functions, ported, against vphysics2's own on the
/// same random joints and solver bodies; every output byte must match.
/// </summary>
public unsafe class RevoluteJointOracleTests
{
    private const int Trials = 20000;

    // The record follows its 0x14-byte header.
    private const int Stride = 0x14 + 0x198;

    [Fact]
    public void ThePrepareMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var prepare = (delegate* unmanaged<RevoluteJointState*, byte**, SolverBody*, float, byte, int>)
            Vphysics2Oracle.At(module, 0x180216240);
        var random = new Random(51);
        var joint = (RevoluteJointState*)NativeMemory.AlignedAlloc((nuint)sizeof(RevoluteJointState), 16);
        var bodies = (SolverBody*)NativeMemory.AlignedAlloc((nuint)(2 * sizeof(SolverBody)), 16);
        var stream = (byte*)NativeMemory.AlignedAlloc(0x400, 16);
        var modes = new int[4];
        try
        {
            for (var i = 0; i < Trials; i++)
            {
                var j = Revolute(random);
                var a = Body(random);
                var b = Body(random);
                var header = new JointHeader
                {
                    Type = 0, BodyA = 0, BodyB = 1,
                    ScaleA = random.Next(6) == 0 ? 0f : 1f, ScaleB = random.Next(6) == 0 ? 0f : 1f,
                };
                var dt = random.Next(2) == 0 ? 1f / 90 : F(random, 0.001, 0.05);
                var warm = random.Next(4) != 0;
                modes[j.Mode]++;

                *joint = j;
                bodies[0] = a;
                bodies[1] = b;
                NativeMemory.Clear(stream, 0x400);
                *(JointHeader*)stream = header;
                var cursor = stream;
                var theirType = prepare(joint, &cursor, bodies, dt, (byte)(warm ? 1 : 0));
                Assert.Equal(Stride, (int)(cursor - stream));

                var ours = j;
                var record = default(RevoluteRecord);
                Span<SolverBody> mine = [a, b];
                var ourType = RevoluteJoint.Prepare(ref ours, header, ref record, mine, dt, warm);
                Assert.Equal(theirType, ourType);
                Same(new ReadOnlySpan<byte>(stream + 0x14, 0x198).ToArray(), Bytes(record), $"record, trial {i}");
                Same(Bytes(bodies[0]), Bytes(mine[0]), $"body A, trial {i}");
                Same(Bytes(bodies[1]), Bytes(mine[1]), $"body B, trial {i}");
                Same(Bytes(*joint), Bytes(ours), $"joint, trial {i}");
            }
        }
        finally
        {
            NativeMemory.AlignedFree(joint);
            NativeMemory.AlignedFree(bodies);
            NativeMemory.AlignedFree(stream);
        }
        Assert.All(modes, n => Assert.True(n > Trials / 8));
    }

    [Fact]
    public void TheVelocityIterationAndStoreMatch()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var prepare = (delegate* unmanaged<RevoluteJointState*, byte**, SolverBody*, float, byte, int>)
            Vphysics2Oracle.At(module, 0x180216240);
        var solve = (delegate* unmanaged<byte**, SolverBody*, void>)Vphysics2Oracle.At(module, 0x18021bcd0);
        var store = (delegate* unmanaged<RevoluteJointState*, byte**, void>)Vphysics2Oracle.At(module, 0x18021ac10);
        var random = new Random(52);
        var joint = (RevoluteJointState*)NativeMemory.AlignedAlloc((nuint)sizeof(RevoluteJointState), 16);
        var bodies = (SolverBody*)NativeMemory.AlignedAlloc((nuint)(2 * sizeof(SolverBody)), 16);
        var stream = (byte*)NativeMemory.AlignedAlloc(0x400, 16);
        try
        {
            for (var i = 0; i < Trials; i++)
            {
                *joint = Revolute(random);
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

                var record = *(RevoluteRecord*)(stream + 0x14);
                Span<SolverBody> mine = [bodies[0], bodies[1]];
                var ours = *joint;
                for (var k = 0; k < 3; k++)
                {
                    cursor = stream;
                    solve(&cursor, bodies);
                    Assert.Equal(Stride, (int)(cursor - stream));
                    RevoluteJoint.SolveVelocity(ref record, header, mine);
                    Same(new ReadOnlySpan<byte>(stream + 0x14, 0x198).ToArray(), Bytes(record), $"record, trial {i}");
                    Same(Bytes(bodies[0]), Bytes(mine[0]), $"body A, trial {i}");
                    Same(Bytes(bodies[1]), Bytes(mine[1]), $"body B, trial {i}");
                }
                cursor = stream;
                store(joint, &cursor);
                Assert.Equal(Stride, (int)(cursor - stream));
                RevoluteJoint.StoreImpulses(ref ours, record);
                Same(Bytes(*joint), Bytes(ours), $"joint, trial {i}");
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
    public void ThePositionIterationMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var solve = (delegate* unmanaged<RevoluteJointState*, byte**, SolverBody*, void>)Vphysics2Oracle.At(module, 0x180219190);
        var random = new Random(53);
        var joint = (RevoluteJointState*)NativeMemory.AlignedAlloc((nuint)sizeof(RevoluteJointState), 16);
        var bodies = (SolverBody*)NativeMemory.AlignedAlloc((nuint)(2 * sizeof(SolverBody)), 16);
        var stream = (byte*)NativeMemory.AlignedAlloc(0x400, 16);
        int corrected = 0, skipped = 0;
        try
        {
            for (var i = 0; i < Trials; i++)
            {
                var j = Revolute(random);
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
                var header = new JointHeader { Type = RevoluteJoint.RecordType, BodyA = 0, BodyB = 1, ScaleA = 1f, ScaleB = random.Next(6) == 0 ? 0f : 1f };
                *(JointHeader*)stream = header;
                Span<SolverBody> mine = [bodies[0], bodies[1]];
                var before = Bytes(bodies[0]);
                for (var k = 0; k < 2; k++)
                {
                    var cursor = stream;
                    solve(joint, &cursor, bodies);
                    Assert.Equal(Stride, (int)(cursor - stream));
                    // As for the conical joint: a NaN's sign follows operand
                    // order and atan2f's NaN path is not ported; a settle
                    // never carries one, so such trials stop.
                    if (HasNaN(bodies[0]) || HasNaN(bodies[1]))
                    {
                        skipped++;
                        break;
                    }
                    RevoluteJoint.SolvePosition(ref j, header, mine);
                    Same(Bytes(bodies[0]), Bytes(mine[0]), $"body A, trial {i}");
                    Same(Bytes(bodies[1]), Bytes(mine[1]), $"body B, trial {i}");
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
        Assert.True(corrected > Trials / 2 && skipped < Trials / 10, $"{corrected} trials moved a body, {skipped} went NaN");
    }

    [Fact]
    public void ScalingTheImpulsesMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var scale = (delegate* unmanaged<RevoluteJointState*, float, void>)Vphysics2Oracle.At(module, 0x18021ac80);
        var random = new Random(54);
        var joint = (RevoluteJointState*)NativeMemory.AlignedAlloc((nuint)sizeof(RevoluteJointState), 16);
        try
        {
            for (var i = 0; i < Trials; i++)
            {
                var j = Revolute(random);
                var s = random.Next(3) == 0 ? 0f : F(random, -2, 2);
                *joint = j;
                scale(joint, s);
                RevoluteJoint.ScaleImpulses(ref j, s);
                Assert.Equal(Bytes(*joint), Bytes(j));
            }
        }
        finally
        {
            NativeMemory.AlignedFree(joint);
        }
    }

    /// <summary>Equal bytes, or a failure naming the first differing offset and both floats there.</summary>
    private static void Same(byte[] theirs, byte[] ours, string what)
    {
        for (var o = 0; o < theirs.Length; o++)
            if (theirs[o] != ours[o])
            {
                var at = o & ~3;
                Assert.Fail($"{what}: first difference at 0x{at:x}, vphysics2 {BitConverter.ToSingle(theirs, at):R} ours {BitConverter.ToSingle(ours, at):R}");
            }
    }

    /// <summary>A random revolute joint: every mode, with and without a limit, the linear and angular parts sometimes off.</summary>
    internal static RevoluteJointState Revolute(Random random)
    {
        var j = new RevoluteJointState
        {
            WorldIndex = 0, Type = 3, Flags = (ushort)((random.Next(4) == 0 ? 4 : 0) | (random.Next(6) == 0 ? 8 : 0)),
            Frame1 = UnitQuat(random), Frame2 = UnitQuat(random),
            Anchor1 = V(random, 15), Anchor2 = V(random, 15), Size = 0x1ac,
            Mode = random.Next(4),
            TargetAngle = F(random, -3, 3),
            Frequency = random.Next(5) == 0 ? 0f : F(random, 0, 20), DampingRatio = F(random, 0, 2),
            TargetSpeed = random.Next(3) == 0 ? 0f : F(random, -10, 10),
            MaxTorque = random.Next(5) == 0 ? 0f : F(random, 0, 50000),
            Plasticity = random.Next(2) == 0 ? 0f : F(random, 0, 5), PlasticThreshold = F(random, 0, 0.5),
            Limited = (byte)random.Next(2),
        };
        j.Min = F(random, -3, 0.2);
        j.Max = F(random, j.Min, 3);
        if (random.Next(3) != 0)
        {
            j.MotorImpulse = F(random, -100, 100);
            j.LowImpulse = F(random, 0, 100);
            j.HighImpulse = F(random, -100, 0);
            j.HingeImpulse1 = F(random, -100, 100);
            j.HingeImpulse2 = F(random, -100, 100);
            j.LinearImpulse = V(random, 500);
        }
        return j;
    }
}
