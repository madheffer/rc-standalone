namespace Source2.Compiler.Simulation;

/// <summary>
/// How the settle turns a resting body back into its entity's origin and angles
/// (PhysObj_WriteBackTransform FUN_18105b350 → PhysPart_GetEntityTransform
/// FUN_18105a410, then the node's SetOrigin/SetAngles).
///
/// <para>The body's origin (centre of mass less the rotated, scaled mass-centre
/// offset) and orientation are taken as a CTransform, concatenated with the
/// inverse of the model's first bind pose (identity when the model has none),
/// the result's quaternion turned into angles, and the angles cleaned up:
/// anything under 0.001 becomes 0, and a negative yaw gets 360 added.</para>
/// </summary>
public static class SettleWriteBack
{
    /// <summary>A CTransform: position, uniform scale, rotation.</summary>
    public readonly record struct Frame(Vec3 Position, float Scale, Quat Rotation);

    /// <summary>What the entity gets.</summary>
    public readonly record struct Pose(Vec3 Origin, Vec3 Angles);

    /// <summary>The entity pose of a settled single-body part.</summary>
    /// <param name="body">The resting body.</param>
    /// <param name="bindPose">The model's first bind pose, or null for none.</param>
    public static Pose EntityPose(in RnBodyState body, Frame? bindPose = null)
    {
        var bind = bindPose ?? new Frame(default, 1f, Quat.Identity);
        var frame = Concat(BodyFrame(body), Inverse(bind));
        return new Pose(frame.Position, CleanAngles(Angles(frame.Rotation)));
    }

    /// <summary>
    /// The body's frame as vphysics2's GetTransform gives it (0x1800123b0):
    /// the origin, the body's scale and its orientation.
    /// </summary>
    public static Frame BodyFrame(in RnBodyState b)
    {
        var s = b.Scale;
        var offset = RnMath.Rotate(b.Orientation, new Vec3(s * b.LocalMassCenter.X, s * b.LocalMassCenter.Y, s * b.LocalMassCenter.Z));
        return new Frame(new Vec3(b.Position.X - offset.X, b.Position.Y - offset.Y, b.Position.Z - offset.Z), s, b.Orientation);
    }

    /// <summary>
    /// CTransform concatenation (FUN_181253510): a's rotation and scale carry
    /// b's position; the rotations multiply and are renormalised.
    /// </summary>
    public static Frame Concat(Frame a, Frame b)
    {
        var p = RnMath.Rotate(a.Rotation, b.Position);
        return new Frame(
            new Vec3((p.X * a.Scale) + a.Position.X, (p.Y * a.Scale) + a.Position.Y, (p.Z * a.Scale) + a.Position.Z),
            a.Scale * b.Scale,
            RnMath.Normalize(RnMath.Mul(a.Rotation, b.Rotation)));
    }

    /// <summary>
    /// CTransform inverse at scale 1 (FUN_181253780): the conjugate over its
    /// DPPS length (1.19e-7 standing in for zero), and the position rotated by
    /// the unnormalised conjugate and negated as 0 - v.
    /// </summary>
    public static Frame Inverse(Frame f)
    {
        if (f.Scale != 1f)
            throw new NotSupportedException("inverting a scaled bind pose is not ported");
        float a = f.Rotation.X * -1f, b = f.Rotation.Y * -1f, c = f.Rotation.Z * -1f, w = f.Rotation.W * 1f;
        var v = f.Position;
        var ux = (v.Z * b) - (v.Y * c);
        ux += ux;
        var uy = (v.X * c) - (v.Z * a);
        uy += uy;
        var uz = (v.Y * a) - (v.X * b);
        uz += uz;
        var p = new Vec3(
            0f - (((uz * b) - (uy * c)) + ((w * ux) + v.X)),
            0f - (((ux * c) - (uz * a)) + ((w * uy) + v.Y)),
            0f - (((uy * a) - (ux * b)) + ((w * uz) + v.Z)));
        var len = MathF.Sqrt(((a * a) + (b * b)) + ((c * c) + (w * w)));
        if (len == 0f)
            len = 1.1920929e-07f;
        return new Frame(p, 1f, new Quat(a / len, b / len, c / len, w / len));
    }

    /// <summary>
    /// Quaternion to pitch, yaw, roll in degrees (FUN_181260200 → FUN_18125cba0),
    /// through the rotation matrix, with atan2f and 57.2957764.
    /// </summary>
    public static Vec3 Angles(Quat q)
    {
        float x = q.X, y = q.Y, z = q.Z, w = q.W;
        float x2 = x + x, y2 = y + y, z2 = z + z;
        var m00 = (1f - (y * y2)) - (z * z2);
        var m01 = (y2 * x) - (z2 * w);
        var m10 = (z2 * w) + (y2 * x);
        var m11 = (1f - (x * x2)) - (z * z2);
        var m20 = (z2 * x) - (y2 * w);
        var m21 = (x2 * w) + (z2 * y);
        var m22 = (1f - (x * x2)) - (y * y2);
        var xy = MathF.Sqrt((m10 * m10) + (m00 * m00));
        const float Degrees = 57.2957764f;
        if (xy > 0.001f)
            return new Vec3(Atan2(-m20, xy) * Degrees, Atan2(m10, m00) * Degrees, Atan2(m21, m22) * Degrees);
        return new Vec3(Atan2(-m20, xy) * Degrees, Atan2(-m01, m11) * Degrees, 0f);
    }

    /// <summary>tier0's V_atan2f: the CRT's atan2f, which matches atan2 in double rounded once.</summary>
    private static float Atan2(float y, float x) => (float)Math.Atan2(y, x);

    /// <summary>The node's angle setter (FUN_180fdeda0): tiny angles to 0, yaw made non-negative.</summary>
    public static Vec3 CleanAngles(Vec3 a)
    {
        var p = MathF.Abs(a.X) < 0.001f ? 0f : a.X;
        var y = MathF.Abs(a.Y) < 0.001f ? 0f : a.Y;
        var r = MathF.Abs(a.Z) < 0.001f ? 0f : a.Z;
        while (y < 0f)
            y += 360f;
        return new Vec3(p, y, r);
    }
}
