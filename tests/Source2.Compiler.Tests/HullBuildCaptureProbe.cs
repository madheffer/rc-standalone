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
        // HULLCAP_TRI=<record>: that MapBuilder_TriangleMesh record (--trimesh)
        // against the triangle mesh the port hands the grouping, for the
        // entity's pieces: vertex positions as multisets, triangles by corners.
        if (Environment.GetEnvironmentVariable("HULLCAP_TRI") is { Length: > 0 } triText)
        {
            var rec = calls.First(c => c.TryGetProperty("trimesh", out var t) && t.GetInt32() == int.Parse(triText, System.Globalization.CultureInfo.InvariantCulture));
            var pb = Convert.FromHexString(rec.GetProperty("points").GetString()!);
            var ib = Convert.FromHexString(rec.GetProperty("indices").GetString()!);
            var vp = Enumerable.Range(0, pb.Length / 12).Select(i => new Vector3(BitConverter.ToSingle(pb, i * 12), BitConverter.ToSingle(pb, i * 12 + 4), BitConverter.ToSingle(pb, i * 12 + 8))).ToArray();
            var vi = Enumerable.Range(0, ib.Length / 4).Select(i => BitConverter.ToInt32(ib, i * 4)).ToArray();
            var mine = new List<Vector3>();
            var mineTris = new List<(Vector3, Vector3, Vector3)>();
            foreach (var mesh in entity.GetElements("children").Where(c => c.Type == "CMapMesh"))
                foreach (var (_, positions, faces, local) in BrushHulls.Pieces(mesh, entity))
                {
                    var (pts, tris) = BrushHulls.TriangleMesh(positions, faces, local);
                    mine.AddRange(pts);
                    mineTris.AddRange(tris.Select(t => (pts[t.A], pts[t.B], pts[t.C])));
                }
            static Dictionary<Vector3, int> Counts(IEnumerable<Vector3> v) => v.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            var (cv, cm) = (Counts(vp), Counts(mine));
            output.WriteLine($"trimesh {triText}: Valve {vp.Length} vertices ({cv.Count} distinct positions), {vi.Length / 3} triangles; ours {mine.Count} ({cm.Count} distinct), {mineTris.Count} triangles");
            output.WriteLine($"  positions only Valve's {cv.Keys.Count(k => !cm.ContainsKey(k))}, only ours {cm.Keys.Count(k => !cv.ContainsKey(k))}; Valve lists twice or more {cv.Count(kv => kv.Value > 1)}, ours {cm.Count(kv => kv.Value > 1)}");
            foreach (var k in cm.Keys.Where(k => !cv.ContainsKey(k)).Take(4))
            {
                var nearest = vp.OrderBy(x => Vector3.DistanceSquared(x, k)).First();
                output.WriteLine($"    ours only {k:R}; Valve's nearest {nearest:R} ({Vector3.Distance(nearest, k):G3} away)");
            }
            // Per mesh: how many vertices each identity gives (placed position,
            // mesh-space position, .vmap vertex) over the corners used.
            foreach (var mesh in entity.GetElements("children").Where(c => c.Type == "CMapMesh"))
                foreach (var (_, positions, faces, local, corners, _, _) in BrushHulls.PiecesWithCorners(mesh, entity))
                {
                    var used = faces.SelectMany(f => f).Distinct().ToList();
                    var byLocal = used.Select(v => local[v]).Distinct().Count();
                    var byPlaced = used.Select(v => positions[v]).Distinct().Count();
                    var ids = corners.SelectMany(c => c).Distinct().Count();
                    var pairs = used.GroupBy(v => positions[v]).Count(g => g.Select(v => local[v]).Distinct().Count() > 1);
                    var toWorld = Maps.CTransform.FromNode(mesh).Matrix();
                    var byWorld = used.Select(v => Maps.MapMeshes.Transform(toWorld, local[v])).Distinct().Count();
                    output.WriteLine($"  mesh {mesh.GetValue<int>("nodeID")}: placed {byPlaced}, world {byWorld}, local {byLocal}, .vmap vertices {ids}, placed positions with several local ones {pairs}");
                }
            // Pinch points: per vertex of our position-joined mesh, its triangles
            // split into fans (joined through an edge at that vertex); every fan
            // past the first would be a vertex of its own in a half-edge mesh.
            foreach (var mesh in entity.GetElements("children").Where(c => c.Type == "CMapMesh"))
                foreach (var (_, positions, faces, local) in BrushHulls.Pieces(mesh, entity))
                {
                    var (pts, tris) = BrushHulls.TriangleMesh(positions, faces, local);
                    var around = new List<int>[pts.Count];
                    for (var i = 0; i < around.Length; i++)
                        around[i] = [];
                    for (var t = 0; t < tris.Count; t++)
                        foreach (var v in new[] { tris[t].A, tris[t].B, tris[t].C })
                            around[v].Add(t);
                    var extra = 0;
                    for (var v = 0; v < pts.Count; v++)
                    {
                        var list = around[v];
                        var parent = Enumerable.Range(0, list.Count).ToArray();
                        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
                        for (var i = 0; i < list.Count; i++)
                            for (var j = i + 1; j < list.Count; j++)
                            {
                                int[] a = [tris[list[i]].A, tris[list[i]].B, tris[list[i]].C], b = [tris[list[j]].A, tris[list[j]].B, tris[list[j]].C];
                                if (a.Intersect(b).Count() >= 2)
                                    parent[Find(i)] = Find(j);
                            }
                        extra += Enumerable.Range(0, list.Count).Select(Find).Distinct().Count() - 1;
                    }
                    output.WriteLine($"  mesh {mesh.GetValue<int>("nodeID")}: {pts.Count} vertices, {extra} fans past the first (a half-edge mesh would have {pts.Count + extra})");
                }
            // The join with a corner that repeats a position already taken by an
            // earlier corner of the same triangle given a vertex of its own:
            // "keep" leaves later triangles on the first vertex, "latest" moves
            // them to the new one. Compared with Valve's buffer in order.
            // "weldid": a corner whose welded vertex was met before keeps that
            // piece vertex; the rest join an existing vertex at their position
            // not already in the triangle, else take a new one; the buffer is
            // numbered by first use.
            foreach (var mesh in entity.GetElements("children").Where(c => c.Type == "CMapMesh"))
                foreach (var (_, positions, faces, local) in BrushHulls.Pieces(mesh, entity))
                {
                    var known = new Dictionary<int, int>();
                    var byPos = new Dictionary<Vector3, List<int>>();
                    var count = 0;
                    var raw = new List<int>();
                    foreach (var face in faces)
                    {
                        var got = face.Select(w => known.TryGetValue(w, out var x) ? x : -1).ToArray();
                        for (var j = 0; j < face.Length; j++)
                        {
                            if (got[j] >= 0)
                                continue;
                            var p = positions[face[j]];
                            if (!byPos.TryGetValue(p, out var list))
                                byPos[p] = list = [];
                            var pick = list.LastOrDefault(x => !got.Contains(x), -1);
                            if (pick < 0)
                            {
                                pick = count++;
                                list.Add(pick);
                            }
                            got[j] = pick;
                            known[face[j]] = pick;
                        }
                        raw.AddRange(got);
                    }
                    var order = new Dictionary<int, int>();
                    foreach (var x in raw)
                        order.TryAdd(x, order.Count);
                    var idx = raw.Select(x => order[x]).ToList();
                    var posOf = new Vector3[order.Count];
                    var k = 0;
                    foreach (var face in faces)
                        foreach (var w in face)
                            posOf[idx[k++]] = positions[w];
                    var firstOff = Enumerable.Range(0, Math.Min(posOf.Length, vp.Length)).FirstOrDefault(i => posOf[i] != vp[i], -1);
                    var fi = Enumerable.Range(0, Math.Min(idx.Count, vi.Length)).FirstOrDefault(i => idx[i] != vi[i], -1);
                    output.WriteLine($"  rule weldid, mesh {mesh.GetValue<int>("nodeID")}: {posOf.Length} vertices against {vp.Length}; buffer equal {posOf.SequenceEqual(vp)}, first differs at {firstOff}; indices equal {idx.SequenceEqual(vi)}, first index differing at {fi}");
                    if (fi >= 0 && mesh.GetValue<int>("nodeID") == 96)
                        for (var t = Math.Max(0, fi / 3 - 3); t <= fi / 3 + 1; t++)
                            output.WriteLine($"    tri {t}: welded {string.Join(",", faces[t])} ours {idx[t * 3]},{idx[t * 3 + 1]},{idx[t * 3 + 2]} valve {vi[t * 3]},{vi[t * 3 + 1]},{vi[t * 3 + 2]}; at {string.Join(" ", faces[t].Select(w => positions[w].ToString("R", null)))}");
                }
            foreach (var rule in new[] { "keep", "latest" })
                foreach (var mesh in entity.GetElements("children").Where(c => c.Type == "CMapMesh"))
                    foreach (var (_, positions, faces, local) in BrushHulls.Pieces(mesh, entity))
                    {
                        var index = new Dictionary<Vector3, int>();
                        var pts = new List<Vector3>();
                        var idx = new List<int>();
                        foreach (var face in faces)
                        {
                            var taken = new List<int>();
                            foreach (var v in face)
                            {
                                var p = positions[v];
                                if (!index.TryGetValue(p, out var i) || taken.Contains(i))
                                {
                                    var fresh = !index.ContainsKey(p);
                                    i = pts.Count;
                                    pts.Add(p);
                                    if (fresh || rule == "latest")
                                        index[p] = i;
                                }
                                taken.Add(i);
                                idx.Add(i);
                            }
                        }
                        var same = pts.Count == vp.Length && pts.SequenceEqual(vp);
                        var firstOff = Enumerable.Range(0, Math.Min(pts.Count, vp.Length)).FirstOrDefault(i => pts[i] != vp[i], -1);
                        output.WriteLine($"  rule {rule}, mesh {mesh.GetValue<int>("nodeID")}: {pts.Count} vertices against {vp.Length}; buffer equal {same}, first differs at {firstOff}; indices equal {idx.SequenceEqual(vi)}");
                        var firstIdx = Enumerable.Range(0, Math.Min(idx.Count, vi.Length)).FirstOrDefault(i => idx[i] != vi[i], -1);
                        if (rule == "keep" && firstIdx >= 0 && mesh.GetValue<int>("nodeID") == 96)
                        {
                            var t0 = firstIdx / 3;
                            for (var t = Math.Max(0, t0 - 2); t <= t0 + 1; t++)
                                output.WriteLine($"    triangle {t}: ours {idx[t * 3]},{idx[t * 3 + 1]},{idx[t * 3 + 2]} valve {vi[t * 3]},{vi[t * 3 + 1]},{vi[t * 3 + 2]}; corners {pts[idx[t * 3]]:R} {pts[idx[t * 3 + 1]]:R} {pts[idx[t * 3 + 2]]:R}");
                        }
                    }
            // Valve's duplicated positions: the triangles around each copy.
            var aroundV = new Dictionary<int, List<int>>();
            for (var t = 0; t < vi.Length / 3; t++)
                for (var c = 0; c < 3; c++)
                {
                    if (!aroundV.TryGetValue(vi[t * 3 + c], out var l))
                        aroundV[vi[t * 3 + c]] = l = [];
                    l.Add(t);
                }
            Vector3 FaceNormal(int t) => Vector3.Normalize(Vector3.Cross(vp[vi[t * 3 + 1]] - vp[vi[t * 3]], vp[vi[t * 3 + 2]] - vp[vi[t * 3]]));
            foreach (var g in Enumerable.Range(0, vp.Length).GroupBy(i => vp[i]).Where(g => g.Count() > 1).Take(6))
            {
                output.WriteLine($"  Valve's copies at {g.Key:R}: {string.Join(" | ", g.Select(i => $"v{i} {aroundV.GetValueOrDefault(i)?.Count ?? 0} tris, normal {(aroundV.TryGetValue(i, out var l) ? Vector3.Normalize(l.Aggregate(Vector3.Zero, (s, t) => s + FaceNormal(t))) : Vector3.Zero):F3}"))}");
                var sets = g.Select(i => aroundV.GetValueOrDefault(i)?.SelectMany(t => new[] { vi[t * 3], vi[t * 3 + 1], vi[t * 3 + 2] }).Select(x => vp[x]).ToHashSet() ?? []).ToList();
                output.WriteLine($"    neighbour positions shared between the copies: {sets[0].Intersect(sets[1]).Count() - 1}");
            }
            var vt = Enumerable.Range(0, vi.Length / 3).Select(t => (vp[vi[t * 3]], vp[vi[t * 3 + 1]], vp[vi[t * 3 + 2]])).ToHashSet();
            output.WriteLine($"  triangles of ours found in Valve's: {mineTris.Count(vt.Contains)} of {mineTris.Count}");
        }
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
                    output.WriteLine($"  weld {weldSpec}: mesh {mesh.GetValue<int>("nodeID")} piece {piece.Material}: {v.Length / piece.Stride} vertices, {BrushHulls.TriangleMesh(placed, faces, local, faces).Points.Count} used");
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
