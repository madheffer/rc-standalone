using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The seed outside detection starts from, ported from visbuilder.dll's
/// <c>18004a2f0</c> and the gather under it, <c>18004b260</c>.
///
/// <para>It is not a bounds test and it is not a count of blocked directions. A
/// grid of rays is cast from the region's box centre through each of the box's
/// six faces, and they are tallied into four counters: how many landed on an
/// ordinary surface facing us, how many on a nodraw or coarse-only one, how many
/// on the BACK of a surface, and how many escaped. A threshold tree over those
/// four decides inside, outside, or leave it to the propagation.</para>
///
/// <para>Getting this right matters more on some maps than others, which is
/// exactly why it was worth porting rather than approximating. A 26-direction
/// "is it blocked" vote reproduces ze_hold_em_p's enclosed count to the unit at
/// any threshold from 70% to 96%, and misses probe01 by half, with a cliff
/// between 20 blocked rays and 21.</para>
/// </summary>
public static class VisSeed
{
    /// <summary>Rays a face is divided into, per side. The gather clamps to 2..10.</summary>
    public const int Quality = 5;

    /// <summary>Triangles a ray passes straight through, the gather's <c>0x811</c>.</summary>
    public const ushort Ignored = 0x0811;

    /// <summary>
    /// A hit on one of these is counted apart from an ordinary surface: nodraw,
    /// and the coarse-only geometry the voxelizer stops at 256 units.
    /// </summary>
    public const ushort Insubstantial = 0x1030;

    /// <summary>The last threshold's share, <c>DAT_18017f138</c> as a double.</summary>
    public const double EscapeShare = 0.8;

    /// <summary>What the gather counted for one region.</summary>
    /// <param name="Facing">Rays that landed on an ordinary surface facing the centre.</param>
    /// <param name="Behind">Rays that landed on the BACK of a surface.</param>
    /// <param name="Insubstantial">Rays that landed on a nodraw or coarse-only one, facing.</param>
    /// <param name="Escaped">Rays that hit nothing at all.</param>
    /// <param name="PerFace">Rays per face, which every threshold is a multiple of.</param>
    /// <param name="Nearest">Distance to the closest surface any ray landed on
    /// facing us, ordinary surfaces first and a nodraw or coarse-only one only
    /// when there was no ordinary hit at all, or infinity when nothing was hit.
    /// The seed does not read it; cluster generation does.</param>
    public readonly record struct Counters(
        int Facing, int Behind, int Insubstantial, int Escaped, int PerFace, float Nearest);

    /// <summary>
    /// The direction set for one box: a <see cref="Quality"/> by
    /// <see cref="Quality"/> grid through each of the six faces, from the centre.
    /// </summary>
    public static Vector3[] Directions(Vector3 mins, Vector3 maxs, int quality = Quality)
    {
        var grid = Math.Clamp(quality, 2, 10);
        var half = (maxs - mins) * 0.5f;
        var step = 1f / grid;

        var found = new Vector3[6 * grid * grid];
        var at = 0;
        for (var axis = 0; axis < 3; axis++)
            for (var v = 0; v < grid; v++)
                for (var u = 0; u < grid; u++)
                {
                    var alongV = (((v * step) + (step * 0.5f)) * 2f) - 1f;
                    var alongU = (((u * step) + (step * 0.5f)) * 2f) - 1f;
                    var direction = axis switch
                    {
                        0 => new Vector3(half.X, half.Y * alongV, half.Z * alongU),
                        1 => new Vector3(half.X * alongV, half.Y, half.Z * alongU),
                        _ => new Vector3(half.X * alongV, half.Y * alongU, half.Z),
                    };
                    direction = Vector3.Normalize(direction);
                    found[at++] = direction;
                    found[at++] = -direction;
                }
        return found;
    }

    /// <summary>Cast the grid over one region's box and tally what it found.</summary>
    public static Counters Gather(RayTraceEnvironment scene, Vector3 mins, Vector3 maxs, int quality = Quality)
    {
        ArgumentNullException.ThrowIfNull(scene);

        var grid = Math.Clamp(quality, 2, 10);
        var centre = (mins + maxs) * 0.5f;
        var reach = (scene.Maxs - scene.Mins).Length();

        int facing = 0, behind = 0, insubstantial = 0, escaped = 0;
        float nearOrdinary = float.PositiveInfinity, nearOther = float.PositiveInfinity;
        foreach (var direction in Directions(mins, maxs, quality))
        {
            if (scene.Trace(centre, direction, reach, Ignored) is not { } hit)
            {
                escaped++;
                continue;
            }

            // Facing means the centre is on the plane's positive side, which is
            // dot(n, centre) >= d once the hit point is known to be on the plane.
            if (Vector3.Dot(hit.Normal, centre) < hit.PlaneDistance)
            {
                behind++;
                continue;
            }

            // The distance kept is from the BOX, not from the centre it cast from.
            var away = Away(mins, maxs, centre + (direction * hit.Distance));
            if ((scene.Flags(hit.Triangle) & Insubstantial) != 0)
            {
                insubstantial++;
                nearOther = MathF.Min(nearOther, away);
            }
            else
            {
                facing++;
                nearOrdinary = MathF.Min(nearOrdinary, away);
            }
        }
        var nearest = float.IsInfinity(nearOrdinary) ? nearOther : nearOrdinary;
        return new Counters(facing, behind, insubstantial, escaped, grid * grid, nearest);
    }

    /// <summary>
    /// The threshold tree, <c>18004a2f0</c> verbatim. <c>n</c> is the rays a face
    /// carries and <c>rays</c> is all six faces' worth, so every bound below is a
    /// share of the whole cast.
    /// </summary>
    public static VisOutside.Status Decide(Counters counted)
    {
        int n = counted.PerFace, rays = n * 6;
        var a = counted.Facing;
        var b = counted.Behind;
        var c = counted.Insubstantial;
        var d = counted.Escaped;

        if ((n <= d + b || (a <= rays / 3 && c + a <= rays / 2))
            && (b > 4 || c + a <= n * 2))
        {
            if (n <= b)
                return VisOutside.Status.Outside;
            if (a <= rays / 2 || a + d <= rays * EscapeShare)
                return VisOutside.Status.Unknown;
        }
        return VisOutside.Status.Inside;
    }

    /// <summary>How far a point lies outside a box, zero when it is inside.</summary>
    private static float Away(Vector3 mins, Vector3 maxs, Vector3 point)
        => Vector3.Max(Vector3.Max(mins - point, point - maxs), Vector3.Zero).Length();

    /// <summary>Gather and decide, which is the whole of one region's seed.</summary>
    public static VisOutside.Status Of(RayTraceEnvironment scene, Vector3 mins, Vector3 maxs)
        => Decide(Gather(scene, mins, maxs));
}
