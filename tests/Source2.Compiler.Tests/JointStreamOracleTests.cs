using System.Runtime.InteropServices;
using Source2.Compiler.Simulation;
using Xunit;
using static Source2.Compiler.Tests.JointSolverOracleTests;

namespace Source2.Compiler.Tests;

/// <summary>
/// The island's joint stream, ported (JointSolver), against vphysics2's own
/// wrapper and dispatches on random islands of mixed conical and revolute
/// joints: the prepare (FUN_18030f0b0), velocity iterations (FUN_180312650,
/// the last FUN_18030f860), position iterations (FUN_1803120f0) and the break
/// test (vtable +0x38). Every byte of the stream, bodies and joints must match.
/// </summary>
public unsafe class JointStreamOracleTests
{
    private const int Trials = 3000;

    private const ulong ConicalVtable = 0x1803e0a70;
    private const ulong RevoluteVtable = 0x1803e07e8;

    /// <summary>The island's joint list as RnSolver_SolveSerial passes it (island context +0x30).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x28)]
    private struct JointList
    {
        [FieldOffset(0x00)] public nint* Joints;
        [FieldOffset(0x08)] public int Count;
        [FieldOffset(0x10)] public nint Holder;
        [FieldOffset(0x18)] public byte* Stream;
        [FieldOffset(0x20)] public long Size;
    }

    [Fact]
    public void TheVtablesHoldThePortedFunctions()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        nint Slot(ulong vtable, int offset) => *(nint*)Vphysics2Oracle.At(module, vtable + (ulong)offset);
        Assert.Equal(Vphysics2Oracle.At(module, 0x180229870), Slot(ConicalVtable, 0x28));
        Assert.Equal(Vphysics2Oracle.At(module, 0x180205b40), Slot(ConicalVtable, 0x38));
        Assert.Equal(Vphysics2Oracle.At(module, 0x180229630), Slot(ConicalVtable, 0x08));
        Assert.Equal(Vphysics2Oracle.At(module, 0x180014e10), Slot(ConicalVtable, 0x10));
        Assert.Equal(Vphysics2Oracle.At(module, 0x180216240), Slot(RevoluteVtable, 0x28));
        Assert.Equal(Vphysics2Oracle.At(module, 0x180205b40), Slot(RevoluteVtable, 0x38));
        Assert.Equal(Vphysics2Oracle.At(module, 0x180206f10), Slot(RevoluteVtable, 0x08));
        Assert.Equal(Vphysics2Oracle.At(module, 0x180214250), Slot(RevoluteVtable, 0x10));
    }

    [Fact]
    public void TheStreamMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var prepare = (delegate* unmanaged<JointList*, nint, nint*, void>)Vphysics2Oracle.At(module, 0x18030f0b0);
        var velocity = (delegate* unmanaged<JointList*, void>)Vphysics2Oracle.At(module, 0x180312650);
        var lastVelocity = (delegate* unmanaged<JointList*, nint, nint*, void>)Vphysics2Oracle.At(module, 0x18030f860);
        var position = (delegate* unmanaged<JointList*, void>)Vphysics2Oracle.At(module, 0x1803120f0);
        var random = new Random(61);
        const int MaxBodies = 6, MaxJoints = 6;
        var bodies = (SolverBody*)NativeMemory.AlignedAlloc((nuint)(MaxBodies * sizeof(SolverBody)), 16);
        var holder = (nint*)NativeMemory.AlignedAlloc(0x20, 16);
        var array = (nint*)NativeMemory.AlignedAlloc(MaxJoints * 8, 16);
        var memory = new nint[MaxJoints];
        for (var k = 0; k < MaxJoints; k++)
            memory[k] = (nint)NativeMemory.AlignedAlloc(0x200, 16);
        var stream = (byte*)NativeMemory.AlignedAlloc(0x2000, 16);
        var (skipped, skips, scaled) = (0, 0, 0);
        try
        {
            for (var i = 0; i < Trials; i++)
            {
                var n = random.Next(2, MaxBodies + 1);
                var m = random.Next(1, MaxJoints + 1);
                var ours = new SolverBody[n];
                for (var k = 0; k < n; k++)
                    ours[k] = Flagged(random);
                var joints = new List<RnJoint>();
                for (var k = 0; k < m; k++)
                {
                    var j = random.Next(2) == 0 ? RnJoint.Of(Conical(random)) : RnJoint.Of(RevoluteJointOracleTests.Revolute(random));
                    ref var c = ref j.Common;
                    c.SolverA = random.Next(n);
                    c.SolverB = (c.SolverA + random.Next(1, n)) % n;
                    c.Priority = (sbyte)(random.Next(3) == 0 ? random.Next(-2, 3) : 0);
                    c.IgnoreInfiniteMass = (byte)(random.Next(5) == 0 ? 1 : 0);
                    c.IgnoreInfiniteInertia = (byte)(random.Next(5) == 0 ? 1 : 0);
                    joints.Add(j);
                }

                // Valve's copies: the same bytes behind the type's vtable.
                for (var k = 0; k < n; k++)
                    bodies[k] = ours[k];
                for (var k = 0; k < m; k++)
                {
                    Load(joints[k], memory[k], module);
                    array[k] = memory[k];
                }
                holder[2] = (nint)bodies;
                NativeMemory.Clear(stream, 0x2000);
                var list = new JointList { Joints = array, Count = m, Holder = (nint)holder, Stream = stream };
                var dt = random.Next(2) == 0 ? 1f / 90 : F(random, 0.001, 0.05);
                var bodiesPtr = (nint)bodies;
                var arrayPtr = (nint)array;
                var args = stackalloc nint[3];
                args[0] = (nint)(&arrayPtr);
                args[1] = (nint)(&bodiesPtr);
                args[2] = (nint)(&dt);
                prepare(&list, 0, args);

                // A NaN's sign follows operand order, which the JIT may swap: such trials stop.
                if (Enumerable.Range(0, n).Any(k => HasNaN(bodies[k])) || HasNaN(new ReadOnlySpan<byte>(stream, (int)list.Size)))
                {
                    skipped++;
                    continue;
                }
                var mine = JointSolver.Prepare(joints, ours, dt);
                Assert.Equal(mine.Length, (int)list.Size);
                Same(new ReadOnlySpan<byte>(stream, mine.Length).ToArray(), mine, $"prepared stream, trial {i}");
                Compare(bodies, ours, memory, joints, $"prepare, trial {i}");
                for (var at = 0; at < mine.Length;)
                {
                    var type = BitConverter.ToInt32(mine, at);
                    if (type == JointSolver.Skip)
                    {
                        skips++;
                        at += 4;
                        continue;
                    }
                    if (BitConverter.ToSingle(mine, at + 0xc) == 0f || BitConverter.ToSingle(mine, at + 0x10) == 0f)
                        scaled++;
                    at += 0x14 + (type == ConicalJoint.RecordType ? 0x1d4 : 0x198);
                }

                // Velocities the prepare did not see, then the iterations.
                for (var k = 0; k < n; k++)
                {
                    ours[k].V = V(random, 200);
                    ours[k].W = V(random, 10);
                    bodies[k] = ours[k];
                }
                var iterations = random.Next(1, 4);
                var nan = false;
                for (var it = 0; it < iterations && !nan; it++)
                {
                    var last = it == iterations - 1;
                    if (last)
                    {
                        var world = (nint)0;
                        var lastArgs = stackalloc nint[3];
                        var head = stackalloc nint[2];
                        head[0] = arrayPtr;
                        head[1] = m;
                        lastArgs[0] = (nint)head;
                        lastArgs[1] = (nint)(&bodiesPtr);
                        lastArgs[2] = (nint)(&world);
                        lastVelocity(&list, 0, lastArgs);
                    }
                    else
                    {
                        velocity(&list);
                    }
                    if (nan = Enumerable.Range(0, n).Any(k => HasNaN(bodies[k])))
                        break;
                    JointSolver.SolveVelocity(mine, joints, ours, last);
                    Same(new ReadOnlySpan<byte>(stream, mine.Length).ToArray(), mine, $"stream after velocity {it}, trial {i}");
                    Compare(bodies, ours, memory, joints, $"velocity {it}, trial {i}");
                }
                for (var it = 0; it < 2 && !nan; it++)
                {
                    position(&list);
                    if (nan = Enumerable.Range(0, n).Any(k => HasNaN(bodies[k])))
                        break;
                    JointSolver.SolvePosition(mine, joints, ours);
                    Compare(bodies, ours, memory, joints, $"position {it}, trial {i}");
                }
            }
        }
        finally
        {
            NativeMemory.AlignedFree(bodies);
            NativeMemory.AlignedFree(holder);
            NativeMemory.AlignedFree(array);
            foreach (var p in memory)
                NativeMemory.AlignedFree((void*)p);
            NativeMemory.AlignedFree(stream);
        }
        Assert.True(skipped < Trials / 10, $"{skipped} trials went NaN");
        Console.WriteLine($"joint stream: {Trials - skipped} trials compared, {skips} skipped records, {scaled} scaled headers");
        Assert.True(skips > 50 && scaled > 50, $"{skips} skipped records, {scaled} scaled headers");
    }

    [Fact]
    public void TheBreakTestMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var test = (delegate* unmanaged<nint, byte>)Vphysics2Oracle.At(module, 0x180205b40);
        var random = new Random(62);
        var memory = (nint)NativeMemory.AlignedAlloc(0x200, 16);
        var breaks = 0;
        try
        {
            for (var i = 0; i < 20000; i++)
            {
                var j = random.Next(2) == 0 ? RnJoint.Of(Conical(random)) : RnJoint.Of(RevoluteJointOracleTests.Revolute(random));
                ref var c = ref j.Common;
                c.BreakImpulse = random.Next(3) == 0 ? 0f : F(random, -100, 600);
                c.BreakAngularImpulse = random.Next(3) == 0 ? 0f : F(random, -100, 150);
                if (random.Next(50) == 0)
                    c.BreakImpulse = j.LinearImpulse();
                Load(j, memory, module);
                var theirs = test(memory) != 0;
                Assert.Equal(theirs, JointSolver.Breaks(j));
                breaks += theirs ? 1 : 0;
            }
        }
        finally
        {
            NativeMemory.AlignedFree((void*)memory);
        }
        Assert.InRange(breaks, 2000, 18000);
    }

    /// <summary>A random solver body whose infinite flags mostly follow its mass and inertia, with a solve priority.</summary>
    private static SolverBody Flagged(Random random)
    {
        var b = Body(random);
        b.InfiniteMass = (byte)((b.InvMass == 0f) ^ (random.Next(10) == 0) ? 1 : 0);
        b.InfiniteInertia = (byte)((b.WorldInvInertia.M0 == 0f) ^ (random.Next(10) == 0) ? 1 : 0);
        b.SolvePriority = random.Next(4) == 0 ? random.Next(3) : 0;
        if (random.Next(6) == 0)
        {
            b.InvMass = 0f;
            b.WorldInvInertia = default;
            b.InfiniteMass = b.InfiniteInertia = 1;
        }
        return b;
    }

    private static void Load(RnJoint j, nint memory, nint module)
    {
        NativeMemory.Clear((void*)memory, 0x200);
        var bytes = JointBytes(j);
        Marshal.Copy(bytes, 0, memory, bytes.Length);
        *(nint*)memory = Vphysics2Oracle.At(module, j.Type == RnJoint.ConicalType ? ConicalVtable : RevoluteVtable);
    }

    private static byte[] JointBytes(RnJoint j)
        => j.Type == RnJoint.ConicalType ? Bytes(j.Conical) : Bytes(j.Revolute);

    private static void Compare(SolverBody* bodies, SolverBody[] ours, nint[] memory, List<RnJoint> joints, string what)
    {
        for (var k = 0; k < ours.Length; k++)
            Same(Bytes(bodies[k]), Bytes(ours[k]), $"body {k}, {what}");
        for (var k = 0; k < joints.Count; k++)
        {
            var mine = JointBytes(joints[k]);
            var theirs = new byte[mine.Length];
            Marshal.Copy(memory[k], theirs, 0, theirs.Length);
            Same(theirs[8..], mine[8..], $"joint {k}, {what}");
        }
    }

    /// <summary>Equal bytes, or a failure naming the first differing offset and both floats there.</summary>
    private static void Same(byte[] theirs, byte[] ours, string what)
    {
        Assert.Equal(theirs.Length, ours.Length);
        for (var o = 0; o < theirs.Length; o++)
            if (theirs[o] != ours[o])
            {
                var at = o & ~3;
                Assert.Fail($"{what}: first difference at 0x{at:x}, vphysics2 {BitConverter.ToSingle(theirs, at):R} ours {BitConverter.ToSingle(ours, at):R}");
            }
    }
}
