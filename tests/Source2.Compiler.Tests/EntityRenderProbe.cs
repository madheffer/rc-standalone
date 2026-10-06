using System.Numerics;
using Source2.Compiler.Maps;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>ENTRENDER=&lt;addon&gt;|&lt;map&gt;|&lt;compiled vpk&gt;</c>): each
/// brush entity model with render meshes beside the entity's meshes, depth
/// first, one embedded mesh each: the triangles of every mesh's corners in
/// the entity's space (positions as a multiset) against the embedded mesh's.
/// </summary>
public class EntityRenderProbe(ITestOutputHelper output)
{
    [Fact]
    public void TrianglesPerHammerMesh()
    {
        if (Environment.GetEnvironmentVariable("ENTRENDER") is not { Length: > 0 } spec || spec.Split('|') is not [var addon, var map, var vpk])
            return;
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        var doc = DmxBinary.ReadFile(Path.Combine(cs2, "content", "csgo_addons", addon, "maps", map + ".vmap"));
        using var content = new GameContent(Path.Combine(cs2, "game", "csgo", "pak01_dir.vpk"), Path.Combine(cs2, "game", "csgo_addons", addon));
        var byId = doc.Elements.Where(e => e.Type == "CMapEntity" && e.GetValue<int>("nodeID") is not null)
            .GroupBy(e => e.GetValue<int>("nodeID")!.Value).ToDictionary(g => g.Key, g => g.First());
        using var package = new Package();
        package.Read(vpk);
        int models = 0, meshesSame = 0, meshesTotal = 0, countMismatch = 0;
        foreach (var entry in package.Entries!.GetValueOrDefault("vmdl_c") ?? [])
        {
            if (!entry.DirectoryName.EndsWith("entities", StringComparison.OrdinalIgnoreCase))
                continue;
            package.ReadEntry(entry, out var bytes);
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            var embedded = ((Model)resource.DataBlock!).GetEmbeddedMeshesAndLoD().Select(m => m.Mesh).ToList();
            if (embedded.Count == 0)
                continue;
            var name = entry.FileName;
            if (!int.TryParse(name[(name.LastIndexOf('_') + 1)..], out var id) || !byId.TryGetValue(id, out var entity))
            {
                output.WriteLine($"{name}: no top-level entity");
                continue;
            }
            models++;
            if (Environment.GetEnvironmentVariable("ENTRENDER_FACTS") == "1")
                output.WriteLine($"{name}: {WorldPhysicsAuthorTests.Facts(bytes)}");
            // Faces of a material that draws nothing are not rendered; a mesh left with none has no embedded mesh.
            List<MapMeshCorners.Piece> Drawn(DmxBinary.Element mesh)
            {
                var names = mesh.Get<DmxBinary.Element>("meshData")?.Get<object?[]>("materials") ?? [];
                return [.. MapMeshCorners.Build(mesh, true, null, withTangent: true, allStreams: true)
                    .Where(p => !Physics.EntityPhysicsModels.DrawsNothing(content.Material(p.Material < names.Length ? names[p.Material] as string ?? "" : "")))];
            }
            var meshes = Meshes(entity).Where(m => Drawn(m).Count > 0).ToList();
            if (meshes.Count != embedded.Count)
            {
                countMismatch++;
                output.WriteLine($"{name}: {meshes.Count} meshes, Valve {embedded.Count} embedded: "
                    + string.Join(" / ", embedded.Select(m => string.Join(",", m.Data.GetArray("m_sceneObjects").SelectMany(so => so.GetArray("m_drawCalls")).Select(d => d.GetStringProperty("m_material")))))
                    + "; ours " + string.Join(" / ", meshes.Select(m => string.Join(",", Drawn(m).Select(p => (m.Get<DmxBinary.Element>("meshData")?.Get<object?[]>("materials") ?? [])[p.Material])))));
                continue;
            }
            var toEntity = CTransform.FromNode(entity).Inverse().Matrix();
            for (var i = 0; i < meshes.Count; i++)
            {
                meshesTotal++;
                var toWorld = CTransform.FromNode(meshes[i]).Matrix();
                var ours = new List<string>();
                foreach (var piece in Drawn(meshes[i]))
                {
                    Vector3 P(int c) => MapMeshes.Transform(toEntity, MapMeshes.Transform(toWorld,
                        new Vector3(piece.Vertices[c * piece.Stride], piece.Vertices[(c * piece.Stride) + 1], piece.Vertices[(c * piece.Stride) + 2])));
                    for (var t = 0; t + 2 < piece.Indices.Length; t += 3)
                        ours.Add(Key(P(piece.Indices[t]), P(piece.Indices[t + 1]), P(piece.Indices[t + 2])));
                }
                var theirs = Triangles(embedded[i]);
                var same = ours.Order(StringComparer.Ordinal).SequenceEqual(theirs.Order(StringComparer.Ordinal));
                if (same)
                    meshesSame++;
                else
                    output.WriteLine($"{name} mesh {i}: ours {ours.Count} triangles, Valve {theirs.Count}, shared {ours.Intersect(theirs).Count()}; ex ours {ours.Except(theirs).FirstOrDefault()} valve {theirs.Except(ours).FirstOrDefault()}");
            }
        }
        output.WriteLine($"{models} render models, {countMismatch} with another mesh count, meshes with the same triangles {meshesSame} of {meshesTotal}");
    }

    /// <summary>
    /// <c>ENTMODELS=&lt;addon&gt;|&lt;map&gt;|&lt;compiled vpk&gt;</c>: each render model
    /// of a top-level brush entity built whole (<see cref="EntityRenderModels"/>,
    /// <see cref="EntityModelAuthor"/>) against Valve's: block trees, buffers, facts.
    /// </summary>
    [Fact]
    public void WholeModels()
    {
        if (Environment.GetEnvironmentVariable("ENTMODELS") is not { Length: > 0 } spec || spec.Split('|') is not [var addon, var map, var vpk])
            return;
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        var doc = DmxBinary.ReadFile(Path.Combine(cs2, "content", "csgo_addons", addon, "maps", map + ".vmap"));
        using var content = new GameContent(Path.Combine(cs2, "game", "csgo", "pak01_dir.vpk"), Path.Combine(cs2, "game", "csgo_addons", addon));
        var schema = MapFixtures.GameSchema();
        var physicsModels = Physics.EntityPhysicsModels.Build(doc, map, content.Material, content.CollisionProperty, schema, null, content.SmartProp)
            .ToDictionary(m => Path.GetFileNameWithoutExtension(m.Path), StringComparer.OrdinalIgnoreCase);
        var byId = doc.Elements.Where(e => e.Type == "CMapEntity" && e.GetValue<int>("nodeID") is not null)
            .GroupBy(e => e.GetValue<int>("nodeID")!.Value).ToDictionary(g => g.Key, g => g.First());
        var facts = new Dictionary<string, WorldNodeModels.MaterialFacts>(StringComparer.OrdinalIgnoreCase);
        WorldNodeModels.MaterialFacts Material(string path)
        {
            if (!facts.TryGetValue(path, out var f))
                facts[path] = f = content.Read(path + "_c") is { } b
                    ? new WorldNodeModels.MaterialFacts(MaterialAuthor.ExtractInputSignature(b), MaterialAuthor.ExtractDepthSignature(b))
                    : new WorldNodeModels.MaterialFacts([], []);
            return f;
        }
        if (Environment.GetEnvironmentVariable("ENTMODELS_MAT") is { Length: > 0 } mats)
        {
            foreach (var m in mats.Split(','))
                if (content.Read(m + "_c") is { } raw)
                    output.WriteLine($"SIG {m}: elems {string.Join(",", MaterialAuthor.ExtractInputSignature(raw).Select(e => e.Semantic))}; depth {string.Join(",", MaterialAuthor.ExtractDepthSignature(raw).Select(e => e.Semantic))}");
            foreach (var m in mats.Split(','))
                if (content.Material(m) is { } info)
                    output.WriteLine($"MAT {m}: shader {info.Shader}; ints {string.Join(" ", info.Ints.Select(kv => $"{kv.Key}={kv.Value}"))}; params {string.Join(" ", info.Params.Select(kv => $"{kv.Key}={kv.Value}"))}");
        }
        using var package = new Package();
        package.Read(vpk);
        int models = 0, treesExact = 0, buffersExact = 0, factsExact = 0;
        var tally = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var examples = new Dictionary<string, string>();
        foreach (var entry in package.Entries!.GetValueOrDefault("vmdl_c") ?? [])
        {
            if (!entry.DirectoryName.EndsWith("entities", StringComparison.OrdinalIgnoreCase))
                continue;
            package.ReadEntry(entry, out var valve);
            using var resource = new Resource();
            resource.Read(new MemoryStream(valve));
            if (resource.Blocks.All(b => b.Type != BlockType.MDAT))
                continue;
            var name = entry.FileName;
            if (!int.TryParse(name[(name.LastIndexOf('_') + 1)..], out var id) || !byId.TryGetValue(id, out var entity) || !physicsModels.TryGetValue(name, out var physics))
                continue;
            models++;
            var meshes = Meshes(entity).ToList();
            var pieces = EntityRenderModels.Pieces(entity, meshes, m => Physics.EntityPhysicsModels.DrawsNothing(content.Material(m)), m => Material(m).Signature,
                shift: Environment.GetEnvironmentVariable("ENTMODELS_SHIFT") != "0");
            var path = $"maps/{map}/entities/{name}.vmdl";
            if (Environment.GetEnvironmentVariable("ENTMODELS_NAMES") == "1")
                foreach (var mm in meshes)
                    output.WriteLine($"NAMES {name} ({physics.ClassName}) mesh {mm.GetValue<int>("nodeID")}: {string.Join(",", (mm.Get<DmxBinary.Element>("meshData")?.Get<object?[]>("materials") ?? []).Select(x => x as string))}");
            if (Environment.GetEnvironmentVariable("ENTMODELS_UV") == "1")
            {
                var embeddedMeshes = ((Model)resource.DataBlock!).GetEmbeddedMeshesAndLoD().Select(m => m.Mesh).ToList();
                for (var i = 0; i < Math.Min(pieces.Count, embeddedMeshes.Count); i++)
                {
                    var uvField = embeddedMeshes[i].VBIB.VertexBuffers[0].InputLayoutFields.FirstOrDefault(f => f.SemanticName == "TEXCOORD");
                    var e = pieces[i].Entry;
                    var at = 0;
                    foreach (var st in e.Facts.Streams)
                    {
                        if (st.Name == "texcoord")
                            break;
                        at += st.Count;
                    }
                    var maxShifted = Enumerable.Range(0, e.Vertices.Length / e.Stride).Max(c => MathF.Max(MathF.Abs(e.Vertices[(c * e.Stride) + at]), MathF.Abs(e.Vertices[(c * e.Stride) + at + 1])));
                    output.WriteLine($"UV {name} {i}: valve {uvField.Format} ours-precise {e.Facts.Streams.FirstOrDefault(x => x.Name == "texcoord")?.Precise} max {maxShifted:R} material {e.Material}");
                }
            }
            var raws = new List<WorldNodeModels.Built>();
            var built = pieces.Select(p =>
            {
                var b = WorldNodeModels.Build(path, [p.Entry], false, Material, mapMesh: false, layout: new WorldNodeModels.MaterialFacts(p.Layout, []));
                raws.Add(b);
                return new EntityModelAuthor.Mesh(p.Name, b.Model with { EntityClass = physics.ClassName }, b.Buffers, b.References);
            }).ToList();
            var physRed2 = Physics.WorldPhysicsTrees.Red2(physics.Physics, content.SurfaceName, physics.ClassName);
            var phys = Physics.WorldPhysicsTrees.Phys(physics.Physics, partFlags: physics.Physics.Meshes.Count > 0 ? 2u : 0u);
            var file = EntityModelAuthor.Container(built, phys, physRed2);
            // Trees block by block in order (MDATs by position).
            List<(string Type, ValveKeyValue.KVObject Tree)> Blocks(byte[] bytes)
            {
                using var r = new Resource();
                r.Read(new MemoryStream(bytes));
                return [.. r.Blocks.Select(b => (b.Type.ToString(), b switch
                {
                    BinaryKV3 k => k.Data.Root,
                    ResourceEditInfo2 e => e.Data?.Root,
                    _ => null,
                })).Where(x => x.Item2 != null).Select(x => (x.Item1, x.Item2!))];
            }
            var (vb, ob) = (Blocks(valve), Blocks(file));
            var lines = new List<string>();
            if (vb.Count != ob.Count)
                lines.Add($"blocks {string.Join(",", vb.Select(x => x.Type))} vs {string.Join(",", ob.Select(x => x.Type))}");
            else
                for (var i = 0; i < vb.Count; i++)
                    lines.AddRange(KvTreeDiff.Diff(vb[i].Tree, ob[i].Tree).Select(l => $"{vb[i].Type}{i}{l}"));
            if (lines.Count > 0)
                output.WriteLine($"TREES {name}: {lines.Count} lines, {string.Join(" | ", lines.Take(3))}");
            if (lines.Count == 0)
                treesExact++;
            foreach (var l in lines)
            {
                var key = System.Text.RegularExpressions.Regex.Replace(l.Split(':')[0], @"\[\d+\]|MDAT\d+|\d+\.", "[]");
                tally[key] = tally.GetValueOrDefault(key) + 1;
                examples.TryAdd(key, $"{name}: {l}");
            }
            var valveBuffers = WorldNodeModelAuthorTests.BufferBlocks(valve);
            var ours = built.SelectMany(m => m.Buffers).ToList();
            if (valveBuffers.Count == ours.Count && valveBuffers.Zip(ours).All(p => p.First.Bytes.AsSpan().SequenceEqual(p.Second.Bytes)))
                buffersExact++;
            else
            {
                tally["buffers"] = tally.GetValueOrDefault("buffers") + 1;
                var vm = ((Model)resource.DataBlock!).GetEmbeddedMeshesAndLoD().Select(m => m.Mesh).ToList();
                for (var i = 0; i < Math.Min(vm.Count, raws.Count); i++)
                {
                    var vbuf = vm[i].VBIB.VertexBuffers[0];
                    var (vd, od, st) = (vbuf.Data, raws[i].RawVertexBuffers[0], (int)vbuf.ElementSizeInBytes);
                    var diffs = Enumerable.Range(0, Math.Min(vd.Length, od.Length)).Where(x => vd[x] != od[x])
                        .GroupBy(x => { var f = vbuf.InputLayoutFields.Last(f => f.Offset <= x % st); return f.SemanticName + f.SemanticIndex; }).Select(g => $"{g.Key}:{g.Count()}").ToList();
                    var vi = vm[i].VBIB.IndexBuffers[0].Data;
                    var oi = raws[i].Model.SceneObjects[0].Draws.Count > 0 ? raws[i].Buffers[^1].Bytes : [];
                    if (diffs.Count > 0 || vd.Length != od.Length)
                        output.WriteLine($"BUF {name} {i}: vb {vd.Length}/{od.Length} {string.Join(" ", diffs)} fmt {string.Join(",", vbuf.InputLayoutFields.Select(f => $"{f.SemanticName}{f.SemanticIndex}:{(int)f.Format}"))} first {(Enumerable.Range(0, Math.Min(vd.Length, od.Length)).FirstOrDefault(x => vd[x] != od[x], -1) is var fx and >= 0 ? $"v{fx / st} valve {Convert.ToHexString(vd, fx / st * st, st)} ours {Convert.ToHexString(od, fx / st * st, st)}" : "")}");
                    var uvf = vbuf.InputLayoutFields.FirstOrDefault(f => f.SemanticName == "TEXCOORD" && f.SemanticIndex == 0);
                    if (diffs.Count > 0 && (int)uvf.Format is 37 or 34 && vd.Length == od.Length)
                    {
                        var e = pieces[i].Entry;
                        for (var v = 0; v < vd.Length / st; v++)
                        {
                            var (va, oa) = (BitConverter.ToInt16(vd, v * st + (int)uvf.Offset), BitConverter.ToInt16(od, v * st + (int)uvf.Offset));
                            if (va != oa)
                            {
                                output.WriteLine($"UVQ fmt {(int)uvf.Format} valve {va:x4} ours {oa:x4} ({(float)BitConverter.Int16BitsToHalf(va)} {(float)BitConverter.Int16BitsToHalf(oa)}) source {e.Vertices[0]}");
                                break;
                            }
                        }
                    }
                }
            }
            static string[] F(byte[] b) => WorldPhysicsAuthorTests.Facts(b).Split(' ').Select(x => x.StartsWith("RERL", StringComparison.Ordinal) ? "RERL" : x).ToArray();
            if (F(file).SequenceEqual(F(valve)))
                factsExact++;
        }
        output.WriteLine($"{models} render models: trees exact {treesExact}, buffers exact {buffersExact}, container facts {factsExact}");
        foreach (var (k, v) in tally)
            output.WriteLine($"{v,5} {examples.GetValueOrDefault(k, k)}");
    }

    /// <summary>
    /// <c>ENTALL=&lt;addon&gt;|&lt;map&gt;|&lt;compiled vpk&gt;</c>: every brush entity model
    /// the compile writes (MapCompile's path: physics only, or with render meshes)
    /// against Valve's file: block trees in order and container facts.
    /// </summary>
    [Fact]
    public void EveryEntityModel()
    {
        if (Environment.GetEnvironmentVariable("ENTALL") is not { Length: > 0 } spec || spec.Split('|') is not [var addon, var map, var vpk])
            return;
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        var doc = DmxBinary.ReadFile(Path.Combine(cs2, "content", "csgo_addons", addon, "maps", map + ".vmap"));
        MapPrefabs.Attach(doc, MapPrefabs.FromContent(Path.Combine(cs2, "content", "csgo_addons", addon)));
        MapDeformers.Apply(doc);
        using var content = new GameContent(Path.Combine(cs2, "game", "csgo", "pak01_dir.vpk"), Path.Combine(cs2, "game", "csgo_addons", addon));
        var models = Physics.EntityPhysicsModels.Build(doc, map, content.Material, content.CollisionProperty, MapFixtures.GameSchema(), null, content.SmartProp);
        using var package = new Package();
        package.Read(vpk);
        int render = 0, renderExact = 0, physics = 0, physicsExact = 0, missing = 0;
        foreach (var m in models)
        {
            var entry = package.FindEntry(m.Path + "_c");
            if (entry == null)
            {
                missing++;
                continue;
            }
            package.ReadEntry(entry, out var valve);
            var ours = m.PhysicsOnly ? Physics.EntityPhysicsModels.Author(m, content.SurfaceName) : EntityRenderModels.Author(m, content);
            List<(string Type, ValveKeyValue.KVObject Tree)> Blocks(byte[] bytes)
            {
                using var r = new Resource();
                r.Read(new MemoryStream(bytes));
                return [.. r.Blocks.Select(b => (b.Type.ToString(), b switch
                {
                    BinaryKV3 k => k.Data.Root,
                    ResourceEditInfo2 e => e.Data?.Root,
                    _ => null,
                })).Where(x => x.Item2 != null).Select(x => (x.Item1, x.Item2!))];
            }
            var (vb, ob) = (Blocks(valve), Blocks(ours));
            var same = vb.Count == ob.Count && vb.Zip(ob).All(p => p.First.Type == p.Second.Type && !KvTreeDiff.Diff(p.First.Tree, p.Second.Tree).Any());
            static string[] F(byte[] b) => WorldPhysicsAuthorTests.Facts(b).Split(' ').Select(x => x.StartsWith("RERL", StringComparison.Ordinal) ? "RERL" : x).ToArray();
            same &= F(ours).SequenceEqual(F(valve));
            if (m.PhysicsOnly)
            {
                physics++;
                physicsExact += same ? 1 : 0;
            }
            else
            {
                render++;
                renderExact += same ? 1 : 0;
            }
            if (!same)
                output.WriteLine($"{m.Path} ({(m.PhysicsOnly ? "physics" : "render")}) differs");
        }
        output.WriteLine($"{map}: render models exact {renderExact} of {render}, physics-only exact {physicsExact} of {physics}, {missing} not in the package");
    }

    private static IEnumerable<DmxBinary.Element> Meshes(DmxBinary.Element node)
    {
        foreach (var c in node.GetElements("children"))
        {
            if (c.Type == "CMapMesh")
                yield return c;
            else
                foreach (var m in Meshes(c))
                    yield return m;
        }
    }

    private static List<string> Triangles(ValveResourceFormat.ResourceTypes.Mesh mesh)
    {
        var tris = new List<string>();
        foreach (var so in mesh.Data.GetArray("m_sceneObjects"))
            foreach (var dc in so.GetArray("m_drawCalls"))
            {
                var vb = mesh.VBIB.VertexBuffers[dc.GetArray("m_vertexBuffers")[0].GetInt32Property("m_hBuffer")];
                var pos = VBIB.GetVector3AttributeArray(vb, vb.InputLayoutFields.First(f => f.SemanticName is "POSITION" or "position"));
                var ib = mesh.VBIB.IndexBuffers[dc.GetSubCollection("m_indexBuffer").GetInt32Property("m_hBuffer")];
                var start = dc.GetInt32Property("m_nStartIndex");
                var n = dc.GetInt32Property("m_nIndexCount");
                var bas = dc.GetInt32Property("m_nBaseVertex");
                int I(int at) => (ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, at * 2) : BitConverter.ToInt32(ib.Data, at * 4)) + bas;
                for (var i = 0; i + 2 < n; i += 3)
                    tris.Add(Key(pos[I(start + i)], pos[I(start + i + 1)], pos[I(start + i + 2)]));
            }
        return tris;
    }

    private static string Key(Vector3 a, Vector3 b, Vector3 c)
        => string.Join("|", new[] { a, b, c }.Select(v => $"{v.X:R},{v.Y:R},{v.Z:R}").Order(StringComparer.Ordinal));
}
