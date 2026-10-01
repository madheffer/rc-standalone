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
        var (_, entities) = MapMeshes.ReadWithEntities(DmxBinary.ReadFile(vmap));
        var candidates = entities.Where(e => e.Element.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname") == "prop_static").ToList();
        string F(float x) => BitConverter.SingleToInt32Bits(x) == int.MinValue ? "-0" : x.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        output.WriteLine($"{props.Count} prop entries, {candidates.Count} prop_static");
        var shown = 0;
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
                Vector3 Place(Vector3 p) => MapMeshes.Transform(place, scale * p);
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
                            var exact = Enumerable.Range(0, nv).Count(k => Place(positions[lo + k]) == new Vector3(entry.Vertices[k * entry.Stride], entry.Vertices[(k * entry.Stride) + 1], entry.Vertices[(k * entry.Stride) + 2]));
                            var indicesSame = entry.Indices.SequenceEqual(Enumerable.Range(start, count).Select(i => Index(i) + bas - lo));
                            // Stream candidates, counted over the whole entry.
                            var nf = vb.InputLayoutFields.First(f => f.SemanticName == "NORMAL");
                            var (normals, tangents) = ValveResourceFormat.Blocks.VBIB.GetNormalTangentArray(vb, nf);
                            Vector3 Rot(Vector3 d) => new((place[0] * d.X) + (place[1] * d.Y) + (place[2] * d.Z), (place[4] * d.X) + (place[5] * d.Y) + (place[6] * d.Z), (place[8] * d.X) + (place[9] * d.Y) + (place[10] * d.Z));
                            Vector3 Ent(int k, int at) => new(entry.Vertices[(k * entry.Stride) + at], entry.Vertices[(k * entry.Stride) + at + 1], entry.Vertices[(k * entry.Stride) + at + 2]);
                            var tally = new SortedDictionary<string, int>();
                            void Count(string name, bool ok) { if (ok) tally[name] = tally.GetValueOrDefault(name) + 1; }
                            for (var k = 0; k < nv; k++)
                            {
                                var n = normals[lo + k];
                                var t = tangents[lo + k];
                                var en = Ent(k, 3);
                                var et = Ent(k, 6);
                                Count("normal inv-transpose", NodeMeshEntries.Normalise(Rot(n / scale)) == en);
                                Count("normal scaled", NodeMeshEntries.Normalise(Rot(n * scale)) == en);
                                Count("normal plain", NodeMeshEntries.Normalise(Rot(n)) == en);
                                Count("tangent scaled", NodeMeshEntries.Normalise(Rot(new Vector3(t.X, t.Y, t.Z) * scale)) == et);
                                Count("tangent inv-transpose", NodeMeshEntries.Normalise(Rot(new Vector3(t.X, t.Y, t.Z) / scale)) == et);
                                Count("tangent w", t.W == entry.Vertices[(k * entry.Stride) + 9]);
                                var tf = vb.InputLayoutFields.First(f => f.SemanticName == "TEXCOORD");
                                var rawUv = vb.Data.AsSpan(((lo + k) * (int)vb.ElementSizeInBytes) + (int)tf.Offset, 4);
                                float eu = entry.Vertices[(k * entry.Stride) + 10], ev2 = entry.Vertices[(k * entry.Stride) + 11];
                                var su = BitConverter.ToInt16(rawUv[..2]);
                                var sv = BitConverter.ToInt16(rawUv[2..4]);
                                Count("uv snorm /32767", MathF.Max(su / 32767f, -1f) == eu && MathF.Max(sv / 32767f, -1f) == ev2);
                                Count("uv snorm *1/32767", MathF.Max(su * (1f / 32767f), -1f) == eu && MathF.Max(sv * (1f / 32767f), -1f) == ev2);
                                Count("uv half", (float)BitConverter.ToHalf(rawUv[..2]) == eu && (float)BitConverter.ToHalf(rawUv[2..4]) == ev2);
                                if (vb.InputLayoutFields.FirstOrDefault(f => f.SemanticName == "COLOR") is { SemanticName: not null } cf)
                                {
                                    var rc = vb.Data.AsSpan(((lo + k) * (int)vb.ElementSizeInBytes) + (int)cf.Offset, 4).ToArray();
                                    Count("color /255", Enumerable.Range(0, 4).All(i => rc[i] / 255f == entry.Vertices[(k * entry.Stride) + 12 + i]));
                                    Count("color *0.003921569", Enumerable.Range(0, 4).All(i => rc[i] * 0.003921569f == entry.Vertices[(k * entry.Stride) + 12 + i]));
                                }
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
            if (!found && shown++ < 8)
                output.WriteLine($"entry {Path.GetFileName(entry.Material)} id {BitConverter.ToInt32(entry.Raw, 0x40)}: no draw call found for {first}");
        }
    }
}
