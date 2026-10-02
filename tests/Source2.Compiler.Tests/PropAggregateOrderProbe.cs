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
        var frames = Frames(entities);
        var frameHits = new SortedDictionary<string, int>();
        var source = Sources(content, entities, vmap, Environment.GetEnvironmentVariable("PROPAGG_WELD") != "0");
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

    /// <summary>
    /// Exploration (same inputs, <c>NODEENTRIES</c> for BuildNode's bounds):
    /// each agg_prop aggregate of the .vwnod, its draws' source models and
    /// aggregate meshes beside the .vmap's props: fragment matrices against
    /// PropTransform, and each draw's instances against
    /// WRBMeshList_SortMorton's keys (GROUND_TRUTH 43).
    /// </summary>
    [Fact]
    public void Grouping()
    {
        if (Environment.GetEnvironmentVariable("PROPAGG") is not { } vpk || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap
            || CS2Fixtures.StockPak() is not { } pak)
            return;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vmap)))!;
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        var (_, entities) = MapMeshes.ReadWithEntities(DmxBinary.ReadFile(vmap));
        var source = Sources(content, entities, vmap, true);
        var all = source.Values.SelectMany(x => x).Distinct().ToList();
        var byTriangles = new Dictionary<string, List<string>>();
        foreach (var c in all)
        {
            var k = string.Join(";", Enumerable.Range(0, c.Indices.Length / 3).Select(t => Key(c.Positions[c.Indices[t * 3]], c.Positions[c.Indices[(t * 3) + 1]], c.Positions[c.Indices[(t * 3) + 2]])).Order());
            if (!byTriangles.TryGetValue(k, out var l))
                byTriangles[k] = l = [];
            if (!l.Contains(c.Model))
                l.Add(c.Model);
        }
        var props = entities.Where(e => e.Element.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname") == "prop_static").ToList();
        output.WriteLine($"{props.Count} prop_static, {props.Select(e => e.Element.Get<DmxBinary.Element>("entity_properties")!.Get<string>("model")).Distinct().Count()} models");
        // Each prop: walk index, model, our placement matrix.
        var placed = entities.Select((e, walk) => (e, walk)).Where(x => !x.e.Hidden && x.e.Element.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname") == "prop_static")
            .Select(x =>
            {
                var prop = Physics.WorldCollision.PropOf(x.e);
                return (Walk: x.walk, Model: prop.Model, M: PropTransform.Matrix(prop.Origin, prop.Angles, prop.Scales));
            }).ToList();
        // Each model's LOD 0 draw bounds (model space), for the prop's centre (Matrix3x4_TransformAABB of each, unioned).
        var boundsOf = new Dictionary<string, List<(Vector3 Lo, Vector3 Hi)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in placed.Select(q => q.Model).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var list = boundsOf[model] = [];
            if (content.LoadedModel(model + "_c") is not { } md)
                continue;
            foreach (var (mesh, _, _, lod) in md.GetEmbeddedMeshesAndLoD())
            {
                if ((lod & 1) == 0)
                    continue;
                foreach (var so in mesh.Data.GetArray("m_sceneObjects"))
                    foreach (var call in so.GetArray("m_drawCalls"))
                    {
                        var vb = mesh.VBIB.VertexBuffers[call.GetArray("m_vertexBuffers")[0].GetInt32Property("m_hBuffer")];
                        var ib = mesh.VBIB.IndexBuffers[call.GetSubCollection("m_indexBuffer").GetInt32Property("m_hBuffer")];
                        var pos = ValveResourceFormat.Blocks.VBIB.GetVector3AttributeArray(vb, vb.InputLayoutFields.First(f => f.SemanticName == "POSITION"));
                        int I(int i) => ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, i * 2) : BitConverter.ToInt32(ib.Data, i * 4);
                        var st = call.GetInt32Property("m_nStartIndex");
                        var bs = call.GetInt32Property("m_nBaseVertex");
                        var used = Enumerable.Range(st, call.GetInt32Property("m_nIndexCount")).Select(i => pos[I(i) + bs]).ToList();
                        list.Add((used.Aggregate(Vector3.Min), used.Aggregate(Vector3.Max)));
                    }
            }
        }
        Vector2 Centre(float[] m, string model)
        {
            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);
            foreach (var (blo, bhi) in boundsOf.GetValueOrDefault(model, []))
                for (var c = 0; c < 8; c++)
                {
                    var p = new Vector3((c & 1) != 0 ? bhi.X : blo.X, (c & 2) != 0 ? bhi.Y : blo.Y, (c & 4) != 0 ? bhi.Z : blo.Z);
                    var w = new Vector3(m[0] * p.X + m[1] * p.Y + m[2] * p.Z + m[3], m[4] * p.X + m[5] * p.Y + m[6] * p.Z + m[7], m[8] * p.X + m[9] * p.Y + m[10] * p.Z + m[11]);
                    lo = Vector3.Min(lo, w);
                    hi = Vector3.Max(hi, w);
                }
            return new Vector2((lo.X + hi.X) * 0.5f, (lo.Y + hi.Y) * 0.5f);
        }
        static uint Spread(uint v)
        {
            v &= 0xffff;
            v = (v << 8 ^ v) & 0xff00ff;
            v = (v << 4 ^ v) & 0xf0f0f0f;
            v = (v * 4 ^ v) & 0x33333333;
            return (v * 2 ^ v) & 0x55555555;
        }
        static uint Morton(Vector2 c, Vector2 o) => Spread((uint)((long)(c.X - o.X) >> 4)) + (Spread((uint)((long)(c.Y - o.Y) >> 4)) * 2);
        var allCentres = placed.Select(q => Centre(q.M, q.Model)).ToList();
        var minCentre = allCentres.Aggregate(Vector2.Min);
        var origins = new List<(string Name, Vector2 O)> { ("zero", Vector2.Zero), ("-16384", new Vector2(-16384)), ("min centre", minCentre) };
        if (Environment.GetEnvironmentVariable("PROPAGG_ORIGIN") is { } ot)
        {
            var parts = ot.Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            origins.Add(("given", new Vector2(parts[0], parts[1])));
        }
        if (Environment.GetEnvironmentVariable("NODEENTRIES") is { } capture)
        {
            // BuildNode's closing bounds: every output entry's vertices.
            var lo = new Vector2(float.MaxValue);
            foreach (var c in NodeEntriesFromVmap.Read(capture).Where(c => c.Stage == "BuildNode:out"))
            {
                var at = c.Layout.First(x => x.Name.Equals("position", StringComparison.OrdinalIgnoreCase)).First;
                for (var v = 0; v < c.Vertices.Length / c.Stride; v++)
                    lo = Vector2.Min(lo, new Vector2(c.Vertices[(v * c.Stride) + at], c.Vertices[(v * c.Stride) + at + 1]));
            }
            origins.Add(("node bounds", lo));
            output.WriteLine($"node bounds min {lo}");
        }
        output.WriteLine($"min centre {minCentre}");
        var mortonOk = new int[origins.Count];
        var mortonDraws = 0;
        var failShown = 0;
        int matched = 0, bitwise = 0, ambiguous = 0, drawOrderFirstWalk = 0, drawOrderOther = 0, ascending = 0, descending = 0, mixed = 0;
        using var package = new ValvePak.Package();
        package.Read(vpk);
        var limit = int.TryParse(Environment.GetEnvironmentVariable("PROPAGG_SHOW"), out var sh) ? sh : 8;
        int shown = 0, meshes = 0, transformed = 0, fragments = 0;
        foreach (var entry in package.Entries!.SelectMany(kv => kv.Value).Where(e => e.GetFullPath().EndsWith(".vwnod_c", StringComparison.Ordinal)))
        {
            package.ReadEntry(entry, out var nodeBytes);
            using var nodeResource = new Resource();
            nodeResource.Read(new MemoryStream(nodeBytes));
            var data = ((WorldNode)nodeResource.DataBlock!).Data;
            if (Environment.GetEnvironmentVariable("PROPAGG_NODEKEYS") == "1")
            {
                output.WriteLine($"NODE {entry.GetFullPath()}: {string.Join(", ", data.Keys)}");
                var so0 = data.GetArray("m_sceneObjects")[0];
                output.WriteLine($"NODE object 0: {string.Join(", ", so0.Keys.Select(x => $"{x}={so0[x]}"))}");
                var lo = new Vector3(float.MaxValue);
                foreach (var so in data.GetArray("m_sceneObjects"))
                    if (so["m_vMinBounds"] is { } mb)
                        lo = Vector3.Min(lo, new Vector3(Convert.ToSingle(mb.Values.ElementAt(0)), Convert.ToSingle(mb.Values.ElementAt(1)), Convert.ToSingle(mb.Values.ElementAt(2))));
                output.WriteLine($"NODE scene object min {lo}");
            }
            foreach (var o in data.GetArray("m_aggregateSceneObjects") ?? [])
            {
                var name = o.GetStringProperty("m_renderableModel") ?? "";
                if (!name.Contains("agg_prop", StringComparison.Ordinal))
                    continue;
                var agg = package.FindEntry(Path.ChangeExtension(name, ".vmdl_c"))!;
                package.ReadEntry(agg, out var bytes);
                using var resource = new Resource();
                resource.Read(new MemoryStream(bytes));
                var draws = new List<string>();
                var drawModels = new List<List<string>>();
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
                            var k = string.Join(";", Enumerable.Range(0, dc.GetInt32Property("m_nIndexCount") / 3).Select(t => Key(V(I(start + (t * 3))), V(I(start + (t * 3) + 1)), V(I(start + (t * 3) + 2))))
                                .Where(x => x.Split('|').Distinct().Count() > 1).Order());
                            draws.Add($"{Path.GetFileNameWithoutExtension(string.Join("+", byTriangles.GetValueOrDefault(k, ["?"])))} v{dc.GetInt32Property("m_nVertexCount")}");
                            drawModels.Add(byTriangles.GetValueOrDefault(k, []));
                        }
                var am = o.GetArray("m_aggregateMeshes") ?? [];
                var ft = o["m_fragmentTransforms"];
                var nft = ft?.ValueType == ValveKeyValue.KVValueType.BinaryBlob ? ((byte[])ft).Length / 48 : ft?.Count ?? 0;
                // Each aggregate mesh's prop: same model as its draw, same translation.
                var walks = new List<(int Draw, int Walk)>();
                var centres = new List<(int Draw, Vector2 C)>();
                for (var i = 0; i < am.Count && ft != null && ft.ValueType != ValveKeyValue.KVValueType.BinaryBlob; i++)
                {
                    var f = ft.Values.ElementAt(i).Values.SelectMany(r => r.ValueType == ValveKeyValue.KVValueType.Array ? r.Values.Select(x => (float)(double)x) : [(float)(double)r]).ToArray();
                    var d = am[i].GetInt32Property("m_nDrawCallIndex");
                    var hits = placed.Where(q => drawModels[d].Contains(q.Model, StringComparer.OrdinalIgnoreCase) && MathF.Abs(q.M[3] - f[3]) < 0.01f && MathF.Abs(q.M[7] - f[7]) < 0.01f && MathF.Abs(q.M[11] - f[11]) < 0.01f).ToList();
                    if (hits.Count == 0)
                        continue;
                    matched++;
                    ambiguous += hits.Count > 1 ? 1 : 0;
                    bitwise += hits[0].M.Zip(f).All(z => BitConverter.SingleToInt32Bits(z.First) == BitConverter.SingleToInt32Bits(z.Second)) ? 1 : 0;
                    walks.Add((d, hits[0].Walk));
                    centres.Add((d, Centre(hits[0].M, hits[0].Model)));
                }
                if (walks.Count == am.Count && am.Count > 0)
                {
                    foreach (var g in centres.GroupBy(c => c.Draw).Where(g => g.Count() > 1))
                    {
                        if (name.Contains(Environment.GetEnvironmentVariable("PROPAGG_DUMP") ?? " "))
                            output.WriteLine($"CENTRES {name} draw {g.Key}: {string.Join(" ", g.Select(c => $"({c.C.X:0.##},{c.C.Y:0.##})"))}");
                        mortonDraws++;
                        for (var oi = 0; oi < origins.Count; oi++)
                        {
                            var keys = g.Select(c => Morton(c.C, origins[oi].O)).ToList();
                            mortonOk[oi] += keys.SequenceEqual(keys.Order()) ? 1 : 0;
                            if (origins[oi].Name == "node bounds" && !keys.SequenceEqual(keys.Order()) && failShown++ < 1000)
                                output.WriteLine($"FAIL {Path.GetFileName(name)} draw {g.Key}: {string.Join(" ", g.Select(c => $"({c.C.X:0.##},{c.C.Y:0.##})={Morton(c.C, origins[oi].O):x}"))}");
                        }
                    }
                    var firstWalk = walks.GroupBy(w => w.Draw).OrderBy(g => g.Key).Select(g => g.Min(w => w.Walk)).ToList();
                    if (firstWalk.SequenceEqual(firstWalk.Order()))
                        drawOrderFirstWalk++;
                    else
                        drawOrderOther++;
                    foreach (var g in walks.GroupBy(w => w.Draw).Where(g => g.Count() > 1))
                    {
                        var seq = g.Select(w => w.Walk).ToList();
                        if (seq.SequenceEqual(seq.Order()))
                            ascending++;
                        else if (seq.SequenceEqual(seq.OrderDescending()))
                            descending++;
                        else
                            mixed++;
                    }
                    if (shown < limit)
                        output.WriteLine($"  walks: {string.Join(" ", walks.Select(w => $"{w.Draw}:{w.Walk}"))}");
                }
                meshes += am.Count;
                transformed += am.Count(m => m.GetInt32Property("m_bHasTransform") != 0);
                fragments += nft;
                if (shown++ < limit)
                {
                    output.WriteLine($"{name} layer {o.GetInt32Property("m_nLayer")}: {draws.Count} draws, {am.Count} aggregate meshes, {nft} fragment transforms, flags all {o["m_allFlags"]} any {o["m_anyFlags"]}");
                    output.WriteLine($"  draws: {string.Join(", ", draws)}");
                    output.WriteLine($"  meshes: {string.Join(" ", am.Select(m => $"{m.GetInt32Property("m_nDrawCallIndex")}{(m.GetInt32Property("m_bHasTransform") != 0 ? "T" : "")}/{m["m_objectFlags"]}/{m.GetInt32Property("m_nLODSetupIndex")}"))}");
                    if (ft != null && nft > 0)
                        output.WriteLine($"  fragment 0 ({ft.ValueType}): {(ft.ValueType == ValveKeyValue.KVValueType.BinaryBlob ? "blob" : string.Join(" ", ft.Values.First().Values.SelectMany(r => r.ValueType == ValveKeyValue.KVValueType.Array ? r.Values.Select(x => x.ToString()) : [r.ToString()])))}");
                    if (Environment.GetEnvironmentVariable("PROPAGG_KEYS") == "1" && am.Count > 0)
                        output.WriteLine($"  first mesh: {string.Join(", ", am[0].Keys.Select(x => $"{x}={am[0][x]}"))}");
                }
            }
        }
        output.WriteLine($"MATCH {matched} meshes to props ({ambiguous} ambiguous), {bitwise} matrices bitwise; draw order by first walk {drawOrderFirstWalk}, other {drawOrderOther}; within a draw ascending {ascending}, descending {descending}, mixed {mixed}");
        output.WriteLine($"MORTON {mortonDraws} draws with several instances; keys ascending: {string.Join(", ", origins.Select((o, i) => $"{o.Name} {mortonOk[i]}"))}");
        output.WriteLine($"TOTAL {shown} agg_prop aggregates, {meshes} aggregate meshes, {transformed} with a transform, {fragments} fragment transforms");
    }

    // Each prop_static's placement matrix, by model.
    static Dictionary<string, List<float[]>> Frames(List<MapMeshes.EntityNode> entities)
    {
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
        return frames;
    }

    // Every model the .vmap names, each draw (unpacked and welded) under its material, its skins' and any materialoverride.
    static Dictionary<string, List<(Vector3[] Positions, int[] Indices, string Model)>> Sources(GameContent content, List<MapMeshes.EntityNode> entities, string vmap, bool welded)
    {
        var models = entities.Select(e => e.Element.Get<DmxBinary.Element>("entity_properties")).Where(p => !string.IsNullOrEmpty(p?.Get<string>("model")))
            .Select(p => p!.Get<string>("model")!)
            .Concat(System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(vmap, System.Text.Encoding.Latin1), @"models/[a-z0-9_/]+\.vmdl").Select(m => m.Value)).Distinct().ToList();
        var source = new Dictionary<string, List<(Vector3[] Positions, int[] Indices, string Model)>>();
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
        return source;
    }
}
