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
        VisPreMerge.Run(sets);
        VisClusterSet.MergeAll(rte, sets, VisClusters.PassTarget(tree, compact), VisClusters.Cubes(tree, compact));
        var collapsed = VisRegions.Collapse(regions, inside.Regions);
        var assigned = VisAssign.Run(sets, compact.Leaves.Count, collapsed, _ => true);
        return (VisPvs.Build(tree, shipped.MaxBounds, compact, assigned, sets), rte);
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
            (pairs, useful) => output.WriteLine($"  pass: {pairs:n0} rays, {useful:n0} useful"));
        var (rows, same, mine, theirs) = Diff(matrix, cap["after0"].Blob);
        output.WriteLine($"cluster centres: {passes} passes; rows identical {same}/{rows}, bits ours-only {mine:n0} valve-only {theirs:n0}");
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
