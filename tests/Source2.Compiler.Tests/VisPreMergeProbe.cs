using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Cluster generation and the distance pre-merge against a merge capture's pass 0
/// entry, leaves compared by node slot (the compile's own numbering):
/// <c>PREMERGE_PROBE=&lt;stem&gt;</c> reads %TEMP%/vis_capture/&lt;stem&gt;.bin and
/// its scene &lt;stem&gt;.merge.rte. Our generated sets are cached beside it
/// (&lt;stem&gt;.ours.gen) so later runs skip the six minute generation.
/// </summary>
public class VisPreMergeProbe(ITestOutputHelper output)
{
    [Fact]
    public void PassZeroBySlot()
    {
        if (Environment.GetEnvironmentVariable("PREMERGE_PROBE") is not { Length: > 0 } stem)
            return;
        var dir = Path.Combine(Path.GetTempPath(), "vis_capture");
        var rte = RayTraceEnvironment.ReadFile(Path.Combine(dir, stem + ".merge.rte"));
        var config = VisConfig.Read(Path.Combine(dir, stem + ".merge.viscfg"));
        var (mins, maxs) = rte.TracedBounds;
        var (min, max) = VisVoxelizer.RootCube(mins, maxs, 8f);
        var hints = VisVoxelizer.VoxelHints(config.Hints, mins, maxs, min, max, 8f);
        var tree = VisVoxelizer.Build(rte, min, max, 8f, hints);
        var side = VisVoxelizer.VoxelsPerRoot(min, max, 8f) / VisVoxelizer.VoxelsPerLeaf;
        var regions = VisRegions.Build(tree, side);
        var inside = VisOutside.Detect(tree, regions, rte, 8f);
        var compact = VisRegions.Compact(regions, inside.Regions);
        var slots = VisRegions.Slots(tree);

        var cache = Path.Combine(dir, stem + ".ours.gen");
        List<List<VisMerge.Cluster>> generated;
        if (File.Exists(cache))
            generated = ReadSets(File.ReadAllBytes(cache));
        else
        {
            var sets = VisClusters.Generate(rte, tree, compact, VisClusters.SplitHints.From(config.Hints));
            File.WriteAllBytes(cache, WriteSets(sets.Select(s => s.Clusters)));
            generated = ReadSets(File.ReadAllBytes(cache));
        }
        // Our clusters renumbered to slots, so both sides speak the same leaf ids.
        foreach (var set in generated)
            foreach (var c in set)
                for (var k = 0; k < c.Voxels.Count; k++)
                    c.Voxels[k] = (c.Voxels[k].Mask, slots[c.Voxels[k].Leaf]);

        var pre = generated.Select(s => new VisClusterSet.Set { Clusters = s }).ToList();
        var result = VisPreMerge.Run(pre);
        output.WriteLine($"pre-merge {result.Before} -> {result.After}");

        var theirs = Pass(Path.Combine(dir, stem + ".bin"), 0);
        // Where every slot's voxels sit after the pre-merge, on each side.
        static Dictionary<int, (int Set, ulong Mask)> Owners(IEnumerable<List<VisMerge.Cluster>> sets)
        {
            var found = new Dictionary<int, (int, ulong)>();
            var i = 0;
            foreach (var set in sets)
            {
                foreach (var c in set)
                    foreach (var (mask, leaf) in c.Voxels)
                        found[leaf] = (i, found.TryGetValue(leaf, out var o) ? o.Item2 | mask : mask);
                i++;
            }
            return found;
        }
        var mine = Owners(pre.Select(s => s.Clusters));
        var valve = Owners(theirs);
        var onlyMine = mine.Keys.Except(valve.Keys).Order().ToList();
        var onlyValve = valve.Keys.Except(mine.Keys).Order().ToList();
        output.WriteLine($"slots ours {mine.Count}, Valve {valve.Count}; only ours {onlyMine.Count} [{string.Join(",", onlyMine.Take(20))}], only Valve {onlyValve.Count} [{string.Join(",", onlyValve.Take(20))}]");
        var maskDiff = mine.Keys.Intersect(valve.Keys).Where(k => mine[k].Mask != valve[k].Mask).Order().ToList();
        output.WriteLine($"slots with different masks: {maskDiff.Count} [{string.Join(", ", maskDiff.Take(12).Select(k => $"{k}: {mine[k].Mask:x}/{valve[k].Mask:x}"))}]");
        var setDiff = mine.Keys.Intersect(valve.Keys).Where(k => mine[k].Set != valve[k].Set).Order().ToList();
        output.WriteLine($"slots in a different set: {setDiff.Count}");
        foreach (var k in setDiff.Take(30))
        {
            var (a, b) = (mine[k].Set, valve[k].Set);
            output.WriteLine($"  slot {k}: ours set {a} ({Describe(pre[a].Clusters)}), Valve set {b} ({Describe(theirs[b])})");
        }

        // Word order within each cluster, by slot.
        var orderDiffs = 0;
        for (var i = 0; i < pre.Count && orderDiffs < 4; i++)
            for (var k = 0; k < Math.Min(pre[i].Clusters.Count, theirs[i].Count); k++)
            {
                var (a, b) = (pre[i].Clusters[k].Voxels, theirs[i][k].Voxels);
                if (a.SequenceEqual(b))
                    continue;
                orderDiffs++;
                var at = Enumerable.Range(0, Math.Min(a.Count, b.Count)).FirstOrDefault(j => a[j] != b[j]);
                output.WriteLine($"set {i} cluster {k}: word order parts at {at} of {a.Count}");
                output.WriteLine("  ours  " + string.Join(" ", a.Select(v => $"{v.Leaf}:{v.Mask:x}")));
                output.WriteLine("  valve " + string.Join(" ", b.Select(v => $"{v.Leaf}:{v.Mask:x}")));
            }

        // The generated set of each such slot, as both sides stand before the merge.
        foreach (var k in setDiff.Take(6))
        {
            var g = generated.FindIndex(s => s.Any(c => c.Voxels.Any(v => v.Leaf == k)));
            output.WriteLine($"  slot {k} generated as set {g}: {Describe(generated[g])} box {generated[g][0].Mins}-{generated[g][0].Maxs}");
        }
    }

    /// <summary>
    /// Generation against a --premerge capture's "gen" sets, region by region
    /// (leaves as slots), then the distance pre-merge against its "premerged"
    /// sets. <c>PREMERGE_GEN=&lt;stem&gt;</c>, the capture's own scene.
    /// </summary>
    [Fact]
    public void GenerationThenPreMerge()
    {
        if (Environment.GetEnvironmentVariable("PREMERGE_GEN") is not { Length: > 0 } stem)
            return;
        var dir = Path.Combine(Path.GetTempPath(), "vis_capture");
        var rte = RayTraceEnvironment.ReadFile(Path.Combine(dir, stem + ".merge.rte"));
        var config = VisConfig.Read(Path.Combine(dir, stem + ".merge.viscfg"));
        var (mins, maxs) = rte.TracedBounds;
        var (min, max) = VisVoxelizer.RootCube(mins, maxs, 8f);
        var hints = VisVoxelizer.VoxelHints(config.Hints, mins, maxs, min, max, 8f);
        var tree = VisVoxelizer.Build(rte, min, max, 8f, hints);
        var side = VisVoxelizer.VoxelsPerRoot(min, max, 8f) / VisVoxelizer.VoxelsPerLeaf;
        var regions = VisRegions.Build(tree, side);
        var inside = VisOutside.Detect(tree, regions, rte, 8f);
        var compact = VisRegions.Compact(regions, inside.Regions);
        var slots = VisRegions.Slots(tree);
        var cache = Path.Combine(dir, stem + ".ours.gen");
        if (!File.Exists(cache))
            File.WriteAllBytes(cache, WriteSets(VisClusters.Generate(rte, tree, compact, VisClusters.SplitHints.From(config.Hints)).Select(s => s.Clusters)));
        var generated = ReadSets(File.ReadAllBytes(cache));
        foreach (var set in generated)
            foreach (var c in set)
                for (var k = 0; k < c.Voxels.Count; k++)
                    c.Voxels[k] = (c.Voxels[k].Mask, slots[c.Voxels[k].Leaf]);

        var theirsGen = Event(Path.Combine(dir, stem + ".bin"), "gen");
        output.WriteLine($"generation: sets ours {generated.Count}, Valve {theirsGen.Count}; clusters ours {generated.Sum(s => s.Count)}, Valve {theirsGen.Sum(s => s.Count)}");
        static string Sig(List<VisMerge.Cluster> set) => string.Join("|", set.Select(c => $"{c.Mins}{c.Maxs}{c.OpenSpace}:" + string.Join(",", c.Voxels.Select(v => $"{v.Leaf}/{v.Mask:x}"))));
        // By box: each set keyed by its first cluster's leaf slot and box, so
        // a missing region does not shift every comparison after it.
        static string Region(List<VisMerge.Cluster> set) => set.Count == 0 ? "empty"
            : $"{set.SelectMany(c => c.Voxels).Select(v => v.Leaf).DefaultIfEmpty(-1).Min()}";
        var oursBy = generated.GroupBy(Region).ToDictionary(g => g.Key, g => g.First());
        var theirsBy = theirsGen.GroupBy(Region).ToDictionary(g => g.Key, g => g.First());
        var kinds = new Dictionary<string, int>();
        var shown = 0;
        foreach (var key in oursBy.Keys.Union(theirsBy.Keys))
        {
            if (!oursBy.TryGetValue(key, out var a)) { kinds["only Valve's"] = kinds.GetValueOrDefault("only Valve's") + 1; continue; }
            if (!theirsBy.TryGetValue(key, out var b)) { kinds["only ours"] = kinds.GetValueOrDefault("only ours") + 1; continue; }
            if (Sig(a) == Sig(b))
                continue;
            var kind = a.Count == 1 && b.Count == 1 && a[0].OpenSpace != b[0].OpenSpace ? $"open space ours {a[0].OpenSpace}"
                     : a.Any(c => c.OpenSpace) != b.Any(c => c.OpenSpace) ? "open space with merges"
                     : a.Count != b.Count ? "cluster count" : "cluster content";
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
            if (shown++ < 6)
            {
                output.WriteLine($"  slot {key} ({kind}): ours {Describe(a)}; Valve {Describe(b)}");
                // Each cluster's own box and voxel words (leaf slot / mask).
                foreach (var (who, set) in new[] { ("ours", a), ("Valve", b) })
                    foreach (var c in set)
                        output.WriteLine($"    {who}: {c.Mins}-{c.Maxs} open {c.OpenSpace} {string.Join(",", c.Voxels.Select(v => $"{v.Leaf}/{v.Mask:x}"))}");
            }
        }
        output.WriteLine($"  by region: {string.Join(", ", kinds.Select(kv => $"{kv.Key} {kv.Value}"))}");
        var differ = Enumerable.Range(0, Math.Min(generated.Count, theirsGen.Count)).Where(i => Sig(generated[i]) != Sig(theirsGen[i])).ToList();
        output.WriteLine($"  sets that differ at the same index: {differ.Count}");
        foreach (var i in differ.Take(8))
            output.WriteLine($"  set {i}: ours {Describe(generated[i])}; Valve {Describe(theirsGen[i])}");

        var pre = generated.Select(s => new VisClusterSet.Set { Clusters = s }).ToList();
        var result = VisPreMerge.Run(pre);
        var theirsPre = Event(Path.Combine(dir, stem + ".bin"), "premerged");
        output.WriteLine($"pre-merge: {result.Before} -> {result.After}; sets ours {pre.Count}, Valve {theirsPre.Count}; non-empty ours {pre.Count(s => s.Clusters.Count > 0)}, Valve {theirsPre.Count(s => s.Count > 0)}");
        var differPre = Enumerable.Range(0, Math.Min(pre.Count, theirsPre.Count)).Where(i => Sig(pre[i].Clusters) != Sig(theirsPre[i])).ToList();
        output.WriteLine($"  sets that differ after the pre-merge: {differPre.Count}");
        foreach (var i in differPre.Take(8))
            output.WriteLine($"  set {i}: ours {Describe(pre[i].Clusters)}; Valve {Describe(theirsPre[i])}");
    }

    private static List<List<VisMerge.Cluster>> Event(string path, string ev)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var head = System.Text.Json.JsonDocument.Parse(reader.ReadBytes(reader.ReadInt32())).RootElement;
            var length = reader.ReadInt32();
            if (head.GetProperty("ev").GetString() == ev)
                return ReadSets(reader.ReadBytes(length));
            reader.BaseStream.Seek(length, SeekOrigin.Current);
        }
        throw new InvalidDataException($"no {ev} in {path}");
    }

    private static string Describe(List<VisMerge.Cluster> set) => set.Count == 0 ? "empty"
        : $"{set.Count} clusters, open {set.Count(c => c.OpenSpace)}, words {set.Sum(c => c.Voxels.Count)}, box {set.Select(c => c.Mins).Aggregate(Vector3.Min)}-{set.Select(c => c.Maxs).Aggregate(Vector3.Max)}";

    private static List<List<VisMerge.Cluster>> Pass(string path, int pass)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var head = System.Text.Json.JsonDocument.Parse(reader.ReadBytes(reader.ReadInt32())).RootElement;
            var blob = reader.ReadBytes(reader.ReadInt32());
            if (head.GetProperty("ev").GetString() == "pass" && head.GetProperty("pass").GetInt32() == pass)
                return ReadSets(blob);
        }
        throw new InvalidDataException($"no pass {pass} in {path}");
    }

    internal static List<List<VisMerge.Cluster>> ReadSets(byte[] blob)
    {
        var sets = new List<List<VisMerge.Cluster>>();
        for (var at = 0; at < blob.Length;)
        {
            var length = BitConverter.ToInt32(blob, at + 4);
            sets.Add(VisMergeReplay.Clusters(blob[(at + 8)..(at + 8 + length)]));
            at += 8 + length;
        }
        return sets;
    }

    // The capture's own layout: per set its cluster count and byte length, then
    // each 0x58 byte record followed by its 16 byte (mask, leaf) words.
    private static byte[] WriteSets(IEnumerable<List<VisMerge.Cluster>> sets)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        foreach (var set in sets)
        {
            var record = new MemoryStream();
            var r = new BinaryWriter(record);
            foreach (var c in set)
            {
                var head = new byte[0x58];
                BitConverter.TryWriteBytes(head.AsSpan(0), c.Voxels.Count);
                void Put(int at, Vector3 v)
                {
                    BitConverter.TryWriteBytes(head.AsSpan(at), v.X);
                    BitConverter.TryWriteBytes(head.AsSpan(at + 4), v.Y);
                    BitConverter.TryWriteBytes(head.AsSpan(at + 8), v.Z);
                }
                Put(0x30, c.Mins);
                Put(0x3c, c.Maxs);
                BitConverter.TryWriteBytes(head.AsSpan(0x48), (uint)c.VoxelCount);
                BitConverter.TryWriteBytes(head.AsSpan(0x50), (ushort)c.VoxelSize);
                BitConverter.TryWriteBytes(head.AsSpan(0x52), c.Tag);
                head[0x54] = c.OpenSpace ? (byte)1 : (byte)0;
                r.Write(head);
                foreach (var (mask, leaf) in c.Voxels)
                {
                    r.Write(mask);
                    r.Write(leaf);
                    r.Write(0);
                }
            }
            r.Flush();
            w.Write(set.Count);
            w.Write((int)record.Length);
            w.Write(record.ToArray());
        }
        w.Flush();
        return stream.ToArray();
    }
}
