using System.Runtime.InteropServices;

namespace Source2.Compiler.Simulation;

/// <summary>
/// The per-step copy of a body the island solver works on (0xC0 bytes), laid
/// out as vphysics2 lays it out. It is built from the body at the start of a
/// solve (FUN_1801b6000), integrated and solved in place, and written back
/// at the end (FUN_180313990).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0xC0)]
public struct SolverBody
{
    /// <summary>Linear velocity, already multiplied by the body's time scale.</summary>
    [FieldOffset(0x00)] public Vec3 V;

    /// <summary>World angular velocity, times the time scale.</summary>
    [FieldOffset(0x0c)] public Vec3 W;

    [FieldOffset(0x18)] public Mat3 WorldInvInertia;
    [FieldOffset(0x3c)] public Mat3 LocalInvInertia;
    [FieldOffset(0x60)] public Quat Q;

    /// <summary>The local mass centre times the body's scale.</summary>
    [FieldOffset(0x70)] public Vec3 LocalMassCenter;

    [FieldOffset(0x7c)] public Vec3 Position;
    [FieldOffset(0x88)] public float InvMass;
    [FieldOffset(0x8c)] public float Scale;

    /// <summary>Velocities before gravity and forces; restitution reads them.</summary>
    [FieldOffset(0x90)] public Vec3 V0;
    [FieldOffset(0x9c)] public Vec3 W0;

    [FieldOffset(0xa8)] public float SleepTimer;
    [FieldOffset(0xac)] public float TimeScale;
    [FieldOffset(0xb0)] public float FrictionScale;
    [FieldOffset(0xb4)] public int SolvePriority;
    [FieldOffset(0xb8)] public byte SleepRequest;
    [FieldOffset(0xb9)] public byte SleepAllowed;
    [FieldOffset(0xba)] public byte BodyType;
    [FieldOffset(0xbb)] public byte InfiniteMass;
    [FieldOffset(0xbc)] public byte InfiniteInertia;
    [FieldOffset(0xbd)] public byte ReadyToSleep;

    /// <summary>0 when the body touches another dynamic body; gates restitution.</summary>
    [FieldOffset(0xbe)] public byte NoDynamicContact;
}
