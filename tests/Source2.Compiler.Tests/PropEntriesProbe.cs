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
/// model decoded with VRF: draw calls, vertex counts, and the first vertices
/// moved by the entity's matrix.
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
        var doc = DmxBinary.ReadFile(vmap);
        var byId = new Dictionary<int, DmxBinary.Element>();
        foreach (var e in doc.OfType("CMapEntity"))
            if (e.GetValue<int>("nodeID") is { } id)
                byId[id] = e;
        string F(float x) => x.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var candidates = byId.Values.Where(e => e.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname") == "prop_static"
            && e.GetValue<Vector3>("scales") is { } sc && MathF.Max(MathF.Abs(sc.X), MathF.Max(MathF.Abs(sc.Y), MathF.Abs(sc.Z))) - MathF.Min(MathF.Abs(sc.X), MathF.Min(MathF.Abs(sc.Y), MathF.Abs(sc.Z))) > 1e-4f).ToList();
        output.WriteLine($"{props.Count} prop entries, {candidates.Count} non-uniformly scaled prop_static");
        var shown = 0;
        foreach (var entry in props)
        {
            var first = new Vector3(entry.Vertices[0], entry.Vertices[1], entry.Vertices[2]);
            var found = false;
            foreach (var ent in candidates)
            {
                var modelName = ent.Get<DmxBinary.Element>("entity_properties")!.Get<string>("model")!;
                var model = content.LoadedModel(modelName + "_c");
                if (model is null)
                    continue;
                var m = MapMeshes.Local(ent);
                foreach (var (mesh, index, _, lod) in model.GetEmbeddedMeshesAndLoD())
                {
                    var vbib = mesh.VBIB;
                    foreach (var so in mesh.Data.GetArray("m_sceneObjects"))
                        foreach (var call in so.GetArray("m_drawCalls"))
                        {
                            if (!string.Equals(call.GetStringProperty("m_material"), entry.Material, StringComparison.OrdinalIgnoreCase) && ent.GetValue<int>("nodeID") != 2072)
                                continue;
                            var vb = vbib.VertexBuffers[call.GetArray("m_vertexBuffers")[0].GetInt32Property("m_hBuffer")];
                            var positions = ValveResourceFormat.Blocks.VBIB.GetVector3AttributeArray(vb, vb.InputLayoutFields.First(f => f.SemanticName == "POSITION"));
                            var bas = call.GetInt32Property("m_nBaseVertex");
                            var w = MapMeshes.Transform(m, positions[bas]);
                            if (ent.GetValue<int>("nodeID") == 2072 && BitConverter.ToInt32(entry.Raw, 0x40) == 2072)
                            {
                                var nv = call.GetInt32Property("m_nVertexCount");
                                var best = Enumerable.Range(0, nv).Select(k => (k, d: Vector3.Distance(MapMeshes.Transform(m, positions[bas + k]), first))).MinBy(x => x.d);
                                output.WriteLine($"  2072 {call.GetStringProperty("m_material")} mesh {index} lod {lod}: base {bas} verts {nv}, nearest vertex {best.k} at {best.d}; first transformed {w}");
                                var mp = Enumerable.Range(0, positions.Length).Select(k => positions[k]).ToList();
                                output.WriteLine($"    model local box {new Vector3(mp.Min(v => v.X), mp.Min(v => v.Y), mp.Min(v => v.Z))} {new Vector3(mp.Max(v => v.X), mp.Max(v => v.Y), mp.Max(v => v.Z))}");
                                var ev = Enumerable.Range(0, entry.Vertices.Length / entry.Stride).Select(k => new Vector3(entry.Vertices[k * entry.Stride], entry.Vertices[k * entry.Stride + 1], entry.Vertices[k * entry.Stride + 2])).ToList();
                                var tm = mp.Select(v => MapMeshes.Transform(m, v)).ToList();
                                var ds = ev.Select(v => tm.Min(t => Vector3.Distance(t, v))).OrderBy(x => x).ToList();
                                output.WriteLine($"    entry vertices to nearest model vertex: median {ds[ds.Count / 2]}, max {ds[^1]}, zero {ds.Count(x => x < 1e-3f)} of {ds.Count}");
                                var o = ent.GetValue<Vector3>("origin")!.Value;
                                var sc2 = ent.GetValue<Vector3>("scales")!.Value;
                                for (var sg = 0; sg < 8; sg++)
                                {
                                    var sgn = new Vector3((sg & 1) != 0 ? -1 : 1, (sg & 2) != 0 ? -1 : 1, (sg & 4) != 0 ? -1 : 1);
                                    var tt = mp.Select(v => o + (sgn * sc2 * v)).ToList();
                                    var zz = ev.Count(v => tt.Any(t => Vector3.Distance(t, v) < 1e-2f));
                                    output.WriteLine($"    signs {sgn}: {zz} of {ev.Count} entry vertices hit");
                                }
                                {
                                    var sgn = new Vector3(-1, -1, 1);
                                    var tt = mp.Select(v => o + (sgn * sc2 * v)).ToList();
                                    var map = ev.Take(12).Select(v => tt.FindIndex(t => Vector3.Distance(t, v) < 1e-2f)).ToList();
                                    output.WriteLine($"    vb index of entry vertices 0..11: {string.Join(" ", map)}; exact {ev.Count(v => tt.Contains(v))} of {ev.Count}");
                                    output.WriteLine($"    local matrix {string.Join(" ", m.Select(F))}");
                                }
                                output.WriteLine($"    entry box {new Vector3(ev.Min(v => v.X), ev.Min(v => v.Y), ev.Min(v => v.Z))} {new Vector3(ev.Max(v => v.X), ev.Max(v => v.Y), ev.Max(v => v.Z))} origin {ent.GetValue<Vector3>("origin")}");
                            }
                            if (Vector3.Distance(w, first) > 0.01f)
                                continue;
                            found = true;
                            if (shown++ >= 4)
                                continue;
                            output.WriteLine($"entry id {BitConverter.ToInt32(entry.Raw, 0x40)} {Path.GetFileName(entry.Material)}: {entry.Vertices.Length / entry.Stride} vertices, {entry.Indices.Length} indices <- node {ent.GetValue<int>("nodeID")} {modelName} mesh {index} lod {lod}: verts {call.GetInt32Property("m_nVertexCount")} indices {call.GetInt32Property("m_nIndexCount")}; layout {string.Join(" ", vb.InputLayoutFields.Select(f => $"{f.SemanticName}{f.SemanticIndex}:{f.Format}"))}");
                            output.WriteLine($"  matrix {string.Join(" ", m.Select(F))}");
                            for (var k = 0; k < 2; k++)
                            {
                                var p = positions[bas + k];
                                var q = MapMeshes.Transform(m, p);
                                output.WriteLine($"    vertex {k}: model {F(p.X)},{F(p.Y)},{F(p.Z)} -> {F(q.X)},{F(q.Y)},{F(q.Z)}; entry {string.Join(",", entry.Vertices.Skip(k * entry.Stride).Take(entry.Stride).Select(F))}");
                            }
                        }
                }
            }
            if (!found && shown++ < 8)
                output.WriteLine($"entry id {BitConverter.ToInt32(entry.Raw, 0x40)} {Path.GetFileName(entry.Material)}: no prop found for {first}");
        }
    }
}
