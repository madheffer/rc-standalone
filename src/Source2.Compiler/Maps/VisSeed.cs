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
    /// and the coarse-only geometry the voxelizer stops at 256 units. Tested on
    /// the scene's word (<see cref="RayTraceEnvironment.Flags"/>), which the
    /// loader folded 0x10 into 0x20; a file word 0x130 is nodraw only here
    /// (Mako's 41 seed verdicts).
    /// </summary>
    public const ushort Insubstantial = 0x1030;

    /// <summary>The last threshold's share, <c>DAT_18017f138</c> as a double.</summary>
    public const double EscapeShare = 0.8;

    /// <summary>
    /// What a hit has to be, inside <see cref="Insubstantial"/>, for the gather
    /// to look again: nodraw and nothing else.
    /// </summary>
    public const ushort NoDrawOnly = 0x0020;

    /// <summary>The second look's ignore mask, <c>0x831</c>, which is <see cref="Ignored"/> plus nodraw.</summary>
    public const ushort SeeingThroughNoDraw = 0x0831;

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

    /// <summary>
    /// One ray of a gather as GatherRays leaves it: where it started, its
    /// direction, the hit's distance and whether it landed facing the centre
    /// (the record's +0x1e bits that ClassifyRegion marches on).
    /// </summary>
    public readonly record struct Cast(Vector3 Origin, Vector3 Direction, float Distance, bool Facing);

    /// <summary>
    /// <c>GatherRays</c> (18004c9d0): a <see cref="Quality"/> by <see cref="Quality"/>
    /// grid of directions through each axis' faces from the box centre, each cast
    /// both ways as a segment to <c>MaxCoord * d + centre</c> through the batch
    /// tracer (<see cref="RayTraceEnvironment.Segments"/>, mask 0x811), the rays
    /// that stopped on nodraw alone cast again (<see cref="SecondLook"/>), then
    /// tallied in ray order. <paramref name="casts"/>, when given, receives every
    /// ray's record.
    /// </summary>
    public static Counters Gather(RayTraceEnvironment scene, Vector3 mins, Vector3 maxs, int quality = Quality, List<Cast>? casts = null)
    {
        ArgumentNullException.ThrowIfNull(scene);

        var grid = Math.Clamp(quality, 2, 10);
        float cx = (maxs.X + mins.X) * 0.5f, cy = (maxs.Y + mins.Y) * 0.5f, cz = (maxs.Z + mins.Z) * 0.5f;
        float hx = maxs.X - cx, hy = maxs.Y - cy, hz = maxs.Z - cz;
        var step = 1f / grid;
        var centre = new Vector3(cx, cy, cz);

        var directions = new List<Vector3>(6 * grid * grid);
        for (var axis = 0; axis < 3; axis++)
            for (var v = 0; v < grid; v++)
            {
                var fv = (v * step) + (step * 0.5f);
                fv = (fv + fv) - 1f;
                for (var u = 0; u < grid; u++)
                {
                    var fu = (u * step) + (step * 0.5f);
                    fu = (fu + fu) - 1f;
                    float x = hx, y = hy, z = hz;
                    if (axis == 0)
                        (y, z) = (hy * fv, hz * fu);
                    else if (axis == 1)
                        (x, z) = (hx * fv, hz * fu);
                    else
                        (x, y) = (hx * fv, hy * fu);
                    var d = Normalised(x, y, z);
                    directions.Add(d);
                    directions.Add(new Vector3(-d.X, -d.Y, -d.Z));
                }
            }

        var segments = directions.Select(d => (centre, End(centre, d))).ToList();
        var hits = scene.Segments(segments, Ignored);
        SecondLook(scene, centre, directions, segments, hits);

        int facing = 0, behind = 0, insubstantial = 0, escaped = 0;
        float nearOrdinary = float.MaxValue, nearOther = float.MaxValue;
        for (var i = 0; i < directions.Count; i++)
        {
            var d = directions[i];
            if (hits[i] is not { } hit)
            {
                escaped++;
                casts?.Add(new Cast(centre, d, float.MaxValue, false));
                continue;
            }
            var t = hit.Distance;
            var at = new Vector3((t * d.X) + cx, (t * d.Y) + cy, (t * d.Z) + cz);
            if (!Faces(hit.Normal, centre, at))
            {
                behind++;
                casts?.Add(new Cast(centre, d, t, false));
                continue;
            }
            casts?.Add(new Cast(centre, d, t, true));
            var away = DistanceToBox(mins, maxs, at);
            if ((scene.Flags(hit.Triangle) & Insubstantial) == 0)
            {
                facing++;
                nearOrdinary = MathF.Min(nearOrdinary, away);
            }
            else
            {
                insubstantial++;
                nearOther = MathF.Min(nearOther, away);
            }
        }
        var nearest = nearOrdinary != float.MaxValue ? nearOrdinary : nearOther;
        return new Counters(facing, behind, insubstantial, escaped, grid * grid,
                            nearest == float.MaxValue ? float.PositiveInfinity : nearest);
    }

    /// <summary>
    /// <c>NoDrawSecondLook</c> (18004d1a0): every ray whose hit is nodraw and
    /// nothing else (flags &amp; 0x1030 == 0x20) is cast again through the batch
    /// tracer with nodraw ignored (0x831), and the new hit replaces the first
    /// when the centre faces it and it is an ordinary surface.
    /// </summary>
    internal static void SecondLook(RayTraceEnvironment scene, Vector3 centre, IReadOnlyList<Vector3> directions,
                                    IReadOnlyList<(Vector3, Vector3)> segments, RayTraceEnvironment.Hit?[] hits)
    {
        var again = new List<int>();
        for (var i = 0; i < hits.Length; i++)
            if (hits[i] is { } h && (scene.Flags(h.Triangle) & Insubstantial) == NoDrawOnly)
                again.Add(i);
        if (again.Count == 0)
            return;
        var second = scene.Segments([.. again.Select(i => segments[i])], SeeingThroughNoDraw);
        for (var k = 0; k < again.Count; k++)
        {
            if (second[k] is not { } h)
                continue;
            var d = directions[again[k]];
            var t = h.Distance;
            var at = new Vector3((t * d.X) + centre.X, (t * d.Y) + centre.Y, (t * d.Z) + centre.Z);
            if (Faces(h.Normal, centre, at) && (scene.Flags(h.Triangle) & Insubstantial) == 0)
                hits[again[k]] = h;
        }
    }

    // The facing test both use: (nz cz + ny cy) + nx cx minus the same at the
    // hit point, kept when not negative.
    private static bool Faces(Vector3 n, Vector3 c, Vector3 p)
        => ((n.Z * c.Z) + (n.Y * c.Y) + (n.X * c.X)) - ((n.Z * p.Z) + (n.Y * p.Y) + (n.X * p.X)) >= 0f;

    // The gather's normalise: (y y + z z) + x x, times the reciprocal; the
    // slow path outside 1e-17 to 1e17.
    private static Vector3 Normalised(float x, float y, float z)
    {
        var length = MathF.Sqrt((y * y) + (z * z) + (x * x));
        if (length < 1e-17f || length > 1e17f)
            return length != 0f ? VectorNormalizeSlow.Normalise(new Vector3(x, y, z)) : Vector3.Zero;
        var inv = 1f / length;
        return new Vector3(x * inv, y * inv, z * inv);
    }

    private static Vector3 End(Vector3 o, Vector3 d)
        => new((LightSampler.MaxCoord * d.X) + o.X, (LightSampler.MaxCoord * d.Y) + o.Y, (LightSampler.MaxCoord * d.Z) + o.Z);

    /// <summary><c>DistanceToBox</c> (18010a460): (dz dz + dy dy) + dx dx, each axis the sum of its two overhangs.</summary>
    private static float DistanceToBox(Vector3 mins, Vector3 maxs, Vector3 p)
    {
        float ax = MathF.Max(p.X - maxs.X, 0f), bx = MathF.Max(mins.X - p.X, 0f);
        float ay = MathF.Max(p.Y - maxs.Y, 0f), by = MathF.Max(mins.Y - p.Y, 0f);
        float az = MathF.Max(p.Z - maxs.Z, 0f), bz = MathF.Max(mins.Z - p.Z, 0f);
        float dx = bx + ax, dy = ay + by, dz = az + bz;
        return MathF.Sqrt((dz * dz) + (dy * dy) + (dx * dx));
    }

    /// <summary>
    /// <c>18004b970</c>: a ray that stopped on a surface which is nodraw and
    /// nothing else looks again with nodraw ignored, and the second surface
    /// replaces the first when it is an ordinary one the centre faces. So a
    /// nodraw pane is not what the region sees, the geometry behind it is.
    /// </summary>
    public static RayTraceEnvironment.Hit Behind(
        RayTraceEnvironment scene, Vector3 centre, Vector3 direction, float reach,
        RayTraceEnvironment.Hit hit)
    {
        if ((scene.Flags(hit.Triangle) & Insubstantial) != NoDrawOnly)
            return hit;
        if (scene.Trace(centre, direction, reach, SeeingThroughNoDraw) is not { } again)
            return hit;
        if ((scene.Flags(again.Triangle) & Insubstantial) != 0)
            return hit;

        var landed = centre + (direction * again.Distance);
        return Vector3.Dot(again.Normal, centre) >= Vector3.Dot(again.Normal, landed) ? again : hit;
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

    /// <summary>Gather and decide, which is the whole of one region's seed.</summary>
    public static VisOutside.Status Of(RayTraceEnvironment scene, Vector3 mins, Vector3 maxs)
        => Decide(Gather(scene, mins, maxs));
}
