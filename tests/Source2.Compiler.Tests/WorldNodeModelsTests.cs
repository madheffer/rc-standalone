using Source2.Compiler.Maps;
using ValvePak;
using ValveKeyValue;
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
}
