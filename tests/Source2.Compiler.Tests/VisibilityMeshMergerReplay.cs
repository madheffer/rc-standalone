using System.Numerics;
using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Replays <c>CVisibilityMeshMerger::MergeMeshes</c> captured by
/// <c>tools/vis/capture_meshmerge.py</c> (<c>MESHMERGE=&lt;capture&gt;</c>):
/// each call's input entries go through <see cref="VisibilityMeshMerger"/>
/// with the captured cluster boxes, mutual visibility and settings, and
/// Valve's own <c>CanMerge</c> answers for every pair of inputs standing in
/// for the entry comparison (not ported yet). Every bucket, in order, must
/// hold the same entries with the same vertices, indices and cluster set,
/// and the unclustered list the same meshes and flags.
/// </summary>
public class VisibilityMeshMergerReplay(ITestOutputHelper output)
{
    sealed class Captured
    {
        public int N;
        public int[] Settings = [];
        public List<VisibilityMeshMerger.Entry> Inputs = [];
        public List<WrbMeshEntry?> Facts = [];
        public byte[] Pairs = [];
        public List<(ushort[] Key, List<(float[] V, int[] I, uint Flags, ushort[] Key)> Entries)> Buckets = [];
        public List<(float[] V, int[] I, uint Flags, ushort[] Key)> Unclustered = [];
    }

    [Fact]
    public void MergesMatchValve()
    {
        if (Environment.GetEnvironmentVariable("MESHMERGE") is not { } path)
            return;
        var data = File.ReadAllBytes(path);
        List<(Vector3, Vector3)>[] flat = [];
        float[][] mutual = [];
        var calls = new Dictionary<int, Captured>();
        int total = 0, exact = 0;
        for (var at = 0; at < data.Length;)
        {
            var n = BitConverter.ToInt32(data, at);
            var head = JsonDocument.Parse(data.AsMemory(at + 4, n)).RootElement;
            at += 4 + n;
            var m = BitConverter.ToInt32(data, at);
            var blob = data.AsSpan(at + 4, m).ToArray();
            at += 4 + m;
            var ev = head.GetProperty("ev").GetString();
            var call = head.TryGetProperty("call", out var c) ? c.GetInt32() : -1;
            switch (ev)
            {
                case "vis":
                    (flat, mutual) = Vis(head, blob);
                    break;
                case "call":
                    calls[call] = new Captured
                    {
                        N = head.GetProperty("n").GetInt32(),
                        Settings = [.. head.GetProperty("settings").EnumerateArray().Select(x => x.GetInt32())],
                    };
                    break;
                case "in":
                {
                    var (mesh, flags, key) = Mesh(head, blob);
                    calls[call].Inputs.Add(new VisibilityMeshMerger.Entry { Mesh = mesh, Origin = head.GetProperty("i").GetInt32(), ObjectFlags = flags, Clusters = key });
                    calls[call].Facts.Add(Facts(head, blob));
                    break;
                }
                case "canmerge":
                    calls[call].Pairs = blob;
                    break;
                case "bucket":
                    calls[call].Buckets.Add(([], []));
                    break;
                case "out":
                {
                    var (mesh, flags, key) = Mesh(head, blob);
                    var b = calls[call].Buckets[^1];
                    b.Entries.Add(([.. mesh.Vertices], [.. mesh.Indices], flags, key));
                    calls[call].Buckets[^1] = (key, b.Entries);
                    break;
                }
                case "nov":
                {
                    var (mesh, flags, key) = Mesh(head, blob);
                    calls[call].Unclustered.Add(([.. mesh.Vertices], [.. mesh.Indices], flags, key));
                    break;
                }
                case "done":
                    total++;
                    if (Replay(call, calls[call], flat, mutual))
                        exact++;
                    calls.Remove(call);
                    break;
            }
        }
        output.WriteLine($"{exact} of {total} merges exact");
        Assert.Equal(total, exact);
        Assert.Equal(0, canMergeWrong);
    }

    /// <summary>
    /// <see cref="MeshLists"/> on entries built from the .vmap
    /// (<c>MESHMERGE</c> with <c>NODEENTRIES_VMAP</c>): each merged list, in
    /// call order, against the captured call's inputs by material and
    /// attribute flags, in order.
    /// </summary>
    [Fact]
    public void ListsFromVmap()
    {
        if (Environment.GetEnvironmentVariable("MESHMERGE") is not { } path || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap
            || CS2Fixtures.StockPak() is not { } pak)
            return;
        var data = File.ReadAllBytes(path);
        var calls = new SortedDictionary<int, List<(string Material, ulong Attributes)>>();
        for (var at = 0; at < data.Length;)
        {
            var n = BitConverter.ToInt32(data, at);
            var head = JsonDocument.Parse(data.AsMemory(at + 4, n)).RootElement;
            at += 4 + n;
            var m = BitConverter.ToInt32(data, at);
            var blob = data.AsSpan(at + 4, m).ToArray();
            at += 4 + m;
            var ev = head.GetProperty("ev").GetString();
            if (ev == "call")
                calls.TryAdd(head.GetProperty("call").GetInt32(), []);
            if (ev != "in")
                continue;
            var call = head.GetProperty("call").GetInt32();
            if (!calls.TryGetValue(call, out var list))
                calls[call] = list = [];
            list.Add((head.GetProperty("material").GetString() ?? "", BitConverter.ToUInt64(blob, 0x1b0)));
        }
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vmap)))!;
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_pc_dir.vpk"), Path.Combine(game, "core", "shaders_pc_dir.vpk"));
        IReadOnlyCollection<string> Signature(string material) => content.Read(material + "_c") is { } b
            ? [.. MaterialAuthor.ExtractInputSignature(b).Select(x => x.Semantic)] : [];
        var doc = DmxBinary.ReadFile(vmap);
        var entries = NodeMeshEntries.FromWorld(doc, signature: Signature, rendersAsWorld: NodeEntriesFromVmap.RendersAsWorld(game));
        entries.AddRange(NodePropEntries.FromWorld(doc, content));
        var lists = MeshLists.Merged.ToDictionary(k => k, _ => new List<(string, ulong)>());
        foreach (var e in entries)
        {
            var attributes = content.Material(e.Material) is { } info ? MaterialAttributes.Of(info, shaders, content.TextureSize) : MaterialAttributes.Empty;
            var flags = MeshEntryFlags.Compute(attributes, e.Record).Flags;
            // BuildNode drops +0x1a5 and attribute 0x400000000 (lighting dummies).
            if ((flags & 0x400000000) != 0)
                continue;
            var objectFlags = (e.Source?.Attributes.GetValueOrDefault("renderwithdynamic") is true && e.Instances.Length == 0) ? 0x200u : 0u;
            var kind = MeshLists.Assign(new MeshLists.Input(flags, objectFlags, 0, e.Record.FadeMax, false, false));
            if (kind is { } k && lists.TryGetValue(k, out var list))
                list.Add((e.Material, flags));
        }
        // The overlay pass appends its projections (onto the captured entries
        // it ran on) after the node's entries, with a zero record but +0x98 set.
        if (Environment.GetEnvironmentVariable("NODEENTRIES") is { } entriesPath)
        {
            var captured = NodeEntriesFromVmap.Read(entriesPath).Where(c => c.Stage == "Step25ece0:in").ToList();
            var targets = captured.Select(e => new NodeOverlays.Target(e.Vertices, e.Stride, [.. e.Layout.Select(x => new Physics.MeshWeld.Stream(x.Name, x.First, x.Count, false, x.Type))],
                e.Indices, [.. Enumerable.Range(0, 12).Select(k => BitConverter.ToSingle(e.Raw, 0x1c8 + (k * 4)))], e.Raw[0x1a0] != 0 ? 2 : 1,
                BitConverter.ToInt32(e.Raw, 0x40), BitConverter.ToUInt64(e.Raw, 0x1b0))).ToList();
            var descriptors = NodeOverlays.FromWorld(doc);
            foreach (var projection in NodeOverlays.Project(descriptors, targets))
            {
                var d = descriptors[projection.Overlay];
                var attributes = content.Material(d.Material) is { } info ? MaterialAttributes.Of(info, shaders, content.TextureSize) : MaterialAttributes.Empty;
                // GenerateOverlayMeshes' record: +0x98 is 1 (local_450), so a lightmapped overlay drops NeedsLightProbe.
                var flags = MeshEntryFlags.Compute(attributes, new MeshEntryFlags.Record(0, 0, 0, true, null, false)).Flags;
                var kind = MeshLists.Assign(new MeshLists.Input(flags, 0, d.RenderOrder, 0, false, false));
                if (kind is { } k && lists.TryGetValue(k, out var list))
                    list.Add((d.Material, flags));
            }
        }
        var wrong = 0;
        var callList = calls.Values.ToList();
        for (var i = 0; i < MeshLists.Merged.Count; i++)
        {
            var ours = lists[MeshLists.Merged[i]];
            var valve = i < callList.Count ? callList[i] : [];
            var same = ours.Count == valve.Count && ours.Zip(valve).All(p => string.Equals(p.First.Item1, p.Second.Material, StringComparison.OrdinalIgnoreCase) && p.First.Item2 == p.Second.Attributes);
            output.WriteLine($"{MeshLists.Merged[i]}: ours {ours.Count}, valve {valve.Count}, {(same ? "same" : "DIFFERENT")}");
            if (!same)
            {
                wrong++;
                for (var j = 0; j < Math.Max(ours.Count, valve.Count) && j < 8; j++)
                    output.WriteLine($"   {j}: ours {(j < ours.Count ? $"{Path.GetFileName(ours[j].Item1)} {ours[j].Item2:x}" : "-")} | valve {(j < valve.Count ? $"{Path.GetFileName(valve[j].Material)} {valve[j].Attributes:x}" : "-")}");
            }
        }
        foreach (var (material, attributes) in callList.SelectMany(x => x).Where(x => x.Attributes == 0).DistinctBy(x => x.Material))
        {
            var info = content.Material(material);
            output.WriteLine($"zero-flag {material}: {(info == null ? "not found" : $"shader {info.Shader}, ints {string.Join(" ", info.Ints.Select(kv => $"{kv.Key}={kv.Value}"))}")}");
        }
        Assert.Equal(0, wrong);
    }

    int canMergeWrong;

    bool Replay(int call, Captured cap, List<(Vector3, Vector3)>[] flat, float[][] mutual)
    {
        var n = cap.N;
        var asym = 0;
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
                if (cap.Pairs[i * n + j] != cap.Pairs[j * n + i])
                    asym++;
        // Our WRBMeshEntry_CanMerge on the captured entry facts against Valve's answers.
        if (cap.Facts.All(f => f is not null))
        {
            var wrong = 0;
            for (var i = 0; i < n; i++)
                for (var j = 0; j < n; j++)
                {
                    var ours = WrbMeshEntry.CanMerge(cap.Facts[i]!, cap.Inputs[i].Mesh.VertexCount, cap.Facts[j]!, cap.Inputs[j].Mesh.VertexCount);
                    if (ours != (cap.Pairs[i * n + j] != 0) && wrong++ < 3)
                        output.WriteLine($"   CanMerge({i}, {j}): valve {cap.Pairs[i * n + j]}, ours {ours}");
                }
            output.WriteLine($"call {call}: CanMerge {n * n - wrong}/{n * n} pairs as Valve's");
            canMergeWrong += wrong;
        }
        var merger = new VisibilityMeshMerger(flat, mutual,
            (a, b) => a.Mesh.VertexCount + b.Mesh.VertexCount <= 0x200000 && cap.Pairs[a.Origin * n + b.Origin] != 0)
        {
            MinTriangles = cap.Settings[0],
            MinVertices = cap.Settings[1],
            MinVolume = cap.Settings[2],
            MaxMembership = cap.Settings[3],
            Trace = Environment.GetEnvironmentVariable("MESHMERGE_SHOW") == "1" ? l => output.WriteLine("   . " + l) : null,
        };
        var result = merger.MergeMeshes(cap.Inputs);
        var problems = new List<string>();
        if (result.Buckets.Count != cap.Buckets.Count)
            problems.Add($"{result.Buckets.Count} buckets, valve {cap.Buckets.Count}");
        for (var k = 0; k < Math.Min(result.Buckets.Count, cap.Buckets.Count) && problems.Count < 8; k++)
        {
            var ours = result.Buckets[k];
            var theirs = cap.Buckets[k];
            if (ours.Entries.Count != theirs.Entries.Count)
            {
                problems.Add($"bucket {k} [{string.Join(",", ours.Key)}] has {ours.Entries.Count} entries, valve [{string.Join(",", theirs.Key)}] {theirs.Entries.Count}");
                continue;
            }
            for (var j = 0; j < ours.Entries.Count; j++)
            {
                var diff = Diff(ours.Entries[j], theirs.Entries[j]);
                if (diff != null)
                    problems.Add($"bucket {k} entry {j}: {diff}");
            }
        }
        if (result.Unclustered.Count != cap.Unclustered.Count)
            problems.Add($"{result.Unclustered.Count} unclustered, valve {cap.Unclustered.Count}");
        for (var j = 0; j < Math.Min(result.Unclustered.Count, cap.Unclustered.Count) && problems.Count < 8; j++)
        {
            var diff = Diff(result.Unclustered[j], cap.Unclustered[j]);
            if (diff != null)
                problems.Add($"unclustered {j}: {diff}");
        }
        if (Environment.GetEnvironmentVariable("MESHMERGE_SHOW") == "1")
            for (var k = 0; k < Math.Max(result.Buckets.Count, cap.Buckets.Count); k++)
                output.WriteLine($"   bucket {k}: ours [{(k < result.Buckets.Count ? string.Join(",", result.Buckets[k].Key) : "-")}] "
                    + $"{(k < result.Buckets.Count ? string.Join(" ", result.Buckets[k].Entries.Select(e => $"{e.Mesh.VertexCount}v{e.Mesh.Indices.Count / 3}t")) : "")}"
                    + $" | valve {(k < cap.Buckets.Count ? string.Join(" ", cap.Buckets[k].Entries.Select(e => $"{e.V.Length / Math.Max(1, cap.Inputs[0].Mesh.Stride)}v{e.I.Length / 3}t")) : "")}");
        output.WriteLine($"call {call}: {n} entries -> {cap.Buckets.Count} buckets, {cap.Unclustered.Count} unclustered"
            + (asym != 0 ? $", {asym} asymmetric CanMerge pairs" : "") + (problems.Count == 0 ? ", exact" : ""));
        foreach (var p in problems)
            output.WriteLine("   " + p);
        return problems.Count == 0;
    }

    static string? Diff(VisibilityMeshMerger.Entry ours, (float[] V, int[] I, uint Flags, ushort[] Key) theirs)
    {
        if (!ours.Clusters.AsSpan().SequenceEqual(theirs.Key))
            return $"key [{string.Join(",", ours.Clusters)}], valve [{string.Join(",", theirs.Key)}]";
        if (ours.ObjectFlags != theirs.Flags)
            return $"flags {ours.ObjectFlags:x}, valve {theirs.Flags:x}";
        if (ours.Mesh.Indices.Count != theirs.I.Length || ours.Mesh.Vertices.Count != theirs.V.Length)
            return $"{ours.Mesh.Vertices.Count / Math.Max(1, ours.Mesh.Stride)} vertices {ours.Mesh.Indices.Count} indices, valve {theirs.V.Length / Math.Max(1, ours.Mesh.Stride)} vertices {theirs.I.Length} indices";
        if (!ours.Mesh.Indices.SequenceEqual(theirs.I))
            return "indices differ";
        for (var i = 0; i < theirs.V.Length; i++)
        {
            if (BitConverter.SingleToInt32Bits(ours.Mesh.Vertices[i]) != BitConverter.SingleToInt32Bits(theirs.V[i]))
                return $"vertex float {i} differs";
        }
        return null;
    }

    static (List<(Vector3, Vector3)>[], float[][]) Vis(JsonElement head, byte[] blob)
    {
        var boxes = head.GetProperty("boxes").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        var rows = head.GetProperty("rows").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        var at = 0;
        float F()
        {
            var f = BitConverter.ToSingle(blob, at);
            at += 4;
            return f;
        }
        var flat = new List<(Vector3, Vector3)>[boxes.Length];
        for (var c = 0; c < boxes.Length; c++)
        {
            flat[c] = [];
            for (var k = 0; k < boxes[c]; k++)
                flat[c].Add((new Vector3(F(), F(), F()), new Vector3(F(), F(), F())));
        }
        var mutual = new float[rows.Length][];
        for (var r = 0; r < rows.Length; r++)
        {
            mutual[r] = new float[rows[r]];
            for (var k = 0; k < rows[r]; k++)
                mutual[r][k] = F();
        }
        return (flat, mutual);
    }

    /// <summary>The entry facts CanMerge reads, when the capture has them.</summary>
    static WrbMeshEntry? Facts(JsonElement head, byte[] blob)
    {
        if (!head.TryGetProperty("meshRaw", out var raw))
            return null;
        var mesh = Convert.FromHexString(raw.GetString()!);
        var streams = head.GetProperty("streams").EnumerateArray().Select(x => new WrbMeshEntry.Stream(
            x.GetProperty("name").GetString() ?? "", x.GetProperty("index").GetInt32(), x.GetProperty("count").GetInt32(),
            (byte)x.GetProperty("precise").GetInt32(), x.GetProperty("type").GetInt32())).ToList();
        var floats = head.GetProperty("floats").EnumerateArray().Select(x => x.GetSingle()).ToList();
        return WrbMeshEntry.FromBytes(blob.AsSpan(0, 0x238), mesh, head.GetProperty("material").GetString() ?? "", floats,
                                      head.GetProperty("entryName").GetString() ?? "", streams);
    }

    internal static (VisibilityMeshMerger.Mesh, uint Flags, ushort[] Key) Mesh(JsonElement head, byte[] blob)
    {
        var nv = head.GetProperty("nv").GetInt32();
        var stride = head.GetProperty("stride").GetInt32();
        var ni = head.GetProperty("ni").GetInt32();
        var floats = new float[nv * stride];
        Buffer.BlockCopy(blob, 0x238, floats, 0, floats.Length * 4);
        var idx = new int[ni];
        Buffer.BlockCopy(blob, 0x238 + floats.Length * 4, idx, 0, ni * 4);
        var position = 0;
        foreach (var s in head.GetProperty("streams").EnumerateArray())
        {
            if ((s.GetProperty("name").GetString() ?? "").Contains("position", StringComparison.OrdinalIgnoreCase) && s.GetProperty("count").GetInt32() == 3)
            {
                position = s.GetProperty("first").GetInt32();
                break;
            }
        }
        var key = head.GetProperty("key").EnumerateArray().Select(x => (ushort)x.GetInt32()).ToArray();
        var mesh = new VisibilityMeshMerger.Mesh { Vertices = [.. floats], Stride = stride, PositionOffset = position, Indices = [.. idx] };
        return (mesh, head.GetProperty("flags").GetUInt32(), key);
    }
}
