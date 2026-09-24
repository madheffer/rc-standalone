using System.Numerics;
using System.Runtime.InteropServices;
using Source2.Compiler.Simulation;

namespace Source2.Compiler.Tests;

/// <summary>
/// Extra calls on <see cref="Vphysics2World"/> for the broadphase oracle:
/// the raw objects (CBroadphase, scratch, contact list) and the body calls
/// with a scale. Addresses are vphysics2 2026-09-24.
/// </summary>
internal static unsafe class Vphysics2WorldExtensions
{
    /// <summary>CRnWorld+0x110.</summary>
    public static byte* Broadphase(this Vphysics2World w) => *(byte**)(w.Rn + 0x110);

    /// <summary>CRnWorld+0x220: the per-step scratch; the hierarchy update is at +0, the pair query at +0x4E8.</summary>
    public static byte* Scratch(this Vphysics2World w) => *(byte**)(w.Rn + 0x220);

    /// <summary>CRnWorld+0xA10: the contacts Collide walks, in the order they were made.</summary>
    public static ReadOnlySpan<nint> ActiveContacts(this Vphysics2World w)
        => new(*(nint**)(w.Rn + 0xa18), *(int*)(w.Rn + 0xa10));

    /// <summary>Body slot 0x1B8 with a uniform scale (applied when it changes by 0.001 or more).</summary>
    public static void SetTransform(this Vphysics2World.Body body, Vector3 position, Quaternion orientation, float scale)
    {
        var xf = (float*)NativeMemory.AlignedAlloc(0x20, 0x10);
        xf[0] = position.X;
        xf[1] = position.Y;
        xf[2] = position.Z;
        xf[3] = scale;
        xf[4] = orientation.X;
        xf[5] = orientation.Y;
        xf[6] = orientation.Z;
        xf[7] = orientation.W;
        var set = (delegate* unmanaged<nint*, float*, void>)(*(nint**)body.Wrapper)[0x1b8 / 8];
        set(body.Wrapper, xf);
        NativeMemory.AlignedFree(xf);
    }

    /// <summary>Body slot 0x70 with a per-axis scale.</summary>
    public static nint AddMesh(this Vphysics2World.Body body, byte* mesh, Vector3 scale)
    {
        var add = (delegate* unmanaged<nint*, byte*, Vector3*, byte, void*, nint>)(*(nint**)body.Wrapper)[0x70 / 8];
        return add(body.Wrapper, mesh, &scale, 0, null);
    }

    /// <summary>The CRnShape behind an IPhysicsShape (+8).</summary>
    public static byte* RnShape(nint shape) => *(byte**)(shape + 8);

    /// <summary>A CRnBody's shapes (+0x60 count, +0x68 inline when the capacity is under 2).</summary>
    public static ReadOnlySpan<nint> Shapes(byte* body)
    {
        var count = *(int*)(body + 0x60);
        var alloc = *(uint*)(body + 0x64) & 0x7fffffff;
        if (alloc == 0)
            return default;
        var data = alloc < 2 ? (nint*)(body + 0x68) : *(nint**)(body + 0x68);
        return new(data, count);
    }
}
