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
        var notes = new List<string>();
        // WPBUILD_GPU=1: new-blending materials take their layers from the GPU sampler.
        using var gpu = Environment.GetEnvironmentVariable("WPBUILD_GPU") == "1"
            ? new Source2.Compiler.Gpu.GpuMaterialSampler(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"), models.Read)
            : null;
        if (Environment.GetEnvironmentVariable("WPBUILD_HULLIDX") is { Length: > 0 } idxText)
        {
            var pieces = Physics.WorldCollision.Pieces(doc, name => Physics.WorldCollision.ReadMaterial(models.Material(name), models.CollisionProperty),
                null, gpu == null ? null : gpu.For, models.Physics, models.SmartProp);
            var hullPieces = Physics.WorldCollision.PartOrder(pieces, x => x.Type).Where(x => x.Type == Physics.WorldCollision.HullType).ToList();
            foreach (var i in idxText.Split(',').Select(int.Parse))
                output.WriteLine($"hullidx {i}: node {hullPieces[i].NodeId} {hullPieces[i].MaterialName} verts {hullPieces[i].Points.Length}");
            return;
        }
        if (Environment.GetEnvironmentVariable("WPBUILD_NODEATTRS") is { Length: > 0 } attrNode)
        {
            var el = doc.Elements.First(e => e.GetValue<int>("nodeID")?.ToString() == attrNode);
            foreach (var (k, v) in el.Attributes)
                output.WriteLine($"nodeattr {k} = {(v is DmxBinary.Element ce ? ce.Type : v is object?[] arr ? $"[{arr.Length}]" : v)}");
            var md = el.Get<DmxBinary.Element>("meshData");
            output.WriteLine("nodeattr materials: " + string.Join(", ", md?.Get<object?[]>("materials") ?? []));
            var fm = md?.Get<DmxBinary.Element>("faceData")?.GetElements("streams").FirstOrDefault(x => x.Name.StartsWith("materialindex", StringComparison.Ordinal))?.Get<object?[]>("data") ?? [];
            output.WriteLine("nodeattr face materials: " + string.Join(",", fm.GroupBy(x => x).Select(g => $"{g.Key}x{g.Count()}")));
            return;
        }
        if (Environment.GetEnvironmentVariable("WPBUILD_HULLSURF") is { Length: > 0 } nodesText)
        {
            var wanted = nodesText.Split(',').Select(int.Parse).ToHashSet();
            var pieces = Physics.WorldCollision.Pieces(doc, name => Physics.WorldCollision.ReadMaterial(models.Material(name), models.CollisionProperty),
                null, gpu == null ? null : gpu.For, models.Physics, models.SmartProp);
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
        // WPBUILD_MESHINFO=<node,...>: each mesh node's parent, instances and physics type.
        if (Environment.GetEnvironmentVariable("WPBUILD_MESHINFO") is { Length: > 0 } infoText)
        {
            var wanted = infoText.Split(',').Select(int.Parse).ToHashSet();
            var (meshes, _) = Maps.MapMeshes.ReadWithEntities(doc);
            foreach (var m in meshes.Where(m => wanted.Contains(m.NodeId)))
                output.WriteLine($"meshinfo {m.NodeId}: {m.ParentType}/{m.ParentClass} instances [{string.Join(",", m.Instances)}] physics {m.Element?.Get<string>("physicsType")} origin {m.Origin} angles {m.Angles} scales {m.Scales}");
            foreach (var m in meshes.Where(m => wanted.Contains(m.NodeId) && m.Instances.Length == 0).DistinctBy(m => m.NodeId))
                foreach (var piece in Physics.BrushHulls.PiecesWithCorners(m.Element!, doc.OfType("CMapWorld").First()))
                {
                    var (pts, tris) = Physics.BrushHulls.TriangleMesh(piece.Positions, piece.Faces, piece.Local, piece.CornerIds);
                    output.WriteLine($"meshinfo {m.NodeId} material {piece.Material}: faces {piece.Faces.Length} sizes {string.Join(",", piece.Faces.GroupBy(f => f.Length).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}"))} positions {piece.Positions.Length} points {pts.Count} triangles {tris.Count}");
                }
            return;
        }
        if (Environment.GetEnvironmentVariable("WPBUILD_FINDHASH") is { Length: > 0 } find)
        {
            var built = Physics.WorldPhysics.Build(Physics.WorldCollision.Pieces(doc, name => Physics.WorldCollision.ReadMaterial(models.Material(name), models.CollisionProperty),
                null, gpu == null ? null : gpu.For, models.Physics, models.SmartProp), models.SurfaceName);
            foreach (var source in built.SurfaceSources.Where(x => x.Contains(find, StringComparison.Ordinal)))
                output.WriteLine($"found: {source}");
            return;
        }
        var files = Physics.WorldPhysicsFiles.Build(doc, p[0], p[1], models, gpu == null ? null : gpu.For, notes);
        var mine = files.Model;
        var valve = Read(p[2], files.ModelPath);
        // WPBUILD_HULLORDER=<node>: a convex_single world mesh hulled from several
        // candidate input orders, each against Valve's hull with the same vertices.
        if (Environment.GetEnvironmentVariable("WPBUILD_HULLORDER") is { Length: > 0 } orderNode)
        {
            using var res = new ValveResourceFormat.Resource();
            res.Read(new MemoryStream(valve));
            var valveHulls = ((ValveResourceFormat.ResourceTypes.Model)res.DataBlock!).GetEmbeddedPhys()!.Parts[0].Shape.Hulls
                .Select(h => h.Shape.GetVertexPositions().ToArray()).ToList();
            var (meshes, _) = Maps.MapMeshes.ReadWithEntities(doc);
            var m = meshes.First(x => x.NodeId.ToString() == orderNode);
            var world = doc.OfType("CMapWorld").First();
            foreach (var piece in Physics.BrushHulls.PiecesWithCorners(m.Element!, world))
            {
                var candidates = new List<(string, Vector3[])>();
                candidates.Add(("by id", [.. Physics.BrushHulls.TriangleMesh(piece.Positions, piece.Faces, piece.Local, piece.CornerIds).Points]));
                candidates.Add(("by position", [.. Physics.BrushHulls.TriangleMesh(piece.Positions, piece.Faces, piece.Local).Points]));
                candidates.Add(("weld order", piece.Positions));
                var firstOfId = new Dictionary<int, Vector3>();
                for (var f = 0; f < piece.Faces.Length; f++)
                    for (var c = 0; c < 3; c++)
                        firstOfId.TryAdd(piece.CornerIds[f][c], piece.Positions[piece.Faces[f][c]]);
                candidates.Add(("id ascending", [.. firstOfId.OrderBy(kv => kv.Key).Select(kv => kv.Value)]));
                var weldFirst = new List<Vector3>();
                var seenId = new HashSet<int>();
                var idOfWelded = new Dictionary<int, int>();
                for (var f = 0; f < piece.Faces.Length; f++)
                    for (var c = 0; c < 3; c++)
                        idOfWelded.TryAdd(piece.Faces[f][c], piece.CornerIds[f][c]);
                for (var i = 0; i < piece.Positions.Length; i++)
                    if (idOfWelded.TryGetValue(i, out var id) && seenId.Add(id))
                        weldFirst.Add(piece.Positions[i]);
                candidates.Add(("weld order, one per id", [.. weldFirst]));
                candidates.Add(("face loops, distinct", [.. m.Faces.SelectMany(f => f.Corners).Distinct()]));
                var matName = (m.Element!.Get<DmxBinary.Element>("meshData")?.Get<object?[]>("materials") ?? [])[piece.Material] as string;
                candidates.Add(("face loops, all", [.. m.Faces.Where(f => f.Material == matName).SelectMany(f => f.Corners)]));
                candidates.Add(("triangles, all corners", [.. piece.Faces.SelectMany(f => f).Select(i => piece.Positions[i])]));
                candidates.Add(("fan, all corners", [.. m.Faces.Where(f => f.Material == matName).SelectMany(f => Enumerable.Range(1, Math.Max(0, f.Corners.Length - 2)).SelectMany(k => new[] { f.Corners[0], f.Corners[k], f.Corners[k + 1] }))]));
                candidates.Add(("by welded index", [.. Physics.BrushHulls.TriangleMesh(piece.Positions, piece.Faces, piece.Local, piece.Faces).Points]));
                candidates.Add(("by corner data", [.. Physics.BrushHulls.TriangleMesh(piece.Positions, piece.Faces, piece.Local, piece.CornerData).Points]));
                {
                    var md = m.Element!.Get<DmxBinary.Element>("meshData")!;
                    int[] Arr(string n) => (md.Get<object?[]>(n) ?? []).Select(x => x is int i ? i : -1).ToArray();
                    var nextE = Arr("edgeNextIndices");
                    var toV = Arr("edgeVertexIndices");
                    var firstE = Arr("faceEdgeIndices");
                    var matStream = md.Get<DmxBinary.Element>("faceData")?.GetElements("streams").FirstOrDefault(x => x.Name.StartsWith("materialindex", StringComparison.Ordinal))?.Get<object?[]>("data") ?? [];
                    var idPos = new Dictionary<int, Vector3>();
                    for (var f = 0; f < piece.Faces.Length; f++)
                        for (var c = 0; c < 3; c++)
                            idPos.TryAdd(piece.CornerIds[f][c], piece.Positions[piece.Faces[f][c]]);
                    var loopIds = new List<int>();
                    for (var f = 0; f < firstE.Length; f++)
                    {
                        if (f < matStream.Length && matStream[f] is int mi && mi != piece.Material)
                            continue;
                        var e = firstE[f];
                        do { loopIds.Add(toV[e]); e = nextE[e]; } while (e != firstE[f]);
                    }
                    output.WriteLine($"hullorder material {piece.Material} loop ids without a position: {loopIds.Count(i => !idPos.ContainsKey(i))}");
                    candidates.Add(("id loops, all", [.. loopIds.Where(idPos.ContainsKey).Select(i => idPos[i])]));
                    candidates.Add(("id loops, distinct", [.. loopIds.Where(idPos.ContainsKey).Distinct().Select(i => idPos[i])]));
                }
                {
                    var fvd = m.Element!.Get<DmxBinary.Element>("meshData")!.Get<DmxBinary.Element>("faceVertexData")!;
                    object?[] StreamOf(string n) => fvd.GetElements("streams").FirstOrDefault(x => x.Name.Split(':')[0] == n)?.Get<object?[]>("data") ?? [];
                    foreach (var sn in new[] { "normal", "texcoord", "normal+texcoord" })
                    {
                        var parts = sn.Split('+').Select(StreamOf).ToArray();
                        var st = Enumerable.Range(0, parts.Max(x => x.Length)).Select(i => (object?)string.Join("|", parts.Select(x => i < x.Length ? x[i]?.ToString() : ""))).ToArray();
                        var keys = new Dictionary<(int, string), int>();
                        var ids = piece.Faces.Select((f, fi) => f.Select((_, c) =>
                        {
                            var key = (piece.CornerIds[fi][c], st.Length > piece.CornerData[fi][c] ? st[piece.CornerData[fi][c]]?.ToString() ?? "" : "");
                            if (!keys.TryGetValue(key, out var k))
                                keys[key] = k = keys.Count;
                            return k;
                        }).ToArray()).ToArray();
                        candidates.Add(($"by id+{sn}", [.. Physics.BrushHulls.TriangleMesh(piece.Positions, piece.Faces, piece.Local, ids).Points]));
                    }
                }
                candidates.Add(("by id, reversed", [.. Enumerable.Reverse(Physics.BrushHulls.TriangleMesh(piece.Positions, piece.Faces, piece.Local, piece.CornerIds).Points)]));
                var loopSet = candidates.First(c => c.Item1 == "face loops, all").Item2.ToHashSet();
                var idPoints = candidates[0].Item2;
                var idSet = idPoints.ToHashSet();
                var extra = loopSet.Where(x => !idSet.Contains(x)).ToList();
                output.WriteLine($"hullorder material {piece.Material} loop points not in by-id: {extra.Count} {string.Join(" ", extra.Take(6).Select(x => $"({x.X:R},{x.Y:R},{x.Z:R})"))}");
                var missing = idPoints.Where(x => !loopSet.Contains(x)).ToList();
                output.WriteLine($"hullorder material {piece.Material} by-id points not in loops: {missing.Count} {string.Join(" ", missing.Take(4).Select(x => $"({x.X:R},{x.Y:R},{x.Z:R})"))} nearest {string.Join(" ", missing.Take(4).Select(x => loopSet.MinBy(y => Vector3.DistanceSquared(x, y))).Select(x => $"({x.X:R},{x.Y:R},{x.Z:R})"))}");
                foreach (var (label, input) in candidates)
                {
                    var qh = Physics.RnHullBuilder.BuildHull(input, Physics.RnHullBuilder.Options.MapBuilder, out _);
                    var pts = qh == null ? null : Physics.BrushHulls.ShapePoints([.. qh.HullVertices.Select(v => new Vector3(v.X, v.Y, v.Z))]);
                    var hull = pts == null ? null : Physics.RnHullBuilder.Create(pts, Physics.RnHullBuilder.Options.Compile, out _);
                    var ours = hull?.VertexPositions ?? [];
                    var set = ours.ToHashSet();
                    var at = valveHulls.FindIndex(v => v.Length == set.Count && v.All(set.Contains));
                    var same = at >= 0 && valveHulls[at].SequenceEqual(ours);
                    output.WriteLine($"hullorder material {piece.Material} {label}: input {input.Length} verts {ours.Length} valve hull {at} order {(same ? "SAME" : "differs")}");
                }
            }
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
            var model = Physics.WorldPhysics.Build(Physics.WorldCollision.Pieces(doc, name => Physics.WorldCollision.ReadMaterial(models.Material(name), models.CollisionProperty),
                null, gpu == null ? null : gpu.For, models.Physics, models.SmartProp), models.SurfaceName);
            foreach (var source in model.SurfaceSources)
                output.WriteLine($"surface source: {source}");
            output.WriteLine("listed: " + string.Join(",", model.ListedSurfaces.Select(i => $"{i}:{model.Surfaces[i].Name}")));
            foreach (var piece in Physics.WorldCollision.Pieces(doc, name => Physics.WorldCollision.ReadMaterial(models.Material(name), models.CollisionProperty),
                         null, gpu == null ? null : gpu.For, models.Physics, models.SmartProp).Where(x => x.NodeId.ToString() == Environment.GetEnvironmentVariable("WPBUILD_NODE")))
                output.WriteLine($"piece node {piece.NodeId} {piece.MaterialName} surface '{piece.Physics.SurfaceProperty}' tris {piece.Indices.Length / 3} name '{piece.Name}'");
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
}
