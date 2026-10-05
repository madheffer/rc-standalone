using System.Numerics;
using System.Text.Json;
using Source2.Compiler.Physics;
using ValvePak;
using ValveResourceFormat;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: the map builder's hull input as tools/physics/capture_hullbuild.py
/// records it, rebuilt by the port (MapBuilder options, shape points, the compile
/// options) and matched by vertex set against the hulls Valve shipped in the
/// models whose path contains a filter. A Valve hull the port rebuilds from
/// Valve's own input but not from its own puts the difference in the input.
/// <c>HULLCAP=&lt;capture json&gt;|&lt;vpk&gt;|&lt;model path filter&gt;</c>.
/// </summary>
public class HullBuildCaptureProbe(ITestOutputHelper output)
{
    [Fact]
    public void RebuildFromValvesInput()
    {
        if (Environment.GetEnvironmentVariable("HULLCAP") is not { Length: > 0 } spec || spec.Split('|') is not [var json, var vpk, var filter])
            return;
        static string Key(IEnumerable<Vector3> v) => string.Join(";", v.Select(p => $"{p.X:R},{p.Y:R},{p.Z:R}").Order(StringComparer.Ordinal));

        // Valve's shipped hulls, by vertex set, with the model each comes from.
        var valve = new Dictionary<string, List<string>>();
        using (var package = new Package())
        {
            package.Read(vpk);
            foreach (var entry in package.Entries.GetValueOrDefault("vmdl_c") ?? [])
            {
                var path = entry.GetFullPath();
                if (!path.Contains(filter, StringComparison.Ordinal))
                    continue;
                package.ReadEntry(entry, out var bytes);
                using var res = new Resource();
                res.Read(new MemoryStream(bytes));
                var phys = ((ValveResourceFormat.ResourceTypes.Model)res.DataBlock!).GetEmbeddedPhys();
                foreach (var part in phys?.Parts ?? [])
                    foreach (var h in part.Shape.Hulls)
                    {
                        var key = Key(h.Shape.GetVertexPositions().ToArray());
                        if (!valve.TryGetValue(key, out var list))
                            valve[key] = list = [];
                        list.Add(path);
                    }
            }
        }
        output.WriteLine($"{valve.Values.Sum(l => l.Count)} Valve hulls in models matching '{filter}'");

        // Each captured input rebuilt as EntityPhysicsModels builds a hull.
        var calls = JsonDocument.Parse(File.ReadAllText(json)).RootElement.EnumerateArray().ToList();
        var matched = new Dictionary<string, int>();
        var calledFor = new Dictionary<string, List<int>>();
        var rebuilt = 0;
        foreach (var call in calls)
        {
            if (!call.TryGetProperty("points", out var hex))
                continue;
            var bytes = Convert.FromHexString(hex.GetString()!);
            var input = new Vector3[bytes.Length / 12];
            for (var i = 0; i < input.Length; i++)
                input[i] = new Vector3(BitConverter.ToSingle(bytes, i * 12), BitConverter.ToSingle(bytes, i * 12 + 4), BitConverter.ToSingle(bytes, i * 12 + 8));
            var qh = RnHullBuilder.BuildHull(input, RnHullBuilder.Options.MapBuilder, out _);
            var points = qh == null ? null : BrushHulls.ShapePoints([.. qh.HullVertices.Select(v => new Vector3(v.X, v.Y, v.Z))]);
            var hull = points == null ? null : RnHullBuilder.Create(points, RnHullBuilder.Options.Compile, out _);
            if (hull == null)
                continue;
            hull.RegionSvm = RegionSvmBuilder.Build(hull);
            RnHullBuilder.Transform(hull, RnHullBuilder.Identity);
            rebuilt++;
            if (valve.TryGetValue(Key(hull.VertexPositions), out var models))
                foreach (var m in models.Distinct())
                {
                    matched[m] = matched.GetValueOrDefault(m) + 1;
                    calledFor.TryAdd(m, []);
                    calledFor[m].Add(call.GetProperty("call").GetInt32());
                }
        }
        output.WriteLine($"{calls.Count} captured inputs, {rebuilt} rebuilt into hulls");
        foreach (var path in valve.Values.SelectMany(l => l).Distinct().Order(StringComparer.Ordinal))
            output.WriteLine($"  {path}: {matched.GetValueOrDefault(path)} of {valve.Values.Sum(l => l.Count(p => p == path))} Valve hulls rebuilt from Valve's input"
                + (calledFor.TryGetValue(path, out var ids) ? $", calls {ids.Min()} to {ids.Max()}" : ""));

        // HULLCAP_NODE=<addon>|<map>|<entity node id>: that brush entity's inputs as
        // the port makes them, each matched against the captured ones by point set.
        if (Environment.GetEnvironmentVariable("HULLCAP_NODE") is not { Length: > 0 } node || node.Split('|') is not [var addon, var map, var idText])
            return;
        var capturedInputs = calls.Where(c => c.TryGetProperty("points", out _)).Select(c =>
        {
            var b = Convert.FromHexString(c.GetProperty("points").GetString()!);
            return Enumerable.Range(0, b.Length / 12).Select(i => new Vector3(BitConverter.ToSingle(b, i * 12), BitConverter.ToSingle(b, i * 12 + 4), BitConverter.ToSingle(b, i * 12 + 8))).ToArray();
        }).ToList();
        var capturedKeys = capturedInputs.Select(Key).ToList();
        var doc = DmxBinary.ReadFile(MapFixtures.VmapSource(addon, map)!);
        var entity = doc.Elements.First(e => e.Type == "CMapEntity" && e.GetValue<int>("nodeID") == int.Parse(idText, System.Globalization.CultureInfo.InvariantCulture));
        var ours = new List<Vector3[]>();
        foreach (var mesh in entity.GetElements("children").Where(c => c.Type == "CMapMesh"))
            foreach (var (_, positions, faces, local, corners, _, _) in BrushHulls.PiecesWithCorners(mesh, entity))
                ours.AddRange(BrushHulls.Inputs(positions, faces, BrushHulls.PhysicsType.ConvexMulti, local,
                    Environment.GetEnvironmentVariable("HULLCAP_BYID") switch { "1" => corners, "welded" => faces, _ => null }));
        output.WriteLine($"node {idText}: {ours.Count} inputs of ours, {ours.Count(o => capturedKeys.Contains(Key(o)))} found among the captured; meshes subdivided: "
            + string.Join(",", entity.GetElements("children").Where(c => c.Type == "CMapMesh").Select(m => WorldCollision.Subdivided(m))));
        // HULLCAP_WELD=<streams the weld compares, comma separated; "-" for none>:
        // each piece welded at 1/32 on those alone and grouped by welded vertex.
        if (Environment.GetEnvironmentVariable("HULLCAP_WELD") is { Length: > 0 } weldSpec)
        {
            var keep = weldSpec == "-" ? [] : weldSpec.Split(',');
            var toEntity = Maps.CTransform.FromNode(entity).Inverse().Matrix();
            var variant = new List<Vector3[]>();
            foreach (var mesh in entity.GetElements("children").Where(c => c.Type == "CMapMesh"))
            {
                var toWorld = Maps.CTransform.FromNode(mesh).Matrix();
                foreach (var piece in Maps.MapMeshCorners.Build(mesh))
                {
                    var streams = piece.Streams.Select(st => st.Name == "position" || keep.Contains(st.Name) ? st : st with { Ignored = true }).ToList();
                    var (v, indices) = MeshWeld.Weld(piece.Vertices, piece.Stride, piece.Indices, streams, 1f / 32f, true, []);
                    var local = Enumerable.Range(0, v.Length / piece.Stride).Select(i => new Vector3(v[i * piece.Stride], v[i * piece.Stride + 1], v[i * piece.Stride + 2])).ToArray();
                    var placed = local.Select(p => Maps.MapMeshes.Transform(toEntity, Maps.MapMeshes.Transform(toWorld, p))).ToArray();
                    var faces = Enumerable.Range(0, indices.Length / 3).Select(t => new[] { indices[t * 3], indices[t * 3 + 1], indices[t * 3 + 2] }).ToArray();
                    variant.AddRange(BrushHulls.Inputs(placed, faces, BrushHulls.PhysicsType.ConvexMulti, local, faces));
                }
            }
            output.WriteLine($"  weld on position+{weldSpec}: {variant.Count} inputs, {variant.Count(o => capturedKeys.Contains(Key(o)))} found among the captured");
        }
        // The captured inputs in the same space: those whose points all lie in our inputs' box.
        var lo = ours.SelectMany(o => o).Aggregate(Vector3.Min) - Vector3.One;
        var hi = ours.SelectMany(o => o).Aggregate(Vector3.Max) + Vector3.One;
        var near = capturedInputs.Where(c => c.All(p => Vector3.Clamp(p, lo, hi) == p)).ToList();
        output.WriteLine($"  {near.Count} captured inputs lie in that box; total points ours {ours.Sum(o => o.Length)}, theirs {near.Sum(c => c.Length)}");
        var theirsAll = near.SelectMany(c => c).ToHashSet();
        var oursAll = ours.SelectMany(o => o).ToHashSet();
        output.WriteLine($"  distinct points: ours {oursAll.Count}, theirs {theirsAll.Count}, shared {oursAll.Count(theirsAll.Contains)}");
        // HULLCAP_DUP=1: the points a captured input lists twice, and each welded
        // vertex of ours placed there, with its own (local) position.
        if (Environment.GetEnvironmentVariable("HULLCAP_DUP") == "1")
        {
            var pieces = entity.GetElements("children").Where(c => c.Type == "CMapMesh")
                .SelectMany(m => BrushHulls.PiecesWithCorners(m, entity)).ToList();
            foreach (var o in ours.Where(o => !capturedKeys.Contains(Key(o))).Take(4))
            {
                var best = near.OrderByDescending(c => c.Count(o.Contains)).First();
                foreach (var d in best.GroupBy(p => p).Where(g => g.Count() > 1).Select(g => g.Key).Take(3))
                {
                    output.WriteLine($"  captured lists {d:R} twice");
                    foreach (var (pi, piece) in pieces.Select((x, k) => (k, x)))
                        for (var v = 0; v < piece.Positions.Length; v++)
                            if (piece.Positions[v] == d)
                                output.WriteLine($"    piece {pi} welded vertex {v}: local {piece.Local[v]:R} corners {string.Join(",", piece.Faces.Select((fc, fi) => (fc, fi)).Where(x => x.fc.Contains(v)).Take(3).Select(x => x.fi + ":" + string.Join("/", piece.CornerIds[x.fi])))}");
                }
            }
        }
        foreach (var o in ours.Where(o => !capturedKeys.Contains(Key(o))).Take(6))
        {
            var best = near.OrderByDescending(c => c.Count(o.Contains)).FirstOrDefault();
            output.WriteLine($"  ours only: {o.Length} points; the closest captured has {best?.Length} points, {best?.Count(o.Contains)} shared"
                + (best == null ? "" : $"; ours not in it e.g. {string.Join(" ", o.Where(p => !best.Contains(p)).Take(3))}, its not in ours e.g. {string.Join(" ", best.Where(p => !o.Contains(p)).Take(3))}"));
        }
    }
}
