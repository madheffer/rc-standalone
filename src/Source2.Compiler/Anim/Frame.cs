// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// Frame.cs; checked frame for frame against it (docs/ANIMATION.md).

using Source2.Compiler.Meshes;
using System.Numerics;

namespace Source2.Compiler.Anim;

/// <summary>
/// Represents a single frame of animation data.
/// </summary>
public class Frame
{
    /// <summary>
    /// Gets or sets the frame index.
    /// </summary>
    public int FrameIndex { get; set; } = 1;

    /// <summary>
    /// Gets the bone transforms for this frame.
    /// </summary>
    public FrameBone[] Bones { get; }

    /// <summary>
    /// Gets the flex controller data for this frame.
    /// </summary>
    public float[] Datas { get; }

    /// <summary>
    /// Gets or sets the movement data for this frame.
    /// </summary>
    public AnimationMovement.MovementData Movement { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="Frame"/> class.
    /// </summary>
    public Frame(Skeleton skeleton, FlexController[] flexControllers)
    {
        Bones = new FrameBone[skeleton.Bones.Length];
        Datas = new float[flexControllers.Length];
        Clear(skeleton);
    }

    /// <summary>
    /// Sets a Vector3 attribute for a bone in this frame.
    /// </summary>
    public void SetAttribute(int bone, AnimationChannelAttribute attribute, Vector3 data)
    {
        switch (attribute)
        {
            case AnimationChannelAttribute.Position:
                Bones[bone].Position = data;
                break;

#if DEBUG
            default:
                // An attribute that carries no data of this type is skipped.
                break;
#endif
        }
    }

    /// <summary>
    /// Sets a Quaternion attribute for a bone in this frame.
    /// </summary>
    public void SetAttribute(int bone, AnimationChannelAttribute attribute, Quaternion data)
    {
        switch (attribute)
        {
            case AnimationChannelAttribute.Angle:
                Bones[bone].Angle = data;
                break;

#if DEBUG
            default:
                // An attribute that carries no data of this type is skipped.
                break;
#endif
        }
    }

    /// <summary>
    /// Sets a float attribute for a bone or flex controller in this frame.
    /// </summary>
    public void SetAttribute(int bone, AnimationChannelAttribute attribute, float data)
    {
        switch (attribute)
        {
            case AnimationChannelAttribute.Scale:
                Bones[bone].Scale = data;
                break;

            case AnimationChannelAttribute.Data:
                Datas[bone] = data;
                break;

#if DEBUG
            default:
                // An attribute that carries no data of this type is skipped.
                break;
#endif
        }
    }

    /// <summary>
    /// Resets frame bones to their bind pose and clears flex and movement data.
    /// Should be used on animation change.
    /// </summary>
    /// <param name="skeleton">The same skeleton that was passed to the constructor.</param>
    public void Clear(Skeleton skeleton)
    {
        FrameIndex = -1;

        for (var i = 0; i < Bones.Length; i++)
        {
            Bones[i].Position = skeleton.Bones[i].Position;
            Bones[i].Angle = skeleton.Bones[i].Angle;
            Bones[i].Scale = 1;
        }

        Array.Clear(Datas);
        Movement = default;
    }

    /// <summary>
    /// Resets frame bones to an identity delta and clears flex and movement data. Additive animations
    /// decode deltas over this seed, so channels they do not write stay neutral instead of holding a
    /// bind pose the delta would be composed onto twice.
    /// </summary>
    public void ClearToIdentity()
    {
        FrameIndex = -1;

        for (var i = 0; i < Bones.Length; i++)
        {
            Bones[i].Position = Vector3.Zero;
            Bones[i].Angle = Quaternion.Identity;
            Bones[i].Scale = 1;
        }

        Array.Clear(Datas);
        Movement = default;
    }

    /// <summary>
    /// Each bone's parent-space transform for <see cref="Skeleton.ComputeWorldPose"/>:
    /// scale, then rotation, then translation.
    /// </summary>
    public Matrix4x4[] LocalTransforms()
    {
        var local = new Matrix4x4[Bones.Length];
        for (var i = 0; i < Bones.Length; i++)
            local[i] = Matrix4x4.CreateScale(Bones[i].Scale) * Matrix4x4.CreateFromQuaternion(Bones[i].Angle) * Matrix4x4.CreateTranslation(Bones[i].Position);
        return local;
    }
}
