using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="WorldNodeBuild"/> from the .vmap against a capture of the same
/// compile (<c>NODEENTRIES=&lt;capture&gt;</c>, <c>NODEENTRIES_VMAP=&lt;vmap&gt;</c>):
/// no captured byte feeds ours.
/// </summary>
public class WorldNodeBuildTests(ITestOutputHelper output)
{
    [Fact]
    public void BuildNodeOutput()
    {
        if (Environment.GetEnvironmentVariable("NODEENTRIES") is not { } path || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap
            || CS2Fixtures.StockPak() is not { } pak)
            return;
        var valveOut = NodeEntriesFromVmap.Read(path).Where(c => c.Stage == "BuildNode:out").ToList();
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vmap)))!;
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_pc_dir.vpk"), Path.Combine(game, "core", "shaders_pc_dir.vpk"));
        var doc = MapSource.Read(vmap);
        var world = WorldNodeBuild.WorldEntries(doc, content, shaders, NodeEntriesFromVmap.RendersAsWorld(game));
        var props = WorldNodeBuild.PropEntries(doc, content, shaders);
        var (built, overlays) = WorldNodeBuild.BuildNode(world, props, NodeOverlays.FromWorld(doc));
        output.WriteLine($"world {world.Count}, props {props.Count}, overlay meshes {overlays.Count}");
        // The overlay meshes against Valve's entries after the built ones: vertex counts and floats.
        var valveOverlays = valveOut.Skip(built.Count).ToList();
        int overlayExact = 0, shownOverlay = 0;
        for (var k = 0; k < Math.Min(overlays.Count, valveOverlays.Count); k++)
        {
            var (o, v) = (overlays[k].Mesh, valveOverlays[k]);
            var (wv, wi) = Physics.MeshWeld.Weld(o.Vertices, o.Stride, [.. Enumerable.Range(0, o.Vertices.Length / o.Stride)], o.Streams, 1f / 32f, true);
            // OVERLAY_RENORM=n|t|nt|lm: renormalise the welded normals / tangents (VectorNormalize, or LightMath's order).
            if (Environment.GetEnvironmentVariable("OVERLAY_RENORM") is { } renorm)
                foreach (var st in o.Streams.Where(st => (st.Name == "normal" && renorm.Contains('n')) || (st.Name == "tangent" && renorm.Contains('t'))))
                    for (var x = 0; x < wv.Length / o.Stride; x++)
                    {
                        var at = (x * o.Stride) + st.First;
                        var d = new System.Numerics.Vector3(wv[at], wv[at + 1], wv[at + 2]);
                        LightMath.Normalize(ref d);
                        (wv[at], wv[at + 1], wv[at + 2]) = (d.X, d.Y, d.Z);
                    }
            var same = o.Stride == v.Stride && wi.SequenceEqual(v.Indices) && wv.Select(BitConverter.SingleToInt32Bits).SequenceEqual(v.Vertices.Select(BitConverter.SingleToInt32Bits));
            if (same) overlayExact++;
            else if (shownOverlay++ < 8)
            {
                var at = Enumerable.Range(0, Math.Min(wv.Length, v.Vertices.Length)).FirstOrDefault(x => BitConverter.SingleToInt32Bits(wv[x]) != BitConverter.SingleToInt32Bits(v.Vertices[x]), -1);
                var stream = at < 0 ? "-" : o.Streams.FirstOrDefault(st => at % o.Stride >= st.First && at % o.Stride < st.First + st.Count).Name;
                var ownStreams = string.Join(",", o.Streams.Select(st => $"{st.Name}@{st.First}"));
                var theirStreams = string.Join(",", v.Layout.Select(st => $"{st.Name}@{st.First}"));
                output.WriteLine($"overlay {k} {Path.GetFileName(v.Material)}: indices same {wi.SequenceEqual(v.Indices)}, first float {at} ({stream}) ours {(at >= 0 ? wv[at] : 0):R} valve {(at >= 0 ? v.Vertices[at] : 0):R}; streams {ownStreams} / {theirStreams}");
            }
        }
        output.WriteLine($"overlay meshes exact as projected: {overlayExact} of {valveOverlays.Count}");
        output.WriteLine($"BuildNode out: ours {built.Count}, valve {valveOut.Count}");
        int exact = 0, shown = 0;
        for (var i = 0; i < built.Count; i++)
        {
            var (o, v) = (built[i], valveOut[i]);
            var problems = new List<string>();
            if (!o.Indices.SequenceEqual(v.Indices))
                problems.Add("indices");
            if (!o.Vertices.Select(BitConverter.SingleToInt32Bits).SequenceEqual(v.Vertices.Select(BitConverter.SingleToInt32Bits)))
            {
                // PerVertexLighting is an input from a previous bake (180f0ea90), not built here.
                var lighting = o.Source.Entry.Streams.FirstOrDefault(s => s.Name == "PerVertexLighting");
                var masked = o.Vertices.Select((x, k) => lighting.Name != null && k % o.Source.Entry.Stride >= lighting.First && k % o.Source.Entry.Stride < lighting.First + lighting.Count ? 0 : BitConverter.SingleToInt32Bits(x));
                var theirs = v.Vertices.Select((x, k) => lighting.Name != null && k % v.Stride >= lighting.First && k % v.Stride < lighting.First + lighting.Count ? 0 : BitConverter.SingleToInt32Bits(x));
                problems.Add(o.Vertices.Length == v.Vertices.Length && masked.SequenceEqual(theirs) ? "PerVertexLighting only" : "vertices");
            }
            foreach (var (offset, bytes) in o.Source.Header.Fields())
                if (!v.Raw.AsSpan(offset, bytes.Length).SequenceEqual(bytes))
                    problems.Add($"+0x{offset:x}");
            if (problems.Count == 0 || problems.SequenceEqual(["PerVertexLighting only"]))
                exact++;
            else if (shown++ < 20)
                output.WriteLine($"entry {i} {Path.GetFileName(v.Material)}: {string.Join(", ", problems)}");
        }
        output.WriteLine($"world and prop entries: {exact} of {built.Count} exact; overlay meshes {overlayExact} of {overlays.Count}");
        Assert.Equal(valveOut.Count, built.Count + overlays.Count);
        Assert.Equal(overlays.Count, overlayExact);
        // NODEENTRIES_ALLOW: entries known to differ (atixref's 10 subdivided tangents, ledger 38).
        var allowed = int.TryParse(Environment.GetEnvironmentVariable("NODEENTRIES_ALLOW"), out var a) ? a : 0;
        Assert.True(built.Count - exact <= allowed, $"{built.Count - exact} entries differ");
    }
}

/// <summary>
/// <see cref="WorldNodeBuild.EnvMaps"/> against the captured BuildNode output
/// (<c>NODEENTRIES</c>, <c>NODEENTRIES_VMAP</c>, <c>ENVMAPS=1</c>): each world
/// and prop entry's cubemap (+0xa0) and light probe (+0xa4) handshakes, from
/// the volume records our own entity export makes.
/// </summary>
public class EnvMapsTests(ITestOutputHelper output)
{
    [Fact]
    public void AgainstCapture()
    {
        if (Environment.GetEnvironmentVariable("ENVMAPS") != "1" || Environment.GetEnvironmentVariable("NODEENTRIES") is not { } path
            || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap || CS2Fixtures.StockPak() is not { } pak)
            return;
        var valveOut = NodeEntriesFromVmap.Read(path).Where(c => c.Stage == "BuildNode:out").ToList();
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vmap)))!;
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_pc_dir.vpk"), Path.Combine(game, "core", "shaders_pc_dir.vpk"));
        var doc = MapSource.Read(vmap);
        EntityLumpAuthor.Lighting? lighting = null;
        EntityLumpSet.Author(MapEntities.From(doc), MapFixtures.GameSchema(), Path.GetFileNameWithoutExtension(vmap),
                             MapEntities.FixupEntityNames(doc), doc, MapFixtures.SmartPropLocators, lighting: l => lighting = l);
        foreach (var v in lighting!.Volumes.Probes)
            output.WriteLine("probe " + EnvVolumes.Format(v));
        foreach (var v in lighting.Volumes.Cubemaps)
            output.WriteLine("cubemap " + EnvVolumes.Format(v));
        var world = WorldNodeBuild.WorldEntries(doc, content, shaders, NodeEntriesFromVmap.RendersAsWorld(game));
        var props = WorldNodeBuild.PropEntries(doc, content, shaders);
        var (built, _) = WorldNodeBuild.BuildNode(world, props, []);
        var ours = WorldNodeBuild.EnvMaps(built, lighting.Volumes);
        int same = 0, shown = 0;
        for (var i = 0; i < built.Count; i++)
        {
            var (cubemap, probe) = (BitConverter.ToInt32(valveOut[i].Raw, 0xa0), BitConverter.ToInt32(valveOut[i].Raw, 0xa4));
            if (ours[i] == (cubemap, probe))
                same++;
            else if (shown++ < 20)
                output.WriteLine($"entry {i} {Path.GetFileName(valveOut[i].Material)} id {built[i].Source.Header.NodeId}: ours {ours[i]}, valve ({cubemap}, {probe})");
        }
        output.WriteLine($"cubemap and probe: {same} of {built.Count} as Valve's");
        Assert.Equal(built.Count, same);
    }
}

/// <summary>Exploration (<c>NODEENTRIES=&lt;capture&gt;</c>, <c>NODESTAGES=1</c>): each stage's entry count, and the materials and ids of one stage's range.</summary>
public class NodeStagesProbe(ITestOutputHelper output)
{
    [Fact]
    public void Stages()
    {
        if (Environment.GetEnvironmentVariable("NODESTAGES") != "1" || Environment.GetEnvironmentVariable("NODEENTRIES") is not { } path)
            return;
        var all = NodeEntriesFromVmap.Read(path);
        foreach (var g in all.GroupBy(c => c.Stage))
            output.WriteLine($"{g.Key}: {g.Count()}");
        var range = Environment.GetEnvironmentVariable("NODESTAGES_RANGE")?.Split(',');
        if (range is { Length: 3 })
            foreach (var c in all.Where(c => c.Stage == range[0]).Skip(int.Parse(range[1])).Take(int.Parse(range[2])))
                output.WriteLine($"  {c.Index} {Path.GetFileName(c.Material)} id {BitConverter.ToInt32(c.Raw, 0x40)} attr {BitConverter.ToUInt64(c.Raw, 0x1b0):x} a0 {BitConverter.ToInt32(c.Raw, 0xa0)} a4 {BitConverter.ToInt32(c.Raw, 0xa4)} tex {BitConverter.ToInt32(c.Raw, 0x1b8)}x{BitConverter.ToInt32(c.Raw, 0x1bc)} 1a0 {Convert.ToHexString(c.Raw, 0x1a0, 6)} v{c.Vertices.Length / c.Stride} m1c8 {string.Join(" ", Enumerable.Range(0, 12).Select(k => BitConverter.ToSingle(c.Raw, 0x1c8 + k * 4).ToString("R")))}");
    }
}

/// <summary>
/// <see cref="MaterialStreams"/> on entries built from the .vmap against the
/// merger capture (<c>MESHMERGE</c>, <c>NODEENTRIES_VMAP</c>, <c>MERGESTREAMS=1</c>):
/// each merged list's entries, in order, by material and the (name, index)
/// of every stream PrepareForMerge leaves.
/// </summary>
public class MergerInputStreamsTests(ITestOutputHelper output)
{
    [Fact]
    public void AgainstCapture()
    {
        if (Environment.GetEnvironmentVariable("MERGESTREAMS") != "1" || Environment.GetEnvironmentVariable("MESHMERGE") is not { } path
            || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap || CS2Fixtures.StockPak() is not { } pak)
            return;
        var data = File.ReadAllBytes(path);
        var calls = new SortedDictionary<int, List<(string Material, string Streams)>>();
        for (var at = 0; at < data.Length;)
        {
            var n = BitConverter.ToInt32(data, at);
            var head = System.Text.Json.JsonDocument.Parse(data.AsMemory(at + 4, n)).RootElement;
            at += 4 + n;
            at += 4 + BitConverter.ToInt32(data, at);
            var ev = head.GetProperty("ev").GetString();
            if (ev == "call")
                calls.TryAdd(head.GetProperty("call").GetInt32(), []);
            if (ev != "in")
                continue;
            var call = head.GetProperty("call").GetInt32();
            if (!calls.TryGetValue(call, out var list))
                calls[call] = list = [];
            var raw = head.TryGetProperty("meshRaw", out var mr) ? Convert.FromHexString(mr.GetString()!) : new byte[0x198];
            var tint = raw[0x14f] != 0 ? $" tint {BitConverter.ToSingle(raw, 0x154):R},{BitConverter.ToSingle(raw, 0x158):R},{BitConverter.ToSingle(raw, 0x15c):R},{BitConverter.ToSingle(raw, 0x160):R}" : "";
            list.Add((head.GetProperty("material").GetString() ?? "",
                      string.Join(" ", head.GetProperty("streams").EnumerateArray().Select(x => $"{x.GetProperty("name").GetString()}/{x.GetProperty("index").GetInt32()}")) + tint));
        }
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vmap)))!;
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_pc_dir.vpk"), Path.Combine(game, "core", "shaders_pc_dir.vpk"));
        var doc = MapSource.Read(vmap);
        var world = WorldNodeBuild.WorldEntries(doc, content, shaders, NodeEntriesFromVmap.RendersAsWorld(game));
        var props = WorldNodeBuild.PropEntries(doc, content, shaders);
        var descriptors = NodeOverlays.FromWorld(doc);
        var (built, overlays) = WorldNodeBuild.BuildNode(world, props, descriptors);

        string Filtered(string material, IReadOnlyList<Physics.MeshWeld.Stream> streams)
        {
            var kept = Enumerable.Range(0, streams.Count).ToList();
            if (content.Read(material + "_c") is { } bytes)
                kept = MaterialStreams.Kept(streams, MaterialStreams.Inputs(Source2.Compiler.MaterialAuthor.ExtractInputSignature(bytes).Select(x => x.Semantic)), false);
            return string.Join(" ", kept.Select(i => $"{streams[i].Name}/{streams.Take(i).Count(s => s.Name == streams[i].Name)}"));
        }
        var lists = MeshLists.Merged.ToDictionary(k => k, _ => new List<(string, string)>());
        foreach (var b in built)
        {
            var e = b.Source.Entry;
            var objectFlags = (e.Source?.Attributes.GetValueOrDefault("renderwithdynamic") is true && e.Instances.Length == 0) ? 0x200u : 0u;
            if (MeshLists.Assign(new MeshLists.Input(b.Source.Header.Attributes, objectFlags, 0, e.Record.FadeMax, false, false)) is { } k && lists.TryGetValue(k, out var l))
                l.Add((e.Material, Filtered(e.Material, e.Streams)));
        }
        foreach (var projection in overlays)
        {
            var d = descriptors[projection.Overlay];
            var attributes = content.Material(d.Material) is { } info ? MaterialAttributes.Of(info, shaders, content.TextureSize) : MaterialAttributes.Empty;
            var flags = MeshEntryFlags.Compute(attributes, new MeshEntryFlags.Record(0, 0, 0, true, null, false)).Flags;
            // The overlay's own tintColor, when not white, as the CMesh's tint switch and colour (+0x14f, +0x154).
            var c = d.TintColor;
            static float F(byte x) => x / 255f;
            var tint = c.Any(x => x != 255) ? $" tint {F(c[0]):R},{F(c[1]):R},{F(c[2]):R},{F(c[3]):R}" : "";
            if (MeshLists.Assign(new MeshLists.Input(flags, 0, d.RenderOrder, 0, false, false)) is { } k && lists.TryGetValue(k, out var l))
                l.Add((d.Material, Filtered(d.Material, projection.Mesh.Streams) + tint));
        }
        var callList = calls.Values.ToList();
        int same = 0, total = 0, shown = 0;
        for (var i = 0; i < MeshLists.Merged.Count && i < callList.Count; i++)
        {
            var ours = lists[MeshLists.Merged[i]];
            var valve = callList[i];
            output.WriteLine($"{MeshLists.Merged[i]}: ours {ours.Count}, valve {valve.Count}");
            for (var j = 0; j < Math.Min(ours.Count, valve.Count); j++)
            {
                total++;
                if (ours[j].Item2 == valve[j].Streams)
                    same++;
                else if (shown++ < 12)
                    output.WriteLine($"   {j} {Path.GetFileName(valve[j].Material)}: ours [{ours[j].Item2}] valve [{valve[j].Streams}]");
            }
        }
        output.WriteLine($"stream layouts as Valve's: {same} of {total}");
        Assert.Equal(total, same);
    }
}
