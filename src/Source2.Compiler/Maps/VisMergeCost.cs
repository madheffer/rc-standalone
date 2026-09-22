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
/// visibility each side inherits weighted by the other side's volume, plus the
/// distance between their boxes. Everything else is a multiplier that discourages
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

        return Distance(a, b) + (cost * Scale);
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

    /// <summary>The gap between two boxes, zero when they touch (<c>18002fec0</c>).</summary>
    public static float Distance(Cluster a, Cluster b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var gap = Vector3.Max(Vector3.Max(a.Mins - b.Maxs, b.Mins - a.Maxs), Vector3.Zero);
        return gap.Length();
    }

    private static float Footprint(Vector3 mins, Vector3 maxs)
        => (maxs.Y - mins.Y) * (maxs.X - mins.X);
}
