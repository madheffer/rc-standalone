using System.Numerics;
using System.Buffers.Binary;
using ValveKeyValue;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="WorldPhysicsAuthor"/> against Valve's own world_physics.vmdl_c.
/// <c>WPAUTHOR=&lt;compiled .vpk&gt;|&lt;map&gt;</c> re-authors the file from
/// its own decoded trees: the container facts and every block's decoded tree
/// must come back the same, which pins the container before its trees are
/// built from the map.
/// </summary>
public class WorldPhysicsAuthorTests(ITestOutputHelper output)
{
    // Mako's first soup: six " [Wood]" names reach 52 characters (long), the
    // next non-empty name adds "; ..." and seals it, and later ones add nothing.
    [Fact]
    public void SoupNameSealsAfterFiftyCharacters()
    {
        var (name, isSealed, isLong) = Physics.WorldPhysics.SoupName([.. Enumerable.Repeat(" [Wood]", 9), "", " [brick]"]);
        Assert.Equal(" [Wood];  [Wood];  [Wood];  [Wood];  [Wood];  [Wood]; ...", name);
        Assert.True(isSealed);
        Assert.True(isLong);

        // Empty names are skipped after the first; short joins stay open.
        Assert.Equal((" [wet];  [concrete_block]", false, false), Physics.WorldPhysics.SoupName(["", " [wet]", "", " [concrete_block]"]));
    }

    [Fact]
    public void ReauthorsValvesTrees()
    {
        if (Environment.GetEnvironmentVariable("WPAUTHOR") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        var valve = Read(p[0], $"maps/{p[1]}/world_physics.vmdl_c");
        var trees = Trees(valve);
        var mine = WorldPhysicsAuthor.Container(trees["PHYS"], trees["RED2"], trees["DATA"]);
        var report = Compare(valve, mine);
        output.WriteLine($"valve {Facts(valve)}");
        output.WriteLine($"mine  {Facts(mine)}");
        output.WriteLine($"trees: {string.Join(", ", trees.Select(t => $"{t.Key} {KvTreeDiff.Typed(t.Value, "", 0, int.MaxValue).Count()} lines"))}");
        foreach (var line in report.Take(60))
            output.WriteLine(line);
        Assert.Empty(report);
    }

    /// <summary>
    /// The file built from the map (<see cref="Physics.WorldPhysics"/>) against
    /// Valve's: <c>WPBUILD=&lt;addon&gt;|&lt;map&gt;|&lt;compiled .vpk&gt;</c>,
    /// <c>WPBUILD_SHOW</c> caps the listed differences per block.
    /// </summary>
    [Fact]
    public void BuildsFromTheMap()
    {
        if (Environment.GetEnvironmentVariable("WPBUILD") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        var show = int.TryParse(Environment.GetEnvironmentVariable("WPBUILD_SHOW"), out var n) ? n : 25;
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        var game = Path.Combine(cs2, "game");
        using var models = new SettleBuildTests.PakModels(Path.Combine(game, "csgo", "pak01_dir.vpk"), Path.Combine(game, "csgo_addons", p[0]));
        var doc = DmxBinary.ReadFile(Path.Combine(cs2, "content", "csgo_addons", p[0], "maps", p[1] + ".vmap"));
        Maps.MapPrefabs.Attach(doc, Maps.MapPrefabs.FromContent(Path.Combine(cs2, "content", "csgo_addons", p[0])));
        Maps.MapDeformers.Apply(doc);
        var notes = new List<string>();
        // WPBUILD_GPU=1: new-blending materials take their layers from the GPU sampler.
        using var gpu = Environment.GetEnvironmentVariable("WPBUILD_GPU") == "1"
            ? new Source2.Compiler.Gpu.GpuMaterialSampler(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"), models.Read)
            : null;
        if (Environment.GetEnvironmentVariable("WPBUILD_HULLIDX") is { Length: > 0 } idxText)
        {
            var pieces = Physics.WorldCollision.Pieces(doc, name => Physics.WorldCollision.ReadMaterial(name, models.Material, models.CollisionProperty),
                null, gpu == null ? null : gpu.For, models.Physics, models.SmartProp, models.CollisionProperty);
            var hullPieces = Physics.WorldCollision.PartOrder(pieces, x => x.Type).Where(x => x.Type == Physics.WorldCollision.HullType).ToList();
            foreach (var i in idxText.Split(',').Select(int.Parse))
                output.WriteLine($"hullidx {i}: node {hullPieces[i].NodeId} {hullPieces[i].MaterialName} verts {hullPieces[i].Points.Length}");
            return;
        }
        if (Environment.GetEnvironmentVariable("WPBUILD_NODEATTRS") is { Length: > 0 } attrNode)
        {
            IEnumerable<DmxBinary.Element> All(DmxBinary.Document d) => d.Elements.Concat(d.Elements
                .Select(e => e.Attributes.GetValueOrDefault(Maps.MapPrefabs.DocumentKey)).OfType<DmxBinary.Document>().SelectMany(All));
            var el = All(doc).First(e => e.GetValue<int>("nodeID")?.ToString() == attrNode);
            foreach (var (k, v) in el.Attributes)
                output.WriteLine($"nodeattr {k} = {(v is DmxBinary.Element ce ? ce.Type : v is object?[] arr ? $"[{arr.Length}]" : v)}");
            var md = el.Get<DmxBinary.Element>("meshData");
            output.WriteLine("nodeattr materials: " + string.Join(", ", md?.Get<object?[]>("materials") ?? []));
            foreach (var part in new[] { "vertexData", "faceVertexData", "edgeData", "faceData" })
                output.WriteLine($"nodeattr {part} streams: " + string.Join(", ", md?.Get<DmxBinary.Element>(part)?.GetElements("streams").Select(x => x.Name) ?? []));
            var fm = md?.Get<DmxBinary.Element>("faceData")?.GetElements("streams").FirstOrDefault(x => x.Name.StartsWith("materialindex", StringComparison.Ordinal))?.Get<object?[]>("data") ?? [];
            output.WriteLine("nodeattr face materials: " + string.Join(",", fm.GroupBy(x => x).Select(g => $"{g.Key}x{g.Count()}")));
            return;
        }
        // WPBUILD_SHAPECAP=<capture_physshapes.py json>: the world part's shapes as
        // Valve's gatherer saw them, against our pieces in part order: type,
        // vertex and index counts and the first vertex, the first differences.
        if (Environment.GetEnvironmentVariable("WPBUILD_SHAPECAP") is { Length: > 0 } capPath2)
        {
            var pieces = Physics.WorldCollision.Pieces(doc, name => Physics.WorldCollision.ReadMaterial(name, models.Material, models.CollisionProperty),
                null, gpu == null ? null : gpu.For, models.Physics, models.SmartProp, models.CollisionProperty);
            var ordered = Physics.WorldCollision.PartOrder(pieces, x => x.Type);
            var calls = System.Text.Json.JsonDocument.Parse(File.ReadAllText(capPath2)).RootElement.EnumerateArray()
                .Where(c => c.TryGetProperty("shapes", out _)).ToList();
            var world = calls.MaxBy(c => c.GetProperty("count").GetInt32());
            var shapes = world.GetProperty("shapes").EnumerateArray().ToList();
            output.WriteLine($"shapecap: Valve {shapes.Count} shapes ({shapes.Count(s => s.GetProperty("type").GetInt32() == 3)} meshes), ours {ordered.Count} ({ordered.Count(p => p.Type == Physics.WorldCollision.MeshType)} meshes)");
            int shown2 = 0, differing = 0;
            for (var i = 0; i < Math.Min(shapes.Count, ordered.Count); i++)
            {
                var s = shapes[i];
                var p2 = ordered[i];
                var type = s.GetProperty("type").GetInt32();
                string ourShape = p2.Type == Physics.WorldCollision.MeshType ? $"type {p2.Type} v {p2.Points.Length} i {p2.Indices.Length} v0 {p2.Points.FirstOrDefault()}" : $"type {p2.Type}";
                string theirs = type == 3 ? $"type 3 v {s.GetProperty("vertices").GetInt32()} i {s.GetProperty("indices").GetInt32()} v0 "
                    + (s.TryGetProperty("v0", out var v0) ? $"<{string.Join(", ", v0.EnumerateArray().Select(x => x.GetSingle()))}>" : $"? ({(s.TryGetProperty("err", out var err) ? err.GetString() : "")})") : $"type {type}";
                if (ourShape == theirs)
                    continue;
                differing++;
                if (shown2++ < 25)
                    output.WriteLine($"  shape {i}: valve {theirs} | ours {ourShape} node {p2.NodeId} {p2.MaterialName}");
                // With --dump: the first such mesh's triangles only one side has.
                if (shown2 == 2 && type == 3 && s.TryGetProperty("vdata", out var vd) && s.TryGetProperty("idata", out var id))
                {
                    var vb = Convert.FromHexString(vd.GetString()!);
                    var ib = Convert.FromHexString(id.GetString()!);
                    Vector3 VP(int k) => new(BitConverter.ToSingle(vb, k * 12), BitConverter.ToSingle(vb, k * 12 + 4), BitConverter.ToSingle(vb, k * 12 + 8));
                    static string Tri(Vector3 a, Vector3 b, Vector3 c) => string.Join(" | ", new[] { a, b, c }.Select(v => $"{v.X:R},{v.Y:R},{v.Z:R}").Order(StringComparer.Ordinal));
                    var valveTris = Enumerable.Range(0, ib.Length / 12).Select(t => Tri(VP(BitConverter.ToInt32(ib, t * 12)), VP(BitConverter.ToInt32(ib, t * 12 + 4)), VP(BitConverter.ToInt32(ib, t * 12 + 8)))).ToList();
                    var ourTris = Enumerable.Range(0, p2.Indices.Length / 3).Select(t => Tri(p2.Points[p2.Indices[t * 3]], p2.Points[p2.Indices[t * 3 + 1]], p2.Points[p2.Indices[t * 3 + 2]])).ToList();
                    var valveSet = valveTris.ToHashSet();
                    var ourSet = ourTris.ToHashSet();
                    // Whether a triangle repeats a directed edge an earlier one used, or
                    // whether an earlier one used its edge reversed twice already.
                    var directed = new Dictionary<(int, int), int>();
                    var repeats = new bool[ourTris.Count];
                    for (var t = 0; t < ourTris.Count; t++)
                    {
                        int a0 = p2.Indices[t * 3], b0 = p2.Indices[t * 3 + 1], c0 = p2.Indices[t * 3 + 2];
                        foreach (var e in new[] { (a0, b0), (b0, c0), (c0, a0) })
                        {
                            if (directed.ContainsKey(e))
                                repeats[t] = true;
                            directed[e] = t;
                        }
                    }
                    output.WriteLine($"    triangles repeating a directed edge: {string.Join(",", Enumerable.Range(0, ourTris.Count).Where(t => repeats[t]))}");
                    for (var t = 0; t < ourTris.Count; t++)
                        if (!valveSet.Contains(ourTris[t]))
                            output.WriteLine($"    ours only tri {t}: {ourTris[t]} indices {p2.Indices[t * 3]},{p2.Indices[t * 3 + 1]},{p2.Indices[t * 3 + 2]}");
                    foreach (var t in valveTris.Where(x => !ourSet.Contains(x)).Take(10))
                        output.WriteLine($"    valve only tri: {t}");
                }
            }
            output.WriteLine($"shapecap: {differing} shapes differ");
            return;
        }
        // WPBUILD_LISTED=1: every piece in the part's order (rc 180c28150) with its
        // material and surface property, beside RED2's surface_prop list, which
        // CompilePhysics (18032e3e0) fills from each part's shapes' +0xf0 in order.
        if (Environment.GetEnvironmentVariable("WPBUILD_LISTED") == "1")
        {
            var pieces = Physics.WorldCollision.Pieces(doc, name => Physics.WorldCollision.ReadMaterial(name, models.Material, models.CollisionProperty),
                null, gpu == null ? null : gpu.For, models.Physics, models.SmartProp, models.CollisionProperty);
            var ordered = Physics.WorldCollision.PartOrder(pieces, x => x.Type);
            string? last = null;
            for (var i = 0; i < ordered.Count; i++)
            {
                var x = ordered[i];
                var line = $"type {x.Type} {x.MaterialName} missing {x.MaterialName.EndsWith(".vmat", StringComparison.OrdinalIgnoreCase) && models.Material(x.MaterialName) == null} surface '{x.Physics.SurfaceProperty}' hash {x.Physics.SurfaceHash}";
                if (line != last)
                    output.WriteLine($"piece {i} node {x.NodeId} {line}");
                last = line;
            }
            using var red = new ValveResourceFormat.Resource();
            red.Read(new MemoryStream(Read(p[2], $"maps/{p[1]}/world_physics.vmdl_c")));
            output.WriteLine("valve RED2: " + (red.EditInfo?.ToString() ?? "none"));
            return;
        }
        if (Environment.GetEnvironmentVariable("WPBUILD_HULLSURF") is { Length: > 0 } nodesText)
        {
            var wanted = nodesText.Split(',').Select(int.Parse).ToHashSet();
            var pieces = Physics.WorldCollision.Pieces(doc, name => Physics.WorldCollision.ReadMaterial(name, models.Material, models.CollisionProperty),
                null, gpu == null ? null : gpu.For, models.Physics, models.SmartProp, models.CollisionProperty);
            using var res = new ValveResourceFormat.Resource();
            res.Read(new MemoryStream(Read(p[2], $"maps/{p[1]}/world_physics.vmdl_c")));
            var phys = ((ValveResourceFormat.ResourceTypes.Model)res.DataBlock!).GetEmbeddedPhys()!;
            var hulls = phys.Parts[0].Shape.Hulls;
            foreach (var piece in pieces.Where(x => x.Hull != null && wanted.Contains(x.NodeId)))
            {
                var set = piece.Hull!.VertexPositions.ToHashSet();
                var at = Array.FindIndex(hulls, h => h.Shape.GetVertexPositions().ToArray() is { } v && v.Length == set.Count && v.All(set.Contains));
                output.WriteLine($"hullsurf node {piece.NodeId} {piece.MaterialName} ours '{piece.Physics.SurfaceProperty}' -> valve hull {at} surface {(at >= 0 ? phys.SurfacePropertyHashes[hulls[at].SurfacePropertyIndex] : 0)} attr {(at >= 0 ? hulls[at].CollisionAttributeIndex : -1)}");
            }
            return;
        }
        if (Environment.GetEnvironmentVariable("WPBUILD_WORLDS") == "1")
        {
            var worlds = doc.OfType("CMapWorld").ToList();
            foreach (var w in worlds)
            {
                var holders = doc.Elements.Where(e => e.Attributes.Any(kv => ReferenceEquals(kv.Value, w) || (kv.Value is object?[] arr && arr.Any(x => ReferenceEquals(x, w)))))
                    .Select(e => $"{e.Type}#{e.GetValue<int>("nodeID")} {e.Get<string>("targetMapPath") ?? e.Get<string>("name") ?? ""}");
                output.WriteLine($"world #{w.GetValue<int>("nodeID")} children {w.GetElements("children").Count()} held by {string.Join("; ", holders)}");
            }
            return;
        }
        if (Environment.GetEnvironmentVariable("WPBUILD_PARENTS") == "1")
        {
            var (meshes, _) = Maps.MapMeshes.ReadWithEntities(doc);
            foreach (var g in meshes.GroupBy(m => $"{m.ParentType} instances {(m.Instances.Length > 0 ? "yes" : "no")} physics {m.Element?.Get<string>("physicsType") ?? "?"}").OrderByDescending(g => g.Count()))
                output.WriteLine($"parents: {g.Count(),6} {g.Key}");
            return;
        }
        // WPBUILD_NESTED=1: entity nodes reached through an instance inside a prefab, with their class and solid key.
        if (Environment.GetEnvironmentVariable("WPBUILD_NESTED") == "1")
        {
            var (_, nested) = Maps.MapMeshes.ReadWithEntities(doc);
            foreach (var en in nested.Where(n => n.Through.Count > 0 && n.PrefabChain.Count > 0))
            {
                var keys = en.Element.Get<DmxBinary.Element>("entity_properties");
                output.WriteLine($"nested {en.Element.GetValue<int>("nodeID")}: {keys?.Get<string>("classname")} solid {keys?.Get<string>("solid")} model {keys?.Get<string>("model")}");
            }
            return;
        }
        // WPBUILD_MESHINFO=<node,...>: each mesh node's parent, instances and physics type.
        if (Environment.GetEnvironmentVariable("WPBUILD_MESHINFO") is { Length: > 0 } infoText)
        {
            var wanted = infoText.Split(',').Select(int.Parse).ToHashSet();
            var (meshes, _) = Maps.MapMeshes.ReadWithEntities(doc);
            foreach (var m in meshes.Where(m => wanted.Contains(m.NodeId)))
                output.WriteLine($"meshinfo {m.NodeId}: {m.ParentType}/{m.ParentClass} prefabs [{string.Join(",", m.Prefabs)}] world [{string.Join(" ", m.World.Select(x => x.ToString("R")))}] instances [{string.Join(",", m.Instances)}] physics {m.Element?.Get<string>("physicsType")} origin {m.Origin} angles {m.Angles} scales {m.Scales}");
            foreach (var m in meshes.Where(m => wanted.Contains(m.NodeId)).DistinctBy(m => m.Element))
            {
                var md = m.Element!.Get<DmxBinary.Element>("meshData");
                foreach (var part in new[] { "faceData", "edgeData", "vertexData" })
                    foreach (var st in md?.Get<DmxBinary.Element>(part)?.GetElements("streams").Where(x => x.Name.StartsWith("flags", StringComparison.Ordinal)) ?? [])
                        output.WriteLine($"meshinfo {m.NodeId} {part} {st.Name}: " + string.Join(", ", (st.Get<object?[]>("data") ?? []).GroupBy(x => x?.ToString()).Select(g => $"{g.Key}x{g.Count()}")));
            }
            foreach (var m in meshes.Where(m => wanted.Contains(m.NodeId)).DistinctBy(m => m.Element))
                foreach (var piece in Physics.BrushHulls.PiecesWithCorners(m.Element!, doc.OfType("CMapWorld").First()))
                {
                    var (pts, tris) = Physics.BrushHulls.TriangleMesh(piece.Positions, piece.Faces, piece.Local, piece.CornerIds);
                    output.WriteLine($"meshinfo {m.NodeId} material {piece.Material}: faces {piece.Faces.Length} sizes {string.Join(",", piece.Faces.GroupBy(f => f.Length).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}"))} positions {piece.Positions.Length} points {pts.Count} triangles {tris.Count}");
                }
            return;
        }
        if (Environment.GetEnvironmentVariable("WPBUILD_FINDHASH") is { Length: > 0 } find)
        {
            var built = Physics.WorldPhysics.Build(Physics.WorldCollision.Pieces(doc, name => Physics.WorldCollision.ReadMaterial(name, models.Material, models.CollisionProperty),
                null, gpu == null ? null : gpu.For, models.Physics, models.SmartProp, models.CollisionProperty), models.SurfaceName);
            foreach (var source in built.SurfaceSources.Where(x => x.Contains(find, StringComparison.Ordinal)))
                output.WriteLine($"found: {source}");
            return;
        }
        var files = Physics.WorldPhysicsFiles.Build(doc, p[0], p[1], models, gpu == null ? null : gpu.For, notes);
        var mine = files.Model;
        var valve = Read(p[2], files.ModelPath);
        // WPBUILD_SAVE=<path>: our world_physics.vmdl_c, for inspection.
        if (Environment.GetEnvironmentVariable("WPBUILD_SAVE") is { Length: > 0 } savePath)
            File.WriteAllBytes(savePath, mine);
        // WPBUILD_CONVEXCAP=<capture_convex.py json>: each convex_single mesh's
        // captured hull input (the CMesh vertex buffer, in order) against ours.
        if (Environment.GetEnvironmentVariable("WPBUILD_CONVEXCAP") is { Length: > 0 } capPath)
        {
            using var capDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(capPath));
            var captured = new List<(string Material, Vector3[] Points)>();
            var list = capDoc.RootElement.EnumerateArray().ToList();
            for (var i = 0; i < list.Count; i++)
            {
                if (!list[i].TryGetProperty("convex", out var kind) || kind.GetString() != "single" || !list[i].TryGetProperty("mesh", out var m))
                    continue;
                var stride = m.GetProperty("stride").GetInt32();
                var bytes = Convert.FromHexString(m.TryGetProperty("vdata", out var vd) ? vd.GetString()! : "");
                var pts = new Vector3[m.GetProperty("vertices").GetInt32()];
                for (var k = 0; k < pts.Length; k++)
                    pts[k] = new Vector3(BitConverter.ToSingle(bytes, k * stride * 4), BitConverter.ToSingle(bytes, (k * stride * 4) + 4), BitConverter.ToSingle(bytes, (k * stride * 4) + 8));
                captured.Add((m.GetProperty("material").GetString() ?? "", pts));
            }
            var (meshes, _) = Maps.MapMeshes.ReadWithEntities(doc);
            var world = doc.OfType("CMapWorld").First();
            int exact = 0, sameSet = 0, none = 0;
            foreach (var mesh in meshes.Where(x => x.Element?.Get<string>("physicsType") == "convex_single").DistinctBy(x => (x.NodeId, string.Join(",", x.Instances))))
            {
                var names = mesh.Element!.Get<DmxBinary.Element>("meshData")?.Get<object?[]>("materials") ?? [];
                foreach (var piece in Physics.BrushHulls.PiecesWithCorners(mesh.Element!, world, mesh.Instances.Length > 0 ? mesh.Path : null))
                {
                    var name = piece.Material < names.Length ? names[piece.Material] as string ?? "" : "";
                    var ours = Physics.BrushHulls.TriangleMesh(piece.Positions, piece.Faces, piece.Local, piece.CornerIds).Points.ToArray();
                    var set = ours.ToHashSet();
                    var hit = captured.FindIndex(c => c.Material == name && c.Points.ToHashSet().SetEquals(set));
                    if (hit < 0)
                    {
                        none++;
                        output.WriteLine($"convexcap node {mesh.NodeId} [{string.Join(",", mesh.Instances)}] {name}: no captured input with our {ours.Length} points ({set.Count} distinct)");
                        continue;
                    }
                    var theirs = captured[hit].Points;
                    if (theirs.SequenceEqual(ours))
                        exact++;
                    else
                    {
                        sameSet++;
                        var weld = piece.Positions;
                        output.WriteLine($"convexcap node {mesh.NodeId} [{string.Join(",", mesh.Instances)}] {name}: ours {ours.Length} theirs {theirs.Length} weld {weld.Length}; weld order {(weld.SequenceEqual(theirs) ? "SAME" : "differs")}");
                    }
                }
            }
            output.WriteLine($"convexcap: exact {exact}, same set other order {sameSet}, unmatched {none}; captured {captured.Count}");
            return;
        }
        // WPBUILD_MESHCMP=<soup index>: the soup's vertices, Valve's against ours,
        // around the first difference, with the node each of ours comes from.
        if (Environment.GetEnvironmentVariable("WPBUILD_MESHCMP") is { Length: > 0 } soupText)
        {
            var soup = int.Parse(soupText, System.Globalization.CultureInfo.InvariantCulture);
            Vector3[] VerticesOf(byte[] bytes)
            {
                var blob = Trees(bytes)["PHYS"]["m_parts"]![0]!["m_rnShape"]!["m_meshes"]![soup]!["m_Mesh"]!["m_Vertices"]!.AsBlob();
                return [.. System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Vector3>(blob).ToArray()];
            }
            var theirs = VerticesOf(valve);
            var ours = VerticesOf(mine);
            var at = Enumerable.Range(0, Math.Min(theirs.Length, ours.Length)).FirstOrDefault(i => theirs[i] != ours[i], -1);
            var owner = new Dictionary<Vector3, string>();
            foreach (var piece in Physics.WorldCollision.Pieces(doc, name => Physics.WorldCollision.ReadMaterial(name, models.Material, models.CollisionProperty),
                         null, gpu == null ? null : gpu.For, models.Physics, models.SmartProp, models.CollisionProperty))
                foreach (var pt in piece.Points)
                    owner.TryAdd(pt, $"{piece.NodeId}/{Path.GetFileNameWithoutExtension(piece.MaterialName)}");
            output.WriteLine($"meshcmp soup {soup}: {theirs.Length} vs {ours.Length} vertices, first difference {at}");
            for (var i = Math.Max(0, at - 3); i < Math.Min(ours.Length, at + 12) && at >= 0; i++)
                output.WriteLine($"meshcmp {i}: theirs {theirs[i]:R} ({owner.GetValueOrDefault(theirs[i], "?")}) ours {ours[i]:R} ({owner.GetValueOrDefault(ours[i], "?")})");
            int[] TrianglesOf(byte[] bytes)
            {
                var blob = Trees(bytes)["PHYS"]["m_parts"]![0]!["m_rnShape"]!["m_meshes"]![soup]!["m_Mesh"]!["m_Triangles"]!.AsBlob();
                return [.. System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(blob).ToArray()];
            }
            if (at >= 0)
            {
                foreach (var (label, tris, verts) in new[] { ("theirs", TrianglesOf(valve), theirs), ("ours", TrianglesOf(mine), ours) })
                {
                    var lines = new List<string>();
                    for (var t = 0; t < tris.Length / 3 && lines.Count < 14; t++)
                        if (Enumerable.Range(0, 3).Any(k => tris[(t * 3) + k] >= at - 3 && tris[(t * 3) + k] < at + 5))
                            lines.Add($"t{t}: " + string.Join(" ", Enumerable.Range(0, 3).Select(k => verts[tris[(t * 3) + k]].ToString("F1", null))));
                    foreach (var l in lines)
                        output.WriteLine($"meshcmp {label} {l}");
                }
            }
            var missing = theirs.Except(ours).ToList();
            var extra = ours.Except(theirs).ToList();
            output.WriteLine($"meshcmp positions only theirs: {missing.Count}, only ours: {extra.Count}");
            foreach (var v in missing.Take(10))
                output.WriteLine($"meshcmp  theirs-only {v:R} near ours {ours.MinBy(o => Vector3.DistanceSquared(o, v)):R} ({owner.GetValueOrDefault(ours.MinBy(o => Vector3.DistanceSquared(o, v)), "?")})");
            return;
        }
        // WPBUILD_PRINT=<key>[,<key>...]: those PHYS keys (and the part's shape lists) printed for both.
        if (Environment.GetEnvironmentVariable("WPBUILD_PRINT") is { Length: > 0 } printKeys)
        {
            foreach (var (label, bytes) in new[] { ("valve", valve), ("ours", mine) })
            {
                var phys = Trees(bytes)["PHYS"];
                foreach (var key in printKeys.Split(','))
                {
                    KVObject? node = phys;
                    foreach (var part in key.Split('.'))
                        node = node == null ? null : int.TryParse(part, out var ix) ? node[ix] : node[part];
                    var text = node == null ? "(absent)" : Kv3Text(node);
                    output.WriteLine($"print {label} {key}: {(text.Length > 3000 ? text[..3000] + "..." : text)}");
                }
            }
            return;
        }
        // WPBUILD_SOUPS=1: each mesh soup's name, surface index and triangle count, Valve's and ours.
        if (Environment.GetEnvironmentVariable("WPBUILD_SOUPS") == "1")
        {
            foreach (var (label, bytes) in new[] { ("valve", valve), ("ours", mine) })
            {
                var meshes = Trees(bytes)["PHYS"]["m_parts"]![0]!["m_rnShape"]!["m_meshes"]!;
                var surfaces = Trees(bytes)["PHYS"]["m_surfacePropertyHashes"]!;
                var rows = new List<string>();
                for (var i = 0; i < meshes.Count; i++)
                {
                    var m = meshes[i]!;
                    var tris = m["m_Mesh"]!["m_Triangles"]!.AsBlob().Length / 12;
                    var name = m["m_UserFriendlyName"]?.ToString() ?? "";
                    rows.Add($"{(name.Length > 60 ? name[..60] + "..." : name)} | surface {m["m_nSurfacePropertyIndex"]} | attr {m["m_nCollisionAttributeIndex"]} | {tris} tris");
                }
                output.WriteLine($"soups {label}: {meshes.Count}");
                if (Environment.GetEnvironmentVariable("WPBUILD_FINDPT") is { Length: > 0 } pt)
                {
                    var c = pt.Split(',').Select(float.Parse).ToArray();
                    var want = new Vector3(c[0], c[1], c[2]);
                    for (var i = 0; i < meshes.Count; i++)
                    {
                        var verts = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Vector3>(meshes[i]!["m_Mesh"]!["m_Vertices"]!.AsBlob()).ToArray();
                        var best = verts.Select((v, k) => (D: Vector3.Distance(v, want), K: k)).MinBy(x => x.D);
                        output.WriteLine($"  {label} findpt soup {i}: nearest {verts[best.K]:R} at {best.D}");
                    }
                }
                foreach (var g in rows.GroupBy(r => r).OrderByDescending(g => g.Count()).Take(25))
                    output.WriteLine($"  {label} {g.Count()}x {g.Key}");
            }
            return;
        }
        // WPBUILD_TRICMP=<soup>: triangles (as sorted corner triples) only one
        // side has, with the piece (node/material) owning their corners on ours.
        if (Environment.GetEnvironmentVariable("WPBUILD_TRICMP") is { Length: > 0 } triText)
        {
            var soup = int.Parse(triText, System.Globalization.CultureInfo.InvariantCulture);
            List<string> Keys(byte[] bytes)
            {
                var mesh = Trees(bytes)["PHYS"]["m_parts"]![0]!["m_rnShape"]!["m_meshes"]![soup]!["m_Mesh"]!;
                var v = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Vector3>(mesh["m_Vertices"]!.AsBlob()).ToArray();
                var t = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(mesh["m_Triangles"]!.AsBlob()).ToArray();
                return [.. Enumerable.Range(0, t.Length / 3).Select(i => string.Join(";", new[] { v[t[i * 3]], v[t[(i * 3) + 1]], v[t[(i * 3) + 2]] }.Select(x => x.ToString("R", null)).Order(StringComparer.Ordinal)))];
            }
            var theirs = Keys(valve).GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
            var ours = Keys(mine).GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
            var owner = new Dictionary<string, string>();
            foreach (var piece in Physics.WorldCollision.Pieces(doc, name => Physics.WorldCollision.ReadMaterial(name, models.Material, models.CollisionProperty),
                         null, gpu == null ? null : gpu.For, models.Physics, models.SmartProp, models.CollisionProperty))
                foreach (var pt in piece.Points)
                    owner.TryAdd(pt.ToString("R", null), $"{piece.NodeId}/{Path.GetFileNameWithoutExtension(piece.MaterialName)}");
            var onlyTheirs = theirs.Where(kv => kv.Value > ours.GetValueOrDefault(kv.Key)).Select(kv => (kv.Key, N: kv.Value - ours.GetValueOrDefault(kv.Key))).ToList();
            var onlyOurs = ours.Where(kv => kv.Value > theirs.GetValueOrDefault(kv.Key)).Select(kv => (kv.Key, N: kv.Value - theirs.GetValueOrDefault(kv.Key))).ToList();
            output.WriteLine($"tricmp: triangles only theirs {onlyTheirs.Sum(x => x.N)}, only ours {onlyOurs.Sum(x => x.N)}");
            foreach (var g in onlyTheirs.GroupBy(x => string.Join(" ", x.Key.Split(';').Select(c => owner.GetValueOrDefault(c, "?")).Distinct())).OrderByDescending(g => g.Sum(x => x.N)).Take(15))
                output.WriteLine($"tricmp  theirs-only {g.Sum(x => x.N)} with corners owned by {g.Key}; e.g. {g.First().Key}");
            foreach (var g in onlyOurs.GroupBy(x => string.Join(" ", x.Key.Split(';').Select(c => owner.GetValueOrDefault(c, "?")).Distinct())).OrderByDescending(g => g.Sum(x => x.N)).Take(8))
                output.WriteLine($"tricmp  ours-only {g.Sum(x => x.N)} with corners owned by {g.Key}; e.g. {g.First().Key}");
            return;
        }
        // WPBUILD_HULLVERTS=<index,...>: the hull's vertices, Valve's and ours.
        if (Environment.GetEnvironmentVariable("WPBUILD_HULLVERTS") is { Length: > 0 } vertsText)
        {
            foreach (var (label, bytes) in new[] { ("valve", valve), ("ours", mine) })
            {
                using var res = new ValveResourceFormat.Resource();
                res.Read(new MemoryStream(bytes));
                var hulls = ((ValveResourceFormat.ResourceTypes.Model)res.DataBlock!).GetEmbeddedPhys()!.Parts[0].Shape.Hulls;
                foreach (var i in vertsText.Split(',').Select(int.Parse))
                    output.WriteLine($"hullverts {i} {label}: " + string.Join(" ", hulls[i].Shape.GetVertexPositions().ToArray().Select(v => $"({v.X:R},{v.Y:R},{v.Z:R})")));
            }
            return;
        }
        output.WriteLine($"valve {Facts(valve)}");
        output.WriteLine($"mine  {Facts(mine)}");
        var ta = Trees(valve);
        var tb = Trees(mine);
        var total = 0;
        foreach (var (name, tree) in ta)
        {
            var diffs = tb.TryGetValue(name, out var other) ? KvTreeDiff.Diff(tree, other, 100000) : ["missing"];
            total += diffs.Count;
            output.WriteLine($"{name}: {diffs.Count} differences");
            foreach (var d in diffs.Take(show))
                output.WriteLine($"  {name}{d}");
        }
        foreach (var note in notes.Take(10))
            output.WriteLine($"note: {note}");
        if (Environment.GetEnvironmentVariable("WPBUILD_SURFACES") == "1")
        {
            var model = Physics.WorldPhysics.Build(Physics.WorldCollision.Pieces(doc, name => Physics.WorldCollision.ReadMaterial(name, models.Material, models.CollisionProperty),
                null, gpu == null ? null : gpu.For, models.Physics, models.SmartProp, models.CollisionProperty), models.SurfaceName);
            foreach (var source in model.SurfaceSources)
                output.WriteLine($"surface source: {source}");
            for (var i = 0; i < model.AttributeSources.Count; i++)
                output.WriteLine($"attribute source {i}: {model.AttributeSources[i]}");
            output.WriteLine("listed: " + string.Join(",", model.ListedSurfaces.Select(i => $"{i}:{model.Surfaces[i].Name}")));
            foreach (var piece in Physics.WorldCollision.Pieces(doc, name => Physics.WorldCollision.ReadMaterial(name, models.Material, models.CollisionProperty),
                         null, gpu == null ? null : gpu.For, models.Physics, models.SmartProp, models.CollisionProperty).Where(x => x.NodeId.ToString() == Environment.GetEnvironmentVariable("WPBUILD_NODE")))
                output.WriteLine($"piece node {piece.NodeId} type {piece.Type} {piece.MaterialName} solid {piece.Physics.Solid} points {piece.Points.Length} surface '{piece.Physics.SurfaceProperty}' tris {piece.Indices.Length / 3} name '{piece.Name}'");
            var hashes = ta["PHYS"]["m_surfacePropertyHashes"];
            output.WriteLine("valve surfaces: " + string.Join(",", hashes!.Select(h => h.ToString())));
        }

        // The manifest beside it: container facts, RED2's tree, DATA's bytes.
        var valveManifest = Read(p[2], files.ManifestPath);
        var manifest = new List<string>();
        using (var a = new Resource())
        using (var b = new Resource())
        {
            a.Read(new MemoryStream(valveManifest));
            b.Read(new MemoryStream(files.Manifest));
            if (a.Version != b.Version || !a.Blocks.Select(x => x.Type).SequenceEqual(b.Blocks.Select(x => x.Type)))
                manifest.Add("container: version or block order");
            if (!a.ExternalReferences!.ResourceRefInfoList.Select(r => (r.Id, r.Name)).SequenceEqual(b.ExternalReferences!.ResourceRefInfoList.Select(r => (r.Id, r.Name))))
                manifest.Add("RERL differs");
        }
        manifest.AddRange(KvTreeDiff.Diff(Trees(valveManifest)["RED2"], Trees(files.Manifest)["RED2"]).Select(l => $"RED2{l}"));
        if (!Block(valveManifest, "DATA").SequenceEqual(Block(files.Manifest, "DATA")))
            manifest.Add("DATA bytes differ");
        output.WriteLine($"manifest: {manifest.Count} differences");
        foreach (var d in manifest.Take(show))
            output.WriteLine($"  {d}");
        Assert.Equal(0, total + manifest.Count);
    }

    private static byte[] Block(byte[] bytes, string name)
    {
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        var block = resource.Blocks.First(b => b.Type.ToString() == name);
        return bytes.AsSpan((int)block.Offset, (int)block.Size).ToArray();
    }

    internal static byte[] Read(string vpk, string path)
    {
        using var package = new Package();
        package.Read(vpk);
        package.ReadEntry(package.FindEntry(path)!, out var bytes);
        return bytes;
    }

    /// <summary>Each KV3 block's decoded tree by block name.</summary>
    internal static Dictionary<string, KVObject> Trees(byte[] bytes)
    {
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        var trees = new Dictionary<string, KVObject>();
        foreach (var block in resource.Blocks)
        {
            KVObject? tree = block switch
            {
                KeyValuesOrNTRO k => k.Data,
                BinaryKV3 k => k.Data.Root,
                ResourceEditInfo2 r => r.Data?.Root,
                _ => null,
            };
            if (tree != null)
                trees[block.Type.ToString()] = tree;
        }
        return trees;
    }

    /// <summary>The container facts (version, block order, KV3 compression) and every block's tree.</summary>
    internal static List<string> Compare(byte[] valve, byte[] mine)
    {
        var report = new List<string>();
        var a = Facts(valve);
        var b = Facts(mine);
        if (a != b)
            report.Add($"container: {a} vs {b}");
        var ta = Trees(valve);
        var tb = Trees(mine);
        foreach (var (name, tree) in ta)
        {
            if (!tb.TryGetValue(name, out var other))
            {
                report.Add($"{name}: missing");
                continue;
            }
            report.AddRange(KvTreeDiff.Diff(tree, other).Select(l => $"{name}{l}"));
        }
        return report;
    }

    // Resource version, then each block's name, KV3 version and compression method.
    internal static string Facts(byte[] bytes)
    {
        var parts = new List<string> { $"v{BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6))}" };
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        for (var i = 0; i < count; i++)
        {
            var at = 16 + (i * 12);
            var offset = at + 4 + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4));
            parts.Add($"{System.Text.Encoding.ASCII.GetString(bytes, at, 4)}:{Convert.ToHexString(bytes, offset, 4)}:m{BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 20))}:{Convert.ToHexString(bytes, offset + 4, 16)}");
        }
        return string.Join(" ", parts);
    }

    private static string Kv3Text(KVObject node)
    {
        var sb = new System.Text.StringBuilder();
        void Walk(KVObject v, int depth)
        {
            switch (v.ValueType)
            {
                case KVValueType.Collection:
                    sb.Append('{');
                    foreach (var c in v.Children)
                    {
                        sb.Append(' ').Append(c.Key).Append('=');
                        Walk(c.Value, depth + 1);
                    }
                    sb.Append(" }");
                    break;
                case KVValueType.Array:
                    sb.Append('[');
                    foreach (var x in v.Values)
                    {
                        Walk(x, depth + 1);
                        sb.Append(',');
                    }
                    sb.Append(']');
                    break;
                case KVValueType.BinaryBlob:
                    sb.Append($"blob({v.AsBlob().Length})");
                    break;
                default:
                    sb.Append(v.ToString());
                    break;
            }
        }
        Walk(node, 0);
        return sb.ToString();
    }
}
