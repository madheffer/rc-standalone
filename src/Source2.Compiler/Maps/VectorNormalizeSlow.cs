using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// VectorNormalize_Slow (FUN_18125d000), where mathlib's VectorNormalize
/// (18013e340 and its inlined copies) sends a length under 1e-17 or over 1e17
/// that is not zero: the squared length summed (y y + x x) + z z in double,
/// its square root, the components times the reciprocal, each rounded to
/// float; the length returned clamped to FLT_MAX. HullSimplifier and
/// VisibilityMeshMerger carry the same port inline.
/// </summary>
internal static class VectorNormalizeSlow
{
    public static Vector3 Normalise(Vector3 v, out float length)
    {
        double dy = v.Y, dx = v.X, dz = v.Z;
        var len = Math.Sqrt(((dy * dy) + (dx * dx)) + (dz * dz));
        var inverse = 1.0 / len;
        length = (float)Math.Min(len, 3.4028234663852886e+38);
        return new Vector3((float)(inverse * dx), (float)(inverse * dy), (float)(inverse * dz));
    }

    /// <summary><see cref="Normalise(Vector3, out float)"/> without the length.</summary>
    public static Vector3 Normalise(Vector3 v) => Normalise(v, out _);
}
