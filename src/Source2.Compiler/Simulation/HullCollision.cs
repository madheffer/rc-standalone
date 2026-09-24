using System.Runtime.InteropServices;
using Source2.Compiler.Physics;

namespace Source2.Compiler.Simulation;

/// <summary>
/// A hull shape as the narrowphase takes it: the hull and the shape's uniform
/// scale (shape +0xc0 and +0xb8). Valve passes it as {RnHull*, float}.
/// </summary>
public readonly record struct HullRef(RnHull Hull, float Scale);

/// <summary>
/// The separating-axis cache a hull pair keeps between steps (0x10 bytes, at
/// CRnConvexContact +0xd4 and at +0x2c of each mesh triangle's cache):
/// type 1 is a face of A with a vertex of B, 2 a face of B with a vertex of A,
/// 3 an edge of A with an edge of B, 0 nothing.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 0x10)]
public struct SatCache
{
    public int Type;
    public int Index1;
    public int Index2;
    public float Separation;
}

/// <summary>
/// A separating-axis query's answer (0x0c bytes): the separation along the
/// best axis and the two features that give it (face and vertex, or two edges).
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 0x0c)]
public struct SatQuery
{
    public float Separation;
    public int Index1;
    public int Index2;
}

/// <summary>
/// Rubikon's convex hull against convex hull narrowphase (vphysics2
/// FUN_1802f15e0 and what it calls): a Gregorius-style SAT with a cached
/// axis, face contacts clipped Sutherland-Hodgman style and reduced to four
/// points, or a single edge contact. Every float operation is in the order the
/// DLL runs it; HullCollisionOracleTests checks it byte for byte.
/// </summary>
public static partial class HullCollision
{
    /// <summary>A contact is kept while the shapes are at most this far apart.</summary>
    internal const float Speculative = 0.125f;

    /// <summary>Relative tolerance favouring A's faces over B's (0x1803e31a4).</summary>
    private const float FaceTolerance = 0.98f;

    /// <summary>Relative tolerance favouring faces over edges (0x1803df0a4).</summary>
    private const float EdgeTolerance = 0.9f;

    /// <summary>Absolute tolerance on both, and the cache's drift limit (0x1803df1d4).</summary>
    private const float AbsoluteTolerance = 0.015625f;

    /// <summary>
    /// The hull pair's manifold (FUN_1802f15e0). <paramref name="old"/> is last
    /// step's manifold (empty for none) for the warm start; the result goes to
    /// <paramref name="result"/> and the axis to <paramref name="cache"/>.
    /// Returns whether the pair touches (within the speculative distance).
    /// </summary>
    /// <remarks>
    /// Point slots past the count, and bytes 0x26..0x27 of each point, are left
    /// as they were, except that an edge contact replacing a face contact
    /// copies Valve's uninitialised stack manifold there; nothing reads them.
    /// </remarks>
    public static bool Collide(ReadOnlySpan<CachedManifold> old, ref CachedManifold result,
                               in RnTransform xfA, HullRef a, in RnTransform xfB, HullRef b,
                               ref SatCache cache, int triangle = -1)
    {
        var r = Revalidate(old, ref result, xfA, a, xfB, b, ref cache, triangle);
        if (r != 2)
            return r == 1;

        var faceA = FaceQuery(xfA, a, xfB, b);
        if (faceA.Separation > Speculative)
            return Separated(ref cache, 1, faceA);
        var faceB = FaceQuery(xfB, b, xfA, a);
        if (faceB.Separation > Speculative)
            return Separated(ref cache, 2, faceB);
        var edge = EdgeQuery(xfA, a, xfB, b);
        if (edge.Separation > Speculative)
            return Separated(ref cache, 3, edge);

        var sepA = faceA.Separation - Speculative;
        var sepB = faceB.Separation - Speculative;
        var flip = sepB > sepA * FaceTolerance + AbsoluteTolerance;
        var ok = flip
            ? FaceContact(old, ref result, xfB, b, xfA, a, faceB, true, ref cache, triangle)
            : FaceContact(old, ref result, xfA, a, xfB, b, faceA, false, ref cache, triangle);
        if (!ok)
            return EdgeContact(old, ref result, xfA, a, xfB, b, edge, ref cache, triangle);

        // maxss: A's unless B's is larger.
        var faceSep = sepA > sepB ? sepA : sepB;
        if (result.PointCount > 1)
            faceSep = MinSeparation(xfA, xfB, result) - Speculative;
        if (edge.Separation - Speculative > faceSep * EdgeTolerance + AbsoluteTolerance)
        {
            CachedManifold edgeManifold = default;
            if (EdgeContact(old, ref edgeManifold, xfA, a, xfB, b, edge, ref cache, triangle))
                result = edgeManifold;
        }
        return true;
    }

    private static bool Separated(ref SatCache cache, int type, in SatQuery query)
    {
        cache.Index1 = query.Index1;
        cache.Index2 = query.Index2;
        cache.Separation = query.Separation;
        cache.Type = type;
        return false;
    }

    /// <summary>
    /// Re-tests the cached axis (FUN_1802f62d0): 0 when it still separates the
    /// pair, 1 when it rebuilt the manifold from it, 2 when the full query has
    /// to run. The contact is rebuilt only if the separation along the cached
    /// axis moved less than 1/64 and the cached axis was touching.
    /// </summary>
    internal static int Revalidate(ReadOnlySpan<CachedManifold> old, ref CachedManifold result,
                                   in RnTransform xfA, HullRef a, in RnTransform xfB, HullRef b,
                                   ref SatCache cache, int triangle)
    {
        switch (cache.Type)
        {
            case 1:
            case 2:
            {
                var flip = cache.Type == 2;
                var separation = flip
                    ? FaceSeparation(xfB, b, cache.Index1, xfA, a, out var vertex)
                    : FaceSeparation(xfA, a, cache.Index1, xfB, b, out vertex);
                if (separation > Speculative)
                {
                    cache.Index2 = vertex;
                    cache.Separation = separation;
                    return 0;
                }
                if (cache.Separation > Speculative)
                {
                    cache = default;
                    return 2;
                }
                if (!(AbsoluteTolerance > MathF.Abs(cache.Separation - separation)))
                    return 2;
                var query = new SatQuery { Separation = separation, Index1 = cache.Index1, Index2 = vertex };
                var ok = flip
                    ? FaceContact(old, ref result, xfB, b, xfA, a, query, true, ref cache, triangle)
                    : FaceContact(old, ref result, xfA, a, xfB, b, query, false, ref cache, triangle);
                return ok ? 1 : 2;
            }
            case 3:
            {
                if (!EdgeSeparation(xfA, a, cache.Index1, xfB, b, cache.Index2, out var separation))
                    return 2;
                if (separation > Speculative)
                {
                    cache.Separation = separation;
                    return 0;
                }
                if (cache.Separation > Speculative)
                {
                    cache = default;
                    return 2;
                }
                if (!(AbsoluteTolerance > MathF.Abs(cache.Separation - separation)))
                    return 2;
                var query = new SatQuery { Separation = separation, Index1 = cache.Index1, Index2 = cache.Index2 };
                return EdgeContact(old, ref result, xfA, a, xfB, b, query, ref cache, triangle) ? 1 : 2;
            }
            default:
                return 2;
        }
    }
}
