using System.Numerics;
using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The visibility mesh merger's inputs built from the .vmap alone against the
/// merger capture (<c>MERGEVMAP=1</c>, <c>MESHMERGE</c>, <c>NODEENTRIES_VMAP</c>):
/// per merged list and entry, the material, the vertices bit for bit after
/// PrepareForMerge's stream filter, the indices, every fact CanMerge reads,
/// and CanMerge itself on every pair against Valve's answers.
/// </summary>
public class MergerFromVmapTests(ITestOutputHelper output)
{
    private sealed record Ours(string Material, int Stride, float[] Vertices, int[] Indices, WrbMeshEntry Facts);

    private sealed class Call
    {
        public List<(string Material, int Stride, float[] Vertices, int[] Indices, WrbMeshEntry? Facts)> Inputs = [];
        public byte[] Pairs = [];
        public List<(ushort[] Key, List<(float[] V, int[] I, uint Flags)> Entries)> Buckets = [];
        public List<(float[] V, int[] I, uint Flags)> Unclustered = [];
    }

    [Fact]
    public void InputsAgainstCapture()
    {
        if (Environment.GetEnvironmentVariable("MERGEVMAP") != "1" || Environment.GetEnvironmentVariable("MESHMERGE") is not { } path
            || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap || CS2Fixtures.StockPak() is not { } pak)
            return;
        var data = File.ReadAllBytes(path);
        var calls = new SortedDictionary<int, Call>();
        (List<(Vector3, Vector3)>[] Flat, float[][] Mutual) capturedVis = ([], []);
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
            if (ev == "call")
                calls.TryAdd(call, new Call());
            else if (ev == "in")
            {
                var (mesh, _, _) = VisibilityMeshMergerReplay.Mesh(head, blob);
                var facts = FactsOf(head, blob);
                calls[call].Inputs.Add((head.GetProperty("material").GetString() ?? "", mesh.Stride, [.. mesh.Vertices], [.. mesh.Indices], facts));
            }
            else if (ev == "canmerge")
                calls[call].Pairs = blob;
            else if (ev == "bucket")
                calls[call].Buckets.Add(([], []));
            else if (ev == "out")
            {
                var (mesh, flags, key) = VisibilityMeshMergerReplay.Mesh(head, blob);
                var b = calls[call].Buckets[^1];
                b.Entries.Add(([.. mesh.Vertices], [.. mesh.Indices], flags));
                calls[call].Buckets[^1] = (key, b.Entries);
            }
            else if (ev == "nov")
            {
                var (mesh, flags, _) = VisibilityMeshMergerReplay.Mesh(head, blob);
                calls[call].Unclustered.Add(([.. mesh.Vertices], [.. mesh.Indices], flags));
            }
            else if (ev == "vis")
                capturedVis = VisOf(head, blob);
        }

        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vmap)))!;
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_pc_dir.vpk"), Path.Combine(game, "core", "shaders_pc_dir.vpk"));
        var doc = MapSource.Read(vmap);
        EntityLumpAuthor.Lighting? lighting = null;
        EntityLumpSet.Author(MapEntities.From(doc), MapFixtures.GameSchema(), Path.GetFileNameWithoutExtension(vmap),
                             MapEntities.FixupEntityNames(doc), doc, MapFixtures.SmartPropLocators, lighting: l => lighting = l);
        var world = WorldNodeBuild.WorldEntries(doc, content, shaders, NodeEntriesFromVmap.RendersAsWorld(game));
        var props = WorldNodeBuild.PropEntries(doc, content, shaders);
        var descriptors = NodeOverlays.FromWorld(doc);
        var (built, overlays) = WorldNodeBuild.BuildNode(world, props, descriptors);
        var envMaps = WorldNodeBuild.EnvMaps(built, lighting!.Volumes);

        var lists = WorldNodeBuild.MergerInputs(built, overlays, descriptors, envMaps, content, shaders)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Select(m => new Ours(m.Material, m.Stride, m.Vertices, m.Indices, m.Facts)).ToList());

        var callList = calls.Values.ToList();
        int entries = 0, sameMesh = 0, sameFacts = 0, pairs = 0, pairsSame = 0, shown = 0;
        var fieldMisses = new Dictionary<string, int>();
        for (var li = 0; li < MeshLists.Merged.Count && li < callList.Count; li++)
        {
            var ours = lists[MeshLists.Merged[li]];
            var valve = callList[li];
            output.WriteLine($"{MeshLists.Merged[li]}: ours {ours.Count}, valve {valve.Inputs.Count}");
            var count = Math.Min(ours.Count, valve.Inputs.Count);
            for (var j = 0; j < count; j++)
            {
                entries++;
                var (o, v) = (ours[j], valve.Inputs[j]);
                // PerVertexLighting is a previous bake's output (180f0ea90), not built here.
                var pvl = o.Facts.Streams.Select((st, k) => (st, First: o.Facts.Streams.Take(k).Sum(x => x.Count))).FirstOrDefault(x => x.st.Name == "PerVertexLighting");
                bool Masked(int k) => pvl.st != null && k % o.Stride >= pvl.First && k % o.Stride < pvl.First + pvl.st.Count;
                var meshSame = o.Stride == v.Stride && o.Indices.SequenceEqual(v.Indices) && o.Vertices.Length == v.Vertices.Length
                               && Enumerable.Range(0, o.Vertices.Length).All(k => Masked(k) || BitConverter.SingleToInt32Bits(o.Vertices[k]) == BitConverter.SingleToInt32Bits(v.Vertices[k]));
                if (meshSame)
                    sameMesh++;
                else if (shown++ < 8)
                    output.WriteLine($"   mesh {j} {Path.GetFileName(v.Material)}: stride {o.Stride}/{v.Stride}, vertices {o.Vertices.Length}/{v.Vertices.Length}, indices same {o.Indices.SequenceEqual(v.Indices)}");
                if (v.Facts is { } vf)
                {
                    var misses = Differences(o.Facts, vf);
                    if (misses.Count == 0)
                        sameFacts++;
                    foreach (var miss in misses)
                        if (fieldMisses.TryAdd(miss, 1))
                            output.WriteLine($"   first {miss} difference, {Path.GetFileName(v.Material)}: ours {Describe(o.Facts, miss)} valve {Describe(vf, miss)}");
                        else
                            fieldMisses[miss]++;
                }
            }
            if (valve.Pairs.Length == valve.Inputs.Count * valve.Inputs.Count && ours.Count == valve.Inputs.Count)
                for (var a = 0; a < count; a++)
                    for (var b = 0; b < count; b++)
                    {
                        pairs++;
                        if (WrbMeshEntry.CanMerge(ours[a].Facts, ours[a].Vertices.Length / ours[a].Stride, ours[b].Facts, ours[b].Vertices.Length / ours[b].Stride)
                            == (valve.Pairs[(a * count) + b] != 0))
                            pairsSame++;
                    }
        }
        foreach (var (field, misses) in fieldMisses.OrderByDescending(kv => kv.Value))
            output.WriteLine($"   fact {field}: {misses} entries differ");
        output.WriteLine($"entries {entries}: meshes {sameMesh} exact, facts {sameFacts} exact; CanMerge {pairsSame} of {pairs} pairs as Valve's");
        Assert.Equal(entries, sameMesh);
        Assert.Equal(entries, sameFacts);
        Assert.Equal(pairs, pairsSame);

        // MERGEVMAP_RUN=1: vis from our own trace scene, then the merger on our inputs.
        if (Environment.GetEnvironmentVariable("MERGEVMAP_RUN") != "1" || MapFixtures.GameSchema() is not { } schema)
            return;
        var packages = new[] { "csgo", "core" }.Select(d => { var pk = new ValvePak.Package(); pk.Read(Path.Combine(game, d, "pak01_dir.vpk")); return pk; }).ToList();
        var visFlags = new MaterialVisFlags.Source([Path.Combine(game, "csgo_addons", addon)], packages);
        bool RendersAsWorld(string cls) => schema.IsSolidClass(cls) && schema.HasFlag(cls, "render_as_world_but_physics_as_entity");
        var plain = DmxBinary.ReadFile(vmap);
        var rte = TraceScene.Environment(TraceScene.Triangles(MapMeshes.Read(plain), content.Material, mat => visFlags[mat], RendersAsWorld));
        var (vxvs, flat) = VisBuild.RunWithBlocks(rte, VisConfig.FromMap(MapEntities.From(plain), schema));
        var mutual = VisOutput.MutualVisibility(vxvs);
        var flatSame = flat.Length == capturedVis.Flat.Length && flat.Zip(capturedVis.Flat).All(p => p.First.SequenceEqual(p.Second));
        var mutualSame = mutual.Length == capturedVis.Mutual.Length && mutual.Zip(capturedVis.Mutual).All(p => p.First.SequenceEqual(p.Second));
        output.WriteLine($"vis from the .vmap: {flat.Length} clusters (captured {capturedVis.Flat.Length}), boxes {flat.Sum(f => f.Count)} (captured {capturedVis.Flat.Sum(f => f.Count)}), "
                         + $"as captured {flatSame}, mutual rows {mutual.Length} (captured {capturedVis.Mutual.Length}) as captured {mutualSame}");
        if (!flatSame)
            for (var k = 0; k < Math.Min(flat.Length, capturedVis.Flat.Length); k++)
                if (!flat[k].SequenceEqual(capturedVis.Flat[k]))
                {
                    output.WriteLine($"   first differing cluster {k}: ours {string.Join(" ", flat[k].Take(3))} captured {string.Join(" ", capturedVis.Flat[k].Take(3))}");
                    break;
                }
        int bucketsSame = 0, bucketsTotal = 0;
        for (var li = 0; li < MeshLists.Merged.Count && li < callList.Count; li++)
        {
            var ours = lists[MeshLists.Merged[li]];
            var inputs = ours.Select((o, k) => new VisibilityMeshMerger.Entry
            {
                Mesh = new VisibilityMeshMerger.Mesh { Vertices = [.. o.Vertices], Stride = o.Stride, PositionOffset = 0, Indices = [.. o.Indices] },
                Origin = k, ObjectFlags = o.Facts.ObjectFlags,
            }).ToList();
            // csgo_core's WorldRendererBuilder: 2048 triangles, 2048 vertices, volume 1800, membership 16.
            var merger = new VisibilityMeshMerger(flat, mutual,
                (a, b) => WrbMeshEntry.CanMerge(ours[a.Origin].Facts, a.Mesh.VertexCount, ours[b.Origin].Facts, b.Mesh.VertexCount))
            { MinTriangles = 2048, MinVertices = 2048, MinVolume = 1800, MaxMembership = 16 };
            var result = merger.MergeMeshes(inputs);
            var valve = callList[li];
            var same = result.Buckets.Count == valve.Buckets.Count && result.Unclustered.Count == valve.Unclustered.Count;
            for (var k = 0; same && k < result.Buckets.Count; k++)
            {
                var (ob, vb) = (result.Buckets[k], valve.Buckets[k]);
                same = ob.Key.SequenceEqual(vb.Key) && ob.Entries.Count == vb.Entries.Count
                       && ob.Entries.Zip(vb.Entries).All(p => p.First.Mesh.Indices.SequenceEqual(p.Second.I)
                           && p.First.Mesh.Vertices.Select(BitConverter.SingleToInt32Bits).SequenceEqual(p.Second.V.Select(BitConverter.SingleToInt32Bits)));
            }
            bucketsTotal++;
            if (same)
                bucketsSame++;
            output.WriteLine($"merge {MeshLists.Merged[li]}: ours {result.Buckets.Count} buckets, {result.Unclustered.Count} unclustered; valve {valve.Buckets.Count}, {valve.Unclustered.Count}: {(same ? "exact" : "DIFFERENT")}");
        }
        Assert.Equal(bucketsTotal, bucketsSame);
    }

    private static (List<(Vector3, Vector3)>[], float[][]) VisOf(JsonElement head, byte[] blob)
    {
        var boxes = head.GetProperty("boxes").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        var rows = head.GetProperty("rows").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        var at = 0;
        float F() { var f = BitConverter.ToSingle(blob, at); at += 4; return f; }
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

    private static WrbMeshEntry? FactsOf(JsonElement head, byte[] blob)
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

    private static string Describe(WrbMeshEntry e, string field) => field switch
    {
        "streams" => string.Join(" ", e.Streams.Select(s => $"{s.Name}/{s.Index}/{s.Count}/{s.Precise}/{s.Type}")),
        "attributes" => $"{e.Attributes:x}",
        "objectFlags" => $"{e.ObjectFlags:x}",
        "debugColor" => $"{e.DebugColor} {e.DebugColorValue}",
        "cubemap" or "lightProbe" => $"{e.Cubemap}/{e.LightProbe}",
        _ => "",
    };

    private static List<string> Differences(WrbMeshEntry a, WrbMeshEntry b)
    {
        var d = new List<string>();
        if (a.Attributes != b.Attributes) d.Add("attributes");
        if (!string.Equals(a.Material, b.Material, StringComparison.OrdinalIgnoreCase)) d.Add("material");
        if (a.OverlayOrder != b.OverlayOrder) d.Add("overlayOrder");
        if (a.ObjectFlags != b.ObjectFlags) d.Add("objectFlags");
        if (a.DebugColor != b.DebugColor || (a.DebugColor && a.DebugColorValue != b.DebugColorValue)) d.Add("debugColor");
        if (a.Field28 != b.Field28) d.Add("field28");
        if (a.Stride != b.Stride) d.Add("stride");
        if (!a.Streams.SequenceEqual(b.Streams)) d.Add("streams");
        if (a.Mesh58 != b.Mesh58) d.Add("mesh58");
        if (!a.MeshFloats.SequenceEqual(b.MeshFloats)) d.Add("meshFloats");
        if (a.Field1a3 != b.Field1a3) d.Add("1a3");
        if (a.Field_b8 != b.Field_b8) d.Add("b8");
        if (a.Field1a5 != b.Field1a5) d.Add("1a5");
        if (a.Field1a8 != b.Field1a8) d.Add("1a8");
        if (a.Cubemap != b.Cubemap) d.Add("cubemap");
        if (a.LightProbe != b.LightProbe) d.Add("lightProbe");
        if (a.Mesh184 != b.Mesh184) d.Add("mesh184");
        if (a.Field9c != b.Field9c) d.Add("9c");
        if (a.Field1a1 != b.Field1a1) d.Add("1a1");
        if (a.Field1a0 != b.Field1a0) d.Add("1a0");
        if (a.Field_b0 != b.Field_b0) d.Add("b0");
        if (a.FadeMax != b.FadeMax) d.Add("fadeMax");
        if (a.Name != b.Name) d.Add("name");
        if (!a.Matrix.SequenceEqual(b.Matrix)) d.Add("matrix");
        if (a.Mesh152 != b.Mesh152) d.Add("mesh152");
        return d;
    }
}
