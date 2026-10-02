using System.Numerics;
using Source2.Compiler.Maps;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>PROPENTRIES=1</c> with <c>NODEENTRIES</c> and
/// <c>NODEENTRIES_VMAP</c>): the entries WRBNode_AddStaticProps appends
/// (captured at Step256690's input after the world's) beside the prop's
/// model decoded with VRF. A prop's vertex is placed by origin + R (S p);
/// each entry is matched to a draw call by its first vertex, then its
/// streams are compared with the buffer's.
/// </summary>
public class PropEntriesProbe(ITestOutputHelper output)
{
    [Fact]
    public void Explore()
    {
        if (Environment.GetEnvironmentVariable("PROPENTRIES") != "1" || Environment.GetEnvironmentVariable("NODEENTRIES") is not { } path
            || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap || CS2Fixtures.StockPak() is not { } pak)
            return;
        var all = NodeEntriesFromVmap.Read(path);
        var world = all.Count(c => c.Stage == "BuildNode:in");
        var props = all.Where(c => c.Stage == "Step256690:in").Skip(world).ToList();
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vmap)))!;
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        var (_, entities) = MapMeshes.ReadWithEntities(Maps.MapSource.Read(vmap));
        var candidates = entities.Where(e => e.Element.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname") == "prop_static").ToList();
        string F(float x) => BitConverter.SingleToInt32Bits(x) == int.MinValue ? "-0" : x.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        output.WriteLine($"{props.Count} prop entries, {candidates.Count} prop_static");
        var shown = 0;
        var total = new SortedDictionary<string, int>();
        var vertices = 0;
        foreach (var entry in props)
        {
            var first = new Vector3(entry.Vertices[0], entry.Vertices[1], entry.Vertices[2]);
            var found = false;
            foreach (var ent in candidates)
            {
                var el = ent.Element;
                var modelName = el.Get<DmxBinary.Element>("entity_properties")!.Get<string>("model")!;
                var local = MapMeshes.Local(el);
                var place = ent.Instances.Length == 0 ? local : MapMeshes.Concat(ent.Path, local);
                if (Vector3.Distance(new Vector3(place[3], place[7], place[11]), first) > 500f)
                    continue;
                var model = content.LoadedModel(modelName + "_c");
                if (model is null)
                    continue;
                var scale = el.GetValue<Vector3>("scales") ?? Vector3.One;
                var placed = Source2.Compiler.Physics.WorldCollision.PropOf(ent);
                var pm = PropTransform.Matrix(placed.Origin, placed.Angles, scale);
                Vector3 Place(Vector3 p) => LightMath.Transform34(pm, p);
                foreach (var (mesh, index, _, lod) in model.GetEmbeddedMeshesAndLoD())
                {
                    var vbib = mesh.VBIB;
                    foreach (var so in mesh.Data.GetArray("m_sceneObjects"))
                        foreach (var call in so.GetArray("m_drawCalls"))
                        {
                            var vb = vbib.VertexBuffers[call.GetArray("m_vertexBuffers")[0].GetInt32Property("m_hBuffer")];
                            var positions = ValveResourceFormat.Blocks.VBIB.GetVector3AttributeArray(vb, vb.InputLayoutFields.First(f => f.SemanticName == "POSITION"));
                            var bas = call.GetInt32Property("m_nBaseVertex");
                            var nv = entry.Vertices.Length / entry.Stride;
                            var ib = vbib.IndexBuffers[call.GetSubCollection("m_indexBuffer").GetInt32Property("m_hBuffer")];
                            int Index(int i) => ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, i * 2) : BitConverter.ToInt32(ib.Data, i * 4);
                            var start = call.GetInt32Property("m_nStartIndex");
                            var count = call.GetInt32Property("m_nIndexCount");
                            // The draw call's vertex range: from its lowest referenced vertex.
                            var lo = Enumerable.Range(start, count).Min(Index) + bas;
                            if (lo + nv > positions.Length || Place(positions[lo]) != first)
                                continue;
                            found = true;
                            {
                                var callOrdinal = 0;
                                foreach (var so2 in mesh.Data.GetArray("m_sceneObjects"))
                                    foreach (var c2 in so2.GetArray("m_drawCalls"))
                                    {
                                        if (ReferenceEquals(c2, call))
                                            goto counted;
                                        callOrdinal++;
                                    }
                                counted:
                                var raw = entry.Raw;
                                output.WriteLine($"ORDER ent {candidates.IndexOf(ent)} node {el.GetValue<int>("nodeID")} inst [{string.Join(",", ent.Instances)}] {Path.GetFileNameWithoutExtension(modelName)} mesh {index} call {callOrdinal} | id {BitConverter.ToInt32(raw, 0x40)} 98:{raw[0x98]} c0:{raw[0xc0]:x} 1a0:{raw[0x1a0]} 1a4:{raw[0x1a4]} 1a5:{raw[0x1a5]} attr {BitConverter.ToUInt64(raw, 0x1b0):x}");
                            }
                            var exact = Enumerable.Range(0, nv).Count(k => Place(positions[lo + k]) == new Vector3(entry.Vertices[k * entry.Stride], entry.Vertices[(k * entry.Stride) + 1], entry.Vertices[(k * entry.Stride) + 2]));
                            var indicesSame = entry.Indices.SequenceEqual(Enumerable.Range(start, count).Select(i => Index(i) + bas - lo));
                            // Stream candidates, counted over the whole entry.
                            var nf = vb.InputLayoutFields.First(f => f.SemanticName == "NORMAL");
                            // meshsystem's unpack (PackedNormals), as the loader capture showed.
                            var normals = new Vector3[lo + nv];
                            var tangents = new Vector4[lo + nv];
                            for (var k = 0; k < nv; k++)
                            {
                                var raw = BitConverter.ToUInt32(vb.Data, ((lo + k) * (int)vb.ElementSizeInBytes) + (int)nf.Offset);
                                (normals[lo + k], tangents[lo + k]) = nf.Format.ToString() == "R32_UINT" ? PackedNormals.DecodeR32(raw) : PackedNormals.DecodeR8G8B8A8(raw);
                            }
                            Vector3 Ent(int k, int at) => new(entry.Vertices[(k * entry.Stride) + at], entry.Vertices[(k * entry.Stride) + at + 1], entry.Vertices[(k * entry.Stride) + at + 2]);
                            var tally = new SortedDictionary<string, int>();
                            void Count(string name, bool ok) { if (ok) tally[name] = tally.GetValueOrDefault(name) + 1; }
                            // The port: the loader's 18 floats, then PropTransform with the material's axes, bit for bit.
                            var prop = Source2.Compiler.Physics.WorldCollision.PropOf(ent);
                            var m = PropTransform.Matrix(prop.Origin, prop.Angles, scale);
                            var mat = content.Material(call.GetStringProperty("m_material"));
                            var axes = PropTransform.AxesOf(mat?.Shader, mat?.Params);
                            var tf0 = vb.InputLayoutFields.First(f => f.SemanticName == "TEXCOORD");
                            var buffer = new float[nv * 18];
                            for (var k = 0; k < nv; k++)
                            {
                                var (p0, n0, t0) = (positions[lo + k], normals[lo + k], tangents[lo + k]);
                                var uv = TexcoordOf(vb, tf0, lo + k);
                                float[] one = [p0.X, p0.Y, p0.Z, n0.X, n0.Y, n0.Z, t0.X, t0.Y, t0.Z, t0.W, uv.X, uv.Y];
                                one.CopyTo(buffer, k * 18);
                            }
                            PropTransform.Apply(buffer, 18, 0, 3, 6, 10, 16, m, axes, PropTransform.Place | PropTransform.Texcoords);
                            bool Bits(int k, int at, int width) => Enumerable.Range(at, width).All(i => BitConverter.SingleToInt32Bits(buffer[(k * 18) + i]) == BitConverter.SingleToInt32Bits(entry.Vertices[(k * entry.Stride) + i]));
                            for (var k = 0; k < nv; k++)
                            {
                                Count("position", Bits(k, 0, 3));
                                Count("normal", Bits(k, 3, 3));
                                Count("tangent", Bits(k, 6, 4));
                                Count("texcoord", Bits(k, 10, 2));
                                Count("second texcoord", Bits(k, 16, 2));
                                if (vb.InputLayoutFields.FirstOrDefault(f => f.SemanticName == "COLOR") is { SemanticName: not null } cf)
                                {
                                    var rc = vb.Data.AsSpan(((lo + k) * (int)vb.ElementSizeInBytes) + (int)cf.Offset, 4).ToArray();
                                    Count("color /255", Enumerable.Range(0, 4).All(i => rc[i] / 255f == entry.Vertices[(k * entry.Stride) + 12 + i]));
                                }
                                else
                                    Count("color zero", Enumerable.Range(0, 4).All(i => BitConverter.SingleToInt32Bits(entry.Vertices[(k * entry.Stride) + 12 + i]) == 0));
                            }
                            vertices += nv;
                            total["entries matched"] = total.GetValueOrDefault("entries matched") + 1;
                            if (indicesSame)
                                total["entries with equal indices"] = total.GetValueOrDefault("entries with equal indices") + 1;
                            foreach (var (key, value) in tally)
                                total[key] = total.GetValueOrDefault(key) + value;
                            if (new[] { "position", "normal", "tangent", "texcoord", "second texcoord" }.Any(x => tally.GetValueOrDefault(x) != nv))
                            {
                                for (var k = 0; k < 2; k++)
                                {
                                    output.WriteLine($"    v{k} ours  {string.Join(" ", buffer.Skip(k * 18).Take(18).Select(F))}");
                                    output.WriteLine($"       entry {string.Join(" ", entry.Vertices.Skip(k * entry.Stride).Take(entry.Stride).Select(F))}");
                                }
                                output.WriteLine($"  MISS {modelName} node {el.GetValue<int>("nodeID")} instances [{string.Join(",", ent.Instances)}] scale {scale} {nv}: {string.Join(", ", tally.Select(kv => $"{kv.Key} {kv.Value}"))} shader {mat?.Shader} axes {axes}");
                            }
                            if (shown < 12)
                                output.WriteLine($"  streams of {nv}: {string.Join(", ", tally.Select(kv => $"{kv.Key} {kv.Value}"))}");
                            if (shown++ >= 6)
                                continue;
                            output.WriteLine($"entry {Path.GetFileName(entry.Material)} id {BitConverter.ToInt32(entry.Raw, 0x40)}: {nv} vertices, positions exact {exact}, indices same {indicesSame} <- node {el.GetValue<int>("nodeID")} instances [{string.Join(",", ent.Instances)}] {modelName} mesh {index} lod {lod} call {call.GetStringProperty("m_material")} range {lo}+{nv}");
                            output.WriteLine($"  layout {string.Join(" ", vb.InputLayoutFields.Select(f => $"{f.SemanticName}{f.SemanticIndex}:{f.Format}@{f.Offset}"))} stride {vb.ElementSizeInBytes}");
                            output.WriteLine($"  entry streams {string.Join(" ", entry.Layout.Select(x => $"{x.Name}@{x.First}x{x.Count}:{x.Type}"))}");
                            output.WriteLine($"  scale {scale} place {string.Join(" ", place.Select(F))}");
                            for (var k = 0; k < 2; k++)
                            {
                                var raw = vb.Data.AsSpan((lo + k) * (int)vb.ElementSizeInBytes, (int)vb.ElementSizeInBytes).ToArray();
                                output.WriteLine($"  vertex {k}: raw {Convert.ToHexString(raw)}");
                                output.WriteLine($"            entry {string.Join(" ", entry.Vertices.Skip(k * entry.Stride).Take(entry.Stride).Select(F))}");
                            }
                        }
                }
            }
            if (!found)
                output.WriteLine($"entry {Path.GetFileName(entry.Material)} id {BitConverter.ToInt32(entry.Raw, 0x40)}: no draw call found for {first}");
        }
        {
            var models = candidates.Select(c => c.Element.Get<DmxBinary.Element>("entity_properties")!.Get<string>("model") ?? "").ToList();
            foreach (var name in new[] { "interior_curtain_2", "italy_wood_door_2", "italy_windowkit01_window02_a", "italy_door_shutter_02", "italy_window_shutter_02", "inferno_doorframe_02_56_a", "inferno_stone_trim_block_01", "inferno_stone_trim_block_02", "inferno_stone_trim_block_03", "inferno_stone_trim_block_04", "tile_set_a", "metal_pipe_001b_straight_32", "web_joist_support_001_horizontal_64" })
            {
                var hits = Enumerable.Range(0, models.Count).Where(i => Path.GetFileNameWithoutExtension(models[i]) == name).ToList();
                output.WriteLine($"FIRST {name}: first {hits.FirstOrDefault()} last {hits.LastOrDefault()} count {hits.Count} path {models[hits[0]]}");
            }
            var distinct = models.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            output.WriteLine($"DISTINCT ascending: {string.Join(" ", distinct.Select((m, i) => (m, i)).Where(x => x.m.Contains("curtain_2") || x.m.Contains("wood_door_2") || x.m.Contains("window02_a") || x.m.Contains("door_shutter_02") || x.m.Contains("window_shutter_02") || x.m.Contains("doorframe_02_56") || x.m.Contains("trim_block") || x.m.Contains("tile_set_a") || x.m.Contains("pipe_001b_straight_32") || x.m.Contains("joist_support_001_horizontal_64")).Select(x => $"{x.i}:{Path.GetFileNameWithoutExtension(x.m)}"))}");
        }
        output.WriteLine($"TOTAL {vertices} vertices: {string.Join(", ", total.Select(kv => $"{kv.Key} {kv.Value}"))}");
    }

    /// <summary>
    /// <see cref="NodePropEntries"/> from the .vmap against the prop entries
    /// captured at Step256690's input after the world's (<c>NODEENTRIES</c>,
    /// <c>NODEENTRIES_VMAP</c>): count, order, id, material, streams, every
    /// vertex float bit for bit, and the indices.
    /// </summary>
    [Fact]
    public void BuiltFromVmap()
    {
        if (Environment.GetEnvironmentVariable("NODEENTRIES") is not { } path || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap
            || CS2Fixtures.StockPak() is not { } pak)
            return;
        var all = NodeEntriesFromVmap.Read(path);
        var world = all.Count(c => c.Stage == "BuildNode:in");
        var valve = all.Where(c => c.Stage == "Step256690:in").Skip(world).ToList();
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vmap)))!;
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        var notes = new List<string>();
        var ours = NodePropEntries.FromWorld(Maps.MapSource.Read(vmap), content, notes);
        foreach (var note in notes.Distinct())
            output.WriteLine($"note: {note}");
        output.WriteLine($"{ours.Count} ours, {valve.Count} Valve's");
        // PROPENTRIES_BYID=1: ours reordered to Valve's by (id, material, occurrence), to check content apart from order.
        if (Environment.GetEnvironmentVariable("PROPENTRIES_BYID") == "1")
        {
            var pool = ours.Select((e, i) => (e, i)).ToList();
            var reordered = new List<NodeMeshEntries.Entry>();
            foreach (var v in valve)
            {
                var id = BitConverter.ToInt32(v.Raw, 0x40);
                var hit = pool.FindIndex(x => x.e.NodeId == id && string.Equals(x.e.Material, v.Material, StringComparison.OrdinalIgnoreCase));
                if (hit < 0)
                    break;
                reordered.Add(pool[hit].e);
                pool.RemoveAt(hit);
            }
            if (reordered.Count == valve.Count)
                ours = reordered;
        }
        var exact = 0;
        for (var i = 0; i < Math.Min(ours.Count, valve.Count); i++)
        {
            var (o, v) = (ours[i], valve[i]);
            var problems = new List<string>();
            if (o.NodeId != BitConverter.ToInt32(v.Raw, 0x40))
                problems.Add($"id {o.NodeId} vs {BitConverter.ToInt32(v.Raw, 0x40)}");
            if (!string.Equals(o.Material, v.Material, StringComparison.OrdinalIgnoreCase))
                problems.Add($"material {o.Material} vs {v.Material}");
            if (!o.Streams.Select(x => (x.Name, x.First, x.Count, x.Type)).SequenceEqual(v.Layout.Select(x => (x.Name, x.First, x.Count, x.Type))))
                problems.Add("streams");
            if (!o.Vertices.Select(BitConverter.SingleToInt32Bits).SequenceEqual(v.Vertices.Select(BitConverter.SingleToInt32Bits)))
                problems.Add("vertices");
            if (!o.Indices.SequenceEqual(v.Indices))
                problems.Add("indices");
            if (problems.Count == 0)
                exact++;
            else
                output.WriteLine($"entry {i} {Path.GetFileName(v.Material)}: {string.Join(", ", problems)}");
        }
        output.WriteLine($"{exact} exact");
        Assert.Equal(valve.Count, ours.Count);
        Assert.Equal(valve.Count, exact);
    }

    private static Vector2 TexcoordOf(ValveResourceFormat.Blocks.VBIB.OnDiskBufferData vb, ValveResourceFormat.Blocks.VBIB.RenderInputLayoutField f, int vertex)
    {
        var off = (vertex * (int)vb.ElementSizeInBytes) + (int)f.Offset;
        return f.Format.ToString() switch
        {
            "R16G16_SNORM" => new Vector2(MathF.Max(BitConverter.ToInt16(vb.Data, off) / 32767f, -1f), MathF.Max(BitConverter.ToInt16(vb.Data, off + 2) / 32767f, -1f)),
            "R16G16_FLOAT" => new Vector2((float)BitConverter.ToHalf(vb.Data, off), (float)BitConverter.ToHalf(vb.Data, off + 2)),
            _ => new Vector2(BitConverter.ToSingle(vb.Data, off), BitConverter.ToSingle(vb.Data, off + 4)),
        };
    }

    /// <summary>
    /// Exploration (<c>PROPMESHES</c>, capture_propmeshes.py's file, with
    /// <c>NODEENTRIES_VMAP</c>): each mesh WRB_LoadPropMeshes returned beside
    /// VRF's decode of a draw call of an atixref prop model with the same
    /// material and vertex count, stream by stream.
    /// </summary>
    [Fact]
    public void LoaderAgainstVrf()
    {
        if (Environment.GetEnvironmentVariable("PROPMESHES") is not { } path || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap
            || CS2Fixtures.StockPak() is not { } pak)
            return;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vmap)))!;
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        var (_, entities) = MapMeshes.ReadWithEntities(Maps.MapSource.Read(vmap));
        var models = entities.Select(e => e.Element.Get<DmxBinary.Element>("entity_properties"))
            .Where(p => p?.Get<string>("classname") == "prop_static").Select(p => p!.Get<string>("model")!).Distinct().ToList();
        // Draw calls by (material, vertex count).
        var calls = new Dictionary<(string, int), List<(ValveResourceFormat.Blocks.VBIB.OnDiskBufferData Vb, int Lo)>>();
        foreach (var name in models)
        {
            if (content.LoadedModel(name + "_c") is not { } model)
                continue;
            foreach (var (mesh, _, _, _) in model.GetEmbeddedMeshesAndLoD())
                foreach (var so in mesh.Data.GetArray("m_sceneObjects"))
                    foreach (var call in so.GetArray("m_drawCalls"))
                    {
                        var vbib = mesh.VBIB;
                        var vb = vbib.VertexBuffers[call.GetArray("m_vertexBuffers")[0].GetInt32Property("m_hBuffer")];
                        var ib = vbib.IndexBuffers[call.GetSubCollection("m_indexBuffer").GetInt32Property("m_hBuffer")];
                        int Index(int i) => ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, i * 2) : BitConverter.ToInt32(ib.Data, i * 4);
                        var start = call.GetInt32Property("m_nStartIndex");
                        var count = call.GetInt32Property("m_nIndexCount");
                        var lo = Enumerable.Range(start, count).Min(Index) + call.GetInt32Property("m_nBaseVertex");
                        var key = ((call.GetStringProperty("m_material") ?? "").ToLowerInvariant(), call.GetInt32Property("m_nVertexCount"));
                        if (!calls.TryGetValue(key, out var list))
                            calls[key] = list = [];
                        list.Add((vb, lo));
                    }
        }
        var tally = new SortedDictionary<string, int>();
        void T(string k) => tally[k] = tally.GetValueOrDefault(k) + 1;
        var shown = 0;
        string F(float x) => BitConverter.SingleToInt32Bits(x) == int.MinValue ? "-0" : x.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        using var f = File.OpenRead(path);
        using var r = new BinaryReader(f);
        while (f.Position < f.Length)
        {
            var head = System.Text.Json.JsonDocument.Parse(r.ReadBytes(r.ReadInt32())).RootElement;
            var blob = r.ReadBytes(r.ReadInt32());
            if (head.GetProperty("ev").GetString() != "mesh")
                continue;
            int nv = head.GetProperty("nv").GetInt32(), stride = head.GetProperty("stride").GetInt32();
            var material = head.GetProperty("material").GetString()!.ToLowerInvariant();
            var v = new float[nv * stride];
            Buffer.BlockCopy(blob, 0, v, 0, v.Length * 4);
            var streams = head.GetProperty("streams").EnumerateArray().Select(x => (Name: x.GetProperty("name").GetString()!, First: x.GetProperty("first").GetInt32())).ToList();
            if (!calls.TryGetValue((material, nv), out var candidates))
            {
                T("no draw call");
                continue;
            }
            var hit = false;
            if (Environment.GetEnvironmentVariable("PROPMESHES_OURS") == "1")
            {
                // Several models can share a draw call's positions: take the one whose frames match best.
                int Score((ValveResourceFormat.Blocks.VBIB.OnDiskBufferData Vb, int Lo) c)
                {
                    var pos = ValveResourceFormat.Blocks.VBIB.GetVector3AttributeArray(c.Vb, c.Vb.InputLayoutFields.First(x => x.SemanticName == "POSITION"));
                    if (c.Lo + nv > pos.Length || Enumerable.Range(0, nv).Any(k => pos[c.Lo + k] != new Vector3(v[k * stride], v[(k * stride) + 1], v[(k * stride) + 2])))
                        return -1;
                    var nf2 = c.Vb.InputLayoutFields.First(x => x.SemanticName == "NORMAL");
                    var ta2 = streams.First(x => x.Name == "tangent").First;
                    var tf2 = c.Vb.InputLayoutFields.First(x => x.SemanticName == "TEXCOORD");
                    var ua2 = streams.First(x => x.Name == "texcoord").First;
                    var uvScore = Enumerable.Range(0, nv).Count(k => TexcoordOf(c.Vb, tf2, c.Lo + k) == new Vector2(v[(k * stride) + ua2], v[(k * stride) + ua2 + 1]));
                    return uvScore + Enumerable.Range(0, nv).Count(k =>
                    {
                        var raw = BitConverter.ToUInt32(c.Vb.Data, ((c.Lo + k) * (int)c.Vb.ElementSizeInBytes) + (int)nf2.Offset);
                        var (_, pt) = nf2.Format.ToString() == "R32_UINT" ? PackedNormals.DecodeR32(raw) : PackedNormals.DecodeR8G8B8A8(raw);
                        return pt == new Vector4(v[(k * stride) + ta2], v[(k * stride) + ta2 + 1], v[(k * stride) + ta2 + 2], v[(k * stride) + ta2 + 3]);
                    });
                }
                candidates = [.. candidates.OrderByDescending(Score)];
            }
            foreach (var (vb, lo) in candidates)
            {
                var pos = ValveResourceFormat.Blocks.VBIB.GetVector3AttributeArray(vb, vb.InputLayoutFields.First(x => x.SemanticName == "POSITION"));
                if (lo + nv > pos.Length || Enumerable.Range(0, nv).Any(k => pos[lo + k] != new Vector3(v[k * stride], v[(k * stride) + 1], v[(k * stride) + 2])))
                    continue;
                hit = true;
                T("positions exact");
                var nf = vb.InputLayoutFields.First(x => x.SemanticName == "NORMAL");
                var (normals, tangents) = ValveResourceFormat.Blocks.VBIB.GetNormalTangentArray(vb, nf);
                if (Environment.GetEnvironmentVariable("PROPMESHES_OURS") == "1")
                    for (var k = 0; k < nv; k++)
                    {
                        var raw = BitConverter.ToUInt32(vb.Data, ((lo + k) * (int)vb.ElementSizeInBytes) + (int)nf.Offset);
                        var (pn, pt) = nf.Format.ToString() == "R32_UINT" ? PackedNormals.DecodeR32(raw) : PackedNormals.DecodeR8G8B8A8(raw);
                        normals[lo + k] = pn;
                        tangents[lo + k] = pt;
                    }
                int na = streams.First(x => x.Name == "normal").First, ta = streams.First(x => x.Name == "tangent").First, ua = streams.First(x => x.Name == "texcoord").First;
                var nOk = Enumerable.Range(0, nv).Count(k => normals[lo + k] == new Vector3(v[(k * stride) + na], v[(k * stride) + na + 1], v[(k * stride) + na + 2]));
                var tOk = Enumerable.Range(0, nv).Count(k => tangents[lo + k] == new Vector4(v[(k * stride) + ta], v[(k * stride) + ta + 1], v[(k * stride) + ta + 2], v[(k * stride) + ta + 3]));
                T($"normals {(nOk == nv ? "exact" : "differ")}");
                T($"tangents {(tOk == nv ? "exact" : "differ")}");
                if (tOk != nv)
                {
                    var bad = Enumerable.Range(0, nv).Where(k => tangents[lo + k] != new Vector4(v[(k * stride) + ta], v[(k * stride) + ta + 1], v[(k * stride) + ta + 2], v[(k * stride) + ta + 3])).ToList();
                    var flipped = bad.Count(k => (tangents[lo + k].X * v[(k * stride) + ta]) + (tangents[lo + k].Y * v[(k * stride) + ta + 1]) + (tangents[lo + k].Z * v[(k * stride) + ta + 2]) < -0.99f);
                    output.WriteLine($"  tangent misses {material}: {bad.Count} of {nv}, {flipped} flipped, format {nf.Format}, first raw {BitConverter.ToUInt32(vb.Data, ((lo + bad[0]) * (int)vb.ElementSizeInBytes) + (int)nf.Offset):X8}");
                    var ni = head.GetProperty("ni").GetInt32();
                    var idx = new int[ni];
                    Buffer.BlockCopy(blob, v.Length * 4, idx, 0, ni * 4);
                    var gen = MeshTangents.Corners([.. Enumerable.Range(0, nv).Select(k => new Vector3(v[k * stride], v[(k * stride) + 1], v[(k * stride) + 2]))],
                        [.. Enumerable.Range(0, nv).Select(k => new Vector3(v[(k * stride) + na], v[(k * stride) + na + 1], v[(k * stride) + na + 2]))],
                        [.. Enumerable.Range(0, nv).Select(k => new Vector2(v[(k * stride) + ua], v[(k * stride) + ua + 1]))], idx);
                    var genOk = Enumerable.Range(0, ni).Count(c => gen[c] == new Vector4(v[(idx[c] * stride) + ta], v[(idx[c] * stride) + ta + 1], v[(idx[c] * stride) + ta + 2], v[(idx[c] * stride) + ta + 3]));
                    output.WriteLine($"    MeshTangents on the loader mesh: {genOk} of {ni} corners");
                }
                if ((nOk != nv || tOk != nv) && shown++ < 4)
                {
                    var k = Enumerable.Range(0, nv).First(k => normals[lo + k] != new Vector3(v[(k * stride) + na], v[(k * stride) + na + 1], v[(k * stride) + na + 2]) || tangents[lo + k] != new Vector4(v[(k * stride) + ta], v[(k * stride) + ta + 1], v[(k * stride) + ta + 2], v[(k * stride) + ta + 3]));
                    var raw = vb.Data.AsSpan(((lo + k) * (int)vb.ElementSizeInBytes) + (int)nf.Offset, 4).ToArray();
                    output.WriteLine($"  {material} vertex {k} {nf.Format} raw {Convert.ToHexString(raw)}: vrf n {normals[lo + k]} t {tangents[lo + k]}; valve n {string.Join(",", v.Skip((k * stride) + na).Take(3).Select(F))} t {string.Join(",", v.Skip((k * stride) + ta).Take(4).Select(F))}");
                }
                var tf = vb.InputLayoutFields.First(x => x.SemanticName == "TEXCOORD");
                T($"uv {tf.Format}");
                {
                    var fmt = tf.Format.ToString();
                    var counts = new Dictionary<string, int>();
                    for (var k = 0; k < nv; k++)
                    {
                        var off = ((lo + k) * (int)vb.ElementSizeInBytes) + (int)tf.Offset;
                        float vu = v[(k * stride) + ua], vv = v[(k * stride) + ua + 1];
                        void C(string name, float a, float b) { if (a == vu && b == vv) counts[name] = counts.GetValueOrDefault(name) + 1; }
                        if (fmt == "R16G16_SNORM")
                        {
                            short a = BitConverter.ToInt16(vb.Data, off), b = BitConverter.ToInt16(vb.Data, off + 2);
                            C("max(/32767;-1)", MathF.Max(a / 32767f, -1f), MathF.Max(b / 32767f, -1f));
                            C("*1/32767", a * (1f / 32767f), b * (1f / 32767f));
                            C("double", (float)(a / 32767.0), (float)(b / 32767.0));
                            C("/32768", a / 32768f, b / 32768f);
                            C("(x+0.5)/32767.5", (a + 0.5f) / 32767.5f, (b + 0.5f) / 32767.5f);
                        }
                        else if (fmt == "R16G16_FLOAT")
                            C("half", (float)BitConverter.ToHalf(vb.Data, off), (float)BitConverter.ToHalf(vb.Data, off + 2));
                        else if (fmt.StartsWith("R32G32"))
                            C("float", BitConverter.ToSingle(vb.Data, off), BitConverter.ToSingle(vb.Data, off + 4));
                    }
                    var best = counts.OrderByDescending(kv => kv.Value).FirstOrDefault();
                    T($"uv {fmt} best {best.Key} {(best.Value == nv ? "all" : "partial")}");
                }
                if (Environment.GetEnvironmentVariable("PROPMESHES_DUMP") is { } dump)
                    File.AppendAllLines(dump, Enumerable.Range(0, nv).Select(k =>
                        $"{nf.Format},{Convert.ToHexString(vb.Data.AsSpan(((lo + k) * (int)vb.ElementSizeInBytes) + (int)nf.Offset, nf.Format.ToString().StartsWith("R32") ? 4 : 4))},{string.Join(",", v.Skip((k * stride) + na).Take(3).Select(F))},{string.Join(",", v.Skip((k * stride) + ta).Take(4).Select(F))},{tf.Format},{Convert.ToHexString(vb.Data.AsSpan(((lo + k) * (int)vb.ElementSizeInBytes) + (int)tf.Offset, tf.Format.ToString().Contains("32G32") ? 8 : 4))},{F(v[(k * stride) + ua])},{F(v[(k * stride) + ua + 1])}"));
                break;
            }
            if (!hit)
                T("no position match");
        }
        output.WriteLine(string.Join(", ", tally.Select(kv => $"{kv.Key} {kv.Value}")));
    }
}
