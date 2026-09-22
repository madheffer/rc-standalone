using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The per-cluster visibility sample, ported from visbuilder.dll's
/// <c>180031a20</c> and the setup around it in <c>180030df0</c>.
///
/// <para>This is what <see cref="VisMergeCost"/> reads, and without it the merge
/// has nothing to weigh: the cost is the visibility each side INHERITS, so two
/// clusters that already see the same things merge for the price of the gap
/// between them, and two that do not are 32 or 128 times dearer.</para>
///
/// <para>The sample is local. A ray leaves a cluster's box centre toward every
/// other entry's centre, runs until it meets geometry or reaches the padded
/// box's diagonal, and sets one bit for every entry box the segment crosses. So
/// "visibility" here means which of this region's own voxels, and which of the
/// shell boxes around it, a straight line from this voxel actually reaches.</para>
/// </summary>
public static class VisClusterSample
{
    /// <summary>The padded box's margin before the grow, <c>DAT_18017f148</c>.</summary>
    public const float Margin = 2f;

    /// <summary>The cells of the shell, <c>DAT_18017bfa0</c>.</summary>
    /// <remarks>
    /// A 4x4x4 grid of half-leaf cells over the padded box with the central 2x2x2
    /// removed, which is 56 boxes, and the table in the binary is exactly that
    /// set. They carry no cluster, so they never merge; they exist so a ray that
    /// leaves the region still crosses something and the cost can tell one
    /// direction of escape from another.
    /// </remarks>
    public static readonly int[] Shell =
    [
        .. from cell in Enumerable.Range(0, 64)
           let x = cell & 3
           let y = (cell >> 2) & 3
           let z = (cell >> 4) & 3
           where x is 0 or 3 || y is 0 or 3 || z is 0 or 3
           select cell,
    ];

    /// <summary>One box the merge holds, whether or not it carries a cluster.</summary>
    /// <param name="Mins">Its box.</param>
    /// <param name="Maxs">Its box.</param>
    /// <param name="Padding">A shell box, which never merges and only takes bits.</param>
    public sealed record Entry(Vector3 Mins, Vector3 Maxs, bool Padding);

    /// <summary>The padded box the merge works inside, and its diagonal.</summary>
    public static (Vector3 Mins, Vector3 Maxs) Padded(Vector3 leafMins, float side)
    {
        var grow = (side + (Margin * 2f)) * 0.5f;
        var back = Margin + grow;
        return (leafMins - new Vector3(back),
                leafMins + new Vector3(side) + new Vector3(back));
    }

    /// <summary>
    /// The merge's entries for one region: a cluster per open voxel in bit order,
    /// then the 56 shell boxes, which is the order <c>1800337a0</c> fills them in.
    /// </summary>
    public static List<Entry> Entries(Vector3 leafMins, float side, ulong open)
    {
        var voxel = side * 0.25f;
        var found = new List<Entry>();
        for (var bit = 0; bit < 64; bit++)
        {
            if ((open & (1UL << bit)) == 0)
                continue;
            var at = leafMins + new Vector3(bit & 3, (bit >> 2) & 3, (bit >> 4) & 3) * voxel;
            found.Add(new Entry(at, at + new Vector3(voxel), false));
        }

        var (mins, _) = Padded(leafMins, side);
        var half = side * 0.5f;
        foreach (var cell in Shell)
        {
            var at = mins + new Vector3(cell & 3, (cell >> 2) & 3, (cell >> 4) & 3) * half;
            found.Add(new Entry(at, at + new Vector3(half), true));
        }
        return found;
    }

    /// <summary>
    /// A bit vector per CLUSTER entry, over every entry including the shell. The
    /// shell entries take no vector of their own because they never merge.
    /// </summary>
    public static ulong[][] Visibility(
        RayTraceEnvironment scene, Vector3 leafMins, float side, ulong open, IReadOnlyList<Entry> entries)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(entries);

        var (mins, maxs) = Padded(leafMins, side);
        var reach = (maxs - mins).Length();
        var near = scene.Overlapping(mins, maxs, VisSeed.Ignored);

        var clusters = entries.Count(e => !e.Padding);
        var words = (entries.Count + 63) / 64;
        var centres = new Vector3[entries.Count];
        for (var i = 0; i < entries.Count; i++)
            centres[i] = (entries[i].Mins + entries[i].Maxs) * 0.5f;

        var found = new ulong[clusters][];
        for (var i = 0; i < clusters; i++)
        {
            var bits = new ulong[words];
            var from = centres[i];
            for (var j = 0; j < entries.Count; j++)
            {
                if (j == i)
                    continue;
                var direction = Vector3.Normalize(centres[j] - from);
                var to = from + (direction * Nearest(scene, near, from, direction, reach));
                for (var k = 0; k < entries.Count; k++)
                    if (Crosses(from, to, entries[k].Mins, entries[k].Maxs))
                        bits[k >> 6] |= 1UL << (k & 63);
            }
            found[i] = bits;
        }
        return found;
    }

    /// <summary>
    /// How far a ray gets. Only geometry inside the padded box can matter: a hit
    /// beyond it cuts the segment somewhere there is nothing left to cross, and
    /// the box's own diagonal always carries a ray out of it from any point
    /// inside, so a miss and a distant hit are the same answer.
    /// </summary>
    private static float Nearest(
        RayTraceEnvironment scene, int[] near, Vector3 from, Vector3 direction, float reach)
    {
        var best = reach;
        foreach (var triangle in near)
            if (scene.Meets(triangle, from, direction, 0f, best) is { } hit)
                best = hit.Distance;
        return best;
    }

    /// <summary>Whether a segment reaches into a box, which is the slab test.</summary>
    private static bool Crosses(Vector3 from, Vector3 to, Vector3 mins, Vector3 maxs)
    {
        var along = to - from;
        float enter = 0f, leave = 1f;
        for (var axis = 0; axis < 3; axis++)
        {
            var d = axis == 0 ? along.X : axis == 1 ? along.Y : along.Z;
            var o = axis == 0 ? from.X : axis == 1 ? from.Y : from.Z;
            var lo = axis == 0 ? mins.X : axis == 1 ? mins.Y : mins.Z;
            var hi = axis == 0 ? maxs.X : axis == 1 ? maxs.Y : maxs.Z;
            if (d == 0f)
            {
                if (o < lo || o > hi)
                    return false;
                continue;
            }
            var first = (lo - o) / d;
            var second = (hi - o) / d;
            if (first > second)
                (first, second) = (second, first);
            enter = MathF.Max(enter, first);
            leave = MathF.Min(leave, second);
        }
        return enter <= leave;
    }
}
