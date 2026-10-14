// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// AnimationMovement.cs; checked frame for frame against it (docs/ANIMATION.md).

using ValveKeyValue;
using System.Numerics;

namespace Source2.Compiler.Anim;

/// <summary>
/// Represents root motion movement data for an animation.
/// </summary>
/// <seealso href="https://s2v.app/SchemaExplorer/cs2/animationsystem/CAnimMovement">CAnimMovement</seealso>
public class AnimationMovement
{
    /// <summary>
    /// Represents interpolated movement data containing position and angle.
    /// </summary>
    public readonly struct MovementData
    {
        /// <summary>
        /// Gets the position offset.
        /// </summary>
        public Vector3 Position { get; }

        /// <summary>
        /// Gets the angle offset in degrees.
        /// </summary>
        public float Angle { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="MovementData"/> struct.
        /// </summary>
        public MovementData(Vector3 position, float angle)
        {
            Position = position;
            Angle = angle;
        }
    }

    /// <summary>
    /// Gets the ending frame for this movement.
    /// </summary>
    public int EndFrame { get; }

    /// <summary>
    /// Gets the motion flags for this movement.
    /// </summary>
    public ModelAnimationMotionFlags MotionFlags { get; }

    /// <summary>
    /// Gets the first motion scalar value (v0) for this segment.
    /// </summary>
    public float V0 { get; }

    /// <summary>
    /// Gets the second motion scalar value (v1) for this segment.
    /// </summary>
    public float V1 { get; }

    /// <summary>
    /// Gets the rotation angle in degrees.
    /// </summary>
    public float Angle { get; }

    /// <summary>
    /// Gets the motion vector parameter stored for this segment.
    /// </summary>
    public Vector3 Vector { get; }

    /// <summary>
    /// Gets the translation offset parameter stored for this segment.
    /// </summary>
    public Vector3 Position { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="AnimationMovement"/> class.
    /// </summary>
    public AnimationMovement(KVObject frameBlock)
    {
        EndFrame = frameBlock.GetInt32Property("endframe");
        MotionFlags = (ModelAnimationMotionFlags)frameBlock.GetInt32Property("motionflags");
        V0 = frameBlock.GetFloatProperty("v0");
        V1 = frameBlock.GetFloatProperty("v1");
        Angle = frameBlock.GetFloatProperty("angle");
        Vector = new Vector3(frameBlock.GetFloatArray("vector"));
        Position = new Vector3(frameBlock.GetFloatArray("position"));
    }

    /// <summary>
    /// Interpolates linearly between two movement states using <paramref name="t"/> in the range [0, 1].
    /// </summary>
    public static MovementData Lerp(AnimationMovement? a, AnimationMovement? b, float t)
    {
        return (a, b) switch
        {
            (null, null) => new(),
            (null, _) => Lerp(Vector3.Zero, 0, b.Position, b.Angle, t),
            (_, null) => Lerp(a.Position, a.Angle, Vector3.Zero, 0f, t),
            _ => Lerp(a.Position, a.Angle, b.Position, b.Angle, t),
        };
    }

    /// <summary>
    /// Interpolates linearly between two movement samples using <paramref name="t"/> in the range [0, 1].
    /// </summary>
    public static MovementData Lerp(in MovementData a, in MovementData b, float t)
        => Lerp(a.Position, a.Angle, b.Position, b.Angle, t);

    private static MovementData Lerp(Vector3 aPos, float aAngle, Vector3 bPos, float bAngle, float t)
    {
        var position = Vector3.Lerp(aPos, bPos, t);
        var angle = float.Lerp(aAngle, bAngle, t);

        return new MovementData(position, angle);
    }

    /// <summary>
    /// A rotation's yaw in degrees, as VRF's <c>EntityTransformHelper.ToEulerAngles</c> computes it
    /// (from the left axis when the forward axis points straight up or down).
    /// </summary>
    public static float Yaw(Quaternion rotation)
    {
        var forwardX = 1 - 2 * (rotation.Y * rotation.Y + rotation.Z * rotation.Z);
        var forwardY = 2 * (rotation.X * rotation.Y + rotation.W * rotation.Z);
        var xyDist = MathF.Sqrt(forwardX * forwardX + forwardY * forwardY);
        float yaw;
        if (xyDist > 0.001f)
        {
            yaw = MathF.Atan2(forwardY, forwardX);
        }
        else
        {
            var leftX = 2 * (rotation.X * rotation.Y - rotation.W * rotation.Z);
            var leftY = 1 - 2 * (rotation.X * rotation.X + rotation.Z * rotation.Z);
            yaw = MathF.Atan2(-leftX, leftY);
        }
        return float.RadiansToDegrees(yaw);
    }
}
