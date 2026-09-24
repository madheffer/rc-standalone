using System.Runtime.InteropServices;

namespace Source2.Compiler.Simulation;

/// <summary>
/// The rows the contact prepare writes and every velocity iteration reads, in
/// vphysics2's layout. A contact's rows in the solver stream are a header, then
/// per manifold a <see cref="ManifoldRow"/> followed by its points'
/// <see cref="PointRow"/>s.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x18)]
public struct ContactHeader
{
    /// <summary>0 for a contact, 0x12 when both bodies are immovable and it is skipped.</summary>
    [FieldOffset(0x00)] public int Type;
    [FieldOffset(0x04)] public int BodyA;
    [FieldOffset(0x08)] public int BodyB;
    [FieldOffset(0x0c)] public float MassScaleA;
    [FieldOffset(0x10)] public float MassScaleB;
    [FieldOffset(0x14)] public int ManifoldCount;
}

/// <summary>One normal constraint (0x5c bytes).</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x5c)]
public struct PointRow
{
    [FieldOffset(0x00)] public Vec3 Normal;
    [FieldOffset(0x0c)] public Vec3 RA;
    [FieldOffset(0x18)] public Vec3 RB;
    [FieldOffset(0x24)] public float Bias;

    /// <summary>The soft constraint's gamma; 0 for a rigid contact.</summary>
    [FieldOffset(0x28)] public float Softness;

    /// <summary>1 / (K + gamma).</summary>
    [FieldOffset(0x2c)] public float Mass;
    [FieldOffset(0x30)] public float Impulse;
    [FieldOffset(0x34)] public float Friction;
    [FieldOffset(0x38)] public float InvMassA;
    [FieldOffset(0x3c)] public float InvMassB;

    /// <summary>IA (rA x n) and IB (rB x n).</summary>
    [FieldOffset(0x40)] public Vec3 AngularA;
    [FieldOffset(0x4c)] public Vec3 AngularB;

    /// <summary>The point's distance from the manifold centre, at least 1/32.</summary>
    [FieldOffset(0x58)] public float TwistArm;
}

/// <summary>A manifold's central friction (0x8c bytes).</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x8c)]
public struct FrictionRow
{
    [FieldOffset(0x00)] public Vec3 RA;
    [FieldOffset(0x0c)] public Vec3 RB;
    [FieldOffset(0x18)] public Vec3 T1;
    [FieldOffset(0x24)] public Vec3 T2;

    /// <summary>The inverse of the 2x2 tangent mass, row major.</summary>
    [FieldOffset(0x30)] public float K00;
    [FieldOffset(0x34)] public float K01;
    [FieldOffset(0x38)] public float K10;
    [FieldOffset(0x3c)] public float K11;

    [FieldOffset(0x40)] public float Bias1;
    [FieldOffset(0x44)] public float Bias2;
    [FieldOffset(0x48)] public float Impulse1;
    [FieldOffset(0x4c)] public float Impulse2;
    [FieldOffset(0x50)] public float Limit;
    [FieldOffset(0x54)] public float InvMassA;
    [FieldOffset(0x58)] public float InvMassB;
    [FieldOffset(0x5c)] public Vec3 AngularA1;
    [FieldOffset(0x68)] public Vec3 AngularA2;
    [FieldOffset(0x74)] public Vec3 AngularB1;
    [FieldOffset(0x80)] public Vec3 AngularB2;
}

/// <summary>A manifold's twist friction and its friction row (0xc8 bytes).</summary>
[StructLayout(LayoutKind.Explicit, Size = 0xc8)]
public struct ManifoldRow
{
    [FieldOffset(0x00)] public Vec3 Normal;
    [FieldOffset(0x0c)] public float TwistMass;
    [FieldOffset(0x10)] public float TwistBias;
    [FieldOffset(0x14)] public float TwistImpulse;
    [FieldOffset(0x18)] public float TwistLimit;
    [FieldOffset(0x1c)] public Vec3 TwistA;
    [FieldOffset(0x28)] public Vec3 TwistB;
    [FieldOffset(0x34)] public FrictionRow Friction;

    /// <summary>This record's size in bytes, points included.</summary>
    [FieldOffset(0xc0)] public int Size;
    [FieldOffset(0xc4)] public int PointCount;
}
