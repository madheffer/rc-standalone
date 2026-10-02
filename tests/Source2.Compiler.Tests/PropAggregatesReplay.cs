using System.Numerics;
using Source2.Compiler.Maps;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="NodePropAggregates"/> from the .vmap against the compiled map
/// (<c>PROPAGG=&lt;vpk&gt;</c>, <c>NODEENTRIES_VMAP</c>, <c>NODEENTRIES</c> for
/// BuildNode's closing bounds): each shipped agg_prop aggregate beside ours
/// with the same material and draws, draws compared in triangle and vertex
/// order, fragments by matrix and in order. atixref: all 171 aggregates and
/// 446 draws exact, every fragment set; draw order 138 of 171, fragment
/// order 218 of 361 (heap order, not asserted). <c>PROPAGG_SEARCH=&lt;r&gt;</c>
/// searches the Morton origin around BuildNode's bounds (best: those).
/// </summary>
public class PropAggregatesReplay(ITestOutputHelper output)
{
    static string Key(Vector3 a, Vector3 b, Vector3 c)
        => string.Join("|", new[] { a, b, c }.Select(v => $"{BitConverter.SingleToInt32Bits(v.X):x8}{BitConverter.SingleToInt32Bits(v.Y):x8}{BitConverter.SingleToInt32Bits(v.Z):x8}").Order());

    sealed record ShippedDraw(string Material, List<string> Triangles, List<Vector3> Vertices);

    [Fact]
    public void AgainstCompiledMap()
    {
        if (Environment.GetEnvironmentVariable("PROPAGG") is not { } vpk || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap
            || Environment.GetEnvironmentVariable("NODEENTRIES") is not { } capture || CS2Fixtures.StockPak() is not { } pak)
            return;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vmap)))!;
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_pc_dir.vpk"), Path.Combine(game, "core", "shaders_pc_dir.vpk"));
        var cache = new Dictionary<string, MeshEntryFlags.IAttributes>(StringComparer.OrdinalIgnoreCase);
        MeshEntryFlags.IAttributes Attributes(string material)
        {
            if (!cache.TryGetValue(material, out var a))
                cache[material] = a = content.Material(material) is { } info ? MaterialAttributes.Of(info, shaders, content.TextureSize) : MaterialAttributes.Empty;
            return a;
        }
        var nodeMin = new Vector2(float.MaxValue);
        foreach (var c in NodeEntriesFromVmap.Read(capture).Where(c => c.Stage == "BuildNode:out"))
        {
            var at = c.Layout.First(x => x.Name.Equals("position", StringComparison.OrdinalIgnoreCase)).First;
            for (var v = 0; v < c.Vertices.Length / c.Stride; v++)
                nodeMin = Vector2.Min(nodeMin, new Vector2(c.Vertices[(v * c.Stride) + at], c.Vertices[(v * c.Stride) + at + 1]));
        }
        var notes = new List<string>();
        var document = DmxBinary.ReadFile(vmap);
        var (_, entities) = MapMeshes.ReadWithEntities(document);
        var ours = NodePropAggregates.FromWorld(document, content, Attributes, nodeMin, notes);
        output.WriteLine($"ours: {ours.Count} aggregates, {ours.Sum(a => a.Draws.Count)} draws, {ours.Sum(a => a.Fragments.Count)} fragments; {notes.Count} notes");
        // Our draws keyed by triangle set.
        string SetKey(IEnumerable<string> triangles) => string.Join(";", triangles.Where(t => t.Split('|').Distinct().Count() > 1).Order());
        Vector3 P(NodeDraw.Result g, int v) => new(g.Vertices[v * NodePropEntries.Stride], g.Vertices[(v * NodePropEntries.Stride) + 1], g.Vertices[(v * NodePropEntries.Stride) + 2]);
        List<string> Triangles(NodeDraw.Result g) => [.. Enumerable.Range(0, g.Indices.Length / 3).Select(t => Key(P(g, g.Indices[t * 3]), P(g, g.Indices[(t * 3) + 1]), P(g, g.Indices[(t * 3) + 2])))];
        var ourByDrawSet = new Dictionary<string, (NodePropAggregates.Aggregate Agg, int Draw)>();
        foreach (var a in ours)
            for (var d = 0; d < a.Draws.Count; d++)
                ourByDrawSet.TryAdd(a.Material.ToLowerInvariant() + "#" + SetKey(Triangles(a.Draws[d].Geometry)), (a, d));
        using var package = new ValvePak.Package();
        package.Read(vpk);
        var tally = new SortedDictionary<string, int>();
        void Count(string k) => tally[k] = tally.GetValueOrDefault(k) + 1;
        var shown = 0;
        var shippedCentres = new List<Vector3[]>();
        foreach (var entry in package.Entries!.SelectMany(kv => kv.Value).Where(e => e.GetFullPath().EndsWith(".vwnod_c", StringComparison.Ordinal)))
        {
            package.ReadEntry(entry, out var nodeBytes);
            using var nodeResource = new Resource();
            nodeResource.Read(new MemoryStream(nodeBytes));
            foreach (var o in ((WorldNode)nodeResource.DataBlock!).Data.GetArray("m_aggregateSceneObjects") ?? [])
            {
                var name = o.GetStringProperty("m_renderableModel") ?? "";
                if (!name.Contains("agg_prop", StringComparison.Ordinal))
                    continue;
                package.ReadEntry(package.FindEntry(Path.ChangeExtension(name, ".vmdl_c"))!, out var bytes);
                using var resource = new Resource();
                resource.Read(new MemoryStream(bytes));
                var shipped = new List<ShippedDraw>();
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
                            var idx = Enumerable.Range(start, count).Select(I).ToList();
                            var lo = idx.Min();
                            shipped.Add(new ShippedDraw(dc.GetStringProperty("m_material") ?? "",
                                [.. Enumerable.Range(0, count / 3).Select(t => Key(V(idx[t * 3]), V(idx[(t * 3) + 1]), V(idx[(t * 3) + 2])))],
                                [.. Enumerable.Range(lo, idx.Max() - lo + 1).Select(V)]));
                        }
                // Our aggregate holding the first draw; then every draw and fragment beside it.
                if (!ourByDrawSet.TryGetValue(shipped[0].Material.ToLowerInvariant() + "#" + SetKey(shipped[0].Triangles), out var hit))
                {
                    Count("aggregate without ours");
                    if (shown++ < 6)
                        output.WriteLine($"no match: {Path.GetFileName(name)} ({shipped.Count} draws, {Path.GetFileName(shipped[0].Material)})");
                    continue;
                }
                var agg = hit.Agg;
                // Each shipped draw to one of ours: the same triangle sequence first, else the same triangle set; each of ours used once.
                var ourSeqs = agg.Draws.Select(d => string.Join(";", Triangles(d.Geometry))).ToList();
                var ourSets = agg.Draws.Select(d => SetKey(Triangles(d.Geometry))).ToList();
                var used = new HashSet<int>();
                var map = shipped.Select(_ => -1).ToList();
                var am = o.GetArray("m_aggregateMeshes") ?? [];
                var ft = o["m_fragmentTransforms"];
                string Matrix(int i) => string.Join(",", ft.Values.ElementAt(i).Values.SelectMany(r => r.ValueType == ValveKeyValue.KVValueType.Array ? r.Values.Select(x => BitConverter.SingleToInt32Bits((float)(double)x)) : [BitConverter.SingleToInt32Bits((float)(double)r)]));
                var theirFragments = Enumerable.Range(0, am.Count).Select(i => (Draw: am[i].GetInt32Property("m_nDrawCallIndex"), M: Matrix(i))).ToList();
                var mine = agg.Fragments.Select(f => (f.Draw, M: string.Join(",", f.Matrix.Select(BitConverter.SingleToInt32Bits)))).ToList();
                for (var d = 0; d < shipped.Count; d++)
                {
                    // Draws with the same positions (signs differing in texcoords only): the one sharing most fragments.
                    var seq = string.Join(";", shipped[d].Triangles);
                    var want = theirFragments.Where(t => t.Draw == d).Select(t => t.M).ToHashSet();
                    var k = Enumerable.Range(0, ourSeqs.Count).Where(i => !used.Contains(i) && ourSeqs[i] == seq)
                        .OrderByDescending(i => mine.Count(m => m.Draw == i && want.Contains(m.M))).DefaultIfEmpty(-1).First();
                    if (k >= 0 && used.Add(k))
                        map[d] = k;
                }
                for (var d = 0; d < shipped.Count; d++)
                    if (map[d] < 0)
                    {
                        var set = SetKey(shipped[d].Triangles);
                        var k = Enumerable.Range(0, ourSets.Count).FirstOrDefault(i => !used.Contains(i) && ourSets[i] == set, -1);
                        if (k >= 0 && used.Add(k))
                            map[d] = k;
                    }
                Count(map.Contains(-1) || agg.Draws.Count != shipped.Count ? "aggregate draws differ" : "aggregate same draws");
                if (map.Contains(-1) || agg.Draws.Count != shipped.Count)
                {
                    output.WriteLine($"draws differ: {Path.GetFileName(name)} shipped {shipped.Count}, ours {agg.Draws.Count}, unmatched {map.Count(m => m < 0)}");
                    output.WriteLine($"   shipped: {string.Join(", ", shipped.Select(s => $"{Path.GetFileName(s.Material)} {s.Triangles.Count}t {s.Vertices.Count}v"))}");
                    output.WriteLine($"   ours: {string.Join(", ", agg.Draws.Select(d => $"{d.Model}#{d.Call} {Path.GetFileName(d.Material)} {d.Geometry.Indices.Length / 3}t {d.Geometry.VertexCount}v"))}");
                }
                Count(map.SequenceEqual(Enumerable.Range(0, map.Count)) ? "draw order same" : "draw order differs");
                for (var d = 0; d < shipped.Count; d++)
                {
                    if (map[d] < 0)
                        continue;
                    var g = agg.Draws[map[d]].Geometry;
                    Count(Triangles(g).SequenceEqual(shipped[d].Triangles) ? "draw triangles exact" : "draw triangles differ");
                    Count(Enumerable.Range(0, g.VertexCount).Select(v => P(g, v)).SequenceEqual(shipped[d].Vertices) ? "draw vertices exact" : "draw vertices differ");
                }
                // Fragments: matrices per draw, as sets and in order.
                var theirs = theirFragments.Select(t => (Draw: map[t.Draw], t.M)).ToList();
                foreach (var grp in theirs.GroupBy(t => t.Draw))
                {
                    var a = grp.Select(t => t.M).ToList();
                    var b = mine.Where(m => m.Draw == grp.Key).Select(m => m.M).ToList();
                    Count(a.Order().SequenceEqual(b.Order()) ? "fragment set same" : "fragment set differs");
                    if (!a.Order().SequenceEqual(b.Order()) && shown++ < 12)
                        output.WriteLine($"fragments differ: {Path.GetFileName(name)} draw {grp.Key} ({(grp.Key >= 0 ? agg.Draws[grp.Key].Model : "?")}): shipped {a.Count}, ours {b.Count}, common {a.Intersect(b).Count()}");
                    if (a.Count > 1)
                        Count(a.SequenceEqual(b) ? "fragment order same" : "fragment order differs");
                    if (a.Count > 1)
                    {
                        var ourFrags2 = agg.Fragments.Where(f => f.Draw == grp.Key).ToList();
                        shippedCentres.Add([.. a.Select(m => ourFrags2.First(x => string.Join(",", x.Matrix.Select(BitConverter.SingleToInt32Bits)) == m).Centre)]);
                    }
                    if (a.Count > 1)
                    {
                        var ourFrags2 = agg.Fragments.Where(f => f.Draw == grp.Key).ToList();
                        shippedCentres.Add([.. a.Select(m => ourFrags2.First(x => string.Join(",", x.Matrix.Select(BitConverter.SingleToInt32Bits)) == m).Centre)]);
                    }
                    if (a.Count > 1 && !a.SequenceEqual(b) && orderShown++ < 4)
                    {
                        var ourFrags = agg.Fragments.Where(f => f.Draw == grp.Key).ToList();
                        output.WriteLine($"ORDER {Path.GetFileName(name)} draw {grp.Key} ({agg.Draws[grp.Key].Model}):");
                        foreach (var m in a)
                        {
                            var f = ourFrags.First(x => string.Join(",", x.Matrix.Select(BitConverter.SingleToInt32Bits)) == m);
                            var ent = entities.FirstOrDefault(e => (e.Element.GetValue<int>("nodeID") ?? -2) == f.NodeId && e.Through.Count == 0);
                            var k = ent?.Element.Get<DmxBinary.Element>("entity_properties");
                            output.WriteLine($"   ours #{ourFrags.IndexOf(f)} key {NodePropAggregates.MortonKey(f.Centre, nodeMin):x} node {f.NodeId} walk {(ent == null ? -1 : entities.IndexOf(ent))} through {ent?.Through.Count} skin {k?.Get<string>("skin")} color {k?.Get<string>("rendercolor")} model {k?.Get<string>("model")}");
                        }
                    }
                }
            }
        }
        foreach (var n in notes.Distinct().Take(8))
            output.WriteLine($"note: {n}");
        if (Environment.GetEnvironmentVariable("PROPAGG_SEARCH") is { } range)
        {
            // Origins around nodeMin: how many draws hold their fragments in ascending Morton order.
            var r = int.Parse(range);
            int Score(Vector2 o) => shippedCentres.Count(cs =>
            {
                for (var i = 1; i < cs.Length; i++)
                    if (NodePropAggregates.MortonKey(cs[i], o) < NodePropAggregates.MortonKey(cs[i - 1], o))
                        return false;
                return true;
            });
            var best = (Score: Score(nodeMin), O: nodeMin);
            output.WriteLine($"SEARCH at nodeMin {best.Score} of {shippedCentres.Count}");
            for (var dx = -r; dx <= r; dx += 8)
                for (var dy = -r; dy <= r; dy += 8)
                {
                    var o = nodeMin + new Vector2(dx, dy);
                    var sc = Score(o);
                    if (sc > best.Score)
                        best = (sc, o);
                }
            output.WriteLine($"SEARCH best {best.Score} at {best.O.X:R},{best.O.Y:R} (offset {best.O - nodeMin})");
        }
        output.WriteLine($"TALLY {string.Join(", ", tally.Select(kv => $"{kv.Key} {kv.Value}"))}");
        // Valve's draw and fragment order follow heap addresses (GROUND_TRUTH 43): not asserted.
        Assert.Equal(0, tally.GetValueOrDefault("aggregate without ours") + tally.GetValueOrDefault("aggregate draws differ"));
        Assert.Equal(0, tally.GetValueOrDefault("draw triangles differ") + tally.GetValueOrDefault("draw vertices differ") + tally.GetValueOrDefault("fragment set differs"));
    }
}
