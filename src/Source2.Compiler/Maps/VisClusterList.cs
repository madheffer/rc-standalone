using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The vis-cluster merge after the PVS scan (<c>Logs_ClustersSteps</c>): the
/// assigned clusters become <c>CVisClusterList</c> records carrying a weight, a
/// box and a neighbour list, and are merged pairwise by what merging would cost
/// in voxels drawn, reading the scan's matrix, until the enclosed volume's
/// target is met. The survivors are renumbered from 2 and the sampler's
/// entries, cluster boxes and matrix take the new numbering.
/// </summary>
public static class VisClusterList
{
    /// <summary>The volume a cluster stands for, the <c>param_5</c> of the steps.</summary>
    public const double VolumePerCluster = 1048576.0;

    /// <summary>
    /// <c>ResourceCompiler/VisBuilder/MaxVisClusters</c>, which the steps read
    /// with a default of 2,048 and cap at 4,096. CS2's
    /// <c>game/csgo_core/gameinfo.gi</c> sets it to 4,096.
    /// </summary>
    public const int MaxVisClusters = 4096;

    /// <summary>One <c>CVisClusterList</c> record, 0x58 bytes in the binary.</summary>
    public sealed class Record
    {
        /// <summary>Neighbour ids at <c>+0x00</c>, in the order they were added.</summary>
        public List<int> Neighbors { get; set; } = [];

        /// <summary>Per neighbour, the pair cost (<c>+0x18</c> count, <c>+0x20</c> data).</summary>
        public List<ulong> Costs { get; } = [];

        /// <summary>Voxels it stands for, <c>+0x30</c>; zero once merged away.</summary>
        public ulong Weight { get; set; }

        public Vector3 Mins { get; set; }

        public Vector3 Maxs { get; set; }

        /// <summary>The cheapest neighbour's index, the low 24 bits of <c>+0x50</c>.</summary>
        public int Best { get; set; }

        /// <summary><c>+0x53</c>.</summary>
        public byte Partition { get; set; }

        /// <summary><c>+0x54</c>, zeroed when two differing ones merge. Always 0 here.</summary>
        public ushort Tag { get; set; }

        /// <summary><c>+0x56</c>, the minimum on a merge. The constructor's 8, never overwritten.</summary>
        public ushort Size { get; set; } = 8;

        /// <summary>Costs are current when their count matches the neighbour count.</summary>
        public bool CostsCurrent => Costs.Count == Neighbors.Count;
    }

    /// <summary>What the merge leaves.</summary>
    /// <param name="Map">Per assigned cluster, its new id.</param>
    /// <param name="Clusters">How many ids there now are, the two reserved included.</param>
    /// <param name="Records">The records after renumbering.</param>
    /// <param name="Merges">Every merge in order, as (lower, higher).</param>
    /// <param name="State">The sampler once it has taken the map: entries renumbered, boxes rebuilt.</param>
    public sealed record Result(int[] Map, int Clusters, Record[] Records, List<(int Lo, int Hi)> Merges, VisPvs.State State);

    /// <summary>
    /// The enclosed volume the target comes from: every open entry's box as a
    /// float product summed in double, then with the distance pre-merge's groups
    /// capped at 2^19 each when they average above it.
    /// </summary>
    public static double Volume(VisPvs.State s, double premergeVolume, int premergeGroups)
    {
        var volume = 0.0;
        foreach (var e in s.Entries)
        {
            if (e.Kind != VisVisibility.Open)
                continue;
            var (lo, hi) = VisPvs.RegionBox(s, e);
            volume += (double)((hi.X - lo.X) * (hi.Y - lo.Y) * (hi.Z - lo.Z));
        }
        if (premergeGroups != 0 && premergeVolume < volume && 524288.0 < premergeVolume / premergeGroups)
            volume = (volume - premergeVolume) + (premergeGroups * 524288.0);
        return volume;
    }

    /// <summary>The steps' target before and after the two reserved ids come off.</summary>
    public static (int Target, int Clamped) Target(double volume, int maxVisClusters = MaxVisClusters)
    {
        var cap = Math.Min(maxVisClusters, 0x1000);
        var wanted = (int)(volume / VolumePerCluster);
        int target, clamped;
        if (wanted < 1)
        {
            target = 0x20;
            clamped = Math.Min(target, cap);
        }
        else
        {
            target = (wanted + 0x21) & ~0x1f;
            clamped = target < 0x20 ? 0x20 : Math.Min(target, cap);
        }
        return (target, clamped - 2);
    }

    /// <summary>
    /// Run the steps over the scan's state and matrix. The entries and the
    /// matrix are rewritten in place; the new cluster boxes come back in the
    /// result's state.
    /// </summary>
    /// <param name="s">The scan's state.</param>
    /// <param name="matrix">The matrix the scan left.</param>
    /// <param name="sizes">Per assigned cluster, its voxel size (the sampler's u16 at <c>+0x18</c>).</param>
    /// <param name="volume">From <see cref="Volume"/>.</param>
    /// <param name="grid">The grid size the steps print, the base voxel size.</param>
    public static Result Run(VisPvs.State s, VisPvs.Matrix matrix, IReadOnlyList<int> sizes, double volume, float grid = 8f)
    {
        var (_, clamped) = Target(volume);
        var list = Build(s, sizes, grid);
        var merges = new List<(int, int)>();
        var steps = list.Records.Length >> 13;
        for (var at = steps << 13; clamped < at; at -= 0x2000)
            Merge(list, matrix, at, 0, at < 0x4000 ? 1.05f : 1.1f, merges);
        Merge(list, matrix, clamped, 2, 1.0f, merges);
        var map = list.Map;
        return new Result(map, list.Live, list.Records, merges, Apply(s, map, list.Live));
    }

    /// <summary>The records as <c>Logs_BuiltClustersInS</c> leaves them, before any merge.</summary>
    public static Record[] Built(VisPvs.State s, IReadOnlyList<int> sizes, float grid = 8f) => Build(s, sizes, grid).Records;

    private sealed class List
    {
        public Record[] Records = [];
        public int[] Map = [];
        public int Live;
    }

    // Logs_BuiltClustersInS: weights and neighbour lists from the entries,
    // then every small connected component bridged to what lies around it.
    private static List Build(VisPvs.State s, IReadOnlyList<int> sizes, float grid)
    {
        var n = s.Clusters;
        var voxels = new long[n];
        var lists = new List<int>[n];
        for (var c = 0; c < n; c++)
            lists[c] = [];

        // FUN_180045230: per open entry its popcount, and a pair for every
        // higher cluster within reach of it, added to both lists once.
        var found = new List<int>();
        for (var e = 0; e < s.Entries.Length; e++)
        {
            var entry = s.Entries[e];
            if (entry.Kind != VisVisibility.Open || (uint)entry.Cluster >= (uint)n)
                continue;
            var c = entry.Cluster;
            voxels[c] += BitOperations.PopCount(entry.Cells);
            var (lo, hi) = VisPvs.RegionBox(s, entry);
            var grow = new Vector3(s.BaseVoxelSize);
            ClustersIn(s, lo - grow, hi + grow, found);
            foreach (var id in found)
            {
                if ((uint)c < (uint)id)
                {
                    AddOnce(lists[c], id);
                    AddOnce(lists[id], c);
                }
            }
        }

        var list = new List { Records = new Record[n], Map = new int[n], Live = n };
        for (var c = 0; c < n; c++)
        {
            var weight = (ulong)voxels[c];
            if ((c < sizes.Count ? sizes[c] : 8) > 8)
            {
                weight = (ulong)((double)voxels[c] * 0.5);
                if (weight == 0)
                    weight = 1;
            }
            list.Records[c] = new Record
            {
                Weight = weight, Neighbors = [.. lists[c]], Mins = s.ClusterMins[c], Maxs = s.ClusterMaxs[c],
            };
            list.Map[c] = c;
        }

        // FUN_180044eb0: connected components over the lists, each started from
        // the lowest unlabelled cluster that has voxels.
        var component = new int[n];
        Array.Fill(component, -1);
        var components = 0;
        var queue = new Queue<int>();
        for (var c = 0; c < n; c++)
        {
            if (voxels[c] == 0 || component[c] != -1)
                continue;
            queue.Enqueue(c);
            while (queue.Count > 0)
            {
                var at = queue.Dequeue();
                if (component[at] == components)
                    continue;
                component[at] = components;
                foreach (var next in lists[at])
                {
                    if (component[next] != components)
                        queue.Enqueue(next);
                }
            }
            components++;
        }

        var threshold = (long)(VolumePerCluster / (double)(grid * grid * grid));
        for (var k = 0; k < components; k++)
        {
            long total = 0;
            for (var c = 0; c < n; c++)
            {
                if (component[c] == k)
                    total += voxels[c];
            }
            if (total == 0 || threshold <= total)
                continue;
            var members = new List<int>();
            for (var c = 0; c < n; c++)
            {
                if (component[c] == k)
                    members.Add(c);
            }
            var mn = new Vector3(float.MaxValue);
            var mx = new Vector3(-float.MaxValue);
            foreach (var m in members)
            {
                mn = new Vector3(Min(mn.X, s.ClusterMins[m].X), Min(mn.Y, s.ClusterMins[m].Y), Min(mn.Z, s.ClusterMins[m].Z));
                mx = new Vector3(Max(mx.X, s.ClusterMaxs[m].X), Max(mx.Y, s.ClusterMaxs[m].Y), Max(mx.Z, s.ClusterMaxs[m].Z));
            }
            float dx = mx.X - mn.X, dy = mx.Y - mn.Y, dz = mx.Z - mn.Z;
            var pad = 128f - (MathF.Sqrt((dz * dz) + (dy * dy) + (dx * dx)) * 0.5f);
            if (0f < pad)
            {
                mn = new Vector3(mn.X - pad, mn.Y - pad, mn.Z - pad);
                mx = new Vector3(pad + mx.X, pad + mx.Y, pad + mx.Z);
            }
            var reach = grid * 4f;
            ClustersIn(s, new Vector3(mn.X - reach, mn.Y - reach, mn.Z - reach),
                       new Vector3(mx.X + reach, mx.Y + reach, mx.Z + reach), found);
            foreach (var m in members)
            {
                foreach (var f in found)
                {
                    if (m == f)
                        continue;
                    AddOnce(list.Records[m].Neighbors, f);
                    list.Records[m].Costs.Clear();
                    AddOnce(list.Records[f].Neighbors, m);
                    list.Records[f].Costs.Clear();
                }
            }
        }
        return list;

        // The binary's inline min and max: the held value wins a tie.
        static float Min(float held, float v) => held <= v ? held : v;
        static float Max(float held, float v) => v <= held ? held : v;
    }

    // FUN_18002dd90 then sort and unique: the clusters of open entries whose
    // cells a box overlaps.
    private static void ClustersIn(VisPvs.State s, Vector3 lo, Vector3 hi, List<int> found)
    {
        found.Clear();
        VisPvs.QueryClusters(s, 0, lo, hi, found);
        found.Sort();
        var w = 0;
        for (var r = 0; r < found.Count; r++)
        {
            if (w == 0 || found[w - 1] != found[r])
                found[w++] = found[r];
        }
        found.RemoveRange(w, found.Count - w);
    }

    private static void AddOnce(List<int> list, int id)
    {
        if (!list.Contains(id))
            list.Add(id);
    }

    // Logs_MergedToClustersInSSRecomputePasses: recompute the stale cost lists,
    // take every candidate within the factor of the cheapest and merge it, until
    // the live count reaches the target; then renumber.
    private static void Merge(List list, VisPvs.Matrix matrix, int target, int reserved, float factor,
                              List<(int, int)> merges)
    {
        if (factor <= 1.001f)
            factor = 1.001f;
        var n = list.Records.Length;
        var span = Math.Max(n - 0x2000, 0) / 2048 + 1;
        var partitions = span < 1 ? 1 : Math.Min(span, 250);
        for (var i = 0; i < n; i++)
            list.Records[i].Partition = (byte)(i % partitions);

        while (target < list.Live)
        {
            Recompute(list, matrix, null);
            var candidates = Candidates(list, factor);
            if (partitions < 2)
            {
                if (candidates.Count == 0)
                    break;
                Take(list, matrix, candidates, target, factor, merges);
                continue;
            }

            // Above 10,240 records: the cheapest candidate's partition is then
            // worked alone, recomputing only its own records, for up to 31 more
            // rounds while the target is not met.
            var partition = candidates.Count > 0 ? list.Records[candidates[0].Id].Partition : (byte)0;
            for (var round = 0; ; )
            {
                if (round != 0)
                {
                    Recompute(list, matrix, partition);
                    candidates = PartitionCandidates(list, partition);
                }
                if (candidates.Count != 0)
                    Take(list, matrix, candidates, target, factor, merges);
                if (!(target < list.Live) || ++round >= 32)
                    break;
            }
        }
        Finalise(list, matrix, reserved);
    }

    // The merge walk shared by both paths: every candidate no dearer than the
    // factor over the first, whose cheapest neighbour is still higher and live.
    private static void Take(List list, VisPvs.Matrix matrix, List<(int Id, ulong Cost)> candidates, int target,
                             float factor, List<(int, int)> merges)
    {
        var threshold = ToCount(ToFloat(candidates[0].Cost) * factor);
        var remaining = list.Live - target;
        foreach (var (c, cost) in candidates)
        {
            if (remaining == 0 || threshold < cost)
                break;
            var rec = list.Records[c];
            if (rec.Weight == 0 || !rec.CostsCurrent || rec.Best >= rec.Costs.Count)
                continue;
            if (threshold < rec.Costs[rec.Best])
                break;
            var other = rec.Neighbors[rec.Best];
            if (other < c || list.Records[other].Weight == 0)
                break;
            Pair(list, matrix, c, other);
            merges.Add((c, other));
            remaining--;
        }
    }

    // FUN_1800442d0: one partition's candidates, with no running limit.
    private static List<(int Id, ulong Cost)> PartitionCandidates(List list, byte partition)
    {
        var found = new List<(int, ulong)>();
        for (var c = 0; c < list.Records.Length; c++)
        {
            var rec = list.Records[c];
            if (rec.Partition != partition || rec.Weight == 0 || (uint)rec.Best >= (uint)rec.Costs.Count)
                continue;
            var other = rec.Neighbors[rec.Best];
            if ((uint)other < (uint)c || list.Records[other].Weight == 0)
                continue;
            found.Add((c, rec.Costs[rec.Best]));
        }
        found.Sort((x, y) => x.Item2 != y.Item2 ? x.Item2.CompareTo(y.Item2) : x.Item1.CompareTo(y.Item1));
        return found;
    }

    // CVisClusterList::RecomputeClusterCostLists (or ...ForPartition, the same
    // over one partition's records) and FUN_180042890: each stale record prices
    // every higher live neighbour and keeps the first cheapest.
    private static void Recompute(List list, VisPvs.Matrix matrix, byte? partition)
    {
        var stale = new List<int>();
        for (var c = 0; c < list.Records.Length; c++)
        {
            if (partition is { } p && list.Records[c].Partition != p)
                continue;
            if (list.Records[c].Weight != 0 && !list.Records[c].CostsCurrent)
                stale.Add(c);
        }
        Parallel.ForEach(stale, c =>
        {
            var rec = list.Records[c];
            var costs = new ulong[rec.Neighbors.Count];
            var best = 0;
            var cheapest = ulong.MaxValue;
            for (var i = 0; i < costs.Length; i++)
            {
                var other = rec.Neighbors[i];
                if ((uint)other < (uint)c || list.Records[other].Weight == 0)
                {
                    costs[i] = ulong.MaxValue;
                    continue;
                }
                costs[i] = Cost(list, matrix, c, other);
                if (costs[i] < cheapest)
                {
                    cheapest = costs[i];
                    best = i;
                }
            }
            rec.Costs.Clear();
            rec.Costs.AddRange(costs);
            rec.Best = best;
        });
    }

    // FUN_1800429c0: what merging a and b costs. Every cluster one sees and the
    // other does not is charged its weight, scaled from 8 down to 0.5 as its box
    // gets from 400 to 3200 units from the blind one's box, and each side's sum
    // is multiplied by the blind one's weight.
    private static ulong Cost(List list, VisPvs.Matrix matrix, int a, int b)
    {
        var recs = list.Records;
        var rowA = matrix.Rows[a];
        var rowB = matrix.Rows[b];
        var words = (recs.Length + 31) >> 5;
        ulong sumA = 0, sumB = 0;
        for (var w = 0; w < words; w++)
        {
            var onlyB = rowB[w] & ~rowA[w];
            var onlyA = rowA[w] & ~rowB[w];
            if ((onlyA | onlyB) == 0)
                continue;
            for (var bit = 0; bit < 32; bit++)
            {
                var k = (w << 5) + bit;
                if ((onlyB >> bit & 1) != 0)
                    sumA += Charge(recs[k], recs[a]);
                if ((onlyA >> bit & 1) != 0)
                    sumB += Charge(recs[k], recs[b]);
            }
        }
        var costA = sumA * recs[a].Weight;
        var costB = sumB * recs[b].Weight;
        if (8 < recs[a].Size)
            costA = ToCount((double)costA * 0.25);
        if (8 < recs[b].Size)
            costB = ToCount((double)costB * 0.25);
        var total = (costB + costA) * 0x20;
        if (recs[a].Tag == recs[b].Tag)
            total = costB + costA;
        if (recs[a].Size != recs[b].Size && (recs[a].Size < 9 || recs[b].Size < 9))
            total <<= 7;
        return total;

        static ulong Charge(Record k, Record blind)
        {
            var gap = Vector3.Max(Maxps(k.Mins, blind.Mins) - Minps(k.Maxs, blind.Maxs), Vector3.Zero);
            var sq = gap * gap;
            var length = MathF.Sqrt((sq.X + sq.Y) + sq.Z);
            var t = (length - 400f) / 2800f;
            t = 0f < t ? t : 0f;
            t = t < 1f ? t : 1f;
            var charge = ToCount((double)(8f - (t * 7.5f)) * (double)k.Weight);
            return charge == 0 ? 1 : charge;
        }
    }

    private static Vector3 Maxps(Vector3 x, Vector3 y)
        => new(x.X > y.X ? x.X : y.X, x.Y > y.Y ? x.Y : y.Y, x.Z > y.Z ? x.Z : y.Z);

    private static Vector3 Minps(Vector3 x, Vector3 y)
        => new(x.X < y.X ? x.X : y.X, x.Y < y.Y ? x.Y : y.Y, x.Z < y.Z ? x.Z : y.Z);

    // FUN_180044060: each live record whose cheapest neighbour is higher and
    // live, taken while no dearer than the factor over the cheapest seen so
    // far, sorted by (cost, id).
    private static List<(int Id, ulong Cost)> Candidates(List list, float factor)
    {
        var found = new List<(int, ulong)>();
        var limit = ulong.MaxValue;
        var cheapest = ulong.MaxValue;
        for (var c = 0; c < list.Records.Length; c++)
        {
            var rec = list.Records[c];
            if (rec.Weight == 0 || (uint)rec.Best >= (uint)rec.Costs.Count)
                continue;
            var other = rec.Neighbors[rec.Best];
            if ((uint)other < (uint)c || list.Records[other].Weight == 0)
                continue;
            var cost = rec.Costs[rec.Best];
            if (limit < cost)
                continue;
            found.Add((c, cost));
            if (cost < cheapest)
            {
                limit = ToCount(ToFloat(cost) * factor);
                cheapest = cost;
            }
        }
        found.Sort((x, y) => x.Item2 != y.Item2 ? x.Item2.CompareTo(y.Item2) : x.Item1.CompareTo(y.Item1));
        return found;
    }

    // FUN_180042f00: the higher id folds into the lower.
    private static void Pair(List list, VisPvs.Matrix matrix, int x, int y)
    {
        int lo = Math.Min(x, y), hi = Math.Max(x, y);
        var recs = list.Records;
        var keep = recs[lo];
        var drop = recs[hi];
        keep.Weight += drop.Weight;
        keep.Mins = new Vector3(MinSs(drop.Mins.X, keep.Mins.X), MinSs(drop.Mins.Y, keep.Mins.Y), MinSs(drop.Mins.Z, keep.Mins.Z));
        keep.Maxs = new Vector3(MaxSs(drop.Maxs.X, keep.Maxs.X), MaxSs(drop.Maxs.Y, keep.Maxs.Y), MaxSs(drop.Maxs.Z, keep.Maxs.Z));
        keep.Size = Math.Min(keep.Size, drop.Size);
        if (keep.Tag != drop.Tag)
            keep.Tag = 0;

        foreach (var nb in drop.Neighbors)
        {
            if (nb == lo)
                continue;
            AddOnce(keep.Neighbors, nb);
            keep.Costs.Clear();
        }
        SwapRemove(keep.Neighbors, hi);
        foreach (var nb in keep.Neighbors)
        {
            var other = recs[nb];
            SwapRemove(other.Neighbors, hi);
            AddOnce(other.Neighbors, lo);
            other.Costs.Clear();
        }

        var rowLo = matrix.Rows[lo];
        var rowHi = matrix.Rows[hi];
        for (var w = 0; w < matrix.Words; w++)
            rowLo[w] |= rowHi[w];
        rowLo[hi >> 5] &= ~(1u << (hi & 31));
        rowLo[lo >> 5] |= 1u << (lo & 31);
        for (var k = 0; k < recs.Length; k++)
        {
            if (k == lo || recs[k].Weight == 0 || (rowLo[k >> 5] >> (k & 31) & 1) == 0)
                continue;
            var row = matrix.Rows[k];
            row[lo >> 5] |= 1u << (lo & 31);
            row[hi >> 5] &= ~(1u << (hi & 31));
            recs[k].Costs.Clear();
        }
        drop.Weight = 0;
        foreach (var rec in recs)
        {
            if (rec.Weight != 0 && rec.Neighbors.Count > 0)
                SwapRemove(rec.Neighbors, hi);
        }
        keep.Costs.Clear();
        drop.Costs.Clear();
        for (var i = 0; i < list.Map.Length; i++)
        {
            if (list.Map[i] == hi)
                list.Map[i] = lo;
        }
        list.Live--;

        static float MinSs(float a, float b) => a < b ? a : b;
        static float MaxSs(float a, float b) => a > b ? a : b;
    }

    private static void SwapRemove(List<int> list, int id)
    {
        var at = list.IndexOf(id);
        if (at < 0)
            return;
        if (at != list.Count - 1)
            list[at] = list[^1];
        list.RemoveAt(list.Count - 1);
    }

    // FUN_180043550: the live records renumbered from `reserved` in index order,
    // their lists and matrix rows remapped. Rows past the new count and words
    // past the new width keep what they held, as in the binary.
    private static void Finalise(List list, VisPvs.Matrix matrix, int reserved)
    {
        var old = list.Records;
        var renumber = new int[old.Length];
        var next = reserved;
        for (var i = 0; i < old.Length; i++)
            renumber[i] = old[i].Weight == 0 ? -1 : next++;
        var total = next;
        for (var i = 0; i < list.Map.Length; i++)
            list.Map[i] = renumber[list.Map[i]];

        var fresh = new VisPvs.Matrix(total, total);
        if (reserved != 0)
            Array.Fill(fresh.Rows[0], uint.MaxValue);
        if (reserved > 1)
            Array.Clear(fresh.Rows[1]);
        var records = new Record[total];
        for (var i = 0; i < total; i++)
            records[i] = new Record();
        for (var o = 0; o < old.Length; o++)
        {
            if (old[o].Weight == 0)
                continue;
            var m = list.Map[o];
            var rec = records[m];
            rec.Weight = old[o].Weight;
            rec.Mins = old[o].Mins;
            rec.Maxs = old[o].Maxs;
            rec.Partition = old[o].Partition;
            foreach (var nb in old[o].Neighbors)
            {
                var v = list.Map[nb];
                if ((uint)v < (uint)total)
                    rec.Neighbors.Add(v);
            }
            var row = fresh.Rows[m];
            var oldRow = matrix.Rows[o];
            for (var i = 0; i < list.Map.Length; i++)
            {
                if ((oldRow[i >> 5] >> (i & 31) & 1) == 0)
                    continue;
                var v = list.Map[i];
                if ((uint)v < (uint)total)
                    row[v >> 5] |= 1u << (v & 31);
            }
            if (reserved != 0)
                row[0] |= 1;
        }
        for (var r = reserved; r < total; r++)
            Array.Copy(fresh.Rows[r], matrix.Rows[r], fresh.Words);
        if (reserved != 0)
            Array.Fill(matrix.Rows[0], uint.MaxValue);
        if (reserved > 1)
            Array.Clear(matrix.Rows[1]);
        list.Records = records;
        list.Live = total;
    }

    // FUN_180038130: the sampler takes the map, and its cluster boxes are
    // rebuilt from the open entries alone.
    private static VisPvs.State Apply(VisPvs.State s, int[] map, int total)
    {
        for (var e = 0; e < s.Entries.Length; e++)
        {
            var entry = s.Entries[e];
            if (entry.Kind == VisVisibility.Open)
                s.Entries[e] = entry with { Cluster = map[entry.Cluster] };
        }
        var mins = new Vector3[total];
        var maxs = new Vector3[total];
        Array.Fill(mins, new Vector3(float.MaxValue));
        Array.Fill(maxs, new Vector3(-float.MaxValue));
        foreach (var entry in s.Entries)
        {
            if (entry.Kind != VisVisibility.Open)
                continue;
            var (lo, hi) = VisPvs.RegionBox(s, entry);
            var c = entry.Cluster;
            mins[c] = new Vector3(mins[c].X <= lo.X ? mins[c].X : lo.X, mins[c].Y <= lo.Y ? mins[c].Y : lo.Y,
                                  mins[c].Z <= lo.Z ? mins[c].Z : lo.Z);
            maxs[c] = new Vector3(hi.X <= maxs[c].X ? maxs[c].X : hi.X, hi.Y <= maxs[c].Y ? maxs[c].Y : hi.Y,
                                  hi.Z <= maxs[c].Z ? maxs[c].Z : hi.Z);
        }
        return s with { ClusterMins = mins, ClusterMaxs = maxs };
    }

    private static float ToFloat(ulong v) => v < 1UL << 63 ? (long)v : (float)v;

    // MSVC's float or double to unsigned 64 bit: truncate, with values past
    // 2^63 taken down by 2^63 first and the top bit put back.
    private static ulong ToCount(double v)
    {
        const double top = 9.223372036854776e18;
        if (top <= v)
        {
            var less = v - top;
            if (less < top)
                return (ulong)(long)less + (1UL << 63);
        }
        return (ulong)(long)v;
    }
}
