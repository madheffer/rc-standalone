using System.Runtime.InteropServices;

namespace Source2.Compiler.Simulation;

/// <summary>
/// The CRnJoint header every joint type shares, at the offsets the solver's
/// joint wrapper (FUN_18030f0b0) and the break test (FUN_180205b40) read. The
/// base ctor (FUN_1802042b0) leaves +0x2a..+0x2c and both break thresholds 0.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0xb4)]
public struct JointCommon
{
    /// <summary>+0x08: index in the world's joint list, -1 out of the world.</summary>
    [FieldOffset(0x08)] public int WorldIndex;

    /// <summary>+0x0c: the joint type (3 revolute, 10 conical).</summary>
    [FieldOffset(0x0c)] public int Type;

    /// <summary>+0x28: bit 0 disabled, bit 1 collision between the bodies, bit 2 no linear part, bit 3 no angular part.</summary>
    [FieldOffset(0x28)] public ushort Flags;

    /// <summary>+0x2a: added to the bodies' solve priority difference.</summary>
    [FieldOffset(0x2a)] public sbyte Priority;

    [FieldOffset(0x2b)] public byte IgnoreInfiniteMass;
    [FieldOffset(0x2c)] public byte IgnoreInfiniteInertia;

    /// <summary>+0x38: index in its colour bucket; +0x3c: index in its island's joint list.</summary>
    [FieldOffset(0x38)] public int BucketIndex;
    [FieldOffset(0x3c)] public int IslandIndex;

    /// <summary>+0x40: the colour (0..9, 16 overflow, 0xff none); +0x41: an island edge.</summary>
    [FieldOffset(0x40)] public byte Colour;
    [FieldOffset(0x41)] public byte Active;

    /// <summary>+0x68/+0x6c: the island's solver body indices of body 1 and body 2.</summary>
    [FieldOffset(0x68)] public int SolverA;
    [FieldOffset(0x6c)] public int SolverB;

    /// <summary>+0xa8: the solver size estimate (the record with its header).</summary>
    [FieldOffset(0xa8)] public int Size;

    /// <summary>+0xac: the joint breaks when its linear impulse (vtable +8) is above this, if positive.</summary>
    [FieldOffset(0xac)] public float BreakImpulse;

    /// <summary>+0xb0: the joint breaks when its angular impulse (vtable +0x10) is above this, if positive.</summary>
    [FieldOffset(0xb0)] public float BreakAngularImpulse;
}

/// <summary>
/// A joint as the world holds it: the object's bytes as vphysics2 lays them
/// out, so the CRnJoint header sits at the same offsets for every type.
/// </summary>
public sealed class RnJoint
{
    public const int RevoluteType = 3;
    public const int ConicalType = 10;

    private readonly byte[] _memory;

    private RnJoint(byte[] memory) => _memory = memory;

    public static RnJoint Of(in ConicalJointState s)
    {
        var j = new RnJoint(new byte[Marshal.SizeOf<ConicalJointState>()]);
        j.Conical = s;
        return j;
    }

    public static RnJoint Of(in RevoluteJointState s)
    {
        var j = new RnJoint(new byte[Marshal.SizeOf<RevoluteJointState>()]);
        j.Revolute = s;
        return j;
    }

    public ref JointCommon Common => ref MemoryMarshal.AsRef<JointCommon>(_memory.AsSpan());

    /// <summary>+0x18 / +0x20: the two bodies (FUN_1802055f0 clears a disabled body, or both when they are one).</summary>
    public RnBody? Body1, Body2;

    /// <summary>+0x30: the island it is an edge of.</summary>
    public RnIsland? Island;

    public int Type => Common.Type;

    public ref ConicalJointState Conical => ref MemoryMarshal.AsRef<ConicalJointState>(_memory.AsSpan());

    public ref RevoluteJointState Revolute => ref MemoryMarshal.AsRef<RevoluteJointState>(_memory.AsSpan());

    /// <summary>The record the prepare writes after the 0x14-byte header (the type's vtable +0x28).</summary>
    internal int RecordSize => Type switch
    {
        ConicalType => Marshal.SizeOf<ConicalRecord>(),
        RevoluteType => Marshal.SizeOf<RevoluteRecord>(),
        _ => throw new NotSupportedException($"joint type {Type} is not ported"),
    };

    /// <summary>The accumulated impulses times a factor (vtable +0x18: FUN_18022c400 conical, FUN_18021ac80 revolute).</summary>
    public void ScaleImpulses(float s)
    {
        if (Type == RevoluteType)
        {
            RevoluteJoint.ScaleImpulses(ref Revolute, s);
            return;
        }
        ref var j = ref Conical;
        j.MotorImpulse = new(s * j.MotorImpulse.X, s * j.MotorImpulse.Y, s * j.MotorImpulse.Z);
        j.TwistLowImpulse = s * j.TwistLowImpulse;
        j.TwistHighImpulse = j.TwistHighImpulse * s;
        j.SwingImpulse = j.SwingImpulse * s;
        j.LinearImpulse = new(j.LinearImpulse.X * s, j.LinearImpulse.Y * s, s * j.LinearImpulse.Z);
    }

    /// <summary>|linear impulse| (vtable +8: FUN_180229630 conical, FUN_180206f10 revolute).</summary>
    internal float LinearImpulse()
    {
        var v = Type == ConicalType ? Conical.LinearImpulse : Revolute.LinearImpulse;
        return MathF.Sqrt(v.Z * v.Z + v.Y * v.Y + v.X * v.X);
    }

    /// <summary>The angular impulse (vtable +0x10): 0 for the conical joint (FUN_180014e10), the hinge rows' length for the revolute (FUN_180214250).</summary>
    internal float AngularImpulse()
    {
        if (Type == ConicalType)
            return 0f;
        ref var r = ref Revolute;
        return MathF.Sqrt(r.HingeImpulse2 * r.HingeImpulse2 + r.HingeImpulse1 * r.HingeImpulse1);
    }
}

/// <summary>
/// The island's joint stream as vphysics2 builds and walks it: one record per
/// joint in island order, each a <see cref="JointHeader"/> and the type's
/// record, or the 4-byte type 0x12 the iterations skip.
/// </summary>
public static class JointSolver
{
    /// <summary>The record of a joint whose two bodies are both infinite in a tested axis.</summary>
    public const int Skip = 0x12;

    private const int HeaderSize = 0x14;

    /// <summary>
    /// The wrapper (FUN_18030f0b0): per joint, type 0x12 when neither side can
    /// move it, else the header (solver indices, mass scales 1, the lower
    /// priority side not pushing the higher unless the higher is immovable)
    /// and the type's prepare (vtable +0x28, always warm), which returns the
    /// record type.
    /// </summary>
    public static byte[] Prepare(IReadOnlyList<RnJoint> joints, Span<SolverBody> bodies, float dt)
    {
        var size = 0;
        foreach (var j in joints)
            size += Immovable(j, bodies) ? 4 : HeaderSize + j.RecordSize;
        var stream = new byte[size];
        var at = 0;
        foreach (var j in joints)
        {
            ref var c = ref j.Common;
            if (Immovable(j, bodies))
            {
                MemoryMarshal.Write(stream.AsSpan(at), Skip);
                at += 4;
                continue;
            }
            ref readonly var a = ref bodies[c.SolverA];
            ref readonly var b = ref bodies[c.SolverB];
            ref var h = ref MemoryMarshal.AsRef<JointHeader>(stream.AsSpan(at, HeaderSize));
            h.BodyA = c.SolverA;
            h.BodyB = c.SolverB;
            h.ScaleA = 1f;
            h.ScaleB = 1f;
            var d = c.Priority - b.SolvePriority + a.SolvePriority;
            if (d != 0)
            {
                if (d < 1)
                {
                    if ((c.IgnoreInfiniteMass != 0 || a.InfiniteMass == 0) && (c.IgnoreInfiniteInertia != 0 || a.InfiniteInertia == 0))
                        h.ScaleB = 0f;
                }
                else if ((c.IgnoreInfiniteMass != 0 || b.InfiniteMass == 0) && (c.IgnoreInfiniteInertia != 0 || b.InfiniteInertia == 0))
                {
                    h.ScaleA = 0f;
                }
            }
            var record = stream.AsSpan(at + HeaderSize, j.RecordSize);
            h.Type = j.Type == RnJoint.ConicalType
                ? ConicalJoint.Prepare(ref j.Conical, h, ref MemoryMarshal.AsRef<ConicalRecord>(record), bodies, dt, warm: true)
                : RevoluteJoint.Prepare(ref j.Revolute, h, ref MemoryMarshal.AsRef<RevoluteRecord>(record), bodies, dt, warm: true);
            at += HeaderSize + record.Length;
        }
        return stream;
    }

    private static bool Immovable(RnJoint j, Span<SolverBody> bodies)
    {
        ref var c = ref j.Common;
        ref readonly var a = ref bodies[c.SolverA];
        ref readonly var b = ref bodies[c.SolverB];
        return (c.IgnoreInfiniteMass == 0 && a.InfiniteMass != 0 && b.InfiniteMass != 0)
            || (c.IgnoreInfiniteInertia == 0 && a.InfiniteInertia != 0 && b.InfiniteInertia != 0);
    }

    /// <summary>
    /// One velocity iteration over the stream (FUN_180312650). On the last
    /// (FUN_18030f860) each record's impulses are stored back to its joint, and
    /// every joint, skipped ones too, takes the break test; a broken joint goes
    /// in <paramref name="broken"/> (the world's list, world +0x88 +0x30).
    /// </summary>
    public static void SolveVelocity(byte[] stream, IReadOnlyList<RnJoint> joints, Span<SolverBody> bodies, bool last, List<RnJoint>? broken = null)
    {
        var at = 0;
        for (var i = 0; i < joints.Count; i++)
        {
            var type = MemoryMarshal.Read<int>(stream.AsSpan(at));
            var j = joints[i];
            switch (type)
            {
                case ConicalJoint.RecordType:
                {
                    var h = MemoryMarshal.AsRef<JointHeader>(stream.AsSpan(at, HeaderSize));
                    ref var rec = ref MemoryMarshal.AsRef<ConicalRecord>(stream.AsSpan(at + HeaderSize, Marshal.SizeOf<ConicalRecord>()));
                    ConicalJoint.SolveVelocity(ref rec, h, bodies);
                    if (last)
                        ConicalJoint.StoreImpulses(ref j.Conical, rec);
                    at += HeaderSize + Marshal.SizeOf<ConicalRecord>();
                    break;
                }
                case RevoluteJoint.RecordType:
                {
                    var h = MemoryMarshal.AsRef<JointHeader>(stream.AsSpan(at, HeaderSize));
                    ref var rec = ref MemoryMarshal.AsRef<RevoluteRecord>(stream.AsSpan(at + HeaderSize, Marshal.SizeOf<RevoluteRecord>()));
                    RevoluteJoint.SolveVelocity(ref rec, h, bodies);
                    if (last)
                        RevoluteJoint.StoreImpulses(ref j.Revolute, rec);
                    at += HeaderSize + Marshal.SizeOf<RevoluteRecord>();
                    break;
                }
                case Skip:
                    at += 4;
                    break;
                default:
                    throw new NotSupportedException($"joint record type {type} is not ported");
            }
            if (last && Breaks(j))
            {
                if (broken == null)
                    throw new NotSupportedException("a joint broke and no broken-joint list was given");
                broken.Add(j);
            }
        }
    }

    /// <summary>One position iteration over the stream (FUN_1803120f0), each record on the bodies' current poses.</summary>
    public static void SolvePosition(byte[] stream, IReadOnlyList<RnJoint> joints, Span<SolverBody> bodies)
    {
        var at = 0;
        for (var i = 0; i < joints.Count; i++)
        {
            var type = MemoryMarshal.Read<int>(stream.AsSpan(at));
            switch (type)
            {
                case ConicalJoint.RecordType:
                    ConicalJoint.SolvePosition(ref joints[i].Conical, MemoryMarshal.AsRef<JointHeader>(stream.AsSpan(at, HeaderSize)), bodies);
                    at += HeaderSize + Marshal.SizeOf<ConicalRecord>();
                    break;
                case RevoluteJoint.RecordType:
                    RevoluteJoint.SolvePosition(ref joints[i].Revolute, MemoryMarshal.AsRef<JointHeader>(stream.AsSpan(at, HeaderSize)), bodies);
                    at += HeaderSize + Marshal.SizeOf<RevoluteRecord>();
                    break;
                case Skip:
                    at += 4;
                    break;
                default:
                    throw new NotSupportedException($"joint record type {type} is not ported");
            }
        }
    }

    /// <summary>
    /// The break test (vtable +0x38, FUN_180205b40, both ported types): the
    /// linear impulse above a positive +0xac (a NaN breaks it too), or the
    /// angular impulse above a positive +0xb0.
    /// </summary>
    public static bool Breaks(RnJoint j)
    {
        ref var c = ref j.Common;
        var linear = false;
        if (c.BreakImpulse > 0f)
        {
            var v = j.LinearImpulse();
            linear = !(v < c.BreakImpulse || v == c.BreakImpulse);
        }
        var angular = false;
        if (c.BreakAngularImpulse > 0f)
        {
            var v = j.AngularImpulse();
            angular = c.BreakAngularImpulse <= v && v != c.BreakAngularImpulse;
        }
        return linear || angular;
    }
}
