using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Source2.Compiler.Simulation;

/// <summary>
/// A contact point as the narrowphase leaves it in the contact's cache (0x28
/// bytes): where it sits on each body, and the normal impulse it carried out of
/// the last step, which warm starts the next.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x28)]
public struct CachedPoint
{
    [FieldOffset(0x00)] public Vec3 LocalA;
    [FieldOffset(0x0c)] public Vec3 LocalB;
    [FieldOffset(0x18)] public float Impulse;
    [FieldOffset(0x1c)] public int Feature;

    /// <summary>The triangle or child shape it touches; -1 for the shape itself.</summary>
    [FieldOffset(0x20)] public int SubShape;
    [FieldOffset(0x24)] public int Reserved;
}

/// <summary>
/// A manifold in the contact's cache (0xe0 bytes): up to four points sharing a
/// normal, the friction anchor at their centre, and the friction and twist
/// impulses carried between steps.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0xe0)]
public struct CachedManifold
{
    [FieldOffset(0x00)] public int PointCount;

    /// <summary>The world friction anchor.</summary>
    [FieldOffset(0x04)] public Vec3 Centre;

    /// <summary>The normal, from A to B.</summary>
    [FieldOffset(0x10)] public Vec3 Normal;
    [FieldOffset(0x1c)] public float TwistImpulse;
    [FieldOffset(0x20)] public Vec3 T1;
    [FieldOffset(0x2c)] public float Impulse1;
    [FieldOffset(0x30)] public Vec3 T2;
    [FieldOffset(0x3c)] public float Impulse2;
    [FieldOffset(0x40)] public CachedPoint P0;
    [FieldOffset(0x68)] public CachedPoint P1;
    [FieldOffset(0x90)] public CachedPoint P2;
    [FieldOffset(0xb8)] public CachedPoint P3;

    [UnscopedRef]
    public Span<CachedPoint> Points => MemoryMarshal.CreateSpan(ref P0, 4);
}
