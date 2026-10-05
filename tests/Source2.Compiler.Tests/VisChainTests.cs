using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The whole vis chain on a compile's own .rte and .viscfg against the capture
/// of that compile (tools/vis/capture_pvs.py), stage by stage, stopping at the
/// first that parts: <c>VISCHAIN=&lt;map&gt;</c> with the capture at
/// %TEMP%/vis_capture/&lt;map&gt;.pvs.bin (and its .rte, .viscfg beside it).
/// </summary>
public class VisChainTests(ITestOutputHelper output)
{
    private sealed class Parted(string why) : Exception(why);

    [Fact]
    public void FirstStageThatParts()
    {
        if (Environment.GetEnvironmentVariable("VISCHAIN") is not { Length: > 0 } map)
            return;
        var capture = Path.Combine(Path.GetTempPath(), "vis_capture", map + ".pvs.bin");
        using var cap = new VisBigReplay.CaptureFile(capture);
        var scene = capture[..^".pvs.bin".Length];
        // VISCHAIN_MERGE=1: also the merge passes' entry sets against
        // tools/vis/capture_merge.py's capture (<map>.bin), run on that
        // compile's scene (<map>.merge.rte); the post-assignment stages are
        // then compared only when both compiles traced the same .rte.
        // VISCHAIN_MERGE=1 reads <map>.bin; any other value names the capture's
        // stem (capture_merge.py --passes --out <stem>.bin: entries and exits).
        var mergeSpec = Environment.GetEnvironmentVariable("VISCHAIN_MERGE");
        var merge = mergeSpec is { Length: > 0 };
        var mergeStem = mergeSpec == "1" ? map : mergeSpec;
        var passes = merge ? MergePasses(Path.Combine(Path.GetTempPath(), "vis_capture", mergeStem + ".bin")) : null;
        var pvsComparable = true;
        if (merge)
        {
            var mergeScene = Path.Combine(Path.GetTempPath(), "vis_capture", mergeStem + ".merge");
            pvsComparable = File.ReadAllBytes(scene + ".rte").AsSpan().SequenceEqual(File.ReadAllBytes(mergeScene + ".rte"));
            scene = mergeScene;
        }
        var rte = RayTraceEnvironment.ReadFile(scene + ".rte");
        var config = VisConfig.Read(scene + ".viscfg");
        var progress = Environment.GetEnvironmentVariable("VISCHAIN_PROGRESS");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Say(string line)
        {
            output.WriteLine(line);
            if (progress is { Length: > 0 })
                File.AppendAllText(progress, $"{clock.Elapsed.TotalSeconds:F0} s {line}{Environment.NewLine}");
        }

        static bool SameEntries(VisVisibility.Entry[] ours, byte[] e)
            => e.Length / 16 == ours.Length && Enumerable.Range(0, ours.Length).All(i => BitConverter.ToInt32(e, i * 16) == ours[i].Cluster
                   && BitConverter.ToInt32(e, i * 16 + 4) == ours[i].Packed && BitConverter.ToUInt64(e, i * 16 + 8) == ours[i].Cells);
        static int FirstEntry(VisVisibility.Entry[] ours, byte[] e)
            => Enumerable.Range(0, Math.Min(ours.Length, e.Length / 16)).FirstOrDefault(i => BitConverter.ToInt32(e, i * 16) != ours[i].Cluster
                   || BitConverter.ToInt32(e, i * 16 + 4) != ours[i].Packed || BitConverter.ToUInt64(e, i * 16 + 8) != ours[i].Cells, -1);
        static bool SameBoxes(Vector3[] mins, Vector3[] maxs, (Vector3[] Mins, Vector3[] Maxs) theirs)
            => mins.SequenceEqual(theirs.Mins) && maxs.SequenceEqual(theirs.Maxs);

        void Inspect(string stage, object value)
        {
            if (!pvsComparable && stage is not "merge-pass")
            {
                Say($"{stage}: not compared (the two captures traced different scenes)");
                return;
            }
            switch (stage)
            {
                case "merge-pass" when passes != null:
                {
                    var (pass, sets) = ((int, IReadOnlyList<VisClusterSet.Set>))value;
                    if (!passes.TryGetValue(pass, out var theirs))
                        throw new Parted($"none: the capture ends before merge pass {pass}, and everything before it matched");
                    var why = FirstDifference(sets, theirs);
                    Say($"merge pass {pass}: {sets.Count:n0}/{theirs.Count:n0} sets, {sets.Sum(x => x.Clusters.Count):n0}/{theirs.Sum(x => x.Count):n0} clusters, {why ?? "same"}");
                    if (why != null)
                        throw new Parted(pass == 0 ? "cluster generation (merge pass 0's entry)" : $"merge pass {pass - 1} (its exit)");
                    break;
                }
                case "assign":
                {
                    var s = (VisPvs.State)value;
                    var theirs = VisBigReplay.StateOf(cap);
                    var entries = SameEntries(s.Entries, cap.Blob("entries"));
                    var nodes = s.NodeWords.SequenceEqual(theirs.NodeWords) && s.NodeCounts.SequenceEqual(theirs.NodeCounts);
                    var nodeBoxes = SameBoxes(s.NodeMins, s.NodeMaxs, (theirs.NodeMins, theirs.NodeMaxs));
                    var clusterBoxes = SameBoxes(s.ClusterMins, s.ClusterMaxs, (theirs.ClusterMins, theirs.ClusterMaxs));
                    Say($"assign: entries {s.Entries.Length:n0}/{theirs.Entries.Length:n0} same {entries} (first differing {FirstEntry(s.Entries, cap.Blob("entries"))}), "
                        + $"nodes {s.NodeWords.Length:n0}/{theirs.NodeWords.Length:n0} same {nodes}, node boxes {nodeBoxes}, clusters {s.Clusters:n0}/{theirs.Clusters:n0} boxes {clusterBoxes}");
                    // VISCHAIN_DUMP=<prefix>: both sides' cluster boxes (mins then maxs,
                    // six floats a cluster) to <prefix>.ours.bin and <prefix>.valve.bin.
                    if (Environment.GetEnvironmentVariable("VISCHAIN_DUMP") is { Length: > 0 } dump)
                    {
                        static byte[] Boxes(Vector3[] mins, Vector3[] maxs)
                            => [.. Enumerable.Range(0, mins.Length).SelectMany(i => new[] { mins[i], maxs[i] })
                                .SelectMany(v => BitConverter.GetBytes(v.X).Concat(BitConverter.GetBytes(v.Y)).Concat(BitConverter.GetBytes(v.Z)))];
                        File.WriteAllBytes(dump + ".ours.bin", Boxes(s.ClusterMins, s.ClusterMaxs));
                        File.WriteAllBytes(dump + ".valve.bin", Boxes(theirs.ClusterMins, theirs.ClusterMaxs));
                    }
                    if (!(entries && nodes && nodeBoxes && clusterBoxes))
                        throw new Parted("assignment");
                    break;
                }
                case "scan":
                {
                    var (rows, same, mine, valve) = VisPvsReplay.Diff((VisPvs.Matrix)value, cap.Blob("matrix"));
                    Say($"scan: matrix rows {same:n0}/{rows:n0} identical, bits only ours {mine:n0}, only Valve's {valve:n0}");
                    if (same != rows)
                        throw new Parted("scan");
                    break;
                }
                case "vis-cluster merge":
                {
                    var r = (VisClusterList.Result)value;
                    var entries = SameEntries(r.State.Entries, cap.Blob("appliedentries"));
                    var boxes = SameBoxes(r.State.ClusterMins, r.State.ClusterMaxs, VisBigReplay.Boxes(cap.Blob("appliedboxes")));
                    Say($"vis-cluster merge: clusters {r.Clusters:n0}, entries same {entries}, boxes same {boxes}");
                    if (!(entries && boxes))
                        throw new Parted("vis-cluster merge");
                    break;
                }
                case "borders":
                {
                    var (borders, claims, flat, state) = ((int[], List<VisBorders.Claim>[], List<(Vector3 Min, Vector3 Max)>[], VisPvs.State))value;
                    var be = cap.Blob("borderentries");
                    var bordersSame = borders.SequenceEqual(Enumerable.Range(0, be.Length / 4).Select(i => BitConverter.ToInt32(be, i * 4)));
                    var stateSame = SameEntries(state.Entries, cap.Blob("assigned2entries"));
                    Say($"borders: {borders.Length:n0} border entries same {bordersSame}, consolidated entries same {stateSame}");
                    if (!(bordersSame && stateSame))
                        throw new Parted("borders");
                    break;
                }
                case "sky":
                    Say("sky and sun reached");
                    break;
            }
        }

        try
        {
            var (vxvs, _) = VisBuild.RunWithBlocks(rte, config, stage: name => Say($"done {name}"), inspect: Inspect);
            var (_, shipped) = VisFixtures.RayTraceScene(Environment.GetEnvironmentVariable("VISCHAIN_ADDON") ?? "s2probe", map)!.Value;
            var ours = vxvs.WriteVxvs();
            var theirs = shipped.WriteVxvs();
            Say($"VXVS ours {ours.Length:n0} valve {theirs.Length:n0}, same {ours.AsSpan().SequenceEqual(theirs)}");
        }
        catch (Parted p)
        {
            Say($"first stage that parts: {p.Message}");
            Assert.Fail(p.Message);
        }
    }

    // The sets entering each merge pass, from capture_merge.py's "pass" records.
    private static Dictionary<int, List<List<VisMerge.Cluster>>> MergePasses(string path)
    {
        var found = new Dictionary<int, List<List<VisMerge.Cluster>>>();
        // The capture may still be running: read it shared and stop at a
        // record it has not finished writing.
        using var reader = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        while (reader.BaseStream.Length - reader.BaseStream.Position >= 4)
        {
            var headLength = reader.ReadInt32();
            if (reader.BaseStream.Length - reader.BaseStream.Position < headLength + 4L)
                break;
            var headBytes = reader.ReadBytes(headLength);
            var blobLength = reader.ReadInt32();
            if (reader.BaseStream.Length - reader.BaseStream.Position < blobLength)
                break;
            var blob = reader.ReadBytes(blobLength);
            var head = System.Text.Json.JsonDocument.Parse(headBytes).RootElement;
            var ev = head.GetProperty("ev").GetString();
            if (ev is not ("pass" or "passout"))
                continue;
            var sets = new List<List<VisMerge.Cluster>>();
            for (var at = 0; at < blob.Length;)
            {
                var length = BitConverter.ToInt32(blob, at + 4);
                sets.Add(VisMergeReplay.Clusters(blob[(at + 8)..(at + 8 + length)]));
                at += 8 + length;
            }
            // A pass's exit is the next one's entry; the fifth's is index 5.
            var index = head.GetProperty("pass").GetInt32() + (ev == "passout" ? 1 : 0);
            found.TryAdd(index, sets);
        }
        return found;
    }

    // Valve numbers a voxel's leaf by octree node slot (branches included),
    // ours by place among the leaves in slot order: one consistent mapping,
    // kept across the passes, stands for equality.
    private static readonly Dictionary<int, int> LeafToValve = [];
    private static readonly Dictionary<int, int> LeafFromValve = [];

    private static bool SameVoxels(List<(ulong Mask, int Leaf)> ours, List<(ulong Mask, int Leaf)> theirs)
    {
        if (ours.Count != theirs.Count)
            return false;
        for (var i = 0; i < ours.Count; i++)
        {
            if (ours[i].Mask != theirs[i].Mask)
                return false;
            if (LeafToValve.TryGetValue(ours[i].Leaf, out var v) ? v != theirs[i].Leaf
                : LeafFromValve.TryGetValue(theirs[i].Leaf, out var o) && o != ours[i].Leaf)
                return false;
            LeafToValve[ours[i].Leaf] = theirs[i].Leaf;
            LeafFromValve[theirs[i].Leaf] = ours[i].Leaf;
        }
        return true;
    }

    private static string? FirstDifference(IReadOnlyList<VisClusterSet.Set> ours, List<List<VisMerge.Cluster>> theirs)
    {
        if (ours.Count != theirs.Count)
        {
            // Sets keyed by their lowest leaf: which regions only one side has.
            static int Key(IEnumerable<VisMerge.Cluster> set) => set.SelectMany(c => c.Voxels).Select(v => v.Leaf).DefaultIfEmpty(-1).Min();
            var mine = ours.Select((x, i) => (Key(x.Clusters), i)).ToLookup(t => t.Item1, t => t.i);
            var valve = theirs.Select((x, i) => (Key(x), i)).ToLookup(t => t.Item1, t => t.i);
            var onlyMine = mine.Where(g => !valve.Contains(g.Key)).Select(g => g.Key).OrderBy(k => k).ToList();
            var onlyValve = valve.Where(g => !mine.Contains(g.Key)).Select(g => g.Key).OrderBy(k => k).ToList();
            string Box(List<VisMerge.Cluster> set) => set.Count == 0 ? "empty" : $"{set.Select(c => c.Mins).Aggregate(Vector3.Min)}-{set.Select(c => c.Maxs).Aggregate(Vector3.Max)} ({set.Count} clusters, {set.Sum(c => c.Voxels.Count)} voxel words)";
            var lines = new List<string> { $"set count {ours.Count} vs {theirs.Count}; sets only ours {onlyMine.Count}, only Valve's {onlyValve.Count}" };
            lines.AddRange(onlyMine.Take(12).Select(k => $"  ours only, lowest leaf {k}: {Box(ours[mine[k].First()].Clusters)}"));
            lines.AddRange(onlyValve.Take(12).Select(k => $"  Valve's only, lowest leaf {k}: {Box(theirs[valve[k].First()])}"));
            return string.Join(Environment.NewLine, lines);
        }
        // Sets the distance pre-merge emptied on one side only: where its runs part.
        var emptied = Enumerable.Range(0, ours.Count).Where(i => (ours[i].Clusters.Count == 0) != (theirs[i].Count == 0)).ToList();
        string Describe(List<VisMerge.Cluster> set) => set.Count == 0 ? "empty"
            : $"{set.Count} clusters, open {set.Count(c => c.OpenSpace)}, words {set.Sum(c => c.Voxels.Count)}, box {set[0].Mins}-{set[0].Maxs}";
        var emptiedNote = emptied.Count == 0 ? "" : $"\n    {emptied.Count} sets emptied on one side only: "
            + string.Join("; ", emptied.Take(6).Select(i => $"set {i} ours {Describe(ours[i].Clusters)} / Valve {Describe(theirs[i])}"));
        for (var i = 0; i < ours.Count; i++)
        {
            var (a, b) = (ours[i].Clusters, theirs[i]);
            if (a.Count != b.Count)
                return $"set {i}: {a.Count} clusters vs {b.Count}" + emptiedNote;
            for (var k = 0; k < a.Count; k++)
            {
                var (x, y) = (a[k], b[k]);
                if (!SameVoxels(x.Voxels, y.Voxels))
                {
                    var n = Math.Min(x.Voxels.Count, y.Voxels.Count);
                    var at = Enumerable.Range(0, n).FirstOrDefault(j => x.Voxels[j].Mask != y.Voxels[j].Mask
                        || (LeafToValve.TryGetValue(x.Voxels[j].Leaf, out var mv) ? mv != y.Voxels[j].Leaf
                            : LeafFromValve.ContainsKey(y.Voxels[j].Leaf)), n);
                    var from = Math.Max(0, at - 3);
                    return $"set {i} cluster {k}: voxels differ at {at} of {x.Voxels.Count}/{y.Voxels.Count} (set box {ours[i].Mins}-{ours[i].Maxs}, {a.Count} clusters)"
                        + "\n    ours  " + string.Join(" ", x.Voxels.Skip(from).Take(8).Select(v => $"{v.Leaf}->{(LeafToValve.TryGetValue(v.Leaf, out var m) ? m : -1)}:{v.Mask:x}"))
                        + "\n    valve " + string.Join(" ", y.Voxels.Skip(from).Take(8).Select(v => $"{v.Leaf}<-{(LeafFromValve.TryGetValue(v.Leaf, out var m) ? m : -1)}:{v.Mask:x}")) + emptiedNote;
                }
                if (x.Mins != y.Mins || x.Maxs != y.Maxs)
                    return $"set {i} cluster {k}: box {x.Mins}-{x.Maxs} vs {y.Mins}-{y.Maxs}";
                if (x.VoxelCount != y.VoxelCount || x.VoxelSize != y.VoxelSize || x.Tag != y.Tag || x.OpenSpace != y.OpenSpace)
                    return $"set {i} cluster {k}: count {x.VoxelCount}/{y.VoxelCount} size {x.VoxelSize}/{y.VoxelSize} tag {x.Tag}/{y.Tag} open {x.OpenSpace}/{y.OpenSpace}";
            }
        }
        return null;
    }
}
