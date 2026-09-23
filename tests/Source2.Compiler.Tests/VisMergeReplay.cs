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

    private static (List<Bucket>, Dictionary<int, (Vector3, Vector3)>) Read(string path)
    {
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
