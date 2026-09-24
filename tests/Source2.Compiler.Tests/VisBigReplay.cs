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
        return map.Length == 0 ? null : new CaptureFile(CapturePath(map));
    }

    private static string CapturePath(string map)
        => Environment.GetEnvironmentVariable("BIGPVS_CAPTURE") is { Length: > 0 } other
            ? other : Path.Combine(Path.GetTempPath(), "vis_capture", map + ".pvs.bin");

    // The compile's own scene file: the copy capture_pvs.py keeps beside the
    // capture when there is one, since the next compile of the map overwrites
    // the one under csgo_addons and the .rte is not byte-stable between compiles.
    private static string SceneFile(string map, string ext)
    {
        var capture = CapturePath(map);
        var beside = capture.EndsWith(".pvs.bin") ? capture[..^".pvs.bin".Length] + ext : capture + ext;
        var addon = Environment.GetEnvironmentVariable("BIGPVS_ADDON") ?? "s2c_big";
        return File.Exists(beside) ? beside : Path.Combine(Path.GetTempPath(), "csgo_addons", addon, "maps", map + ext);
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
    /// The cluster-centre generator from an empty matrix: every pass's pairs, and
    /// the matrix it leaves.
    /// </summary>
    [Fact]
    public void TheClusterCentresOnABigMap()
    {
        using var cap = Open(out _);
        if (cap is null)
            return;
        var s = StateOf(cap);
        var rte = Scene();
        var matrix = new VisPvs.Matrix(s.Clusters + 2, s.Clusters + 2);
        var first = FirstPass(cap, 0);
        var pass = 0;
        var samePasses = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var passes = VisPvs.ClusterCentres(s, VisPvs.Neighbors(s), rte, matrix, (pairs, rays) =>
        {
            var theirs = Pairs(cap.Blob("pairs" + (first + pass)));
            var same = pairs.SequenceEqual(theirs);
            samePasses += same ? 1 : 0;
            output.WriteLine($"  pass {pass}: pairs ours {pairs.Count:n0} valve {theirs.Count:n0}, in order {same}; {rays:n0} rays ({clock.Elapsed.TotalSeconds:0}s)");
            pass++;
        });
        var (rows, same, mine, valve) = VisPvsReplay.Diff(matrix, cap.Blob("after0"));
        output.WriteLine($"cluster centres: {passes} passes; rows identical {same}/{rows}, bits ours-only {mine:n0} valve-only {valve:n0}");
        Assert.Equal(pass, samePasses);
        Assert.Equal(rows, same);
    }

    /// <summary>
    /// The border stage on a big map from Valve's merged state: the border
    /// entries, every claim, the rewrite and AssignClusters2; and, when the
    /// capture has them, the two blocks handed to the world renderer.
    /// </summary>
    [Fact]
    public void TheBordersOnABigMap()
    {
        using var cap = Open(out var map);
        if (cap is null)
            return;
        var scan = StateOf(cap);
        var (boxMins, boxMaxs) = Boxes(cap.Blob("appliedboxes"));
        var merged = scan with { Entries = EntriesOf(cap.Blob("appliedentries")), ClusterMins = boxMins, ClusterMaxs = boxMaxs };
        var rte = Scene();
        var (borders, claims) = VisBorders.Sample(merged, rte);

        var be = cap.Blob("borderentries");
        var theirBorders = Enumerable.Range(0, be.Length / 4).Select(i => BitConverter.ToInt32(be, i * 4)).ToArray();
        output.WriteLine($"borders: ours {borders.Length:n0} valve {theirBorders.Length:n0}, same {borders.SequenceEqual(theirBorders)}");

        var blob = cap.Blob("borders");
        int at = 0, same = 0, shown = 0;
        for (var k = 0; k < claims.Length && at < blob.Length; k++)
        {
            var count = BitConverter.ToInt32(blob, at);
            at += 4;
            var theirs = Enumerable.Range(0, count)
                .Select(i => new VisBorders.Claim(V(blob, at + i * 28), V(blob, at + i * 28 + 12), BitConverter.ToInt32(blob, at + i * 28 + 24)))
                .ToList();
            at += count * 28;
            if (theirs.SequenceEqual(claims[k]))
                same++;
            else if (shown++ < 6)
                output.WriteLine($"  border {k} (entry {borders[k]}): ours [{string.Join(" ", claims[k])}] valve [{string.Join(" ", theirs)}]");
        }
        output.WriteLine($"claims: {same:n0}/{claims.Length:n0} identical; total ours {claims.Sum(c => c.Count):n0}");

        var rewritten = VisBorders.Rewrite(merged, borders, claims);
        var rewriteSame = SameState(rewritten, cap.Blob("resampledentries"), cap.Blob("resamplednodes"));
        var consolidated = VisBorders.Consolidate(rewritten);
        var assignedSame = SameState(consolidated, cap.Blob("assigned2entries"), cap.Blob("assigned2nodes"));
        output.WriteLine($"rewrite identical {rewriteSame}; AssignClusters2 identical {assignedSame}");

        var blocks = true;
        if (cap.Has("flatboxes"))
        {
            var flat = VisOutput.FlatClusterBoxes(merged, claims);
            var fb = cap.Blob("flatboxes");
            int fat = 0, flatSame = 0;
            for (var c = 0; c < flat.Length && fat < fb.Length; c++)
            {
                var count = BitConverter.ToInt32(fb, fat);
                fat += 4;
                var theirs = Enumerable.Range(0, count).Select(k => (V(fb, fat + k * 24), V(fb, fat + k * 24 + 12))).ToList();
                fat += count * 24;
                flatSame += theirs.SequenceEqual(flat[c]) ? 1 : 0;
            }
            output.WriteLine($"FlatVisClusterVector: {flatSame:n0}/{flat.Length:n0} clusters identical");
            blocks &= flatSame == flat.Length;
        }
        if (cap.Has("mutualvis"))
        {
            var addon = Environment.GetEnvironmentVariable("BIGPVS_ADDON") ?? "s2c_big";
            var (_, shipped) = VisFixtures.RayTraceScene(addon, map)!.Value;
            var mv = VisOutput.MutualVisibility(shipped);
            var mb = cap.Blob("mutualvis");
            int mat = 0, rowsSame = 0;
            for (var j = 0; j < mv.Length && mat < mb.Length; j++)
            {
                var count = BitConverter.ToInt32(mb, mat);
                mat += 4;
                var ok = count == mv[j].Length;
                for (var k = 0; ok && k < count; k++)
                    ok = BitConverter.ToSingle(mb, mat + k * 4) == mv[j][k];
                mat += count * 4;
                rowsSame += ok ? 1 : 0;
            }
            output.WriteLine($"MutualVisibilityMatrix: {rowsSame:n0}/{mv.Length:n0} rows identical");
            blocks &= rowsSame == mv.Length;
        }
        Assert.Equal(theirBorders, borders);
        Assert.Equal(claims.Length, same);
        Assert.True(rewriteSame && assignedSame && blocks);

        static bool SameState(VisPvs.State st, byte[] e, byte[] n)
            => e.Length / 16 == st.Entries.Length && n.Length / 8 == st.NodeWords.Length
               && Enumerable.Range(0, st.Entries.Length).All(i => BitConverter.ToInt32(e, i * 16) == st.Entries[i].Cluster
                     && BitConverter.ToInt32(e, i * 16 + 4) == st.Entries[i].Packed && BitConverter.ToUInt64(e, i * 16 + 8) == st.Entries[i].Cells)
               && Enumerable.Range(0, st.NodeWords.Length).All(i => BitConverter.ToUInt32(n, i * 8) == st.NodeWords[i]
                     && BitConverter.ToUInt16(n, i * 8 + 4) == st.NodeCounts[i]);
    }

    /// <summary>
    /// One segment against named triangles, every intermediate value printed
    /// round-trip exact. <c>BIGSEG=ox,oy,oz;px,py,pz;tri,tri...</c>.
    /// </summary>
    [Fact]
    public void SegmentDetail()
    {
        if (Environment.GetEnvironmentVariable("BIGSEG") is not { Length: > 0 } spec || Environment.GetEnvironmentVariable("BIGPVS") is null)
            return;
        var parts = spec.Split(';');
        Vector3 P(string t) { var f = t.Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray(); return new(f[0], f[1], f[2]); }
        var (o, p) = (P(parts[0]), P(parts[1]));
        var rte = Scene();
        var hit = rte.Segment(o, p, VisBorders.Ignored);
        output.WriteLine($"segment {o} -> {p}: nearest {(hit is { } h ? $"tri {h.Triangle} t {h.Distance:R}" : "none")}");
        foreach (var tri in parts[2].Split(',').Select(int.Parse))
        {
            var dt = rte.SegmentDetail(o, p, tri);
            output.WriteLine($"  tri {tri}: {(dt is { } x ? $"t {x.T:R} u {x.U:R} v {x.V:R} first {x.First:R} second {x.Second:R} sum {x.First + x.Second:R} denom {x.Denom:R}" : "not traced")}"
                           + $" corners [{string.Join(" ", rte.TracedCorners(tri) ?? [])}]");
        }
    }

    /// <summary>
    /// The tracer the compile rebuilt, against <see cref="TracerKd"/>: the tree
    /// walked from the root side by side (axis, split bits, each leaf's slots in
    /// order), every slot's record against ours field by field, and the box.
    /// Needs a capture with the <c>kd*</c> records.
    /// </summary>
    [Fact]
    public void TheTracerTree()
    {
        using var cap = Open(out _);
        if (cap is null || !cap.Has("kdnodes"))
            return;
        var rte = Scene();
        var kd = new TracerKd(rte);
        var nodes = cap.Blob("kdnodes");
        var index = cap.Blob("kdindex");
        int inner = 0, leaves = 0, differ = 0, shown = 0;
        var stack = new Stack<(int Valve, int Ours, string Path)>();
        stack.Push((0, 0, ""));
        while (stack.Count > 0)
        {
            var (v, o, path) = stack.Pop();
            var word = BitConverter.ToUInt32(nodes, v * 8);
            var ours = kd.Nodes[o];
            var axis = (int)(word & 3);
            string? why = null;
            if (axis != ours.Axis)
                why = $"axis valve {axis} ours {ours.Axis}";
            else if (axis != 3)
            {
                var split = BitConverter.ToSingle(nodes, v * 8 + 4);
                if (BitConverter.SingleToInt32Bits(split) != BitConverter.SingleToInt32Bits(ours.Split))
                    why = $"split valve {split:R} ours {ours.Split:R}";
                else
                {
                    inner++;
                    stack.Push(((int)(word >> 2) + 1, ours.Lower + 1, path + $" {"xyz"[axis]}>{split}"));
                    stack.Push(((int)(word >> 2), ours.Lower, path + $" {"xyz"[axis]}<{split}"));
                    continue;
                }
            }
            else
            {
                leaves++;
                var count = BitConverter.ToInt32(nodes, v * 8 + 4);
                var slots = Enumerable.Range((int)(word >> 2), count).Select(k => BitConverter.ToInt32(index, k * 4)).ToArray();
                if (!slots.SequenceEqual(ours.Slots))
                    why = $"leaf valve [{string.Join(",", slots.Take(12))}{(slots.Length > 12 ? $" ..{slots.Length}" : "")}]"
                        + $" ours [{string.Join(",", ours.Slots.Take(12))}{(ours.Slots.Length > 12 ? $" ..{ours.Slots.Length}" : "")}]";
            }
            if (why is null)
                continue;
            differ++;
            if (shown++ < 8)
                output.WriteLine($"  node {v}/{o}{path}: {why}");
        }
        output.WriteLine($"tree: {inner:n0} splits and {leaves:n0} leaves identical, {differ} differ");

        var records = cap.Blob("kdrecords");
        var order = rte.TracerOrder;
        var slotsCount = records.Length / 0x30;
        int same = 0, badFloat = 0, badAxes = 0, badFlags = 0;
        for (var slot = 0; slot < slotsCount && slot < order.Length; slot++)
        {
            var r = rte.TracedRecord(order[slot]);
            var at = slot * 0x30;
            var ok = true;
            foreach (var k in new[] { 0, 1, 2, 3, 5, 6, 7, 8, 9, 10 })
            {
                if (BitConverter.ToInt32(records, at + k * 4) != BitConverter.SingleToInt32Bits(r[k]))
                {
                    ok = false;
                    if (badFloat++ < 4)
                        output.WriteLine($"  slot {slot} (tri {order[slot]}) float {k}: valve {BitConverter.ToSingle(records, at + k * 4):R} ours {r[k]:R}");
                }
            }
            if (records[at + 0x2c] != (int)r[11] || records[at + 0x2d] != (int)r[12])
            {
                ok = false;
                badAxes++;
            }
            if (BitConverter.ToUInt16(records, at + 0x2e) != rte.Flags(order[slot]))
            {
                ok = false;
                if (badFlags++ < 4)
                    output.WriteLine($"  slot {slot} (tri {order[slot]}) flags valve {BitConverter.ToUInt16(records, at + 0x2e):x4} ours {rte.Flags(order[slot]):x4}");
            }
            same += ok ? 1 : 0;
        }
        output.WriteLine($"records: {same:n0}/{slotsCount:n0} identical (ours {order.Length:n0}); bad floats {badFloat}, axes {badAxes}, flags {badFlags};"
                       + $" slot 4 sample valve {BitConverter.ToSingle(records, 0x10):R}");
        var box = cap.Blob("kdbox");
        var (bmin, bmax) = rte.TracedBounds;
        output.WriteLine($"box valve {V(box, 0)}..{V(box, 12)} ours {bmin}..{bmax}, bits same {V(box, 0) == bmin && V(box, 12) == bmax}");
        Assert.Equal(0, differ);
        Assert.Equal(slotsCount, same);
    }

    /// <summary>
    /// Where named triangles sit in the rebuilt kd tree: every leaf holding
    /// one, its box and the triangle's place in it. <c>BIGKD=tri,tri...</c>.
    /// </summary>
    [Fact]
    public void KdLeaves()
    {
        if (Environment.GetEnvironmentVariable("BIGKD") is not { Length: > 0 } list || Environment.GetEnvironmentVariable("BIGPVS") is null)
            return;
        var rte = Scene();
        var kd = new TracerKd(rte);
        var order = rte.TracerOrder;
        var slotOf = new Dictionary<int, int>();
        for (var s = 0; s < order.Length; s++)
            slotOf[order[s]] = s;
        var wanted = list.Split(',').Select(int.Parse).ToList();
        foreach (var t in wanted)
            output.WriteLine($"tri {t}: slot {(slotOf.TryGetValue(t, out var sl) ? sl : -1)} flags {rte.Flags(t):x4} corners [{string.Join(" ", rte.TracedCorners(t) ?? [])}]");
        var (mins, maxs) = rte.TracedBounds;
        void Visit(int at, Vector3 lo, Vector3 hi, string path)
        {
            var node = kd.Nodes[at];
            if (node.Axis == 3)
            {
                foreach (var t in wanted)
                {
                    if (!slotOf.TryGetValue(t, out var slot))
                        continue;
                    var at2 = Array.IndexOf(node.Slots, slot);
                    if (at2 >= 0)
                        output.WriteLine($"  leaf {at} {lo}..{hi}: tri {t} at {at2} of {node.Slots.Length}; path {path}");
                }
                return;
            }
            Vector3 With(Vector3 v, int a, float x) => a == 0 ? v with { X = x } : a == 1 ? v with { Y = x } : v with { Z = x };
            Visit(node.Lower, lo, With(hi, node.Axis, node.Split), path + $" {"xyz"[node.Axis]}<{node.Split}");
            Visit(node.Lower + 1, With(lo, node.Axis, node.Split), hi, path + $" {"xyz"[node.Axis]}>{node.Split}");
        }
        Visit(0, mins, maxs, "");
    }

    /// <summary>
    /// For the border entries named in <c>BIGDIAG</c> (comma separated): every
    /// segment the border stage traces that meets two or more triangles at the
    /// same nearest distance, and whether those disagree on facing the ray.
    /// </summary>
    [Fact]
    public void BorderTies()
    {
        if (Environment.GetEnvironmentVariable("BIGDIAG") is not { Length: > 0 } list)
            return;
        using var cap = Open(out _);
        if (cap is null)
            return;
        var scan = StateOf(cap);
        var (boxMins, boxMaxs) = Boxes(cap.Blob("appliedboxes"));
        var merged = scan with { Entries = EntriesOf(cap.Blob("appliedentries")), ClusterMins = boxMins, ClusterMaxs = boxMaxs };
        var rte = Scene();
        var cube = VisBorders.CubePoints();
        foreach (var border in list.Split(',').Select(int.Parse))
        {
            var (bmin, bmax) = VisPvs.RegionBox(merged, merged.Entries[border]);
            float cx = (bmax.X + bmin.X) * 0.5f, cy = (bmax.Y + bmin.Y) * 0.5f, cz = (bmax.Z + bmin.Z) * 0.5f;
            float hx = MathF.Max(0f, (bmax.X - cx) - 0.1f), hy = MathF.Max(0f, (bmax.Y - cy) - 0.1f), hz = MathF.Max(0f, (bmax.Z - cz) - 0.1f);
            var points = cube.Select(c => new Vector3(cx + (hx * c.X), (hy * c.Y) + cy, (hz * c.Z) + cz)).ToArray();
            var grow = merged.BaseVoxelSize;
            var neighbours = VisPvs.Entries(merged, bmin - new Vector3(grow), bmax + new Vector3(grow)).Distinct().ToList();
            int ties = 0, split = 0;
            var wide = VisPvs.Entries(merged, bmin - new Vector3(grow * 3), bmax + new Vector3(grow * 3)).Distinct();
            foreach (var e in wide)
            {
                var (elo, ehi) = VisPvs.RegionBox(merged, merged.Entries[e]);
                var leaf = merged.Entries[e].Packed >> 2;
                output.WriteLine($"    record {e} cluster {merged.Entries[e].Cluster} kind {merged.Entries[e].Packed & 3} box {elo}..{ehi}"
                               + $" leaf {leaf} {merged.NodeMins[leaf]}..{merged.NodeMaxs[leaf]} cells {merged.Entries[e].Cells:x16}"
                               + (neighbours.Contains(e) ? " (neighbour)" : ""));
            }
            foreach (var n in neighbours)
            {
                if ((merged.Entries[n].Packed & 3) != 0)
                    continue;
                var (nlo, nhi) = VisPvs.RegionBox(merged, merged.Entries[n]);
                var o = (nlo + nhi) * 0.5f;
                var batch = rte.Segments(points.Select(p => (o, p)).ToArray(), VisBorders.Ignored);
                for (var i = 0; i < points.Length; i++)
                {
                    var single = rte.Segment(o, points[i], VisBorders.Ignored);
                    if (single?.Triangle != batch[i]?.Triangle || single?.Distance != batch[i]?.Distance)
                        output.WriteLine($"    packet differs: cluster {merged.Entries[n].Cluster} {o} -> {points[i]} (#{i}): brute "
                                       + $"{(single is { } s1 ? $"tri {s1.Triangle} n {s1.Normal} t {s1.Distance:R}" : "miss")}, packet "
                                       + $"{(batch[i] is { } b1 ? $"tri {b1.Triangle} n {b1.Normal} t {b1.Distance:R}" : "miss")}");
                }
                foreach (var p in points)
                {
                    if (Environment.GetEnvironmentVariable("BIGDIAG_CLUSTER") is { Length: > 0 } only
                        && merged.Entries[n].Cluster == int.Parse(only) && rte.Segment(o, p, VisBorders.Ignored) is { } seen)
                    {
                        var d0 = p - o;
                        output.WriteLine($"    {o} -> {p}: tri {seen.Triangle} flags {rte.Flags(seen.Triangle):x4} n {seen.Normal} t {seen.Distance} of {d0.Length()},"
                                       + $" facing {(seen.Normal.Z * d0.Z) + (seen.Normal.Y * d0.Y) + (seen.Normal.X * d0.X) < 0f}"
                                       + $" corners [{string.Join(" ", rte.TracedCorners(seen.Triangle) ?? [])}]");
                    }
                    var tied = rte.SegmentTies(o, p, VisBorders.Ignored);
                    if (tied.Count < 2)
                        continue;
                    ties++;
                    var d = p - o;
                    var facing = tied.Select(h => (h.Normal.Z * d.Z) + (h.Normal.Y * d.Y) + (h.Normal.X * d.X) < 0f).Distinct().Count();
                    if (facing > 1)
                    {
                        split++;
                        if (split <= 3)
                            output.WriteLine($"  entry {border} cluster {merged.Entries[n].Cluster}: {o} -> {p}: "
                                           + string.Join(", ", tied.Select(h => $"tri {h.Triangle} n {h.Normal} t {h.Distance}")));
                    }
                }
            }
            output.WriteLine($"entry {border}: leaf {merged.Entries[border].Packed >> 2} {merged.NodeMins[merged.Entries[border].Packed >> 2]}..{merged.NodeMaxs[merged.Entries[border].Packed >> 2]} cells {merged.Entries[border].Cells:x16} box {bmin}..{bmax}; {neighbours.Count} neighbours; tied segments {ties}, of which facing both ways {split}");
        }
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

        var config = VisConfig.Read(SceneFile(map, ".viscfg"));
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
        return RayTraceEnvironment.ReadFile(SceneFile(map, ".rte"));
    }

    private static Vector3 V(byte[] b, int at)
        => new(BitConverter.ToSingle(b, at), BitConverter.ToSingle(b, at + 4), BitConverter.ToSingle(b, at + 8));
}
