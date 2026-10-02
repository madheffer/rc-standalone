using System.Numerics;
using Source2.Compiler.Maps;
using Source2.Compiler.Meshopt;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>PROPAGG=&lt;compiled vpk&gt;</c> with <c>NODEENTRIES_VMAP</c>):
/// each <c>agg_prop</c> world node draw (model space) beside a prop model
/// draw with the same material (or a skin's, or the materialoverride; with
/// <c>PROPAGG_ANYMATERIAL=1</c> any, for smart prop material variables) and
/// triangle set, that draw through the steps of a world draw: meshsystem's
/// unpack with TEXCOORD1, CMesh_Weld at 1e-7, first-use renumbering, vertex
/// cache, overdraw, meshlets. Every one of atixref's 446 is exact.
/// <c>PROPAGG_WELD=0</c> skips the weld; <c>PROPAGG_KIND</c> picks another
/// file kind.
/// </summary>
public class PropAggregateOrderProbe(ITestOutputHelper output)
{
    static string Key(Vector3 a, Vector3 b, Vector3 c)
        => string.Join("|", new[] { a, b, c }.Select(v => $"{BitConverter.SingleToInt32Bits(v.X):x8}{BitConverter.SingleToInt32Bits(v.Y):x8}{BitConverter.SingleToInt32Bits(v.Z):x8}").Order());

    [Fact]
    public void AggregatePropDraws()
    {
        if (Environment.GetEnvironmentVariable("PROPAGG") is not { } vpk || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap
            || CS2Fixtures.StockPak() is not { } pak)
            return;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vmap)))!;
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        var (_, entities) = MapMeshes.ReadWithEntities(DmxBinary.ReadFile(vmap));
        var models = entities.Select(e => e.Element.Get<DmxBinary.Element>("entity_properties")).Where(p => !string.IsNullOrEmpty(p?.Get<string>("model")))
            .Select(p => p!.Get<string>("model")!)
            .Concat(System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(vmap, System.Text.Encoding.Latin1), @"models/[a-z0-9_/]+\.vmdl").Select(m => m.Value)).Distinct().ToList();
        var frames = new Dictionary<string, List<float[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entities)
            if (e.Element.Get<DmxBinary.Element>("entity_properties") is { } k && k.Get<string>("classname") == "prop_static")
            {
                var prop = Physics.WorldCollision.PropOf(e);
                var m = PropTransform.Matrix(prop.Origin, prop.Angles, e.Element.GetValue<Vector3>("scales") ?? Vector3.One);
                if (!frames.TryGetValue(k.Get<string>("model")!, out var fl))
                    frames[k.Get<string>("model")!] = fl = [];
                fl.Add(m);
            }
        var frameHits = new SortedDictionary<string, int>();
        var source = new Dictionary<string, List<(Vector3[] Positions, int[] Indices, string Model)>>();
        var welded = Environment.GetEnvironmentVariable("PROPAGG_WELD") != "0";
        foreach (var name in models)
        {
            if (content.LoadedModel(name + "_c") is not { } model)
                continue;
            foreach (var (mesh, _, _, _) in model.GetEmbeddedMeshesAndLoD())
                foreach (var so in mesh.Data.GetArray("m_sceneObjects"))
                    foreach (var call in so.GetArray("m_drawCalls"))
                    {
                        var vb = mesh.VBIB.VertexBuffers[call.GetArray("m_vertexBuffers")[0].GetInt32Property("m_hBuffer")];
                        var ib = mesh.VBIB.IndexBuffers[call.GetSubCollection("m_indexBuffer").GetInt32Property("m_hBuffer")];
                        var pos = ValveResourceFormat.Blocks.VBIB.GetVector3AttributeArray(vb, vb.InputLayoutFields.First(f => f.SemanticName == "POSITION"));
                        int I(int i) => ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, i * 2) : BitConverter.ToInt32(ib.Data, i * 4);
                        var start = call.GetInt32Property("m_nStartIndex");
                        var count = call.GetInt32Property("m_nIndexCount");
                        var bas = call.GetInt32Property("m_nBaseVertex");
                        var idx = Enumerable.Range(start, count).Select(i => I(i) + bas).ToArray();
                        if (welded)
                        {
                            var lo = idx.Min();
                            var (verts, color) = NodePropEntries.Vertices(vb, lo, idx.Max() - lo + 1);
                            var (wv, wi) = Physics.MeshWeld.Weld(verts, NodePropEntries.Stride, [.. idx.Select(i => i - lo)], NodePropEntries.Streams(color), 1e-7f, true);
                            pos = [.. Enumerable.Range(0, wv.Length / NodePropEntries.Stride).Select(v => new Vector3(wv[v * NodePropEntries.Stride], wv[(v * NodePropEntries.Stride) + 1], wv[(v * NodePropEntries.Stride) + 2]))];
                            idx = wi;
                        }
                        var first = call.GetStringProperty("m_material") ?? "";
                        var groups = model.Data.GetArray("m_materialGroups") ?? [];
                        var keys = new HashSet<string> { first.ToLowerInvariant() };
                        if (groups.Count > 0 && Array.FindIndex(groups[0].GetArray<string>("m_materials"), m => m.Equals(first, StringComparison.OrdinalIgnoreCase)) is var slot and >= 0)
                            foreach (var g in groups)
                                if (g.GetArray<string>("m_materials") is { } ms && slot < ms.Length)
                                    keys.Add(ms[slot].ToLowerInvariant());
                        foreach (var e in entities)
                            if (e.Element.Get<DmxBinary.Element>("entity_properties") is { } ko && ko.Get<string>("model") == name && ko.Get<string>("materialoverride") is { Length: > 0 } ov)
                                keys.Add(ov.ToLowerInvariant());
                        foreach (var key in keys)
                        {
                            if (!source.TryGetValue(key, out var list))
                                source[key] = list = [];
                            list.Add((pos, idx, name));
                        }
                    }
        }
        using var package = new ValvePak.Package();
        package.Read(vpk);
        var tally = new SortedDictionary<string, int>();
        var shown = 0;
        foreach (var entry in package.Entries!.GetValueOrDefault("vmdl_c") ?? [])
        {
            if (!entry.DirectoryName.Contains("worldnodes", StringComparison.OrdinalIgnoreCase) || !entry.FileName.Contains(Environment.GetEnvironmentVariable("PROPAGG_KIND") ?? "agg_prop", StringComparison.Ordinal))
                continue;
            package.ReadEntry(entry, out var bytes);
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            foreach (var (mesh, _, _, _) in ((Model)resource.DataBlock!).GetEmbeddedMeshesAndLoD())
                foreach (var so in mesh.Data.GetArray("m_sceneObjects"))
                    foreach (var dc in so.GetArray("m_drawCalls"))
                    {
                        var vb = mesh.VBIB.VertexBuffers[dc.GetArray("m_vertexBuffers")[0].GetInt32Property("m_hBuffer")];
                        var ib = mesh.VBIB.IndexBuffers[dc.GetSubCollection("m_indexBuffer").GetInt32Property("m_hBuffer")];
                        int I(int i) => ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, i * 2) : BitConverter.ToInt32(ib.Data, i * 4);
                        var bas = dc.GetInt32Property("m_nBaseVertex");
                        Vector3 V(int i)
                        {
                            var at = (int)((i + bas) * vb.ElementSizeInBytes);
                            return new Vector3(BitConverter.ToSingle(vb.Data, at), BitConverter.ToSingle(vb.Data, at + 4), BitConverter.ToSingle(vb.Data, at + 8));
                        }
                        var start = dc.GetInt32Property("m_nStartIndex");
                        var count = dc.GetInt32Property("m_nIndexCount");
                        var shipped = Enumerable.Range(0, count / 3).Select(t => Key(V(I(start + (t * 3))), V(I(start + (t * 3) + 1)), V(I(start + (t * 3) + 2)))).ToList();
                        var material = (dc.GetStringProperty("m_material") ?? "").ToLowerInvariant();
                        var real = shipped.Where(k => k.Split('|').Distinct().Count() > 1).Order().ToList();
                        var pool = source.GetValueOrDefault(material, []);
                        if (Environment.GetEnvironmentVariable("PROPAGG_ANYMATERIAL") == "1")
                            pool = [.. source.Values.SelectMany(x => x).Distinct()];
                        var candidates = pool
                            .Where(c => Enumerable.Range(0, c.Indices.Length / 3).Select(t => Key(c.Positions[c.Indices[t * 3]], c.Positions[c.Indices[(t * 3) + 1]], c.Positions[c.Indices[(t * 3) + 2]])).Order().SequenceEqual(real))
                            .ToList();
                        if (candidates.Count == 0)
                        {
                            tally["no prop draw with the same triangles"] = tally.GetValueOrDefault("no prop draw with the same triangles") + 1;
                            if (shown++ < 400)
                                output.WriteLine($"{entry.FileName} {Path.GetFileName(material)}: {real.Count} triangles; prop draws with this material have {string.Join(",", source.GetValueOrDefault(material, []).Select(c => c.Indices.Length / 3))}");
                            continue;
                        }
                        var exact = false;
                        var shippedVertices = Enumerable.Range(start, count).Select(I).Distinct().Count();
                        var hitFrame = "";
                        foreach (var src in candidates)
                        foreach (var (label, fm) in new[] { ("identity", (float[]?)null) }.Concat(frames.GetValueOrDefault(src.Model, []).SelectMany((f, i) => new[] { ($"instance {i}", (float[]?)f), ($"rotation {i}", (float[]?)[f[0], f[1], f[2], 0, f[4], f[5], f[6], 0, f[8], f[9], f[10], 0]) })))
                        foreach (var flip in new[] { false, true })
                        foreach (var w in new[] { 0.15f, 0f })
                        {
                        if (exact)
                            break;
                        var input = flip ? src.Indices.Select((v, i) => src.Indices[i - (i % 3) + (i % 3 == 0 ? 0 : 3 - (i % 3))]).ToArray() : src.Indices;
                        var (rn, rm) = MeshoptOptimizers.RenumberByFirstUse(input);
                        Vector3 T(Vector3 p) => fm == null ? p : new(fm[0] * p.X + fm[1] * p.Y + fm[2] * p.Z + fm[3], fm[4] * p.X + fm[5] * p.Y + fm[6] * p.Z + fm[7], fm[8] * p.X + fm[9] * p.Y + fm[10] * p.Z + fm[11]);
                        var pos = rm.SelectMany(v => { var q = T(src.Positions[v]); return new[] { q.X, q.Y, q.Z }; }).ToList();
                        var od = MeshoptOptimizers.OptimizeOverdraw(MeshoptOptimizers.OptimizeVertexCache(rn, rm.Length), pos, rm.Length, 3, 1.03f);
                        Vector3 P(int v) => src.Positions[rm[v]];
                            var built = MeshoptMeshlets.Build(od, pos, rm.Length, 3, 255, 48, 48, w);
                            var outIdx = new List<int>();
                            foreach (var ml in built.Meshlets)
                            {
                                MeshoptMeshlets.OptimizeLevel(built.Vertices, ml.VertexOffset, ml.VertexCount, built.Triangles, ml.TriangleOffset, ml.TriangleCount, 4);
                                for (var k = 0; k < ml.TriangleCount * 3; k++)
                                    outIdx.Add(built.Vertices[ml.VertexOffset + built.Triangles[ml.TriangleOffset + k]]);
                                if ((ml.TriangleCount & 1) != 0)
                                    outIdx.AddRange([outIdx[^1], outIdx[^1], outIdx[^1]]);
                            }
                            var inPlace = Enumerable.Range(0, outIdx.Count / 3).Count(t => t < shipped.Count && Key(P(outIdx[t * 3]), P(outIdx[(t * 3) + 1]), P(outIdx[(t * 3) + 2])) == shipped[t]);
                            exact |= inPlace == shipped.Count && outIdx.Count / 3 == shipped.Count;
                            if (exact)
                                hitFrame = label.Split(' ')[0] + $" w {w}" + (flip ? " flipped" : "");
                            if (inPlace != shipped.Count && shown++ < 400 && label == "identity")
                                output.WriteLine($"{entry.FileName} {Path.GetFileName(material)} w {w}: {inPlace}/{shipped.Count} ({outIdx.Count / 3} ours), vertices {shippedVertices} shipped {rm.Length} ours {rm.Select(v => src.Positions[v]).Distinct().Count()} distinct positions, {candidates.Count} candidates, from {src.Model}");
                        }
                        tally[exact ? "exact" : "differs"] = tally.GetValueOrDefault(exact ? "exact" : "differs") + 1;
                        if (exact)
                            frameHits[hitFrame] = frameHits.GetValueOrDefault(hitFrame) + 1;
                    }
        }
        output.WriteLine($"FRAMES {string.Join(", ", frameHits.Select(kv => $"{kv.Key} {kv.Value}"))}");
        output.WriteLine($"TALLY {string.Join(", ", tally.Select(kv => $"{kv.Key} {kv.Value}"))}");
    }
}
