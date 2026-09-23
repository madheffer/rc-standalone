using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The resolution collapse (<c>FUN_180039390</c>, run up to eight times until
/// the record count stops changing): a node whose eight children are all
/// leaves, each with every 2x2x2 octant touched by at most one open record,
/// becomes a leaf itself, its children's records folded in at half resolution.
/// Every pass rebuilds the tree breadth first, so node slots are renumbered.
/// </summary>
public static class VisCollapse
{
    private static readonly ulong[] Octants =
    [
        0x330033UL, 0xcc00ccUL, 0x33003300UL, 0xcc00cc00UL,
        0x33003300000000UL, 0xcc00cc00000000UL, 0x3300330000000000UL, 0xcc00cc0000000000UL,
    ];

    /// <summary>The iterations, as the build runs them. Returns the state and how many ran.</summary>
    public static (VisPvs.State State, int Iterations) Run(VisPvs.State s, ushort[] sixes, Action<int, VisPvs.State>? each = null)
    {
        var iterations = 0;
        var previous = s.Entries.Length;
        do
        {
            (s, sixes) = Once(s, sixes);
            each?.Invoke(iterations, s);
            if (s.Entries.Length == previous)
                break;
            previous = s.Entries.Length;
            iterations++;
        }
        while (iterations < 8);
        return (s, iterations);
    }

    /// <summary>
    /// One pass. <paramref name="sixes"/> is each node's short at <c>+6</c>, a
    /// cluster an empty child contributes as a whole octant when it names one.
    /// </summary>
    public static (VisPvs.State State, ushort[] Sixes) Once(VisPvs.State s, ushort[] sixes)
    {
        var nodes = s.NodeWords.Length;
        var collapsible = new bool[nodes];
        for (var i = 0; i < nodes; i++)
        {
            if ((s.NodeWords[i] & 1) != 0)
                continue;
            var first = (int)(s.NodeWords[i] >> 1);
            var ok = 0;
            for (var k = 0; k < 8; k++)
            {
                var c = first + k;
                if ((s.NodeWords[c] & 1) != 0 && (s.NodeCounts[c] == 0 || Uniform(s, c)))
                    ok++;
            }
            collapsible[i] = ok == 8;
        }

        var words = new List<uint> { s.NodeWords[0] };
        var counts = new List<ushort> { s.NodeCounts[0] };
        var newSixes = new List<ushort> { sixes[0] };
        var mins = new List<Vector3> { s.NodeMins[0] };
        var maxs = new List<Vector3> { s.NodeMaxs[0] };
        var entries = new List<VisVisibility.Entry>(s.Entries.Length);
        var queue = new Queue<(int New, int Old)>();
        queue.Enqueue((0, 0));
        var folded = new List<VisVisibility.Entry>();
        while (queue.Count > 0)
        {
            var (at, old) = queue.Dequeue();
            var word = s.NodeWords[old];
            if (!collapsible[old])
            {
                if ((word & 1) == 0)
                {
                    words[at] = ((uint)words.Count << 1) | (words[at] & 1);
                    var first = (int)(word >> 1);
                    for (var k = 0; k < 8; k++)
                    {
                        var c = first + k;
                        queue.Enqueue((words.Count, c));
                        words.Add(s.NodeWords[c]);
                        counts.Add(s.NodeCounts[c]);
                        newSixes.Add(sixes[c]);
                        mins.Add(s.NodeMins[c]);
                        maxs.Add(s.NodeMaxs[c]);
                    }
                }
                else
                {
                    var start = entries.Count;
                    words[at] = ((uint)start << 1) | (words[at] & 1);
                    var first = (int)(word >> 1);
                    for (var k = 0; k < s.NodeCounts[old]; k++)
                    {
                        var e = s.Entries[first + k];
                        entries.Add(e with { Packed = (e.Packed & 3) | (at * 4) });
                    }
                    counts[at] = (ushort)(entries.Count - start);
                }
                continue;
            }

            folded.Clear();
            var children = (int)(word >> 1);
            for (var o = 0; o < 8; o++)
                Fold(s, sixes, folded, (o & 1) * 2, o & 2, (o >> 1) & 2, children + o);
            words[at] = ((uint)entries.Count << 1) | 1;
            counts[at] = (ushort)folded.Count;
            foreach (var e in folded)
                entries.Add(e with { Packed = (e.Packed & 3) | (at * 4) });
        }
        var state = s with
        {
            Entries = [.. entries], NodeWords = [.. words], NodeCounts = [.. counts],
            NodeMins = [.. mins], NodeMaxs = [.. maxs],
        };
        return (state, [.. newSixes]);
    }

    // FUN_180038970: every octant of the leaf touched by at most one open record.
    private static bool Uniform(VisPvs.State s, int leaf)
    {
        Span<int> touching = stackalloc int[8];
        var first = (int)(s.NodeWords[leaf] >> 1);
        for (var k = 0; k < s.NodeCounts[leaf]; k++)
        {
            var e = s.Entries[first + k];
            if ((e.Packed & 3) != 0)
                continue;
            for (var o = 0; o < 8; o++)
            {
                if ((e.Cells & Octants[o]) != 0)
                    touching[o]++;
            }
        }
        foreach (var t in touching)
        {
            if (t >= 2)
                return false;
        }
        return true;
    }

    // FUN_180038b70: a child's records at half resolution, one bit per touched
    // octant at the child's place in the parent, merged by cluster and kind.
    // An empty child whose +6 names a cluster gives it the whole octant.
    private static void Fold(VisPvs.State s, ushort[] sixes, List<VisVisibility.Entry> into, int px, int py, int pz, int child)
    {
        if (s.NodeCounts[child] == 0)
        {
            if (sixes[child] < s.Clusters)
                into.Add(new VisVisibility.Entry(sixes[child], 0, ((0x330033UL << px) << (py * 4)) << (pz << 4)));
            return;
        }
        var first = (int)(s.NodeWords[child] >> 1);
        for (var k = 0; k < s.NodeCounts[child]; k++)
        {
            var e = s.Entries[first + k];
            if ((e.Packed & 3) != 0)
                continue;
            for (var o = 0; o < 8; o++)
            {
                if ((Octants[o] & e.Cells) == 0)
                    continue;
                var bit = ((1UL << ((o & 1) + px)) << ((((o >> 1) & 1) + py) * 4)) << ((((o >> 2) & 1) + pz) * 16);
                var merged = false;
                for (var j = 0; j < into.Count; j++)
                {
                    if (into[j].Cluster != e.Cluster || ((into[j].Packed ^ e.Packed) & 3) != 0)
                        continue;
                    into[j] = into[j] with { Cells = into[j].Cells | bit };
                    merged = true;
                    break;
                }
                if (!merged)
                    into.Add(new VisVisibility.Entry(e.Cluster, e.Packed, bit));
            }
        }
    }
}
