using System.Runtime.InteropServices;

namespace Source2.Compiler.Simulation;

/// <summary>
/// The simulated half of a Rubikon rigid body (CRnBody), laid out byte for byte
/// the way vphysics2 keeps it, so a body captured out of a live compile loads
/// straight in and Valve's own functions can run on the same memory in tests.
///
/// <para>Only the fields the step reads or writes are named. The pointers Valve
/// keeps in the gaps (world, island, contact edges) have no meaning here; the
/// port keeps those links beside the state. Offsets were read from the
/// solver body build, the integrators and the writeback, and every float field
/// was confirmed against bodies dumped from atixref's settle.</para>
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x280)]
public struct RnBodyState
{
    /// <summary>Index in the world's active list; -1 when asleep or static.</summary>
    [FieldOffset(0x18)] public int ActiveIndex;

    /// <summary>Flags; bit 7 of the low byte asks the island to sleep.</summary>
    [FieldOffset(0x4a)] public ushort Flags4A;

    /// <summary>
    /// The colours this body's constraints hold in a graph-coloured island
    /// (FUN_1802ca570): joints bits 0..9, type-1 10..12, contacts between two
    /// dynamic bodies 13..28, other contacts 29..31. Only kept on dynamic bodies.
    /// </summary>
    [FieldOffset(0x50)] public uint ColourMask;

    /// <summary>0 static, 1 kinematic, 2 dynamic.</summary>
    [FieldOffset(0x54)] public int BodyType;

    /// <summary>
    /// The head of the body's joint edge list (tagged; nodes keep their bodies
    /// at +0x18/+0x20, an enabled byte at +0x41 and next links at +0x48/+0x50).
    /// FUN_1801b6000 walks it for NoDynamicContact. Joints are not ported.
    /// </summary>
    [FieldOffset(0x70)] public nint JointHead;

    /// <summary>A kinematic body's move-to-target controller (<see cref="KinematicTarget"/>), 0 for none.</summary>
    [FieldOffset(0x80)] public nint Controller;

    [FieldOffset(0x88)] public float Scale;
    [FieldOffset(0x90)] public float InertiaScale;
    [FieldOffset(0x94)] public float GravityScale;
    [FieldOffset(0x98)] public float FrictionScale;
    [FieldOffset(0x9c)] public float TimeScale;
    [FieldOffset(0xa0)] public float InertiaDivisor;

    /// <summary>Local inverse inertia before scaling, row major.</summary>
    [FieldOffset(0xa4)] public Mat3 LocalInvInertia;

    [FieldOffset(0xc8)] public Vec3 LocalMassCenter;
    [FieldOffset(0xd4)] public float InvMass;

    /// <summary>World inverse inertia, row major.</summary>
    [FieldOffset(0xd8)] public Mat3 WorldInvInertia;

    /// <summary>World position of the centre of mass.</summary>
    [FieldOffset(0xfc)] public Vec3 Position;

    [FieldOffset(0x108)] public Vec3 LinearVelocity;
    [FieldOffset(0x114)] public Vec3 AngularVelocity;
    [FieldOffset(0x120)] public Quat Orientation;
    [FieldOffset(0x130)] public Quat PreviousOrientation;

    [FieldOffset(0x140)] public float LinearDamping;
    [FieldOffset(0x144)] public float AngularDamping;
    [FieldOffset(0x148)] public float LinearDrag;
    [FieldOffset(0x14c)] public float AngularDrag;

    [FieldOffset(0x158)] public Vec3 Force;
    [FieldOffset(0x164)] public Vec3 Torque;
    [FieldOffset(0x170)] public Vec3 SleepingForce;
    [FieldOffset(0x17c)] public Vec3 SleepingTorque;
    [FieldOffset(0x188)] public Vec3 LinearImpulse;
    [FieldOffset(0x194)] public Vec3 AngularImpulse;
    [FieldOffset(0x1a0)] public float LinearVelocityScale;
    [FieldOffset(0x1a4)] public float AngularVelocityScale;

    /// <summary>Drag per local axis (linear, then angular).</summary>
    [FieldOffset(0x1b4)] public Vec3 LinearDragAxes;
    [FieldOffset(0x1c0)] public Vec3 AngularDragAxes;

    /// <summary>Gravity for this body alone; used when any component is non-zero.</summary>
    [FieldOffset(0x1d0)] public Vec3 GravityOverride;

    [FieldOffset(0x1dc)] public Vec3 PreviousPosition;
    [FieldOffset(0x1e8)] public float Cleared1E8;
    /// <summary>
    /// The radii the continuous test (FUN_1801b8f40) weighs motion against:
    /// the inner one (half of it is the threshold) and the outer one (the
    /// reach of a rotation).
    /// </summary>
    [FieldOffset(0x1ec)] public float InnerRadius;
    [FieldOffset(0x1f0)] public float OuterRadius;

    [FieldOffset(0x1f4)] public float SleepTimer;

    /// <summary>
    /// The frame and velocities at the start of a step whose Solve is the
    /// first of a frame (FUN_18030d370 with ctx +0x38): the body origin,
    /// orientation and both velocities.
    /// </summary>
    [FieldOffset(0x1f8)] public Vec3 FrameOrigin;
    [FieldOffset(0x210)] public Quat FrameOrientation;
    [FieldOffset(0x220)] public Vec3 FrameLinearVelocity;
    [FieldOffset(0x22c)] public Vec3 FrameAngularVelocity;

    [FieldOffset(0x238)] public int MinVelocityIterations;
    [FieldOffset(0x23c)] public int MinPositionIterations;
    [FieldOffset(0x248)] public sbyte SolvePriority;

    /// <summary>
    /// Bit 1 sleeping allowed, bit 2 put to sleep, bit 5 drag enabled.
    /// </summary>
    [FieldOffset(0x249)] public byte Flags249;

    /// <summary>1 sends a fast dynamic body to the second continuous list (world +0xab0).</summary>
    [FieldOffset(0x24b)] public byte ContinuousList;
}

/// <summary>A float triple as vphysics2 stores it.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Vec3
{
    public float X, Y, Z;

    public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }

    public readonly override string ToString() => $"({X:R}, {Y:R}, {Z:R})";
}

/// <summary>A quaternion, x y z w.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Quat
{
    public float X, Y, Z, W;

    public Quat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }

    public static Quat Identity => new(0, 0, 0, 1);

    public readonly override string ToString() => $"({X:R}, {Y:R}, {Z:R}, {W:R})";
}

/// <summary>A row-major 3x3 matrix, M[3 * row + column].</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Mat3
{
    public float M0, M1, M2, M3, M4, M5, M6, M7, M8;

    public float this[int i]
    {
        readonly get => i switch
        {
            0 => M0, 1 => M1, 2 => M2, 3 => M3, 4 => M4, 5 => M5, 6 => M6, 7 => M7, 8 => M8,
            _ => throw new ArgumentOutOfRangeException(nameof(i)),
        };
        set
        {
            switch (i)
            {
                case 0: M0 = value; break;
                case 1: M1 = value; break;
                case 2: M2 = value; break;
                case 3: M3 = value; break;
                case 4: M4 = value; break;
                case 5: M5 = value; break;
                case 6: M6 = value; break;
                case 7: M7 = value; break;
                case 8: M8 = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(i));
            }
        }
    }
}
