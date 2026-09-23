using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The cost that decides which two clusters merge, ported from visbuilder.dll's
/// <c>1800301c0</c>, which is slot 1 of the merge controller vtable at
/// <c>18017bf40</c>.
///
/// <para>Cluster generation has no closed form because of this function: clusters
/// are born one per voxel and then merged in cheapest-first order until 32 remain
/// or the cheapest merge left costs more than the caller's threshold. So the
/// cluster count a map ends up with is whatever this cost produced, and the only
/// way to reproduce it is to evaluate it.</para>
///
/// <para>The shape is the classic visibility heuristic: merging two clusters makes
/// each of them see everything the other sees, so the cost is the extra
/// visibility each side inherits weighted by the other side's volume, plus a
/// term under one for how far apart the boxes are and how little face they share. Everything else is a multiplier that discourages
/// a merge from spanning a floor or spilling across a room.</para>
/// </summary>
public static class VisMergeCost
{
    /// <summary>Coarse clusters count a quarter as much (<c>DAT_18017f128</c>, a double).</summary>
    public const double CoarseWeight = 0.25;

    /// <summary>A voxel size of 9 or more is coarse. Compared as <c>8 &lt; size</c>.</summary>
    public const int CoarseSize = 9;

    /// <summary>Mismatched voxel sizes, when either side is fine (<c>DAT_18017f1a4</c>).</summary>
    public const float SizeMismatchPenalty = 128f;

    /// <summary>Mismatched tags, and the z-span rule (<c>DAT_18017f190</c>).</summary>
    public const float TagMismatchPenalty = 32f;

    /// <summary>A union footprint over the area limit when neither part was (<c>DAT_18017f170</c>).</summary>
    public const float SpreadPenalty = 8f;

    /// <summary>Square units of xy footprint that counts as spread out (<c>DAT_18017f1cc</c>).</summary>
    public const float AreaLimit = 4096f;

    /// <summary>Units of z span that counts as more than one storey (<c>DAT_18017f19c</c>).</summary>
    public const float ZLimit = 80f;

    /// <summary>What the whole weighted term is scaled by (<c>_DAT_18017f174</c>).</summary>
    public const float Scale = 10f;

    /// <summary>
    /// One cluster, as far as the cost reads it. The offsets are the record's own:
    /// visibility is the entry's bit vector, and the rest sits in the 0x58 byte
    /// cluster.
    /// </summary>
    /// <param name="Visibility">Which clusters a ray from this one's centre reaches.</param>
    /// <param name="VoxelCount">Accumulated voxels, at <c>+0x48</c>.</param>
    /// <param name="VoxelSize">The size it was born at, at <c>+0x50</c>.</param>
    /// <param name="Tag">The short at <c>+0x52</c>, which a merge takes the minimum of.</param>
    /// <param name="Mins">Box minimum, at <c>+0x30</c>.</param>
    /// <param name="Maxs">Box maximum, at <c>+0x3c</c>.</param>
    public sealed record Cluster(
        ulong[] Visibility,
        int VoxelCount,
        int VoxelSize,
        short Tag,
        Vector3 Mins,
        Vector3 Maxs);

    /// <summary>What a merge of these two would cost. Lower merges first.</summary>
    public static float Of(Cluster a, Cluster b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var weightA = a.VoxelCount * (a.VoxelSize > 8 ? CoarseWeight : 1.0);
        var weightB = b.VoxelCount * (b.VoxelSize > 8 ? CoarseWeight : 1.0);

        // Each side inherits what the other can see and it cannot, and pays for it
        // over its own volume. The +1 keeps a merge of two clusters that already
        // see the same thing from costing nothing at all.
        var cost = (float)((DifferenceCount(a.Visibility, b.Visibility) * weightB)
                         + (DifferenceCount(b.Visibility, a.Visibility) * weightA)) + 1f;

        if (a.VoxelSize != b.VoxelSize && (a.VoxelSize < CoarseSize || b.VoxelSize < CoarseSize))
            cost *= SizeMismatchPenalty;
        if (a.Tag != b.Tag)
            cost *= TagMismatchPenalty;

        if (a.VoxelSize < CoarseSize || b.VoxelSize < CoarseSize)
        {
            if (Footprint(a.Mins, a.Maxs) <= AreaLimit
                && Footprint(b.Mins, b.Maxs) <= AreaLimit
                && Footprint(Vector3.Min(a.Mins, b.Mins), Vector3.Max(a.Maxs, b.Maxs)) > AreaLimit)
                cost *= SpreadPenalty;

            var span = MathF.Max(a.Maxs.Z, b.Maxs.Z) - MathF.Min(a.Mins.Z, b.Mins.Z);
            if (span > ZLimit
                && (a.Maxs.Z - a.Mins.Z <= ZLimit || b.Maxs.Z - b.Mins.Z <= ZLimit))
                cost *= TagMismatchPenalty;
        }

        return Proximity(a, b) + (cost * Scale);
    }

    /// <summary>How many bits are set in <paramref name="x"/> and clear in <paramref name="y"/>.</summary>
    public static int DifferenceCount(ulong[] x, ulong[] y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);

        var total = 0;
        for (var i = 0; i < x.Length; i++)
            total += System.Numerics.BitOperations.PopCount(x[i] & ~(i < y.Length ? y[i] : 0UL));
        return total;
    }

    /// <summary>What <see cref="Proximity"/> divides the gap by (<c>DAT_18018237c</c>, 1/128).</summary>
    public const float GapScale = 0.0078125f;

    /// <summary>
    /// The term added after the scale, <c>BoxGap</c> in the manifest, read from
    /// its instructions rather than its decompile: Ghidra's output stops at the
    /// square root and everything after it is missing. It is two halves, each
    /// worth at most 0.5.
    ///
    /// <para>The gap half is <c>0.5 * clamp(gap / 128, 0, 1)</c>. The contact
    /// half takes the two boxes' overlap extents, picks the axis with the
    /// largest magnitude and the middle one, and charges <c>0.5 * (1 - shared
    /// area / face area)</c>, where the face is the FIRST box's on those two
    /// axes. So two boxes that touch at an edge pay the full 0.5, and a pair
    /// sharing a whole face pays nothing. The order of every comparison is the
    /// binary's, ties included, because the axis choice is decided by them.</para>
    /// </summary>
    public static float Proximity(Cluster a, Cluster b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var gx = a.Mins.X - b.Maxs.X;
        if (0f > gx)
            gx = b.Mins.X - a.Maxs.X;
        var gy = a.Mins.Y - b.Maxs.Y;
        if (0f > gy)
            gy = b.Mins.Y - a.Maxs.Y;
        var gz = a.Mins.Z - b.Maxs.Z;
        if (0f > gz)
            gz = b.Mins.Z - a.Maxs.Z;
        gx = Max(0f, gx);
        gy = Max(0f, gy);
        gz = Max(0f, gz);
        var gap = MathF.Sqrt((gz * gz) + (gy * gy) + (gx * gx));
        var far = Min(Max(gap * GapScale, 0f), 1f) * 0.5f;

        Span<float> shared =
        [
            Min(b.Maxs.X, a.Maxs.X) - Max(b.Mins.X, a.Mins.X),
            Min(b.Maxs.Y, a.Maxs.Y) - Max(b.Mins.Y, a.Mins.Y),
            Min(b.Maxs.Z, a.Maxs.Z) - Max(b.Mins.Z, a.Mins.Z),
        ];
        var ax = MathF.Abs(shared[0]);
        var ay = MathF.Abs(shared[1]);
        var az = MathF.Abs(shared[2]);

        // The widest axis, and the one after it; the rotation is how the binary
        // encodes it, so it is kept as it is rather than as the sort it amounts to.
        int widest, next;
        if (ax > ay)
            (widest, next) = ax > az ? (0, 1) : (2, 0);
        else
            (widest, next) = ay > az ? (1, 2) : (2, 0);
        int narrowest;
        if (ay > ax)
            narrowest = az > ax ? 0 : 2;
        else
            narrowest = az <= ay ? 2 : 1;
        if (next == narrowest)
            next = (next + 1) % 3;

        var extent = a.Maxs - a.Mins;
        var face = Axis(extent, next) * Axis(extent, widest);
        var touch = 0f;
        if (face > 0f)
            touch = 0.5f - (Min(Max(shared[next] * shared[widest] / face, 0f), 1f) * 0.5f);
        return touch + far;
    }

    private static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    // MAXSS and MINSS: the first operand when the comparison holds, else the second.
    private static float Max(float x, float y) => x > y ? x : y;

    private static float Min(float x, float y) => x < y ? x : y;

    private static float Footprint(Vector3 mins, Vector3 maxs)
        => (maxs.Y - mins.Y) * (maxs.X - mins.X);
}
