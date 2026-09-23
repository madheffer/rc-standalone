using System.Numerics;
using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The merge passes replayed against a capture of Valve's own, bucket by bucket.
///
/// <para><c>tools/vis/capture_merge.py</c> runs the real compile under Frida and
/// records every call of the merge loop: the set it was handed, the leaf boxes
/// its pairs index, the visibility it sampled, the candidate lists it priced,
/// every pair it merged and the set it gave back. Each bucket here gets exactly
/// that input, so nothing upstream is in the comparison, and each stage is
/// scored apart: the sampler on Valve's clusters, the cost on Valve's
/// visibility, and the merge order on both. The first merge that differs names
/// the defect. Behind <c>REPLAY=&lt;map&gt;</c>, with the capture in
/// <c>%TEMP%/vis_capture</c> and the compile's scene still beside it.</para>
/// </summary>
public class VisMergeReplay(ITestOutputHelper output)
{
    private sealed record Bucket(
        int Id, int Pass, float Limit, int Budget, Vector3 Mins, Vector3 Maxs,
        List<VisMerge.Cluster> In, List<(int Owner, int Other, float Cost)> Merges,
        float Returned, int Out)
    {
        public ulong Set { get; set; }
        public List<ulong[]>? Vis { get; set; }
        public List<List<(int Other, float Cost)>>? Candidates { get; set; }
    }

    private static readonly Dictionary<string, string> Addons = new()
    {
        ["probe01"] = "s2c_rc_probe", ["cardtest"] = "s2c_rc_probe", ["ze_hold_em_p"] = "s2c_lighting",
    };

    [Fact]
    public void EveryBucketAgainstTheCapture()
    {
        if (Environment.GetEnvironmentVariable("REPLAY") is not { Length: > 0 } map)
            return;

        var path = Path.Combine(Path.GetTempPath(), "vis_capture", map + ".bin");
        Assert.True(File.Exists(path), $"no capture at {path}; run tools/vis/capture_merge.py first");
        var rte = RayTraceEnvironment.ReadFile(
            Path.Combine(Path.GetTempPath(), "csgo_addons", Addons[map], "maps", map + ".rte"));

        var (buckets, leaves) = Read(path);
        (Vector3, float) Cube(int leaf)
        {
            var (lo, hi) = leaves[leaf];
            return (lo, hi.X - lo.X);
        }

        var only = Environment.GetEnvironmentVariable("REPLAY_ONLY");
        foreach (var pass in buckets.GroupBy(b => b.Pass).OrderBy(g => g.Key))
        {
            int visSets = 0, visExact = 0, clusters = 0, clustersExact = 0;
            long ourOnly = 0, theirOnly = 0;
            int costs = 0, costsExact = 0, sets = 0, orderExact = 0, countExact = 0, retExact = 0;
            foreach (var b in pass.Where(b => b.Vis is not null))
            {
                if (only is { Length: > 0 } && b.Id.ToString() != only)
                    continue;

                // The sampler, on Valve's clusters.
                var ours = Clone(b.In);
                VisClusterSample.SampleInto(rte, ours, b.Mins, b.Maxs, padded: false, Cube);
                var setExact = true;
                for (var i = 0; i < ours.Count; i++)
                {
                    var a = ours[i].Visibility;
                    var v = b.Vis![i];
                    long mine = 0, theirs = 0;
                    for (var w = 0; w < Math.Max(a.Length, v.Length); w++)
                    {
                        var x = w < a.Length ? a[w] : 0;
                        var y = w < v.Length ? v[w] : 0;
                        mine += BitOperations.PopCount(x & ~y);
                        theirs += BitOperations.PopCount(y & ~x);
                    }
                    ourOnly += mine;
                    theirOnly += theirs;
                    if ((mine != 0 || theirs != 0) && Environment.GetEnvironmentVariable("REPLAY_BITS") is { Length: > 0 })
                    {
                        var c = b.In[i];
                        var which = Enumerable.Range(0, b.In.Count)
                            .Where(j => ((j >> 6) < v.Length && (v[j >> 6] >> (j & 63) & 1) != 0)
                                     != ((j >> 6) < a.Length && (a[j >> 6] >> (j & 63) & 1) != 0))
                            .Take(6).Select(j => $"{j}{(((j >> 6) < v.Length && (v[j >> 6] >> (j & 63) & 1) != 0) ? "v" : "o")}");
                        output.WriteLine($"   bits  bucket {b.Id} n={b.In.Count} cluster {i} box {c.Mins} {c.Maxs}"
                                       + $" size {c.VoxelSize} pairs {c.Voxels.Count}: ours-only {mine} valve-only {theirs}"
                                       + $" [{string.Join(' ', which)}]");
                    }
                    clusters++;
                    if (mine == 0 && theirs == 0)
                        clustersExact++;
                    else
                        setExact = false;
                }
                visSets++;
                if (setExact)
                    visExact++;

                // The cost, on Valve's visibility: every price Valve's candidate
                // lists hold, recomputed, compared to the bit.
                var theirsIn = Clone(b.In);
                for (var i = 0; i < theirsIn.Count; i++)
                    theirsIn[i].Visibility = b.Vis![i];
                for (var i = 0; i < theirsIn.Count; i++)
                    foreach (var (other, cost) in b.Candidates![i])
                    {
                        var mineCost = VisMergeCost.Of(Read(theirsIn[i]), Read(theirsIn[other]));
                        costs++;
                        if (BitConverter.SingleToInt32Bits(mineCost) == BitConverter.SingleToInt32Bits(cost))
                            costsExact++;
                        else if (costs - costsExact <= 5)
                            output.WriteLine($"   cost  bucket {b.Id} {i}->{other}: ours {mineCost:R} valve {cost:R}");
                    }

                // The merge, on Valve's visibility.
                var seq = new List<(int, int, float)>();
                var ret = VisMerge.Run(rte, theirsIn, b.Mins, b.Maxs, b.Limit, b.Budget, padded: false,
                                       Cube, sampled: true, merged: (o, t, c) => seq.Add((o, t, c)));
                sets++;
                var first = FirstDifference(seq, b.Merges);
                if (first < 0)
                    orderExact++;
                else if (sets - orderExact <= 8)
                {
                    var (vo, vt, vc) = first < b.Merges.Count ? b.Merges[first] : (-1, -1, float.NaN);
                    var (mo, mt, mc) = first < seq.Count ? seq[first] : (-1, -1, float.NaN);
                    output.WriteLine($"   order bucket {b.Id} n={b.In.Count} budget={b.Budget}"
                                   + $" limit={b.Limit:R}: first difference at merge {first}"
                                   + $" of {b.Merges.Count}: ours ({mo},{mt},{mc:R}) valve ({vo},{vt},{vc:R})");
                }
                if (theirsIn.Count == b.Out)
                    countExact++;
                if (BitConverter.SingleToInt32Bits(ret) == BitConverter.SingleToInt32Bits(b.Returned))
                    retExact++;
            }

            if (sets == 0)
                continue;
            output.WriteLine($"pass {pass.Key}: sampler {visExact}/{visSets} sets exact,"
                           + $" {clustersExact}/{clusters} clusters, bits ours-only {ourOnly:n0}"
                           + $" valve-only {theirOnly:n0} | cost {costsExact:n0}/{costs:n0} exact"
                           + $" | merge order {orderExact}/{sets}, survivors {countExact}/{sets},"
                           + $" returned {retExact}/{sets}");
        }
    }

    [Fact]
    public void TheBucketingAgainstTheCapture()
    {
        if (Environment.GetEnvironmentVariable("REPLAY") is not { Length: > 0 } map)
            return;
        var rte = RayTraceEnvironment.ReadFile(
            Path.Combine(Path.GetTempPath(), "csgo_addons", Addons[map], "maps", map + ".rte"));
        var (buckets, _) = Read(Path.Combine(Path.GetTempPath(), "vis_capture", map + ".bin"));

        foreach (var (k, entry) in Entries.OrderBy(e => e.Key))
        {
            var pass = VisClusterSet.Passes[k];
            var first = buckets.Where(b => b.Pass == k).GroupBy(b => b.Budget).MaxBy(g => g.Key)!
                               .OrderBy(b => b.Set).ToList();
            output.WriteLine($"pass {k}: scene box valve {entry.SceneMins:R} {entry.SceneMaxs:R}"
                           + $" | rte {rte.Mins:R} {rte.Maxs:R}");
            output.WriteLine($"   traced bounds {rte.TracedBounds.Mins:R} {rte.TracedBounds.Maxs:R}"
                           + $" exact: {rte.TracedBounds.Mins == entry.SceneMins && rte.TracedBounds.Maxs == entry.SceneMaxs}");
            foreach (var (label, lo, hi) in new[] { ("valve box", entry.SceneMins, entry.SceneMaxs),
                                                    ("traced bounds", rte.TracedBounds.Mins, rte.TracedBounds.Maxs) })
            {
                var input = entry.Sets.Select(c => new VisClusterSet.Set { Clusters = [.. c] });
                var ours = VisClusterSet.Bucket(lo, hi, input, pass.Cell, pass.Margin);
                int boxes = 0, orders = 0;
                for (var i = 0; i < Math.Min(ours.Count, first.Count); i++)
                {
                    if (ours[i].Mins == first[i].Mins && ours[i].Maxs == first[i].Maxs)
                        boxes++;
                    var a = ours[i].Clusters;
                    var b = first[i].In;
                    if (a.Count == b.Count && a.Zip(b).All(p => p.First.Mins == p.Second.Mins
                            && p.First.Maxs == p.Second.Maxs && p.First.VoxelCount == p.Second.VoxelCount))
                        orders++;
                }
                output.WriteLine($"   {label}: buckets ours {ours.Count} valve {first.Count},"
                               + $" boxes exact {boxes}, cluster order exact {orders}");
            }
        }
    }

    /// <summary>
    /// Our own generation and pre-merge against the set list Valve's first pass
    /// was handed, set by set. Leaf ids are numbered differently on the two
    /// sides, so a cluster is compared by its box, voxel count, size, tag and
    /// the boxes and masks of its pairs.
    /// </summary>
    [Fact]
    public void TheGenerationAgainstTheCapture()
    {
        if (Environment.GetEnvironmentVariable("REPLAY") is not { Length: > 0 } map)
            return;
        var (_, leaves) = Read(Path.Combine(Path.GetTempPath(), "vis_capture", map + ".bin"));
        var valve = Entries[0].Sets;
        if (VisFixtures.RayTraceScene(Addons[map], map) is not var (rte, shipped))
            return;

        var tree = VisVoxelizer.Build(rte, shipped.MinBounds, shipped.MaxBounds, shipped.GridSize);
        var side = VisVoxelizer.VoxelsPerRoot(shipped.MinBounds, shipped.MaxBounds, shipped.GridSize)
                 / VisVoxelizer.VoxelsPerLeaf;
        var regions = VisRegions.Build(tree, side);
        var inside = VisOutside.Detect(tree, regions, rte, shipped.GridSize);
        var compact = VisRegions.Compact(regions, inside.Regions);
        var cube = VisClusters.Cubes(tree, compact);
        var sets = VisClusters.Generate(rte, tree, compact);
        var generated = sets.Sum(x => x.Clusters.Count);
        var ourRounds = new List<List<(Vector3 Mins, Vector3 Maxs)>>();
        var pre = VisPreMerge.Run(sets, r => ourRounds.Add([.. r]));
        var premerge = Path.Combine(Path.GetTempPath(), "vis_capture", map + ".premerge.jsonl");
        if (File.Exists(premerge))
        {
            var theirRounds = File.ReadLines(premerge).Select(l =>
            {
                var b = Convert.FromHexString(JsonDocument.Parse(l).RootElement.GetProperty("hex").GetString()!);
                return Enumerable.Range(0, b.Length / 28).Select(i => (Mins: V(b, i * 28), Maxs: V(b, i * 28 + 12))).ToList();
            }).ToList();
            output.WriteLine($"pre-merge: started {pre.Before} rounds ours {string.Join(",", ourRounds.Select(r => r.Count))}"
                           + $" valve {string.Join(",", theirRounds.Select(r => r.Count))}");
            for (var k = 0; k < Math.Min(ourRounds.Count, theirRounds.Count); k++)
            {
                var a = ourRounds[k];
                var b = theirRounds[k];
                var same = a.Count == b.Count && a.Zip(b).All(p => p.First == p.Second);
                var setA = a.Select(x => $"{x.Mins}{x.Maxs}").ToHashSet();
                var setB = b.Select(x => $"{x.Mins}{x.Maxs}").ToHashSet();
                output.WriteLine($"  round {k}: in order {same}, same set {setA.SetEquals(setB)},"
                               + $" ours-only {setA.Except(setB).Count()} valve-only {setB.Except(setA).Count()}");
                if (!setA.SetEquals(setB))
                {
                    output.WriteLine($"    ours-only {string.Join(" ", setA.Except(setB).Take(4))}");
                    output.WriteLine($"    valve-only {string.Join(" ", setB.Except(setA).Take(4))}");
                    break;
                }
            }
        }

        if (Environment.GetEnvironmentVariable("REPLAY_PASS0") is { Length: > 0 })
        {
            // Pass 0's first merges on OUR input, sampled with OUR leaf cubes,
            // against the visibility Valve sampled for the same buckets.
            var (bucketsValve, _) = Read(Path.Combine(Path.GetTempPath(), "vis_capture", map + ".bin"));
            var firsts = bucketsValve.Where(b => b.Pass == 0).GroupBy(b => b.Budget).MaxBy(g => g.Key)!
                                     .OrderBy(b => b.Set).ToList();
            var (lo0, hi0) = rte.TracedBounds;
            var ours0 = VisClusterSet.Bucket(lo0, hi0, sets, 512f, 0f);
            for (var i = 0; i < ours0.Count && i < firsts.Count; i++)
            {
                var b = firsts[i];
                if (b.Vis is null)
                    continue;
                var probeSet = ours0[i].Clusters.Select(c => new VisMerge.Cluster
                {
                    Voxels = [.. c.Voxels], Mins = c.Mins, Maxs = c.Maxs, VoxelCount = c.VoxelCount,
                    VoxelSize = c.VoxelSize, Tag = c.Tag, OpenSpace = c.OpenSpace,
                }).ToList();
                VisClusterSample.SampleInto(rte, probeSet, ours0[i].Mins, ours0[i].Maxs, padded: false, cube);
                var bad = Enumerable.Range(0, probeSet.Count).Where(k => !probeSet[k].Visibility.SequenceEqual(b.Vis[k])).ToList();
                if (bad.Count > 0)
                {
                    var k0 = bad[0];
                    var c = probeSet[k0];
                    output.WriteLine($"pass0 bucket {i}: {bad.Count} clusters sample differently; first {k0} box {c.Mins} {c.Maxs}"
                                   + $" pairs {string.Join(",", c.Voxels.Select(v => $"{v.Mask:x}@{v.Leaf}:{cube(v.Leaf).Corner}+{cube(v.Leaf).Side}"))}");
                    output.WriteLine($"          valve pairs {string.Join(",", b.In[k0].Voxels.Select(v => $"{v.Mask:x}@{v.Leaf}:{leaves[v.Leaf].Item1}-{leaves[v.Leaf].Item2}"))}");
                    break;
                }
            }
            // Our average of the first merges' returns against the limit Valve
            // handed every second merge.
            var seconds = bucketsValve.Where(b => b.Pass == 0).GroupBy(b => b.Budget).MinBy(g => g.Key)!.ToList();
            var total = 0f;
            foreach (var b in firsts)
                total += b.Returned;
            var avg = total / firsts.Count;
            output.WriteLine($"pass0 average ours {avg:R} ({BitConverter.SingleToInt32Bits(avg):x8}),"
                           + $" valve second-merge limit {seconds[0].Limit:R} ({BitConverter.SingleToInt32Bits(seconds[0].Limit):x8})");
            // The pass itself, our way, bucket by bucket.
            var secondsOrdered = seconds.OrderBy(b => b.Set).ToList();
            var perCell = firsts[0].Budget / 2;
            var costs0 = new float[ours0.Count];
            var firstOut = new int[ours0.Count];
            var firstSeq = new List<(int, int, float)>[ours0.Count];
            for (var i = 0; i < ours0.Count; i++)
            {
                var seq = new List<(int, int, float)>();
                costs0[i] = VisMerge.Run(rte, ours0[i].Clusters, ours0[i].Mins, ours0[i].Maxs, firsts[i].Limit,
                                         perCell * 2, padded: false, cube, merged: (o, t, c) => seq.Add((o, t, c)));
                firstOut[i] = ours0[i].Clusters.Count;
                firstSeq[i] = seq;
            }
            var sum0 = 0f;
            foreach (var c in costs0)
                sum0 += c;
            var avg0 = sum0 / ours0.Count;
            for (var i = 0; i < ours0.Count; i++)
            {
                var seq = new List<(int, int, float)>();
                var ret = VisMerge.Run(rte, ours0[i].Clusters, ours0[i].Mins, ours0[i].Maxs, avg0,
                                       perCell, padded: false, cube, merged: (o, t, c) => seq.Add((o, t, c)));
                var f = firsts[i];
                var sc = secondsOrdered[i];
                if (firstOut[i] != f.Out || ours0[i].Clusters.Count != sc.Out
                    || BitConverter.SingleToInt32Bits(costs0[i]) != BitConverter.SingleToInt32Bits(f.Returned))
                    output.WriteLine($"pass0 bucket {i}: first out ours {firstOut[i]} valve {f.Out} (ret {costs0[i]:R} vs {f.Returned:R},"
                                   + $" merges first diff at {FirstDifference(firstSeq[i], f.Merges)}),"
                                   + $" second out ours {ours0[i].Clusters.Count} valve {sc.Out}"
                                   + $" (merges first diff at {FirstDifference(seq, sc.Merges)} of {sc.Merges.Count}; in ours {firstOut[i]} valve {sc.In.Count})");
            }
            output.WriteLine($"pass0 average ours (chain) {avg0:R}");
            output.WriteLine("pass0 sampling check done");
        }

        if (Environment.GetEnvironmentVariable("REPLAY_CHAIN") is { Length: > 0 })
        {
            // The whole chain on our own input, compared at every pass entry.
            string Brief(VisMerge.Cluster c) => $"{c.Mins}{c.Maxs}|{c.VoxelCount}|{c.VoxelSize}|{c.Voxels.Count}";
            var target = Environment.GetEnvironmentVariable("REPLAY_TARGET") is { Length: > 0 } t ? int.Parse(t) : VisClusters.PassTarget(tree, compact);
            VisClusterSet.MergeAll(rte, sets, target, cube, entering: (k, now) =>
            {
                if (!Entries.TryGetValue(k, out var e))
                    return;
                var mine = now.Select(x => string.Join(";", x.Clusters.Select(Brief))).ToList();
                var theirs = e.Sets.Select(x => string.Join(";", x.Select(Brief))).ToList();
                var at = Enumerable.Range(0, Math.Min(mine.Count, theirs.Count)).Where(i => mine[i] != theirs[i]).ToList();
                output.WriteLine($"chain pass {k} entry: sets ours {mine.Count} valve {theirs.Count},"
                               + $" clusters ours {now.Sum(x => x.Clusters.Count)} valve {e.Sets.Sum(x => x.Count)},"
                               + $" differing sets {at.Count} first {(at.Count > 0 ? at[0] : -1)}");
            });
            output.WriteLine($"chain final: {sets.Sum(x => x.Clusters.Count)} clusters (target {target})");
        }

        string Key(VisMerge.Cluster c, Func<int, Vector3> corner)
            => $"{c.Mins}{c.Maxs}|{c.VoxelCount}|{c.VoxelSize}|{c.Tag}|{c.OpenSpace}|"
             + string.Join(",", c.Voxels.Select(v => $"{v.Mask:x}@{corner(v.Leaf)}").Order());
        Vector3 Ours(int leaf) => cube(leaf).Corner;
        Vector3 Theirs(int leaf) => leaves.TryGetValue(leaf, out var b) ? b.Item1 : new Vector3(float.NaN);

        output.WriteLine($"sets ours {sets.Count} valve {valve.Count}; clusters ours"
                       + $" {sets.Sum(x => x.Clusters.Count)} (generated {generated}) valve {valve.Sum(x => x.Count)}");
        var mine = sets.Select(x => x.Clusters.Select(c => Key(c, Ours)).Order().ToList()).ToList();
        var theirs = valve.Select(x => x.Select(c => Key(c, Theirs)).Order().ToList()).ToList();
        var byContent = theirs.GroupBy(k => string.Join(";", k)).ToDictionary(g => g.Key, g => g.Count());
        int matched = 0, orderExact = 0, shown = 0;
        foreach (var k in mine)
            if (byContent.TryGetValue(string.Join(";", k), out var n) && n > 0)
            {
                byContent[string.Join(";", k)] = n - 1;
                matched++;
            }
        for (var i = 0; i < Math.Min(mine.Count, theirs.Count); i++)
        {
            if (mine[i].SequenceEqual(theirs[i]))
            {
                orderExact++;
                continue;
            }
            if (shown++ < 6)
                output.WriteLine($"  set {i}: ours {mine[i].Count} clusters {string.Join(" / ", mine[i].Take(2))}"
                               + $"\n        valve {theirs[i].Count} clusters {string.Join(" / ", theirs[i].Take(2))}");
        }
        output.WriteLine($"sets identical in content {matched}/{valve.Count}, at the same position {orderExact}");

        // Which leaf each set starts at, on both sides, to see what orders them.
        var firstLeaf = valve.Select(x => x.SelectMany(c => c.Voxels).Select(v => v.Leaf).DefaultIfEmpty(-1).Min()).ToList();
        var rising = firstLeaf.Zip(firstLeaf.Skip(1)).Count(p => p.Second > p.First);
        output.WriteLine($"valve: sets whose lowest leaf rises over the previous set's: {rising}/{valve.Count - 1};"
                       + $" first leaves {string.Join(",", firstLeaf.Take(12))}");
        output.WriteLine($"valve leaf boxes 0..5: {string.Join(" ", Enumerable.Range(0, 6).Where(leaves.ContainsKey).Select(l => $"{l}:{leaves[l].Item1}-{leaves[l].Item2}"))}");
        output.WriteLine($"our leaf boxes 0..5: {string.Join(" ", Enumerable.Range(0, 6).Select(l => $"{l}:{cube(l).Corner}+{cube(l).Side}"))}");
        // Valve numbers a leaf by its node's slot in the pool. Two schemes a
        // downward build could allocate by, scored against the captured ids.
        var depth = tree.BranchesPerLevel.Count;
        var dfs = new Dictionary<(int, (int, int, int)), int>();
        var bfs = new Dictionary<(int, (int, int, int)), int>();
        {
            var next = 1;
            void Walk(int at, (int X, int Y, int Z) cell)
            {
                if (at == 0 || !tree.BranchCells.Contains((at, cell)))
                    return;
                var first = next;
                next += 8;
                for (var o = 0; o < 8; o++)
                    dfs[(at - 1, (cell.X * 2 + (o & 1), cell.Y * 2 + ((o >> 1) & 1), cell.Z * 2 + ((o >> 2) & 1)))] = first + o;
                for (var o = 0; o < 8; o++)
                    Walk(at - 1, (cell.X * 2 + (o & 1), cell.Y * 2 + ((o >> 1) & 1), cell.Z * 2 + ((o >> 2) & 1)));
            }
            Walk(depth, (0, 0, 0));
            var queue = new Queue<(int, (int X, int Y, int Z))>();
            queue.Enqueue((depth, (0, 0, 0)));
            next = 1;
            while (queue.Count > 0)
            {
                var (at, cell) = queue.Dequeue();
                if (at == 0 || !tree.BranchCells.Contains((at, cell)))
                    continue;
                for (var o = 0; o < 8; o++)
                {
                    var child = (at - 1, (cell.X * 2 + (o & 1), cell.Y * 2 + ((o >> 1) & 1), cell.Z * 2 + ((o >> 2) & 1)));
                    bfs[child] = next++;
                    queue.Enqueue(child);
                }
            }
        }
        int dfsHit = 0, bfsHit = 0, placed = 0;
        foreach (var (id, (lo, hi)) in leaves)
        {
            var sideLen = hi.X - lo.X;
            var level = BitOperations.Log2((uint)(sideLen / tree.LeafSize));
            var c = (lo - tree.Origin) / sideLen;
            var key = (level, ((int)MathF.Round(c.X), (int)MathF.Round(c.Y), (int)MathF.Round(c.Z)));
            if (!dfs.ContainsKey(key))
                continue;
            placed++;
            if (dfs[key] == id)
                dfsHit++;
            if (bfs.TryGetValue(key, out var b) && b == id)
                bfsHit++;
        }
        var offsets = new SortedDictionary<int, (int Count, int Lowest)>();
        foreach (var (id, (lo, hi)) in leaves)
        {
            var sideLen = hi.X - lo.X;
            var c = (lo - tree.Origin) / sideLen;
            var key = (BitOperations.Log2((uint)(sideLen / tree.LeafSize)),
                       ((int)MathF.Round(c.X), (int)MathF.Round(c.Y), (int)MathF.Round(c.Z)));
            if (dfs.TryGetValue(key, out var d))
            {
                var o = offsets.TryGetValue(d - id, out var had) ? had : (Count: 0, Lowest: int.MaxValue);
                offsets[d - id] = (o.Count + 1, Math.Min(o.Lowest, id));
            }
        }
        output.WriteLine("dfs - valve id: " + string.Join(", ", offsets.Select(o => $"{o.Key}: {o.Value.Count} from id {o.Value.Lowest}")));
        output.WriteLine($"leaf numbering: of {leaves.Count} captured leaves, {placed} placed in our tree;"
                       + $" depth first equal {dfsHit}, breadth first equal {bfsHit}; our nodes {tree.Nodes}");

        // One differing leaf, taken apart on our side.
        {
            var probe = new Vector3(896, -1004, 48);
            for (var l = 0; l < regions.Leaves.Count; l++)
            {
                var lf = regions.Leaves[l];
                var sz = tree.LeafSize * (1 << lf.Level);
                var corner = tree.Origin + new Vector3(lf.Cell.X, lf.Cell.Y, lf.Cell.Z) * sz;
                if (corner != probe)
                    continue;
                output.WriteLine($"leaf {l} at {corner} side {sz}: solid {lf.Solid:x16}, bit44 solid {(lf.Solid >> 44) & 1}");
                for (var r = 0; r < regions.Regions.Count; r++)
                    if (regions.Regions[r].Leaf == l)
                        output.WriteLine($"   region {r}: open {regions.Regions[r].Open:x16} status {inside.Regions[r]}");
            }
        }

        var ourFirst = sets.Select(x => x.Clusters.SelectMany(c => c.Voxels).Select(v => v.Leaf).DefaultIfEmpty(-1).Min()).ToList();
        output.WriteLine($"ours: first leaves {string.Join(",", ourFirst.Take(12))}; leaves {compact.Leaves.Count}, valve leaf table seen {leaves.Count} max {leaves.Keys.Max()}");
    }

    private static int FirstDifference(List<(int, int, float)> ours, List<(int, int, float)> theirs)
    {
        for (var i = 0; i < Math.Max(ours.Count, theirs.Count); i++)
        {
            if (i >= ours.Count || i >= theirs.Count)
                return i;
            var (a, b, c) = ours[i];
            var (x, y, z) = theirs[i];
            if (a != x || b != y || BitConverter.SingleToInt32Bits(c) != BitConverter.SingleToInt32Bits(z))
                return i;
        }
        return -1;
    }

    private static VisMergeCost.Cluster Read(VisMerge.Cluster c)
        => new(c.Visibility, c.VoxelCount, c.VoxelSize, c.Tag, c.Mins, c.Maxs);

    private static List<VisMerge.Cluster> Clone(List<VisMerge.Cluster> from)
        => [.. from.Select(c => new VisMerge.Cluster
        {
            Voxels = [.. c.Voxels], Mins = c.Mins, Maxs = c.Maxs, VoxelCount = c.VoxelCount,
            VoxelSize = c.VoxelSize, Tag = c.Tag, OpenSpace = c.OpenSpace,
        })];

    private sealed record Entry(Vector3 SceneMins, Vector3 SceneMaxs, List<List<VisMerge.Cluster>> Sets);

    private static readonly Dictionary<int, Entry> Entries = [];

    private static (List<Bucket>, Dictionary<int, (Vector3, Vector3)>) Read(string path)
    {
        Entries.Clear();
        var buckets = new Dictionary<int, Bucket>();
        var pending = new Dictionary<int, JsonElement>();
        var inputs = new Dictionary<int, List<VisMerge.Cluster>>();
        var leaves = new Dictionary<int, (Vector3, Vector3)>();
        var vis = new Dictionary<int, byte[]>();
        using var reader = new BinaryReader(File.OpenRead(path));
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var head = JsonDocument.Parse(reader.ReadBytes(reader.ReadInt32())).RootElement;
            var blob = reader.ReadBytes(reader.ReadInt32());
            switch (head.GetProperty("ev").GetString())
            {
                case "leaves":
                    for (var at = 0; at + 28 <= blob.Length; at += 28)
                        leaves[BitConverter.ToInt32(blob, at)] = (V(blob, at + 4), V(blob, at + 16));
                    break;
                case "in":
                    pending[head.GetProperty("id").GetInt32()] = head.Clone();
                    inputs[head.GetProperty("id").GetInt32()] = Clusters(blob);
                    break;
                case "vis":
                    vis[head.GetProperty("id").GetInt32()] = blob;
                    break;
                case "pass" when head.TryGetProperty("scene", out var scene):
                {
                    var box = scene.EnumerateArray().Select(e => e.GetSingle()).ToArray();
                    var sets = new List<List<VisMerge.Cluster>>();
                    for (var at = 0; at < blob.Length;)
                    {
                        var length = BitConverter.ToInt32(blob, at + 4);
                        sets.Add(Clusters(blob[(at + 8)..(at + 8 + length)]));
                        at += 8 + length;
                    }
                    Entries[head.GetProperty("pass").GetInt32()] = new Entry(
                        new Vector3(box[0], box[1], box[2]), new Vector3(box[3], box[4], box[5]), sets);
                    break;
                }
                case "out":
                {
                    var id = head.GetProperty("id").GetInt32();
                    var start = pending[id];
                    var box = start.GetProperty("box").EnumerateArray().Select(e => e.GetSingle()).ToArray();
                    var merges = new List<(int, int, float)>();
                    float cost = 0;
                    foreach (var m in head.GetProperty("merges").EnumerateArray())
                    {
                        var kind = m[0].GetString();
                        if (kind == "c")
                            cost = m[3].GetSingle();
                        else
                            merges.Add((m[1].GetInt32(), m[2].GetInt32(), cost));
                    }
                    var bucket = new Bucket(id, start.GetProperty("pass").GetInt32(),
                        start.GetProperty("limit").GetSingle(), start.GetProperty("budget").GetInt32(),
                        new Vector3(box[0], box[1], box[2]), new Vector3(box[3], box[4], box[5]),
                        inputs[id], merges, head.GetProperty("ret").GetSingle(), head.GetProperty("n").GetInt32());
                    if (start.TryGetProperty("set", out var set))
                        bucket.Set = Convert.ToUInt64(set.GetString()!, 16);
                    if (vis.TryGetValue(id, out var v))
                        (bucket.Vis, bucket.Candidates) = Visibility(v, inputs[id].Count);
                    buckets[id] = bucket;
                    break;
                }
            }
        }
        return ([.. buckets.Values.OrderBy(b => b.Id)], leaves);
    }

    private static Vector3 V(byte[] b, int at)
        => new(BitConverter.ToSingle(b, at), BitConverter.ToSingle(b, at + 4), BitConverter.ToSingle(b, at + 8));

    private static List<VisMerge.Cluster> Clusters(byte[] blob)
    {
        var found = new List<VisMerge.Cluster>();
        var at = 0;
        while (at < blob.Length)
        {
            var pairs = BitConverter.ToInt32(blob, at);
            var c = new VisMerge.Cluster
            {
                Mins = V(blob, at + 0x30),
                Maxs = V(blob, at + 0x3c),
                VoxelCount = (int)BitConverter.ToUInt32(blob, at + 0x48),
                VoxelSize = BitConverter.ToUInt16(blob, at + 0x50),
                Tag = BitConverter.ToInt16(blob, at + 0x52),
                OpenSpace = blob[at + 0x54] != 0,
            };
            at += 0x58;
            for (var k = 0; k < pairs; k++, at += 16)
                c.Voxels.Add((BitConverter.ToUInt64(blob, at), BitConverter.ToInt32(blob, at + 8)));
            found.Add(c);
        }
        return found;
    }

    private static (List<ulong[]>, List<List<(int, float)>>) Visibility(byte[] blob, int clusters)
    {
        var bits = new List<ulong[]>();
        var candidates = new List<List<(int, float)>>();
        var at = 0;
        for (var slot = 0; at < blob.Length; slot++)
        {
            var words = BitConverter.ToInt32(blob, at);
            var count = BitConverter.ToInt32(blob, at + 4);
            at += 8;
            var v = new ulong[(words + 1) / 2];
            for (var w = 0; w < words; w++)
                v[w >> 1] |= (ulong)BitConverter.ToUInt32(blob, at + (w * 4)) << ((w & 1) * 32);
            at += words * 4;
            var list = new List<(int, float)>();
            for (var k = 0; k < count; k++)
                list.Add((BitConverter.ToInt32(blob, at + (k * 8)), BitConverter.ToSingle(blob, at + (k * 8) + 4)));
            at += count * 8;
            if (slot < clusters)
            {
                bits.Add(v);
                candidates.Add(list);
            }
        }
        return (bits, candidates);
    }
}
