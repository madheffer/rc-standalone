using Source2.Compiler.Lighting;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The lightmap chart meshes from the .vmap against a full compile's capture
/// (tools/vis/capture_nodeentries.py --full; <c>NODEENTRIES=&lt;capture&gt;</c>,
/// <c>NODEENTRIES_VMAP=&lt;vmap&gt;</c>): our node entries after the
/// T-junction fix, chosen and sorted as BakeLightMaps and BakeLighting do,
/// against the list GenerateCharts was given. No captured byte feeds ours.
/// </summary>
public class ChartMeshesTests(ITestOutputHelper output)
{
    /// <summary>Our node entries before BuildNode's closing weld, the importance volumes, and each material's RenderBackfaces.</summary>
    internal sealed record Inputs(List<ChartMeshes.Entry> Entries, List<System.Numerics.Vector3[]> Volumes, Dictionary<string, bool> RenderBackfaces);

    internal static Inputs? OurInputs(string vmap)
    {
        if (CS2Fixtures.StockPak() is not { } pak)
            return null;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vmap)))!;
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_pc_dir.vpk"), Path.Combine(game, "core", "shaders_pc_dir.vpk"));
        var doc = MapSource.Read(vmap);
        var world = WorldNodeBuild.WorldEntries(doc, content, shaders, NodeEntriesFromVmap.RendersAsWorld(game), buildBakes: true);
        var props = WorldNodeBuild.PropEntries(doc, content, shaders, buildBakes: true);
        var built = WorldNodeBuild.BeforeWeld(world, props);
        var backfaces = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in built.Select(b => b.Source.Entry.Material).Distinct())
            backfaces[m] = content.Material(m) is { } info && (MaterialAttributes.Of(info, shaders, content.TextureSize).Int(MeshEntryFlags.Key("renderbackfaces")) ?? 0) != 0;
        return new Inputs([.. built.Select(b => new ChartMeshes.Entry(b.Vertices, b.Source.Entry.Stride, b.Source.Entry.Streams, b.Indices, b.Source.Entry.Material,
                                                                    b.Source.Header.Attributes, b.Source.Header.Baked, b.Source.Header.TextureWidth, b.Source.Header.TextureHeight,
                                                                    b.Source.Header.Byte1a0 != 0 ? 1f : LightPow.Pow(2f, b.Source.Entry.LightmapBias), b.Source.Header.Field9c,
                                                                    b.Source.Header.Byte1a0 != 0 && (b.Source.Header.Fieldc0 & 0x10) != 0))],
                          WorldNodeBuild.ImportanceVolumes(world, props), backfaces);
    }

    [Fact]
    public void ChartedEntriesAreValves()
    {
        if (Environment.GetEnvironmentVariable("NODEENTRIES") is not { } path || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap
            || OurInputs(vmap) is not { } inputs)
            return;
        var entries = inputs.Entries;
        var valve = NodeEntriesFromVmap.Read(path).Where(c => c.Stage == "GenerateCharts:in").ToList();
        var ours = ChartMeshes.Sort(entries.Select(ChartMeshes.Strip).Where(ChartMeshes.IsBaked));
        output.WriteLine($"node entries {entries.Count}, charted: ours {ours.Length}, valve {valve.Count}");
        // The node's entries as BakeLightMaps finds them, where the choice differs.
        var before = NodeEntriesFromVmap.Read(path).Where(c => c.Stage == "BakeLightMaps:in").ToList();
        for (var i = 0; i < Math.Min(entries.Count, before.Count); i++)
        {
            var (o, v) = (ChartMeshes.Strip(entries[i]), before[i]);
            var vBaked = v.Raw[0x98] != 0;
            var vAttr = BitConverter.ToUInt64(v.Raw, 0x1b0);
            if (o.Baked != vBaked || o.Attributes != vAttr || o.VertexCount != v.Vertices.Length / v.Stride)
                output.WriteLine($"  node {i} {Path.GetFileName(v.Material)}: ours {o.VertexCount} v, baked {o.Baked}, {o.Attributes:x}; valve {v.Vertices.Length / v.Stride} v, baked {vBaked}, {vAttr:x}");
        }
        var misses = new List<string>();
        for (var i = 0; i < Math.Max(ours.Length, valve.Count); i++)
        {
            if (i >= ours.Length || i >= valve.Count)
            {
                misses.Add($"entry {i}: only in {(i >= ours.Length ? "valve" : "ours")}");
                continue;
            }
            var (o, v) = (ours[i], valve[i]);
            var problems = new List<string>();
            if (o.Stride != v.Stride || !o.Vertices.Select(BitConverter.SingleToInt32Bits).SequenceEqual(v.Vertices.Select(BitConverter.SingleToInt32Bits)))
                problems.Add($"vertices ({o.VertexCount} / {v.Vertices.Length / v.Stride})");
            if (!o.Indices.SequenceEqual(v.Indices))
                problems.Add("indices");
            if (!string.Join(",", o.Streams.Select(s => $"{s.Name}@{s.First}")).Equals(string.Join(",", v.Layout.Select(s => $"{s.Name}@{s.First}")), StringComparison.Ordinal))
                problems.Add("streams");
            if (BitConverter.ToUInt64(v.Raw, 0x1b0) != o.Attributes)
                problems.Add($"attributes {o.Attributes:x} / {BitConverter.ToUInt64(v.Raw, 0x1b0):x}");
            if (problems.Count > 0)
                misses.Add($"entry {i} {Path.GetFileName(v.Material)}: {string.Join(", ", problems)}");
        }
        foreach (var m in misses.Take(20))
            output.WriteLine(m);
        Assert.Empty(misses);
    }

    /// <summary>
    /// The chart meshes as PrepareChartMesh hands them to the charting
    /// (<see cref="ChartMeshPrep"/>) against capture_charts.py's (<c>CHARTCAP</c>),
    /// matched by their vertex bytes since the capture is in job order.
    /// </summary>
    [Fact]
    public void PreparedMeshesAreValves()
    {
        if (Environment.GetEnvironmentVariable("CHARTCAP") is not { } capture || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap
            || OurInputs(vmap) is not { } inputs)
            return;
        var calls = new Dictionary<string, System.Text.Json.JsonElement>();
        var enters = new List<System.Text.Json.JsonElement>();
        foreach (var line in File.ReadLines(capture))
        {
            var e = System.Text.Json.JsonDocument.Parse(line).RootElement.Clone();
            var stage = e.GetProperty("stage").GetString();
            if (stage == "call")
                calls[e.GetProperty("mesh").GetString()!] = e;
            else if (stage == "enter")
                enters.Add(e);
        }
        var volumes = inputs.Volumes;
        output.WriteLine($"importance volumes {volumes.Count}: {string.Join("; ", volumes.Select(v => $"{v.Length} points"))}");
        var charted = ChartMeshes.Sort(inputs.Entries.Select(ChartMeshes.Strip).Where(ChartMeshes.IsBaked));
        var options = new ChartMeshPrep.Options();
        var misses = new List<string>();
        foreach (var (e, i) in charted.Select((e, i) => (e, i)))
        {
            var aspect = e.TextureWidth > 0 && e.TextureHeight > 0 ? (float)e.TextureWidth / e.TextureHeight : 1f;
            var ours = ChartMeshPrep.Prepare(e, options, false, inputs.RenderBackfaces.GetValueOrDefault(e.Material), volumes,
                                             [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0], aspect, e.MeshScale * e.EntryScale).Input;
            var match = enters.FirstOrDefault(x => Convert.FromHexString(x.GetProperty("vertices").GetString()!).AsSpan().SequenceEqual(ours.Vertices));
            if (match.ValueKind == System.Text.Json.JsonValueKind.Undefined)
            {
                misses.Add($"mesh {i} {Path.GetFileName(e.Material)}: no captured mesh has our {ours.Vertices.Length / ours.Stride} vertices");
                continue;
            }
            var call = calls[match.GetProperty("mesh").GetString()!];
            var head = Convert.FromHexString(match.GetProperty("head").GetString()!);
            var problems = new List<string>();
            void Check(string what, bool same) { if (!same) problems.Add(what); }
            Check("stride", ours.Stride == BitConverter.ToInt32(head, 0x20));
            Check("offsets", ours.PositionOffset == BitConverter.ToInt32(head, 0x58) && ours.TexcoordOffset == BitConverter.ToInt32(head, 0x5c));
            Check("indices", Ints(match, "indices").SequenceEqual(ours.Indices));
            Check("corners", Ints(match, "corners").SequenceEqual(ours.CornerNeighbours));
            Check("source vertex", Ints(match, "list").SequenceEqual(ours.SourceVertex));
            Check("scales", Ints(call, "scales").SequenceEqual(ours.TriangleScales.Select(BitConverter.SingleToInt32Bits)));
            Check("aspect", call.GetProperty("xmm1").GetUInt32() == BitConverter.SingleToUInt32Bits(ours.Aspect));
            Check("scale", call.GetProperty("xmm2").GetUInt32() == BitConverter.SingleToUInt32Bits(ours.Scale));
            if (problems.Count > 0)
                misses.Add($"mesh {i} {Path.GetFileName(e.Material)}: {string.Join(", ", problems)}");
        }
        output.WriteLine($"{charted.Length - misses.Count} of {charted.Length} chart meshes as Valve's");
        foreach (var m in misses.Take(20))
            output.WriteLine(m);
        Assert.Empty(misses);
    }

    /// <summary>
    /// The whole input side from the .vmap: chosen, sorted, prepared, charted,
    /// assembled and packed, against Valve's lightmap_packing_geometry.dat
    /// (<c>LMGEOM=&lt;dat&gt;</c>), the settings from <see cref="LightmapSettings"/>.
    /// </summary>
    [Fact]
    public void PackingGeometryIsValves()
    {
        if (Environment.GetEnvironmentVariable("LMGEOM") is not { } dat
            || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap || OurInputs(vmap) is not { } inputs)
            return;
        // The world's keys (none of the lightmap ones on probe01), CS2's MinSpecLightmapSize
        // and its LightmapChannels (no directional_irradiance_sh2_dc).
        var keys = MapSource.Read(vmap).OfType("CMapWorld").FirstOrDefault()?.Get<DmxBinary.Element>("entity_properties")?.Attributes
            .Where(a => a.Value is string).ToDictionary(a => a.Key, a => (string)a.Value!) ?? [];
        var settings = LightmapSettings.From(keys, 0x1000, 4096, sh2Dc: false);
        output.WriteLine($"settings {settings}");
        var luxel = new List<float>();
        var (ours, packed) = ChartMeshes.Build(inputs.Entries, inputs.Volumes, m => inputs.RenderBackfaces.GetValueOrDefault(m), settings, 120f, luxel);
        // With the packer capture (PACKCAP), the luxel uvs the packer starts from against Valve's.
        if (Environment.GetEnvironmentVariable("PACKCAP") is { } packcap)
        {
            var enter = File.ReadLines(packcap).Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement.Clone()).First(l => l.GetProperty("stage").GetString() == "pack_enter");
            var (_, valveUvs) = LightmapPackerTests.Geometry(enter);
            var wrong = Enumerable.Range(0, Math.Min(luxel.Count, valveUvs.Length)).Where(i => BitConverter.SingleToInt32Bits(luxel[i]) != BitConverter.SingleToInt32Bits(valveUvs[i])).ToList();
            output.WriteLine($"luxel uvs: {wrong.Count} of {luxel.Count} floats differ");
            foreach (var i in wrong.Take(6))
                output.WriteLine($"  float {i} (vertex {i / 2}): ours {luxel[i]:R} valve {valveUvs[i]:R}");
        }
        var valve = PackingGeometry.Read(File.ReadAllBytes(dat));
        var misses = new List<string>();
        void Check(string what, bool same) { if (!same) misses.Add(what); }
        Check($"vertex count {ours.VertexCount} / {valve.VertexCount}", ours.VertexCount == valve.VertexCount);
        Check("vertices", ours.Vertices.AsSpan().SequenceEqual(valve.Vertices));
        Check("indices", ours.Indices.SequenceEqual(valve.Indices));
        Check($"charts {ours.Charts.Length} / {valve.Charts.Length}", ours.Charts.Length == valve.Charts.Length);
        Check("chart records", ours.Charts.SequenceEqual(valve.Charts));
        Check("triangle charts", ours.TriangleCharts.SequenceEqual(valve.TriangleCharts));
        Check("atlas uvs", ours.AtlasUv.Select(BitConverter.SingleToInt32Bits).SequenceEqual(valve.AtlasUv.Select(BitConverter.SingleToInt32Bits)));
        Check("file bytes", ours.Write().AsSpan().SequenceEqual(File.ReadAllBytes(dat)));
        output.WriteLine($"packed {packed}; {ours.VertexCount} vertices, {ours.Charts.Length} charts");
        if (!ours.Charts.SequenceEqual(valve.Charts))
            foreach (var i in Enumerable.Range(0, Math.Min(ours.Charts.Length, valve.Charts.Length)).Where(i => ours.Charts[i] != valve.Charts[i]).Take(5))
                output.WriteLine($"  chart {i}: ours {ours.Charts[i]}, valve {valve.Charts[i]}");
        if (!ours.Vertices.AsSpan().SequenceEqual(valve.Vertices))
        {
            var at = Enumerable.Range(0, Math.Min(ours.Vertices.Length, valve.Vertices.Length)).First(i => ours.Vertices[i] != valve.Vertices[i]);
            output.WriteLine($"  first vertex byte differing {at} (vertex {at / 0x28}, +0x{at % 0x28:x})");
        }
        foreach (var m in misses)
            output.WriteLine("  " + m);
        Assert.Empty(misses);
    }

    /// <summary>
    /// 18020e6a0 / 18020efc0 on each captured chart mesh (<c>CHARTCAP</c>): the
    /// corner neighbours from its own vertices and indices, with the normal
    /// split and without. Meshes whose triangles were flipped differ in their
    /// indices (the adjacency reads the mesh's own winding), so they show up
    /// in neither count.
    /// </summary>
    [Fact]
    public void CornersOfCapturedMeshes()
    {
        if (Environment.GetEnvironmentVariable("CHARTCAP") is not { } capture)
            return;
        int total = 0, split = 0, joined = 0;
        foreach (var line in File.ReadLines(capture))
        {
            var e = System.Text.Json.JsonDocument.Parse(line).RootElement;
            if (e.GetProperty("stage").GetString() != "enter")
                continue;
            var head = Convert.FromHexString(e.GetProperty("head").GetString()!);
            var stride = BitConverter.ToInt32(head, 0x20) / 4;
            var b = Convert.FromHexString(e.GetProperty("vertices").GetString()!);
            var v = Enumerable.Range(0, b.Length / 4).Select(i => BitConverter.ToSingle(b, i * 4)).ToArray();
            int Off(int o) => BitConverter.ToInt32(head, o) / 4;
            var streams = new List<Physics.MeshWeld.Stream> { new("position", Off(0x58), 3, false, 0), new("texcoord", Off(0x5c), 2, false, 0), new("normal", Off(0x60), 3, false, 0) };
            var indices = Ints(e, "indices");
            var corners = Ints(e, "corners");
            total++;
            if (ChartMeshPrep.CornerNeighbours(ChartMeshPrep.VertexIds(v, stride, streams, indices, 5 | 2)).SequenceEqual(corners))
                split++;
            if (ChartMeshPrep.CornerNeighbours(ChartMeshPrep.VertexIds(v, stride, streams, indices, 5)).SequenceEqual(corners))
                joined++;
        }
        output.WriteLine($"{total} meshes: {split} match with the normal split, {joined} without");
        Assert.Equal(total, split);
    }

    private static int[] Ints(System.Text.Json.JsonElement e, string name)
    {
        var b = Convert.FromHexString(e.GetProperty(name).GetString() ?? "");
        return [.. Enumerable.Range(0, b.Length / 4).Select(i => BitConverter.ToInt32(b, i * 4))];
    }
}
