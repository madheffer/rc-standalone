// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// ModelAnimationMotionFlags.cs.

namespace Source2.Compiler.Anim;

/// <summary>Which components of a movement segment move (<c>m_nFlags</c>).</summary>
[Flags]
public enum ModelAnimationMotionFlags
{
    /// <summary>Translates in X.</summary>
    TX = 64,

    /// <summary>Translates in Y.</summary>
    TY = 128,

    /// <summary>Translates in Z.</summary>
    TZ = 256,

    /// <summary>Rotates about Z.</summary>
    RZ = 2048,

    /// <summary>Moves linearly.</summary>
    Linear = 4096,
}
