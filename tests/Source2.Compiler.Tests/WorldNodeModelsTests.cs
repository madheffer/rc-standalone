using Source2.Compiler.Maps;
using ValvePak;
using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="WorldNodeModels"/> from our own compile of a .vmap against a
/// -world -vis package's node models: <c>WNMODELS=&lt;vmap&gt;|&lt;compiled vpk&gt;</c>.
/// Per model: the four trees (field diffs), the buffer blocks (bytes) and
/// the container facts.
/// </summary>
public class WorldNodeModelsTests(ITestOutputHelper output)
{
    [Fact]
    public void AgainstPackage()
    {
        if (Environment.GetEnvironmentVariable("WNMODELS") is not { Length: > 0 } spec || spec.Split('|') is not [var vmap, var vpk])
            return;
        using var compiled = WorldNodeCompileTests.Compile(vmap, output.WriteLine);
        if (compiled == null)
            return;
        var map = Path.GetFileNameWithoutExtension(vmap);
        foreach (var u in compiled.Result.Unported)
            output.WriteLine($"unported: {u}");
        var signatures = new Dictionary<string, WorldNodeModels.MaterialFacts>(StringComparer.OrdinalIgnoreCase);
        WorldNodeModels.MaterialFacts Material(string path)
        {
            if (!signatures.TryGetValue(path, out var f))
                signatures[path] = f = compiled.Content.Read(path + "_c") is { } bytes
                    ? new WorldNodeModels.MaterialFacts(MaterialAuthor.ExtractInputSignature(bytes), MaterialAuthor.ExtractDepthSignature(bytes))
                    : new WorldNodeModels.MaterialFacts([], []);
            return f;
        }
        var ours = compiled.Result.Objects.Select(o => (o.Name, o.Meshes, Aggregate: false))
            .Concat(compiled.Result.Aggregates.Select(a => (a.Name, a.Meshes, Aggregate: true)))
            .ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

        using var package = new Package();
        package.Read(vpk);
        int count = 0, treesExact = 0, buffersExact = 0, factsExact = 0, barLighting = 0;
        var tally = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var builtByName = new Dictionary<string, WorldNodeModels.Built>(StringComparer.OrdinalIgnoreCase);
        var examples = new Dictionary<string, string>();
        foreach (var entry in package.Entries!.GetValueOrDefault("vmdl_c") ?? [])
        {
            if (!entry.DirectoryName.EndsWith("worldnodes", StringComparison.OrdinalIgnoreCase))
                continue;
            var name = Path.GetFileNameWithoutExtension(entry.FileName);
            count++;
            if (!ours.TryGetValue(name, out var o))
            {
                output.WriteLine($"  {name}: not ours");
                continue;
            }
            package.ReadEntry(entry, out var valve);
            WorldNodeModels.Built built;
            try
            {
                built = WorldNodeModels.Build($"maps/{map}/worldnodes/{name}.vmdl", o.Meshes, o.Aggregate, Material);
            }
            catch (Exception ex)
            {
                output.WriteLine($"  {name}: {ex.GetType().Name} {ex.Message}");
                continue;
            }
            builtByName[name] = built;
            var trees = WorldPhysicsAuthorTests.Trees(valve);
            var mine = new Dictionary<string, KVObject>
            {
                ["MDAT"] = WorldNodeModelTrees.Mdat(built.Model),
                ["CTRL"] = WorldNodeModelTrees.Ctrl(built.Model),
                ["RED2"] = WorldNodeModelTrees.Red2(built.Model),
                ["DATA"] = WorldNodeModelTrees.Data(built.Model),
            };
            var lines = new List<string>();
            foreach (var (block, tree) in mine)
                lines.AddRange(KvTreeDiff.Diff(trees[block], tree).Select(l => block + l));
            if (lines.Count == 0)
                treesExact++;
            foreach (var l in lines)
            {
                var key = System.Text.RegularExpressions.Regex.Replace(l.Split(':')[0], @"\[\d+\]", "[]");
                tally[key] = tally.GetValueOrDefault(key) + 1;
                examples.TryAdd(key, $"{name}: {l}");
            }
            var valveBuffers = WorldNodeModelAuthorTests.BufferBlocks(valve);
            var sameBuffers = valveBuffers.Count == built.Buffers.Count && valveBuffers.Zip(built.Buffers).All(p => p.First.Type == p.Second.Type && p.First.Bytes.AsSpan().SequenceEqual(p.Second.Bytes));
            if (sameBuffers)
                buffersExact++;
            else
            {
                var detail = string.Join(" ", valveBuffers.Zip(built.Buffers).Select((p, i) => $"{p.First.Type}{i} {p.First.Bytes.Length}/{p.Second.Bytes.Length}{(p.First.Bytes.AsSpan().SequenceEqual(p.Second.Bytes) ? " same" : "")}"));
                tally["buffers"] = tally.GetValueOrDefault("buffers") + 1;
                // The first decoded difference per vertex buffer: vertex, byte, layout field.
                using var vr = new ValveResourceFormat.Resource();
                vr.Read(new MemoryStream(valve));
                var vbib = ((ValveResourceFormat.ResourceTypes.Model)vr.DataBlock!).GetEmbeddedMeshesAndLoD().First().Mesh.VBIB;
                // Exact but for color 1, the previous bake's per-vertex lighting (an input).
                var lightingOnly = vbib.VertexBuffers.Count == built.RawVertexBuffers.Count && valveBuffers[^1].Bytes.AsSpan().SequenceEqual(built.Buffers[^1].Bytes);
                for (var b = 0; b < Math.Min(vbib.VertexBuffers.Count, built.RawVertexBuffers.Count); b++)
                {
                    var vb = vbib.VertexBuffers[b];
                    var light = vb.InputLayoutFields.Where(f => f.SemanticName.Equals("color", StringComparison.OrdinalIgnoreCase) && f.SemanticIndex == 1).ToList();
                    var stride = (int)vb.ElementSizeInBytes;
                    lightingOnly &= vb.Data.Length == built.RawVertexBuffers[b].Length && Enumerable.Range(0, vb.Data.Length)
                        .All(i => vb.Data[i] == built.RawVertexBuffers[b][i] || light.Any(f => i % stride >= f.Offset && i % stride < f.Offset + 8));
                }
                if (lightingOnly)
                    barLighting++;
                for (var b = 0; b < Math.Min(vbib.VertexBuffers.Count, built.RawVertexBuffers.Count); b++)
                {
                    var (vd, od, st) = (vbib.VertexBuffers[b].Data, built.RawVertexBuffers[b], (int)vbib.VertexBuffers[b].ElementSizeInBytes);
                    var at = Enumerable.Range(0, Math.Min(vd.Length, od.Length)).FirstOrDefault(i => vd[i] != od[i], -1);
                    var diffs = Enumerable.Range(0, Math.Min(vd.Length, od.Length)).Where(i => vd[i] != od[i]).GroupBy(i => vbib.VertexBuffers[b].InputLayoutFields.Last(f => f.Offset <= i % st).SemanticName + vbib.VertexBuffers[b].InputLayoutFields.Last(f => f.Offset <= i % st).SemanticIndex).Select(g => $"{g.Key}:{g.Count()}");
                    if (at >= 0)
                        output.WriteLine($"    vb{b} len {vd.Length}/{od.Length}, first diff vertex {at / st} byte {at % st}: valve {Convert.ToHexString(vd, at / st * st, st)} ours {Convert.ToHexString(od, at / st * st, st)}; by field {string.Join(" ", diffs)}");
                }
                output.WriteLine($"  {name}: buffers {valveBuffers.Count}/{built.Buffers.Count}: {detail}");
            }
            var file = WorldNodeModelAuthor.Container(built.Buffers, mine["MDAT"], mine["CTRL"], built.References, mine["RED2"], mine["DATA"]);
            static string[] Facts(byte[] b) => WorldPhysicsAuthorTests.Facts(b).Split(' ')
                .Select(x => x.StartsWith("RERL", StringComparison.Ordinal) ? "RERL" : x).ToArray();
            if (Facts(file).SequenceEqual(Facts(valve)) && WorldNodeModelAuthorTests.References(valve).SequenceEqual(built.References))
                factsExact++;
            else
                output.WriteLine($"  {name}: facts {string.Join(" ", Facts(valve))} refs {string.Join(",", WorldNodeModelAuthorTests.References(valve))} vs {string.Join(" ", Facts(file))} refs {string.Join(",", built.References)}");
            if (lines.Count > 0)
                output.WriteLine($"  {name}: {lines.Count} tree lines, first {lines[0]}");
        }
        // The node's scene file.
        string Model(string n) => $"maps/{map}/worldnodes/{n}.vmdl";
        static float Boost(WrbMeshEntry f) => f.Field1a3 != 0 && (f.ObjectFlags & 0x100000) != 0 ? f.Field_b8 : 1f;
        var sceneObjects = compiled.Result.Objects.Select(o => new WorldNodeSceneTrees.SceneObject(Model(o.Name), o.Flags, o.OverlayOrder,
            o.Meshes[0].Facts.Cubemap, o.Meshes[0].Facts.LightProbe, Boost(o.Meshes[0].Facts))).ToList();
        var aggregateObjects = compiled.Result.Aggregates.Where(a => builtByName.ContainsKey(a.Name)).Select(a => new WorldNodeSceneTrees.Aggregate(Model(a.Name), a.Layer,
            builtByName[a.Name].DrawSources.Select((e, i) => new WorldNodeSceneTrees.AggregateMesh(i, e.Facts.ObjectFlags, e.Facts.LightProbe,
                new System.Numerics.Vector3(e.Facts.Field28.X, e.Facts.Field28.Y, e.Facts.Field28.Z) * 255f)).ToList())).ToList();
        var scene = WorldNodeSceneTrees.Data(sceneObjects, aggregateObjects);
        var vwnod = package.Entries!["vwnod_c"].First();
        package.ReadEntry(vwnod, out var vwnodBytes);
        var sceneLines = KvTreeDiff.Diff(WorldPhysicsAuthorTests.Trees(vwnodBytes)["DATA"], scene).ToList();
        output.WriteLine($"vwnod: {sceneLines.Count} lines");
        foreach (var l in sceneLines.Take(30))
            output.WriteLine("  " + l);
        // world.vwrld with Valve's timestamp and fingerprint; node bounds from every mesh.
        var vwrldEntry = package.Entries!["vwrld_c"].First();
        package.ReadEntry(vwrldEntry, out var vwrldBytes);
        var vwrldValve = WorldPhysicsAuthorTests.Trees(vwrldBytes)["DATA"];
        var (lo, hi) = (compiled.NodeMin, compiled.NodeMax);
        var builder = vwrldValve["m_builderParams"]!;
        var world = WorldTrees.Data(1024f, [new WorldTrees.Node(lo, hi, $"maps\\{map}\\worldnodes\\n0")], [$"maps/{map}/entities/default_ents.vents"],
                                    (uint)builder["m_nCompileTimestamp"]!, (ulong)builder["m_nCompileFingerprint"]!);
        var worldLines = KvTreeDiff.Diff(vwrldValve, world).ToList();
        output.WriteLine($"vwrld: {worldLines.Count} lines");
        foreach (var l in worldLines.Take(10))
            output.WriteLine("  " + l);
        var vrman = package.Entries!["vrman_c"].First(e => e.FileName == "n0");
        package.ReadEntry(vrman, out var vrmanBytes);
        using (var r = new ValveResourceFormat.Resource())
        {
            r.Read(new MemoryStream(vrmanBytes));
            var data = r.GetBlockByType(ValveResourceFormat.BlockType.DATA)!;
            var groups = ResourceManifestAuthor.ReadData(vrmanBytes.AsSpan((int)data.Offset, (int)data.Size));
            output.WriteLine($"n0.vrman: {string.Join(" / ", groups.Select(g => string.Join(", ", g)))}");
        }
        output.WriteLine($"{map}: {count} node models, trees exact {treesExact}, buffers exact {buffersExact} (+{barLighting} bar previous-bake lighting), container facts {factsExact}");
        foreach (var (k, v) in tally)
            output.WriteLine($"{v,5} {examples.GetValueOrDefault(k, k)}");
    }

    /// <summary>
    /// <c>WNPROPS=&lt;vmap&gt;|&lt;compiled vpk&gt;</c>: each shipped agg_prop model beside
    /// ours (<see cref="WorldNodeModels.BuildPropAggregate"/>), matched by the
    /// first draw's material and triangle set: the trees and buffers where our
    /// draw order is Valve's (heap-ordered elsewhere, ledger 43).
    /// </summary>
    [Fact]
    public void PropAggregateModels()
    {
        if (Environment.GetEnvironmentVariable("WNPROPS") is not { Length: > 0 } spec || spec.Split('|') is not [var vmap, var vpk]
            || CS2Fixtures.StockPak() is not { } pak)
            return;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vmap)))!;
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_pc_dir.vpk"), Path.Combine(game, "core", "shaders_pc_dir.vpk"));
        var doc = MapSource.Read(vmap);
        var world = WorldNodeBuild.WorldEntries(doc, content, shaders, NodeEntriesFromVmap.RendersAsWorld(game));
        var props = WorldNodeBuild.PropEntries(doc, content, shaders);
        var (built, _) = WorldNodeBuild.BuildNode(world, props, NodeOverlays.FromWorld(doc));
        var nodeMin = new System.Numerics.Vector2(float.MaxValue);
        foreach (var b in built)
            for (var k = 0; k + 1 < b.Vertices.Length; k += b.Source.Entry.Stride)
                nodeMin = System.Numerics.Vector2.Min(nodeMin, new System.Numerics.Vector2(b.Vertices[k], b.Vertices[k + 1]));
        var cache = new Dictionary<string, MeshEntryFlags.IAttributes>(StringComparer.OrdinalIgnoreCase);
        MeshEntryFlags.IAttributes Attributes(string material)
        {
            if (!cache.TryGetValue(material, out var a))
                cache[material] = a = content.Material(material) is { } info ? MaterialAttributes.Of(info, shaders, content.TextureSize) : MaterialAttributes.Empty;
            return a;
        }
        var ours = NodePropAggregates.FromWorld(doc, content, Attributes, nodeMin);
        var facts = new Dictionary<string, WorldNodeModels.MaterialFacts>(StringComparer.OrdinalIgnoreCase);
        WorldNodeModels.MaterialFacts Material(string path)
        {
            if (!facts.TryGetValue(path, out var f))
                facts[path] = f = content.Read(path + "_c") is { } bytes
                    ? new WorldNodeModels.MaterialFacts(MaterialAuthor.ExtractInputSignature(bytes), MaterialAuthor.ExtractDepthSignature(bytes))
                    : new WorldNodeModels.MaterialFacts([], []);
            return f;
        }
        static string Key3(System.Numerics.Vector3 a, System.Numerics.Vector3 b, System.Numerics.Vector3 c)
            => string.Join("|", new[] { a, b, c }.Select(v => $"{v.X:R},{v.Y:R},{v.Z:R}").Order(StringComparer.Ordinal));
        static List<string> Ordered(IReadOnlyList<System.Numerics.Vector3> p, IReadOnlyList<int> idx)
            => [.. Enumerable.Range(0, idx.Count / 3).Select(t => Key3(p[idx[t * 3]], p[idx[(t * 3) + 1]], p[idx[(t * 3) + 2]]))];
        var byFirst = new Dictionary<string, NodePropAggregates.Aggregate>();
        foreach (var a in ours)
        {
            var g = a.Draws[0].Geometry;
            var pos = Enumerable.Range(0, g.VertexCount).Select(v => new System.Numerics.Vector3(g.Vertices[v * NodePropEntries.Stride], g.Vertices[(v * NodePropEntries.Stride) + 1], g.Vertices[(v * NodePropEntries.Stride) + 2])).ToList();
            byFirst.TryAdd(a.Material.ToLowerInvariant() + "#" + string.Join(";", Ordered(pos, g.Indices)), a);
        }
        using var package = new Package();
        package.Read(vpk);
        int models = 0, matched = 0, treesExact = 0, buffersExact = 0;
        var tally = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var examples = new Dictionary<string, string>();
        foreach (var entry in package.Entries!.GetValueOrDefault("vmdl_c") ?? [])
        {
            if (!entry.FileName.Contains("_agg_prop_", StringComparison.Ordinal))
                continue;
            models++;
            package.ReadEntry(entry, out var valve);
            using var resource = new ValveResourceFormat.Resource();
            resource.Read(new MemoryStream(valve));
            var mesh = ((ValveResourceFormat.ResourceTypes.Model)resource.DataBlock!).GetEmbeddedMeshesAndLoD().First().Mesh;
            var dc = mesh.Data.GetArray("m_sceneObjects")[0].GetArray("m_drawCalls")[0];
            var vb = mesh.VBIB.VertexBuffers[dc.GetArray("m_vertexBuffers")[0].GetInt32Property("m_hBuffer")];
            var pos = ValveResourceFormat.Blocks.VBIB.GetVector3AttributeArray(vb, vb.InputLayoutFields.First(f => f.SemanticName == "POSITION"));
            var ib = mesh.VBIB.IndexBuffers[dc.GetSubCollection("m_indexBuffer").GetInt32Property("m_hBuffer")];
            var start = dc.GetInt32Property("m_nStartIndex");
            var off = dc.GetInt32Property("m_nAppliedIndexOffset");
            var idx = Enumerable.Range(start, dc.GetInt32Property("m_nIndexCount")).Select(i => (ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, i * 2) : BitConverter.ToInt32(ib.Data, i * 4)) - off).ToList();
            var local = pos.Skip(off).ToList();
            var key = (dc.GetStringProperty("m_material") ?? "").ToLowerInvariant() + "#" + string.Join(";", Ordered(local, idx));
            if (!byFirst.TryGetValue(key, out var agg))
                continue;
            matched++;
            if (Environment.GetEnvironmentVariable("WNPROPS_DEPTH") == "1")
                output.WriteLine($"DEPTH {agg.Material}: valve vbs {mesh.VBIB.VertexBuffers.Count}, depth elems {string.Join(",", Material(agg.Material).Depth.Select(e => e.Semantic))}");
            var name = $"maps/{Path.GetFileNameWithoutExtension(vmap)}/worldnodes/{Path.GetFileNameWithoutExtension(entry.FileName)}.vmdl";
            var mine = WorldNodeModels.BuildPropAggregate(name, agg, Material);
            var trees = WorldPhysicsAuthorTests.Trees(valve);
            var lines = new List<string>();
            foreach (var (block, tree) in new Dictionary<string, KVObject> { ["MDAT"] = WorldNodeModelTrees.Mdat(mine.Model), ["CTRL"] = WorldNodeModelTrees.Ctrl(mine.Model),
                                                                            ["RED2"] = WorldNodeModelTrees.Red2(mine.Model), ["DATA"] = WorldNodeModelTrees.Data(mine.Model) })
                lines.AddRange(KvTreeDiff.Diff(trees[block], tree).Select(l => block + l));
            if (lines.Count == 0)
                treesExact++;
            foreach (var l in lines)
            {
                var k = System.Text.RegularExpressions.Regex.Replace(l.Split(':')[0], @"\[\d+\]", "[]");
                tally[k] = tally.GetValueOrDefault(k) + 1;
                examples.TryAdd(k, $"{entry.FileName}: {l}");
            }
            var vbs = WorldNodeModelAuthorTests.BufferBlocks(valve);
            if (vbs.Count == mine.Buffers.Count && vbs.Zip(mine.Buffers).All(p => p.First.Bytes.AsSpan().SequenceEqual(p.Second.Bytes)))
                buffersExact++;
        }
        output.WriteLine($"agg_prop models: {models}, matched {matched}, trees exact {treesExact}, buffers exact {buffersExact}");
        foreach (var (k, v) in tally)
            output.WriteLine($"{v,5} {examples[k]}");
    }
}
