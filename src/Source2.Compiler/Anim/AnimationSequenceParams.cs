// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// AnimationSequenceParams.cs; checked frame for frame against it (docs/ANIMATION.md).

using ValveKeyValue;

namespace Source2.Compiler.Anim;

/// <summary>
/// Contains sequence parameters for animation transitions.
/// </summary>
/// <seealso href="https://s2v.app/SchemaExplorer/cs2/animationsystem/CAnimSequenceParams">CAnimSequenceParams</seealso>
public readonly struct AnimationSequenceParams
{
    /// <summary>
    /// Gets the fade-in time in seconds.
    /// </summary>
    public float FadeInTime { get; init; }

    /// <summary>
    /// Gets the fade-out time in seconds.
    /// </summary>
    public float FadeOutTime { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="AnimationSequenceParams"/> struct.
    /// </summary>
    public AnimationSequenceParams(KVObject data)
    {
        FadeInTime = data.GetFloatProperty("m_flFadeInTime");
        FadeOutTime = data.GetFloatProperty("m_flFadeOutTime");
    }
}
