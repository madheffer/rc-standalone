using System.Runtime.InteropServices;

namespace Source2.Compiler.Simulation;

/// <summary>
/// A body's frame as the narrowphase takes it (0x30 bytes): the rotation matrix
/// of its orientation, in <see cref="RnMath.Matrix"/>'s element order, and the
/// body origin, which is the centre of mass less the rotated, scaled mass-centre
/// offset.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct RnTransform
{
    public Mat3 R;
    public Vec3 T;

    /// <summary>
    /// The frame the collide worker builds for a body (FUN_1801ecfb0): from the
    /// orientation, the centre of mass and the scale times the local mass centre.
    /// </summary>
    public static RnTransform Of(Quat q, Vec3 centreOfMass, Vec3 scaledMassCentre)
    {
        var offset = RnMath.Rotate(q, scaledMassCentre);
        return new RnTransform
        {
            R = RnMath.Matrix(q),
            T = new(centreOfMass.X - offset.X, centreOfMass.Y - offset.Y, centreOfMass.Z - offset.Z),
        };
    }

    /// <summary>The frame of a body in the world, from its state.</summary>
    public static RnTransform Of(in RnBodyState b) => Of(
        b.Orientation, b.Position,
        new Vec3(b.Scale * b.LocalMassCenter.X, b.Scale * b.LocalMassCenter.Y, b.Scale * b.LocalMassCenter.Z));
}
