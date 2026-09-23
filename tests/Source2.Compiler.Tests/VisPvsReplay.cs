using System.Numerics;
using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The post-assignment half against <c>tools/vis/capture_pvs.py</c>'s capture of
/// the live compile: the sampler's arrays at the start of the scan, the
/// neighbour list, and the matrix the scan leaves. Behind <c>PVS=&lt;map&gt;</c>.
/// </summary>
public class VisPvsReplay(ITestOutputHelper output)
{
    private static readonly Dictionary<string, string> Addons = new()
    {
        ["probe01"] = "s2c_rc_probe", ["cardtest"] = "s2c_rc_probe", ["ze_hold_em_p"] = "s2c_lighting",
    };

    internal static Dictionary<string, (JsonElement Head, byte[] Blob)> Capture(string map)
    {
        var found = new Dictionary<string, (JsonElement, byte[])>();
        using var reader = new BinaryReader(File.OpenRead(
            Path.Combine(Path.GetTempPath(), "vis_capture", map + ".pvs.bin")));
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var head = JsonDocument.Parse(reader.ReadBytes(reader.ReadInt32())).RootElement.Clone();
            found[head.GetProperty("ev").GetString()!] = (head, reader.ReadBytes(reader.ReadInt32()));
        }
        return found;
    }

    /// <summary>Our own pipeline up to and including assignment, as the scan sees it.</summary>
    internal static VisPvs.State? Ours(string map) => OursWithScene(map)?.State;

    internal static (VisPvs.State State, RayTraceEnvironment Rte)? OursWithScene(string map)
        => OursInFull(map) is { } all ? (all.State, all.Rte) : null;

    /// <summary>The same, with what the later stages also read: the pre-merge's result and each cluster's voxel size.</summary>
    internal static (VisPvs.State State, RayTraceEnvironment Rte, VisPreMerge.Result PreMerge, int[] Sizes)? OursInFull(string map)
    {
        if (VisFixtures.RayTraceScene(Addons[map], map) is not var (rte, shipped))
            return null;
        var tree = VisVoxelizer.Build(rte, shipped.MinBounds, shipped.MaxBounds, shipped.GridSize);
        var side = VisVoxelizer.VoxelsPerRoot(shipped.MinBounds, shipped.MaxBounds, shipped.GridSize)
                 / VisVoxelizer.VoxelsPerLeaf;
        var regions = VisRegions.Build(tree, side);
        var inside = VisOutside.Detect(tree, regions, rte, shipped.GridSize);
        var compact = VisRegions.Compact(regions, inside.Regions);
        var sets = VisClusters.Generate(rte, tree, compact);
        var pre = VisPreMerge.Run(sets);
        VisClusterSet.MergeAll(rte, sets, VisClusters.PassTarget(tree, compact), VisClusters.Cubes(tree, compact));
        var collapsed = VisRegions.Collapse(regions, inside.Regions);
        var assigned = VisAssign.Run(sets, compact.Leaves.Count, collapsed, _ => true);
        var sizes = sets.SelectMany(set => set.Clusters).Select(c => c.VoxelSize).ToArray();
        return (VisPvs.Build(tree, shipped.MaxBounds, compact, assigned, sets), rte, pre, sizes);
    }

    /// <summary>Compare a matrix with a captured one, row by row.</summary>
    internal static (int Rows, int Same, long OursOnly, long ValveOnly) Diff(VisPvs.Matrix m, byte[] blob)
    {
        var words = m.Words;
        int same = 0;
        long mine = 0, theirs = 0;
        for (var r = 0; r < m.Rows.Length; r++)
        {
            var eq = true;
            for (var w = 0; w < words; w++)
            {
                var v = BitConverter.ToUInt32(blob, ((r * words) + w) * 4);
                var o = m.Rows[r][w];
                mine += System.Numerics.BitOperations.PopCount(o & ~v);
                theirs += System.Numerics.BitOperations.PopCount(v & ~o);
                eq &= v == o;
            }
            if (eq)
                same++;
        }
        return (m.Rows.Length, same, mine, theirs);
    }

    [Fact]
    public void TheClusterCentreGenerator()
    {
        if (Environment.GetEnvironmentVariable("PVS") is not { Length: > 0 } map)
            return;
        var cap = Capture(map);
        var (s, rte) = OursWithScene(map)!.Value;
        var neighbours = VisPvs.Neighbors(s);
        var matrix = new VisPvs.Matrix(s.Clusters + 2, s.Clusters + 2);
        {
            var tested = VisPvs.Diagonal(s.Clusters);
            var first = VisPvs.Pairs(new VisPvs.Matrix(s.Clusters + 2, s.Clusters + 2), neighbours, tested);
            var blob = cap["pairs0"].Blob;
            var valvePairs = Enumerable.Range(0, blob.Length / 8)
                .Select(i => (BitConverter.ToInt32(blob, i * 8), BitConverter.ToInt32(blob, i * 8 + 4))).ToList();
            var a = first.ToHashSet();
            var b = valvePairs.ToHashSet();
            output.WriteLine($"pass 1 pairs ours {first.Count} valve {valvePairs.Count}; in order {first.SequenceEqual(valvePairs)};"
                           + $" ours-only [{string.Join(" ", a.Except(b).Take(8))}] valve-only [{string.Join(" ", b.Except(a).Take(8))}]");
            foreach (var (x, y) in a.Except(b).Take(3))
                output.WriteLine($"   ({x},{y}): neighbours of {x} [{string.Join(",", neighbours[x].Neighbors)}]; of {y} [{string.Join(",", neighbours[y].Neighbors)}]");
        }
        var passes = VisPvs.ClusterCentres(s, neighbours, rte, matrix,
            (pairs, rays) => output.WriteLine($"  pass: {pairs.Count:n0} pairs, {rays:n0} rays"));
        var (rows, same, mine, theirs) = Diff(matrix, cap["after0"].Blob);
        output.WriteLine($"cluster centres: {passes} passes; rows identical {same}/{rows}, bits ours-only {mine:n0} valve-only {theirs:n0}");
    }

    /// <summary>A captured matrix, as ours.</summary>
    internal static VisPvs.Matrix Load(JsonElement head, byte[] blob)
    {
        var bits = head.GetProperty("bits").GetInt32();
        var m = new VisPvs.Matrix(head.GetProperty("rows").GetInt32(), bits);
        for (var r = 0; r < m.Rows.Length; r++)
        {
            for (var w = 0; w < m.Words; w++)
                m.Rows[r][w] = BitConverter.ToUInt32(blob, ((r * m.Words) + w) * 4);
        }
        return m;
    }

    /// <summary>The pairs each begin-pass of one generator produced, in pass order.</summary>
    internal static List<List<(int, int)>> PassPairs(Dictionary<string, (JsonElement Head, byte[] Blob)> cap, int generator)
        => cap.Where(kv => kv.Key.StartsWith("pairs") && kv.Value.Head.GetProperty("generator").GetInt32() == generator)
              .OrderBy(kv => int.Parse(kv.Key[5..]))
              .Select(kv => Enumerable.Range(0, kv.Value.Blob.Length / 8)
                  .Select(i => (BitConverter.ToInt32(kv.Value.Blob, i * 8), BitConverter.ToInt32(kv.Value.Blob, i * 8 + 4))).ToList())
              .ToList();

    /// <summary>The whole scan against the matrix the compile leaves, generator set chosen by the map's pvstype.</summary>
    [Fact]
    public void TheWholeScan()
    {
        if (Environment.GetEnvironmentVariable("PVS") is not { Length: > 0 } map)
            return;
        var cap = Capture(map);
        var (s, rte) = OursWithScene(map)!.Value;
        var config = VisConfig.Read(Path.Combine(Path.GetTempPath(), "csgo_addons", Addons[map], "maps", map + ".viscfg"));
        var valve = cap.Where(kv => kv.Key.StartsWith("pairs"))
            .OrderBy(kv => int.Parse(kv.Key[5..])).Select(kv => kv.Value.Blob.Length / 8).ToList();
        var pass = 0;
        var matrix = VisPvs.Scan(s, rte, config, (name, pairs, rays) =>
        {
            // Valve's empty closing begin-pass is captured too; ours returns before recording it.
            while (pass < valve.Count && valve[pass] == 0 && pairs.Count != 0)
                pass++;
            output.WriteLine($"  {name}: pairs ours {pairs.Count:n0} valve {(pass < valve.Count ? valve[pass] : -1):n0}, {rays:n0} rays");
            pass++;
        });
        var (rows, same, mine, theirs) = Diff(matrix, cap["matrix"].Blob);
        output.WriteLine($"pvstype {config.PvsType}: rows identical {same}/{rows}, bits ours-only {mine:n0} valve-only {theirs:n0}");
        Assert.Equal(rows, same);
    }

    /// <summary>
    /// The vis-cluster merge, seeded with the compile's own post-scan matrix so
    /// it is measured alone: the built records, every merge in order, the map,
    /// and the sampler once it has taken the map.
    /// </summary>
    [Fact]
    public void TheVisClusterMerge()
    {
        if (Environment.GetEnvironmentVariable("PVS") is not { Length: > 0 } map)
            return;
        var cap = Capture(map);
        var (s, _, pre, sizes) = OursInFull(map)!.Value;
        var steps = cap["steps"].Head;
        output.WriteLine($"pre-merge: ours {pre.Volume} over {pre.After} groups, valve"
                       + $" {steps.GetProperty("premergeVolume").GetDouble()} over {steps.GetProperty("premergeGroups").GetInt32()}");
        var infos = cap["steps"].Blob;
        var sizeSame = Enumerable.Range(0, Math.Min(sizes.Length, infos.Length / 4))
            .Count(i => BitConverter.ToUInt16(infos, i * 4) == sizes[i]);
        output.WriteLine($"voxel sizes: {sizeSame}/{infos.Length / 4} identical (ours {sizes.Length})");

        var volume = VisClusterList.Volume(s, pre.Volume, pre.After);
        output.WriteLine($"volume {volume:n0}, target {VisClusterList.Target(volume)}");

        var built = VisClusterList.Built(s, sizes);
        var blob = cap["built"].Blob;
        int at = 0, same = 0, listSame = 0, weightSame = 0, boxSame = 0, shown = 0;
        for (var c = 0; c < built.Length && at < blob.Length; c++)
        {
            var count = BitConverter.ToInt32(blob, at);
            var ids = Enumerable.Range(0, count).Select(k => BitConverter.ToInt32(blob, at + 4 + k * 4)).ToList();
            at += 4 + count * 4;
            var weight = BitConverter.ToUInt64(blob, at);
            var box = (V(blob, at + 8), V(blob, at + 20));
            at += 32;
            bool l = ids.SequenceEqual(built[c].Neighbors), w = weight == built[c].Weight,
                 b = box == (built[c].Mins, built[c].Maxs);
            listSame += l ? 1 : 0; weightSame += w ? 1 : 0; boxSame += b ? 1 : 0;
            if (l && w && b)
                same++;
            else if (shown++ < 5)
                output.WriteLine($"  record {c}: weight ours {built[c].Weight} valve {weight};"
                               + $" ours [{string.Join(",", built[c].Neighbors)}] valve [{string.Join(",", ids)}]");
        }
        output.WriteLine($"built: {same}/{built.Length} identical (lists {listSame}, weights {weightSame}, boxes {boxSame})");

        var matrix = Load(cap["matrix"].Head, cap["matrix"].Blob);
        var result = VisClusterList.Run(s, matrix, sizes, volume);
        var mb = cap["merges"].Blob;
        var theirs = Enumerable.Range(0, mb.Length / 8).Select(i => ((int)BitConverter.ToUInt32(mb, i * 8), (int)BitConverter.ToUInt32(mb, i * 8 + 4))).ToList();
        var prefix = 0;
        while (prefix < Math.Min(theirs.Count, result.Merges.Count) && theirs[prefix] == result.Merges[prefix])
            prefix++;
        output.WriteLine($"merges: ours {result.Merges.Count} valve {theirs.Count}, identical prefix {prefix}"
                       + (prefix < Math.Min(theirs.Count, result.Merges.Count) ? $"; at {prefix} ours {result.Merges[prefix]} valve {theirs[prefix]}" : ""));

        var cm = cap["clustermap"];
        var mapSame = Enumerable.Range(0, Math.Min(result.Map.Length, cm.Blob.Length / 4)).Count(i => BitConverter.ToInt32(cm.Blob, i * 4) == result.Map[i]);
        output.WriteLine($"map: {mapSame}/{cm.Blob.Length / 4} identical; clusters ours {result.Clusters} valve {cm.Head.GetProperty("total").GetInt32()}");

        var ae = cap["appliedentries"].Blob;
        var entriesSame = Enumerable.Range(0, Math.Min(ae.Length / 16, result.State.Entries.Length))
            .Count(i => BitConverter.ToInt32(ae, i * 16) == result.State.Entries[i].Cluster);
        var ab = cap["appliedboxes"].Blob;
        var boxesSame = Enumerable.Range(0, Math.Min(ab.Length / 24, result.State.Clusters))
            .Count(i => V(ab, i * 24) == result.State.ClusterMins[i] && V(ab, i * 24 + 12) == result.State.ClusterMaxs[i]);
        var (rows, rowsSame, mine, valveOnly) = Diff(matrix, cap["appliedmatrix"].Blob);
        output.WriteLine($"applied: entries {entriesSame}/{ae.Length / 16}, boxes {boxesSame}/{ab.Length / 24},"
                       + $" matrix rows {rowsSame}/{rows} (bits ours-only {mine:n0} valve-only {valveOnly:n0})");
        Assert.Equal(theirs, result.Merges);
    }

    /// <summary>
    /// The border sampling, from the state the vis-cluster merge leaves (seeded
    /// with the compile's post-scan matrix): which records are borders, and each
    /// one's claims, cluster and box, in order.
    /// </summary>
    [Fact]
    public void TheBorderSampling()
    {
        if (Environment.GetEnvironmentVariable("PVS") is not { Length: > 0 } map)
            return;
        var cap = Capture(map);
        var (s, rte, pre, sizes) = OursInFull(map)!.Value;
        var merged = VisClusterList.Run(s, Load(cap["matrix"].Head, cap["matrix"].Blob), sizes,
                                        VisClusterList.Volume(s, pre.Volume, pre.After));
        var (borders, claims) = VisBorders.Sample(merged.State, rte);

        var be = cap["borderentries"].Blob;
        var theirBorders = Enumerable.Range(0, be.Length / 4).Select(i => BitConverter.ToInt32(be, i * 4)).ToArray();
        output.WriteLine($"borders: ours {borders.Length} valve {theirBorders.Length}, same {borders.SequenceEqual(theirBorders)}");

        var blob = cap["borders"].Blob;
        int at = 0, same = 0, countSame = 0, shown = 0;
        for (var k = 0; k < claims.Length && at < blob.Length; k++)
        {
            var count = BitConverter.ToInt32(blob, at);
            at += 4;
            var theirs = Enumerable.Range(0, count)
                .Select(i => new VisBorders.Claim(V(blob, at + i * 28), V(blob, at + i * 28 + 12), BitConverter.ToInt32(blob, at + i * 28 + 24)))
                .ToList();
            at += count * 28;
            if (count == claims[k].Count)
                countSame++;
            if (theirs.SequenceEqual(claims[k]))
                same++;
            else if (shown++ < 6)
                output.WriteLine($"  border {k} (entry {borders[k]}): ours [{string.Join(" ", claims[k])}] valve [{string.Join(" ", theirs)}]");
        }
        output.WriteLine($"claims: {same}/{claims.Length} identical, counts {countSame}; total ours {claims.Sum(c => c.Count)}");

        var rewritten = VisBorders.Rewrite(merged.State, borders, claims);
        var re = cap["resampledentries"].Blob;
        var entriesSame = Enumerable.Range(0, Math.Min(re.Length / 16, rewritten.Entries.Length)).Count(i =>
            BitConverter.ToInt32(re, i * 16) == rewritten.Entries[i].Cluster && BitConverter.ToInt32(re, i * 16 + 4) == rewritten.Entries[i].Packed
            && BitConverter.ToUInt64(re, i * 16 + 8) == rewritten.Entries[i].Cells);
        var rn = cap["resamplednodes"].Blob;
        var nodesSame = Enumerable.Range(0, Math.Min(rn.Length / 8, rewritten.NodeWords.Length)).Count(i =>
            BitConverter.ToUInt32(rn, i * 8) == rewritten.NodeWords[i] && BitConverter.ToUInt16(rn, i * 8 + 4) == rewritten.NodeCounts[i]);
        var sixes = Enumerable.Range(0, rn.Length / 8).Select(i => BitConverter.ToUInt16(rn, i * 8 + 6)).Distinct();
        output.WriteLine($"rewritten: entries ours {rewritten.Entries.Length} valve {re.Length / 16}, identical {entriesSame};"
                       + $" nodes {nodesSame}/{rn.Length / 8}; node +6 values [{string.Join(",", sixes.Take(4))}]");
        Assert.Equal(claims.Length, same);
        Assert.Equal(re.Length / 16, entriesSame);
    }

    /// <summary>
    /// The stages after the borders, each fed our own output of the one before
    /// (the scan seeded with the compile's matrix): AssignClusters2, sky
    /// visibility, and every collapse iteration.
    /// </summary>
    [Fact]
    public void TheLateStages()
    {
        if (Environment.GetEnvironmentVariable("PVS") is not { Length: > 0 } map)
            return;
        var cap = Capture(map);
        var (s, rte, pre, sizes) = OursInFull(map)!.Value;
        var matrix = Load(cap["matrix"].Head, cap["matrix"].Blob);
        var merged = VisClusterList.Run(s, matrix, sizes, VisClusterList.Volume(s, pre.Volume, pre.After));
        var (borders, claims) = VisBorders.Sample(merged.State, rte);
        var state = VisBorders.Consolidate(VisBorders.Rewrite(merged.State, borders, claims));
        output.WriteLine($"assign2: {Same(state, cap["assigned2entries"].Blob, cap["assigned2nodes"].Blob, null)}");

        var sky = VisSky.Visible(state, rte, matrix);
        var (skyHead, skyBlob) = cap["sky"];
        var theirs = Enumerable.Range(0, skyBlob.Length / 4).Select(i => BitConverter.ToUInt32(skyBlob, i * 4)).ToArray();
        output.WriteLine($"sky: valve ok {skyHead.GetProperty("ok").GetInt32()} with {theirs.Sum(w => System.Numerics.BitOperations.PopCount(w))} clusters;"
                       + $" ours {(sky is null ? "none" : $"{sky.Sum(w => System.Numerics.BitOperations.PopCount(w))} clusters, identical {sky.SequenceEqual(theirs)}")}");

        var sixes = Enumerable.Repeat((ushort)0xffff, state.NodeWords.Length).ToArray();
        var (collapsed, _) = VisCollapse.Run(state, sixes, (k, st) =>
        {
            if (cap.ContainsKey("collapsedentries" + k))
                output.WriteLine($"collapse {k}: {Same(st, cap["collapsedentries" + k].Blob, cap["collapsednodes" + k].Blob, cap["collapsedboxes" + k].Blob)}");
        });
        Assert.NotNull(collapsed);
    }

    private static string Same(VisPvs.State s, byte[] entries, byte[] nodes, byte[]? boxes)
    {
        var e = Enumerable.Range(0, Math.Min(entries.Length / 16, s.Entries.Length)).Count(i =>
            BitConverter.ToInt32(entries, i * 16) == s.Entries[i].Cluster && BitConverter.ToInt32(entries, i * 16 + 4) == s.Entries[i].Packed
            && BitConverter.ToUInt64(entries, i * 16 + 8) == s.Entries[i].Cells);
        var n = Enumerable.Range(0, Math.Min(nodes.Length / 8, s.NodeWords.Length)).Count(i =>
            BitConverter.ToUInt32(nodes, i * 8) == s.NodeWords[i] && BitConverter.ToUInt16(nodes, i * 8 + 4) == s.NodeCounts[i]);
        var b = boxes is null ? -1 : Enumerable.Range(0, Math.Min(boxes.Length / 24, s.NodeMins.Length))
            .Count(i => V(boxes, i * 24) == s.NodeMins[i] && V(boxes, i * 24 + 12) == s.NodeMaxs[i]);
        return $"entries {e}/{entries.Length / 16} (ours {s.Entries.Length}), nodes {n}/{nodes.Length / 8} (ours {s.NodeWords.Length})"
             + (boxes is null ? "" : $", boxes {b}");
    }

    [Fact]
    public void TheBoundaryPointGenerator()
    {
        if (Environment.GetEnvironmentVariable("PVS") is not { Length: > 0 } map)
            return;
        var cap = Capture(map);
        var (s, rte) = OursWithScene(map)!.Value;
        var neighbours = VisPvs.Neighbors(s);
        var matrix = Load(cap["after0"].Head, cap["after0"].Blob);
        var theirs = PassPairs(cap, 1);
        var points = VisPvs.TracePoints(s);
        output.WriteLine($"trace points: {points.Sum(c => c.Sum(side => side.Length)):n0}"
                       + $" (+x..+z, the compile's log total: {points.Sum(c => c.Take(5).Sum(side => side.Length)):n0})");
        var pass = 0;
        var passes = VisPvs.BoundaryPoints(s, neighbours, rte, matrix, (pairs, cast) =>
        {
            var valve = pass < theirs.Count ? theirs[pass] : [];
            output.WriteLine($"  pass {pass}: pairs ours {pairs.Count:n0} valve {valve.Count:n0}, in order {pairs.SequenceEqual(valve)}; {cast:n0} rays");
            pass++;
        });
        var (rows, same, mine, valveOnly) = Diff(matrix, cap["after1"].Blob);
        output.WriteLine($"boundary points: {passes} passes; rows identical {same}/{rows}, bits ours-only {mine:n0} valve-only {valveOnly:n0}");
    }

    [Fact]
    public void TheScanInputsAndNeighbours()
    {
        if (Environment.GetEnvironmentVariable("PVS") is not { Length: > 0 } map)
            return;
        var cap = Capture(map);
        var s = Ours(map);
        Assert.NotNull(s);

        var (_, ent) = cap["entries"];
        var entriesSame = 0;
        var n = ent.Length / 16;
        for (var i = 0; i < Math.Min(n, s.Entries.Length); i++)
        {
            var e = s.Entries[i];
            if (BitConverter.ToInt32(ent, i * 16) == e.Cluster && BitConverter.ToInt32(ent, i * 16 + 4) == e.Packed
                && BitConverter.ToUInt64(ent, i * 16 + 8) == e.Cells)
                entriesSame++;
            else if (entriesSame == i)
                output.WriteLine($"  first entry diff at {i}: ours ({e.Cluster},{e.Leaf},{e.Kind},{e.Cells:x})"
                               + $" valve ({BitConverter.ToInt32(ent, i * 16)},{BitConverter.ToInt32(ent, i * 16 + 4) >> 2},"
                               + $"{BitConverter.ToInt32(ent, i * 16 + 4) & 3},{BitConverter.ToUInt64(ent, i * 16 + 8):x})");
        }
        output.WriteLine($"entries: ours {s.Entries.Length} valve {n}, identical {entriesSame}");

        var (_, nodes) = cap["nodes"];
        var (_, boxes) = cap["nodeboxes"];
        int nodesSame = 0, boxesSame = 0;
        for (var i = 0; i < Math.Min(nodes.Length / 8, s.NodeWords.Length); i++)
        {
            if (BitConverter.ToUInt32(nodes, i * 8) == s.NodeWords[i] && BitConverter.ToUInt16(nodes, i * 8 + 4) == s.NodeCounts[i])
                nodesSame++;
            else if (nodesSame == i)
                output.WriteLine($"  first node diff at {i}: ours {s.NodeWords[i]:x}/{s.NodeCounts[i]}"
                               + $" valve {BitConverter.ToUInt32(nodes, i * 8):x}/{BitConverter.ToUInt16(nodes, i * 8 + 4)}");
            if (V(boxes, i * 24) == s.NodeMins[i] && V(boxes, i * 24 + 12) == s.NodeMaxs[i])
                boxesSame++;
        }
        output.WriteLine($"nodes: ours {s.NodeWords.Length} valve {nodes.Length / 8}, identical {nodesSame}, boxes {boxesSame}");

        var (_, cb) = cap["clusterboxes"];
        var cbSame = Enumerable.Range(0, Math.Min(cb.Length / 24, s.Clusters))
            .Count(i => V(cb, i * 24) == s.ClusterMins[i] && V(cb, i * 24 + 12) == s.ClusterMaxs[i]);
        output.WriteLine($"cluster boxes: ours {s.Clusters} valve {cb.Length / 24}, identical {cbSame}");
        foreach (var i in Enumerable.Range(0, Math.Min(cb.Length / 24, s.Clusters))
                     .Where(i => !(V(cb, i * 24) == s.ClusterMins[i] && V(cb, i * 24 + 12) == s.ClusterMaxs[i])).Take(3))
            output.WriteLine($"  cluster {i}: ours {s.ClusterMins[i]} {s.ClusterMaxs[i]} valve {V(cb, i * 24)} {V(cb, i * 24 + 12)}"
                           + $" entries {s.Entries.Count(e => e.Kind == 0 && e.Cluster == i)}");

        var lists = VisPvs.Neighbors(s);
        var (_, nb) = cap["neighbors"];
        int at = 0, same = 0, shown = 0;
        long total = 0;
        for (var c = 0; c < s.Clusters && at < nb.Length; c++)
        {
            var acc = BitConverter.ToInt64(nb, at);
            var count = BitConverter.ToInt32(nb, at + 8);
            var theirs = Enumerable.Range(0, count).Select(k => BitConverter.ToInt32(nb, at + 12 + k * 4)).ToList();
            at += 12 + count * 4;
            total += count;
            if (acc == lists[c].Voxels && theirs.SequenceEqual(lists[c].Neighbors))
                same++;
            else if (shown++ < 5)
                output.WriteLine($"  cluster {c}: voxels ours {lists[c].Voxels} valve {acc};"
                               + $" ours [{string.Join(",", lists[c].Neighbors)}] valve [{string.Join(",", theirs)}]");
        }
        output.WriteLine($"neighbours: {same}/{s.Clusters} clusters identical; total ours"
                       + $" {lists.Sum(l => l.Neighbors.Count)} valve {total}");
    }

    private static Vector3 V(byte[] b, int at)
        => new(BitConverter.ToSingle(b, at), BitConverter.ToSingle(b, at + 4), BitConverter.ToSingle(b, at + 8));
}
