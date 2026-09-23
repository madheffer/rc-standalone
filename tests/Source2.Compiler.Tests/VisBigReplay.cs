using System.Numerics;
using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The paths only a big map reaches, replayed from Valve's own captured state so
/// they are measured alone: the 8,000,000 pair limit, large-cluster-region
/// pairs, and the vis-cluster merge's 8,192 steps and partitions. The scan
/// state is built straight from the capture rather than from our pipeline.
/// Behind <c>BIGPVS=&lt;map&gt;</c>, reading <c>%TEMP%/vis_capture/&lt;map&gt;.pvs.bin</c>.
/// </summary>
public class VisBigReplay(ITestOutputHelper output)
{
    /// <summary>A capture file indexed by record, each blob read when asked for.</summary>
    internal sealed class CaptureFile : IDisposable
    {
        private readonly FileStream _file;
        private readonly Dictionary<string, (JsonElement Head, long Offset, int Length)> _index = [];

        public CaptureFile(string path)
        {
            _file = File.OpenRead(path);
            using var reader = new BinaryReader(_file, System.Text.Encoding.UTF8, leaveOpen: true);
            while (_file.Position < _file.Length)
            {
                var head = JsonDocument.Parse(reader.ReadBytes(reader.ReadInt32())).RootElement.Clone();
                var length = reader.ReadInt32();
                _index[head.GetProperty("ev").GetString()!] = (head, _file.Position, length);
                _file.Seek(length, SeekOrigin.Current);
            }
        }

        public bool Has(string ev) => _index.ContainsKey(ev);

        public JsonElement Head(string ev) => _index[ev].Head;

        public byte[] Blob(string ev)
        {
            var (_, offset, length) = _index[ev];
            var blob = new byte[length];
            _file.Seek(offset, SeekOrigin.Begin);
            _file.ReadExactly(blob);
            return blob;
        }

        public void Dispose() => _file.Dispose();
    }

    private static CaptureFile? Open(out string map)
    {
        map = Environment.GetEnvironmentVariable("BIGPVS") ?? "";
        return map.Length == 0 ? null : new CaptureFile(Path.Combine(Path.GetTempPath(), "vis_capture", map + ".pvs.bin"));
    }

    /// <summary>The sampler's arrays at scan entry, exactly as the compile held them.</summary>
    internal static VisPvs.State StateOf(CaptureFile cap)
    {
        var e = cap.Blob("entries");
        var entries = new VisVisibility.Entry[e.Length / 16];
        for (var i = 0; i < entries.Length; i++)
            entries[i] = new VisVisibility.Entry(BitConverter.ToInt32(e, i * 16), BitConverter.ToInt32(e, i * 16 + 4),
                                                 BitConverter.ToUInt64(e, i * 16 + 8));
        var n = cap.Blob("nodes");
        var words = new uint[n.Length / 8];
        var counts = new ushort[words.Length];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = BitConverter.ToUInt32(n, i * 8);
            counts[i] = BitConverter.ToUInt16(n, i * 8 + 4);
        }
        var (nodeMins, nodeMaxs) = Boxes(cap.Blob("nodeboxes"));
        var (clusterMins, clusterMaxs) = Boxes(cap.Blob("clusterboxes"));
        return new VisPvs.State(entries, words, counts, nodeMins, nodeMaxs, clusterMins, clusterMaxs, 8f);
    }

    private static (Vector3[] Mins, Vector3[] Maxs) Boxes(byte[] b)
    {
        var mins = new Vector3[b.Length / 24];
        var maxs = new Vector3[mins.Length];
        for (var i = 0; i < mins.Length; i++)
        {
            mins[i] = V(b, i * 24);
            maxs[i] = V(b, i * 24 + 12);
        }
        return (mins, maxs);
    }

    private static VisPvs.Matrix Matrix(CaptureFile cap, string ev)
    {
        var head = cap.Head(ev);
        var blob = cap.Blob(ev);
        var m = new VisPvs.Matrix(head.GetProperty("rows").GetInt32(), head.GetProperty("bits").GetInt32());
        for (var r = 0; r < m.Rows.Length; r++)
            Buffer.BlockCopy(blob, r * m.Words * 4, m.Rows[r], 0, m.Words * 4);
        return m;
    }

    private static List<(int, int)> Pairs(byte[] blob)
        => Enumerable.Range(0, blob.Length / 8).Select(i => (BitConverter.ToInt32(blob, i * 8), BitConverter.ToInt32(blob, i * 8 + 4))).ToList();

    private static int FirstPass(CaptureFile cap, int generator)
    {
        for (var k = 0; cap.Has("pairs" + k); k++)
        {
            if (cap.Head("pairs" + k).GetProperty("generator").GetInt32() == generator)
                return k;
        }
        return -1;
    }

    [Fact]
    public void TheNeighbourListOnABigMap()
    {
        using var cap = Open(out _);
        if (cap is null)
            return;
        var s = StateOf(cap);
        var ours = VisPvs.Neighbors(s);
        var nb = cap.Blob("neighbors");
        int at = 0, same = 0;
        for (var c = 0; c < s.Clusters && at < nb.Length; c++)
        {
            var acc = BitConverter.ToInt64(nb, at);
            var count = BitConverter.ToInt32(nb, at + 8);
            var theirs = Enumerable.Range(0, count).Select(k => BitConverter.ToInt32(nb, at + 12 + k * 4)).ToList();
            at += 12 + count * 4;
            if (acc == ours[c].Voxels && theirs.SequenceEqual(ours[c].Neighbors))
                same++;
        }
        output.WriteLine($"neighbours: {same}/{s.Clusters} clusters identical");
        Assert.Equal(s.Clusters, same);
    }

    /// <summary>
    /// The boundary generator's first pass on Valve's post-centres matrix: the
    /// 8,000,000 pair limit is checked after each cluster, so the pass stops just
    /// past it. Pair building only, no rays.
    /// </summary>
    [Fact]
    public void ThePairLimit()
    {
        using var cap = Open(out _);
        if (cap is null)
            return;
        var s = StateOf(cap);
        var first = FirstPass(cap, 1);
        var ours = VisPvs.Pairs(Matrix(cap, "after0"), VisPvs.Neighbors(s), VisPvs.Diagonal(s.Clusters), limit: 8_000_000);
        var theirs = Pairs(cap.Blob("pairs" + first));
        output.WriteLine($"boundary pass 1: ours {ours.Count:n0} valve {theirs.Count:n0}, in order {ours.SequenceEqual(theirs)}");
        Assert.Equal(theirs, ours);
    }

    /// <summary>
    /// The large-cluster-regions generator from Valve's matrix after the boundary
    /// generator: every pass's pairs, and the matrix it leaves.
    /// </summary>
    [Fact]
    public void TheLargeClusterRegions()
    {
        using var cap = Open(out _);
        if (cap is null)
            return;
        var s = StateOf(cap);
        var rte = Scene();
        var matrix = Matrix(cap, "after1");
        var first = FirstPass(cap, 2);
        var pass = 0;
        var passes = VisPvs.LargeClusterRegions(s, VisPvs.Neighbors(s), rte, matrix, (pairs, rays) =>
        {
            var theirs = Pairs(cap.Blob("pairs" + (first + pass)));
            output.WriteLine($"  pass {pass}: pairs ours {pairs.Count:n0} valve {theirs.Count:n0}, in order {pairs.SequenceEqual(theirs)}; {rays:n0} rays");
            pass++;
        });
        var (rows, same, mine, valve) = VisPvsReplay.Diff(matrix, cap.Blob("after2"));
        output.WriteLine($"large cluster regions: {passes} passes; rows identical {same}/{rows}, bits ours-only {mine:n0} valve-only {valve:n0}");
        Assert.Equal(rows, same);
    }

    /// <summary>
    /// The vis-cluster merge on Valve's post-scan matrix, through every 8,192
    /// step and the partitioned rounds: each step's merges in order, and the map.
    /// </summary>
    [Fact]
    public void TheSteppedAndPartitionedMerge()
    {
        using var cap = Open(out _);
        if (cap is null)
            return;
        var s = StateOf(cap);
        var steps = cap.Head("steps");
        var infos = cap.Blob("steps");
        var sizes = Enumerable.Range(0, infos.Length / 4).Select(i => (int)BitConverter.ToUInt16(infos, i * 4)).ToArray();
        var volume = VisClusterList.Volume(s, steps.GetProperty("premergeVolume").GetDouble(), steps.GetProperty("premergeGroups").GetInt32());
        output.WriteLine($"volume {volume:n0}, target {VisClusterList.Target(volume)}");
        var result = VisClusterList.Run(s, Matrix(cap, "matrix"), sizes, volume);

        var theirs = new List<(int, int)>();
        for (var k = 0; cap.Has("merges" + k); k++)
        {
            var step = Pairs(cap.Blob("merges" + k));
            output.WriteLine($"  step {k}: valve {step.Count:n0} merges to {cap.Head("clustermap" + k).GetProperty("total").GetInt32():n0}");
            theirs.AddRange(step);
        }
        var prefix = 0;
        while (prefix < Math.Min(theirs.Count, result.Merges.Count) && theirs[prefix] == result.Merges[prefix])
            prefix++;
        output.WriteLine($"merges: ours {result.Merges.Count:n0} valve {theirs.Count:n0}, identical prefix {prefix:n0}"
                       + (prefix < Math.Min(theirs.Count, result.Merges.Count) ? $"; at {prefix} ours {result.Merges[prefix]} valve {theirs[prefix]}" : ""));
        var map = cap.Blob("clustermap");
        output.WriteLine($"final clusters ours {result.Clusters} valve {cap.Head("clustermap").GetProperty("total").GetInt32()}");
        Assert.Equal(theirs, result.Merges);
    }

    /// <summary>
    /// Sky, sun and the collapse on a big map, each from Valve's own state at
    /// that point: the entries after the merge (the sun's open cells) and after
    /// AssignClusters2 (sky, sun, collapse), with the final matrix.
    /// </summary>
    [Fact]
    public void TheLateStagesOnABigMap()
    {
        using var cap = Open(out var map);
        if (cap is null)
            return;
        var scan = StateOf(cap);
        var (boxMins, boxMaxs) = Boxes(cap.Blob("appliedboxes"));
        var merged = scan with { Entries = EntriesOf(cap.Blob("appliedentries")), ClusterMins = boxMins, ClusterMaxs = boxMaxs };
        var n = cap.Blob("assigned2nodes");
        var words = new uint[n.Length / 8];
        var counts = new ushort[words.Length];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = BitConverter.ToUInt32(n, i * 8);
            counts[i] = BitConverter.ToUInt16(n, i * 8 + 4);
        }
        var assigned = merged with { Entries = EntriesOf(cap.Blob("assigned2entries")), NodeWords = words, NodeCounts = counts };
        var rte = Scene();
        var matrix = Matrix(cap, "appliedmatrix");

        var sky = VisSky.Visible(assigned, rte, matrix);
        var theirSky = Words(cap.Blob("sky"));
        output.WriteLine($"sky: valve {Pop(theirSky)} clusters, ours {(sky is null ? "none" : $"{Pop(sky)}, identical {sky.SequenceEqual(theirSky)}")}");

        var addon = Environment.GetEnvironmentVariable("BIGPVS_ADDON") ?? "s2c_big";
        var config = VisConfig.Read(Path.Combine(Path.GetTempPath(), "csgo_addons", addon, "maps", map + ".viscfg"));
        var theirSun = Words(cap.Blob("sun"));
        var sun = config.DirToSun is { } dir ? VisSun.Visible(assigned, rte, dir, VisSun.OpenCells(merged)) : null;
        output.WriteLine($"sun: valve {Pop(theirSun)} clusters, ours {(sun is null ? "none" : $"{Pop(sun)}, identical {sun.SequenceEqual(theirSun)}")}");

        var same = 0;
        var iterations = 0;
        VisCollapse.Run(assigned, Enumerable.Repeat((ushort)0xffff, words.Length).ToArray(), (k, st) =>
        {
            iterations++;
            if (!cap.Has("collapsedentries" + k))
                return;
            var e = cap.Blob("collapsedentries" + k);
            var nn = cap.Blob("collapsednodes" + k);
            var ok = e.Length / 16 == st.Entries.Length && nn.Length / 8 == st.NodeWords.Length
                  && Enumerable.Range(0, st.Entries.Length).All(i => BitConverter.ToInt32(e, i * 16) == st.Entries[i].Cluster
                        && BitConverter.ToInt32(e, i * 16 + 4) == st.Entries[i].Packed && BitConverter.ToUInt64(e, i * 16 + 8) == st.Entries[i].Cells)
                  && Enumerable.Range(0, st.NodeWords.Length).All(i => BitConverter.ToUInt32(nn, i * 8) == st.NodeWords[i]
                        && BitConverter.ToUInt16(nn, i * 8 + 4) == st.NodeCounts[i]);
            output.WriteLine($"collapse {k}: entries {st.Entries.Length:n0} valve {e.Length / 16:n0}, nodes {st.NodeWords.Length:n0} valve {nn.Length / 8:n0}, identical {ok}");
            same += ok ? 1 : 0;
        });
        Assert.True(sky is not null && sky.SequenceEqual(theirSky));
        Assert.True(sun is not null && sun.SequenceEqual(theirSun));
        Assert.Equal(iterations, same);

        static uint[] Words(byte[] b) => Enumerable.Range(0, b.Length / 4).Select(i => BitConverter.ToUInt32(b, i * 4)).ToArray();
        static int Pop(uint[] w) => w.Sum(x => BitOperations.PopCount(x));
    }

    /// <summary>
    /// The output assembly on a big map, from Valve's own final state (the last
    /// collapse, the final matrix, sky and sun), against the VXVS the compile shipped.
    /// </summary>
    [Fact]
    public void TheOutputOnABigMap()
    {
        using var cap = Open(out var map);
        if (cap is null)
            return;
        var addon = Environment.GetEnvironmentVariable("BIGPVS_ADDON") ?? "s2c_big";
        var (_, shipped) = VisFixtures.RayTraceScene(addon, map)!.Value;
        var last = 0;
        while (cap.Has("collapsedentries" + (last + 1)))
            last++;
        var scan = StateOf(cap);
        var (boxMins, boxMaxs) = Boxes(cap.Blob("appliedboxes"));
        var n = cap.Blob("collapsednodes" + last);
        var words = new uint[n.Length / 8];
        var counts = new ushort[words.Length];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = BitConverter.ToUInt32(n, i * 8);
            counts[i] = BitConverter.ToUInt16(n, i * 8 + 4);
        }
        var (nodeMins, nodeMaxs) = Boxes(cap.Blob("collapsedboxes" + last));
        var final = scan with
        {
            Entries = EntriesOf(cap.Blob("collapsedentries" + last)), NodeWords = words, NodeCounts = counts,
            NodeMins = nodeMins, NodeMaxs = nodeMaxs, ClusterMins = boxMins, ClusterMaxs = boxMaxs,
        };
        uint[]? Vec(string ev) => cap.Has(ev) && cap.Head(ev).GetProperty("ok").GetInt32() != 0
            ? Enumerable.Range(0, cap.Blob(ev).Length / 4).Select(i => BitConverter.ToUInt32(cap.Blob(ev), i * 4)).ToArray() : null;
        var ours = VisOutput.Build(final, Matrix(cap, "appliedmatrix"), Vec("sky"), Vec("sun"), shipped.MinBounds, shipped.MaxBounds);
        var a = ours.WriteVxvs();
        var b = shipped.WriteVxvs();
        var differing = Enumerable.Range(0, Math.Min(a.Length, b.Length)).Count(i => a[i] != b[i]) + Math.Abs(a.Length - b.Length);
        output.WriteLine($"clusters {ours.BaseClusterCount}/{shipped.BaseClusterCount}, nodes {ours.Nodes.Length}/{shipped.Nodes.Length},"
                       + $" regions {ours.Regions.Length}/{shipped.Regions.Length}, masks {ours.Masks.Length}/{shipped.Masks.Length},"
                       + $" enclosed {ours.EnclosedClusterList.Length}/{shipped.EnclosedClusterList.Length}");
        output.WriteLine($"VXVS bytes: ours {a.Length:n0} valve {b.Length:n0}, differing {differing:n0}");
        Assert.Equal(0, differing);
    }

    /// <summary>
    /// Where the time goes on a big map's rays: a sample of the large-region
    /// generator's first-pass pairs, traced and walked separately, one thread.
    /// Behind <c>BENCH=1</c>.
    /// </summary>
    [Fact]
    public void Benchmark()
    {
        if (Environment.GetEnvironmentVariable("BENCH") is null)
            return;
        using var cap = Open(out _);
        if (cap is null)
            return;
        var s = StateOf(cap);
        var rte = Scene();
        var reach = VisPvs.Reach(rte);
        var pairs = Pairs(cap.Blob("pairs" + FirstPass(cap, 2)));
        var lists = VisPvs.EntriesByCluster(s);
        var rays = new List<VisPvs.Ray>();
        var random = new Random(5);
        while (rays.Count < 200_000)
        {
            var (a, b) = pairs[random.Next(pairs.Count)];
            int lo = Math.Min(a, b), hi = Math.Max(a, b);
            var ea = lists[lo][random.Next(lists[lo].Count)];
            var eb = lists[hi][random.Next(lists[hi].Count)];
            var (la, ha) = VisPvs.RegionBox(s, s.Entries[ea]);
            var (lb, hb) = VisPvs.RegionBox(s, s.Entries[eb]);
            rays.Add(VisPvs.Toward((la + ha) * 0.5f, (lb + hb) * 0.5f));
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var ends = new Vector3?[rays.Count];
        for (var i = 0; i < rays.Count; i++)
            ends[i] = VisPvs.Sight(rte, rays[i], reach);
        var trace = clock.Elapsed.TotalSeconds;
        clock.Restart();
        long clusters = 0;
        for (var i = 0; i < rays.Count; i++)
        {
            if (ends[i] is { } end && VisPvs.Walk(s, rays[i].Origin, end, through: true) is { } ids)
                clusters += ids.Count;
        }
        var walk = clock.Elapsed.TotalSeconds;
        output.WriteLine($"{rays.Count:n0} rays, one thread: trace {rays.Count / trace:n0}/s, walk {rays.Count / walk:n0}/s; {clusters / (double)rays.Count:0.0} clusters a walk");
        output.WriteLine($"Valve's 608,325,057 rays at this rate on 16 threads: {608325057.0 / (1 / (trace / rays.Count + walk / rays.Count)) / 16 / 60:0} minutes");
    }

    private static VisVisibility.Entry[] EntriesOf(byte[] e)
    {
        var entries = new VisVisibility.Entry[e.Length / 16];
        for (var i = 0; i < entries.Length; i++)
            entries[i] = new VisVisibility.Entry(BitConverter.ToInt32(e, i * 16), BitConverter.ToInt32(e, i * 16 + 4),
                                                 BitConverter.ToUInt64(e, i * 16 + 8));
        return entries;
    }

    private static RayTraceEnvironment Scene()
    {
        var map = Environment.GetEnvironmentVariable("BIGPVS")!;
        var addon = Environment.GetEnvironmentVariable("BIGPVS_ADDON") ?? "s2c_big";
        return RayTraceEnvironment.ReadFile(Path.Combine(Path.GetTempPath(), "csgo_addons", addon, "maps", map + ".rte"));
    }

    private static Vector3 V(byte[] b, int at)
        => new(BitConverter.ToSingle(b, at), BitConverter.ToSingle(b, at + 4), BitConverter.ToSingle(b, at + 8));
}
