using System.Numerics;
using Source2.Compiler.Physics;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: brush entity hulls built from the <c>.vmap</c> the way the map
/// builder builds them (<see cref="BrushHulls"/>), against the entity models
/// the same map compiled to. Points go to world space through the mesh's
/// matrix and then into the entity's through its inverse, as the current
/// compiler rounds them (<c>HULL_XFORM</c>: 0 world minus origin, 1 the
/// concatenated matrix, 2 world then inverse, the default). Hulls are matched
/// by vertex set, as the order across pieces is not the mesh order.
/// <c>HULLVMAP=&lt;vmap&gt;|&lt;compiled vpk&gt;</c>; <c>HULL_FROM=1</c> starts
/// each face loop at the half-edge's start vertex instead of its end;
/// <c>HULL_SHOW</c> caps the listed differences.
/// </summary>
public class HullFromVmap(ITestOutputHelper output)
{
    [Fact]
    public void BrushEntityHullsFromSource()
    {
        if (Environment.GetEnvironmentVariable("HULLVMAP") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var fromStart = Environment.GetEnvironmentVariable("HULL_FROM") == "1";
        var show = int.TryParse(Environment.GetEnvironmentVariable("HULL_SHOW"), out var s) ? s : 12;
        var doc = DmxBinary.Read(File.ReadAllBytes(parts[0]));
        using var package = new Package();
        package.Read(parts[1]);
        var models = package.Entries.SelectMany(kv => kv.Value)
            .Where(e => e.TypeName == "vmdl_c" && e.GetFullPath().Contains("/entities/", StringComparison.Ordinal))
            .ToDictionary(e => Path.GetFileNameWithoutExtension(e.FileName).Split('_')[^1], StringComparer.Ordinal);
        var tally = new Dictionary<string, int>();
        if (Environment.GetEnvironmentVariable("HULL_PATHOF") is { } pathOf)
        {
            void Find(DmxBinary.Element node, List<DmxBinary.Element> path)
            {
                foreach (var c in node.GetElements("children"))
                {
                    path.Add(c);
                    if ((c.GetValue<int>("nodeID") ?? -1).ToString(System.Globalization.CultureInfo.InvariantCulture) == pathOf || (path.Count > 1 && (path[^2].GetValue<int>("nodeID") ?? -1).ToString(System.Globalization.CultureInfo.InvariantCulture) == pathOf))
                        output.WriteLine("PATH " + string.Join(" > ", path.Select(e => $"{e.Type}#{e.GetValue<int>("nodeID")} o={e.GetValue<Vector3>("origin")} a={e.GetValue<Vector3>("angles")}")));
                    Find(c, path);
                    path.RemoveAt(path.Count - 1);
                }
            }
            foreach (var w in doc.OfType("CMapWorld"))
                Find(w, []);
            foreach (var inst in doc.OfType("CMapInstance"))
                output.WriteLine($"INSTANCE #{inst.GetValue<int>("nodeID")} target {inst.Get<DmxBinary.Element>("target")?.Type}#{inst.Get<DmxBinary.Element>("target")?.GetValue<int>("nodeID")}");
        }
        if (Environment.GetEnvironmentVariable("HULL_MESHATTRS") == "1")
        {
            var first = doc.OfType("CMapMesh").First();
            foreach (var (k, v) in first.Attributes)
                output.WriteLine($"MESHATTR {k} : {v?.GetType().Name} {(v is byte[] bytes ? $"{bytes.Length} bytes" : v?.ToString())}");
            foreach (var (k, v) in first.Get<DmxBinary.Element>("meshData")!.Attributes)
                output.WriteLine($"MESHDATA {k} : {v?.GetType().Name}");
        }
        if (Environment.GetEnvironmentVariable("HULL_ATTRS") == "1")
        {
            var seenAttrs = new Dictionary<string, int>();
            foreach (var mesh in doc.OfType("CMapMesh"))
                foreach (var (k, v) in mesh.Attributes)
                    if (k.StartsWith("physics", StringComparison.Ordinal))
                    {
                        var key2 = $"{k}={v}";
                        seenAttrs[key2] = seenAttrs.GetValueOrDefault(key2) + 1;
                    }
            foreach (var root in doc.OfType("CMapWorld"))
                foreach (var (k, v) in root.Attributes)
                    if (k.Contains("simplif", StringComparison.OrdinalIgnoreCase) || k.Contains("physics", StringComparison.OrdinalIgnoreCase))
                        output.WriteLine($"world {k}={v}");
            foreach (var (k, v) in seenAttrs.OrderBy(x => x.Key))
                output.WriteLine($"{k}: {v}");
        }
        var shown = 0;
        foreach (var entity in doc.OfType("CMapEntity"))
        {
            var id = entity.GetValue<int>("nodeID") ?? -1;
            if (!models.TryGetValue(id.ToString(System.Globalization.CultureInfo.InvariantCulture), out var entry))
                continue;
            if ((Environment.GetEnvironmentVariable("HULL_XFORM") == "0" || Environment.GetEnvironmentVariable("HULL_SKIPROT") == "1") && (entity.GetValue<Vector3>("angles") ?? Vector3.Zero) != Vector3.Zero)
            {
                Count(tally, "entity rotated");
                continue;
            }
            var origin = entity.GetValue<Vector3>("origin") ?? Vector3.Zero;
            var className = entity.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname") ?? "";
            var ours = new List<RnHull?>();
            var skip = false;
            var simplified = false;
            var simplifiedHulls = new HashSet<RnHull>(ReferenceEqualityComparer.Instance);
            foreach (var mesh in Meshes(entity))
            {
                if ((Environment.GetEnvironmentVariable("HULL_XFORM") == "0" || Environment.GetEnvironmentVariable("HULL_SKIPROT") == "1") && (mesh.GetValue<Vector3>("angles") ?? Vector3.Zero) != Vector3.Zero)
                {
                    skip = true;
                    break;
                }
                if (Environment.GetEnvironmentVariable("HULL_DEBUGID") is { } dbg && dbg == id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                {
                    var md = mesh.Get<DmxBinary.Element>("meshData")!;
                    var st = md.Get<DmxBinary.Element>("vertexData")!.GetElements("streams").First(x => x.Name.StartsWith("position:", StringComparison.Ordinal));
                    output.WriteLine($"DEBUG entity origin {entity.GetValue<Vector3>("origin"):R} angles {entity.GetValue<Vector3>("angles")} mesh origin {mesh.GetValue<Vector3>("origin"):R} angles {mesh.GetValue<Vector3>("angles")} scales {mesh.GetValue<Vector3>("scales")}");
                    output.WriteLine("DEBUG raw " + string.Join(" ", st.Get<object?[]>("data")!.Take(8).Select(o => ((Vector3)o!).ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
                    if (Environment.GetEnvironmentVariable("HULL_DEBUGMESH") is { } dm && dm == $"{mesh.GetValue<int>("nodeID")}")
                    {
                        var fv = md.Get<DmxBinary.Element>("faceVertexData")!.GetElements("streams").ToList();
                        var nx = md.Get<object?[]>("edgeNextIndices")!.Select(x => (int)x!).ToArray();
                        var tv = md.Get<object?[]>("edgeVertexIndices")!.Select(x => (int)x!).ToArray();
                        var fe = md.Get<object?[]>("faceEdgeIndices")!.Select(x => (int)x!).ToArray();
                        var pos = st.Get<object?[]>("data")!;
                        output.WriteLine("DEBUGMESH streams " + string.Join(" ", fv.Select(x => x.Name)));
                        foreach (var fd in md.Get<DmxBinary.Element>("faceData")!.GetElements("streams"))
                            output.WriteLine($"DEBUGMESH face0 {fd.Name} {fd.Get<object?[]>("data")![0]}");
                        output.WriteLine("DEBUGMESH materials " + string.Join(" ", md.Get<object?[]>("materials")!.Select(o => o?.ToString())));
                        var evd = md.Get<object?[]>("edgeVertexDataIndices")!.Select(x => (int)x!).ToArray();
                        // Every corner in triangulation order: face, texcoord.
                        var tc = fv.First(x => x.Name == "texcoord:0").Get<object?[]>("data")!;
                        using var dump = new StreamWriter(Path.Combine(Path.GetTempPath(), $"corners_{dm}.txt"));
                        for (var f = 0; f < fe.Length; f++)
                        {
                            var loop = new List<int>();
                            var e0 = fe[f];
                            do { loop.Add(e0); e0 = nx[e0]; } while (e0 != fe[f]);
                            var lp = loop.Select(x => (Vector3)pos[md.Get<object?[]>("vertexDataIndices")!.Select(y => (int)y!).ToArray()[tv[x]]]!).ToArray();
                            int[] cut = loop.Count == 3 ? [0, 1, 2] : Source2.Compiler.Maps.PolygonTriangulator.Triangulate(lp);
                            foreach (var j in cut)
                            {
                                var uv = (Vector2)tc[evd[loop[j]]]!;
                                dump.WriteLine($"{f} {uv.X:R} {uv.Y:R}");
                            }
                        }
                        var vdi = md.Get<object?[]>("vertexDataIndices")!.Select(x => (int)x!).ToArray();
                        for (int e = fe[0], k = 0; k < 6; e = nx[e], k++)
                            output.WriteLine($"DEBUGMESH e{e} fv{evd[e]} v{tv[e]} vd{vdi[tv[e]]} {(Vector3)pos[vdi[tv[e]]]!:R} " + string.Join(" | ", fv.Select(x => x.Get<object?[]>("data")![evd[e]]?.ToString())));
                    }
                    foreach (var holder in new[] { "vertexData", "faceVertexData", "edgeData", "faceData" })
                        foreach (var sx in md.Get<DmxBinary.Element>(holder)?.GetElements("streams") ?? [])
                            output.WriteLine($"DEBUG {holder} {sx.Name} {string.Join(" ", (sx.Get<object?[]>("data") ?? []).Take(4).Select(o => o?.ToString()))}");
                }
                if (Corners() is { } weldIns)
                    CompareCorners(weldIns, tally, mesh, $"{entry.GetFullPath()} ({className}) mesh {mesh.GetValue<int>("nodeID")}", ref shown, show);
                var stored = PhysicsTypeOf(mesh);
                var matNames = (mesh.Get<DmxBinary.Element>("meshData")?.Get<object?[]>("materials") ?? []).Select(x => Path.GetFileNameWithoutExtension(x as string ?? "")).ToArray();
                if (Environment.GetEnvironmentVariable("HULL_NOCLIP") == "1" && matNames.Length > 0 && matNames.All(m => m.Contains("clip", StringComparison.OrdinalIgnoreCase)))
                    continue;
                var type = BrushHulls.Resolve(stored, true, className == "func_shatterglass", false, false);
                var (positions, faces, local) = Read(mesh, origin, fromStart, entity);
                var materialOf = MaterialIndices(mesh, faces.Length);
                var runs = HullSimplifier.Runs;
                try
                {
                    foreach (var m in materialOf.Distinct().OrderBy(x => x))
                    {
                        if (m >= 0 && m < matNames.Length && Environment.GetEnvironmentVariable("HULL_NOCLIP") == "1" && matNames[m].Contains("clip", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var piece = faces.Where((_, f) => materialOf[f] == m).ToArray();
                        if (Environment.GetEnvironmentVariable("HULL_WELD") != "0")
                            (positions, piece, local) = Welded(mesh, entity!, m);
                        if (Environment.GetEnvironmentVariable("HULL_PIECES") is { } hp && hp == id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        {
                            var tm = BrushHulls.TriangleMesh(positions, piece, local);
                            var lo = tm.Points.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
                            var hi = tm.Points.Aggregate(new Vector3(float.MinValue), Vector3.Max);
                            output.WriteLine($"PIECE mesh {mesh.GetValue<int>("nodeID")} {type} material {m} {(m >= 0 && m < matNames.Length ? matNames[m] : "?")}: {piece.Length} faces, {tm.Points.Count} points, {lo} .. {hi}");
                        }
                        if (Phys() is { } captured && type != BrushHulls.PhysicsType.Mesh && type != BrushHulls.PhysicsType.None)
                            ComparePhys(captured, tally, BrushHulls.TriangleMesh(positions, piece, local), $"{entry.GetFullPath()} ({className}) mesh {mesh.GetValue<int>("nodeID")} material {m}", ref shown, show);
                        // BrushHulls.Build one input at a time, to see which
                        // hulls went through the simplifier.
                        foreach (var input in BrushHulls.Inputs(positions, piece, type, local))
                        {
                            var before = HullSimplifier.Runs;
                            var qh = RnHullBuilder.BuildHull(input, RnHullBuilder.Options.MapBuilder, out _);
                            var points = qh == null ? null : BrushHulls.ShapePoints([.. qh.HullVertices.Select(v => new Vector3(v.X, v.Y, v.Z))]);
                            var hull = points == null ? null : RnHullBuilder.Create(points, RnHullBuilder.Options.Compile, out _);
                            // A piece the builder cannot hull adds nothing.
                            if (hull == null)
                                continue;
                            hull.RegionSvm = RegionSvmBuilder.Build(hull);
                            RnHullBuilder.Transform(hull, RnHullBuilder.Identity);
                            if (HullSimplifier.Runs != before)
                                simplifiedHulls.Add(hull);
                            ours.Add(hull);
                        }
                        if (Environment.GetEnvironmentVariable("HULL_BRUTE") == "1")
                            Brute(entry, package, positions, piece, type, local);
                    }
                }
                catch (NotSupportedException ex)
                {
                    Count(tally, "unported: " + ex.Message);
                    skip = true;
                    break;
                }
                simplified |= HullSimplifier.Runs != runs;
            }
            if (skip)
                continue;
            package.ReadEntry(entry, out var bytes);
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            var shipped = resource.DataBlock is Model model && model.GetEmbeddedPhys() is { } phys
                ? phys.Parts.SelectMany(p => p.Shape.Hulls).Select(h => h.Shape).ToList()
                : [];
            if (shipped.Count != ours.Count)
            {
                Count(tally, "hull count differs");
                if (shown++ < show)
                {
                    output.WriteLine($"{entry.GetFullPath()} ({className}): shipped {shipped.Count} hulls, ours {ours.Count}; shipped parts " +
                        string.Join(" ", (resource.DataBlock as Model)?.GetEmbeddedPhys()?.Parts.Select(p => $"[{p.Shape.Hulls.Length} hulls {p.Shape.Meshes.Length} meshes {p.Shape.Spheres.Length} spheres {p.Shape.Capsules.Length} capsules]") ?? []));
                    foreach (var mesh in Meshes(entity))
                    {
                        mesh.Attributes.TryGetValue("physicsType", out var pt);
                        var (positions, faces, local) = Read(mesh, origin, fromStart, entity);
                        var inputs = BrushHulls.Inputs(positions, faces, BrushHulls.PhysicsType.ConvexMulti);
                        var mats = string.Join(",", (mesh.Get<DmxBinary.Element>("meshData")?.Get<object?[]>("materials") ?? []).Select(x => Path.GetFileNameWithoutExtension(x as string ?? "")));
                        output.WriteLine($"  mesh {mesh.GetValue<int>("nodeID")} [{mats}] physicsType={pt ?? "(absent)"} ({pt?.GetType().Name}) verts {positions.Length} faces {faces.Length} groups [{string.Join(",", inputs.Select(i => i.Length))}]");
                    }
                    if (resource.DataBlock is Model m2 && m2.GetEmbeddedPhys() is { } p2)
                        foreach (var part in p2.Parts)
                            foreach (var hd in part.Shape.Hulls)
                                output.WriteLine($"  shipped part hull verts {hd.Shape.GetVertexPositions().Length} min {hd.Shape.Min} max {hd.Shape.Max} attr {hd.CollisionAttributeIndex} surf {hd.SurfacePropertyIndex} name {hd.UserFriendlyName}");
                    foreach (var h in ours)
                        output.WriteLine($"  our hull min {h?.BoundsMin} max {h?.BoundsMax}");
                }
                continue;
            }
            var pool = ours.ToList();
            for (var i = 0; i < shipped.Count; i++)
            {
                var want = new HashSet<Vector3>(shipped[i].GetVertexPositions().ToArray());
                var match = pool.FirstOrDefault(h => h != null && h.VertexPositions.Length == want.Count && h.VertexPositions.All(want.Contains));
                if (match == null && Environment.GetEnvironmentVariable("HULL_NEAR") == "1")
                {
                    var c = shipped[i].Centroid;
                    var near = pool.Where(h => h != null).OrderBy(h => Vector3.Distance(h!.Centroid, c)).FirstOrDefault();
                    if (near != null && shown < show)
                    {
                        var wantList = shipped[i].GetVertexPositions().ToArray();
                        var diffPts = near.VertexPositions.Where(p => !want.Contains(p)).Take(3).Select(p => $"{p} (nearest valve {wantList.OrderBy(q => Vector3.Distance(p, q)).First()})");
                        output.WriteLine($"  near miss: valve {want.Count} verts, ours {near.VertexPositions.Length}; ours not in valve: {string.Join(" ", diffPts)}");
                    }
                }
                if (match != null)
                    pool.Remove(match);
                if (Environment.GetEnvironmentVariable("HULL_SVM") == "1" && _svmShown++ < 3 && shipped[i].RegionSVM is { } svm)
                {
                    output.WriteLine($"SVM {entry.GetFullPath()} hull {i}: {shipped[i].GetVertexPositions().Length} verts, {shipped[i].GetPlanes().Length} faces");
                    foreach (var line in svm.Data.ToKV3String().Split((char)10).Take(80))
                        output.WriteLine("SVM   " + line.TrimEnd());
                }
                var diff = Differences(shipped[i], match);
                var verdict = diff.Count == 0 ? "hull exact" : diff[0].Split(' ')[0];
                Count(tally, $"class {className}: {verdict}");
                Count(tally, verdict);
                if (match != null && SvmDifference(shipped[i], match) is var svmDiff)
                {
                    Count(tally, "svm " + (svmDiff ?? "exact").Split(' ')[0]);
                    if (svmDiff != null && _svmDiffShown++ < show)
                        output.WriteLine($"SVMDIFF {entry.GetFullPath()} hull {i} ({diff.Count == 0}): {svmDiff}");
                }
                var viaSimplifier = match != null ? simplifiedHulls.Contains(match) : simplified;
                if (viaSimplifier)
                {
                    Count(tally, "simplified, " + verdict);
                    output.WriteLine($"SIMPLIFIED {entry.GetFullPath()} ({className}) hull {i}: {(diff.Count == 0 ? "exact" : string.Join("; ", diff.Take(6)))}");
                }
                if (diff.Count > 0 && shown++ < show)
                    output.WriteLine($"{entry.GetFullPath()} ({className}) hull {i}: {string.Join("; ", diff.Take(6))}");
            }
        }
        foreach (var (k, v) in tally.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            output.WriteLine($"{k}: {v}");
    }

    // HULL_PHYS=<capture_weld.py --phys output>: the triangle mesh Valve's
    // map builder hulled each brush piece from, keyed by its positions.
    private static Dictionary<string, List<(Vector3[] Points, int[] Triangles)>>? _phys;
    private static List<(Vector3[] Points, int[] Triangles)> _physAll = [];

    private static Dictionary<string, List<(Vector3[] Points, int[] Triangles)>>? Phys()
    {
        if (_phys != null || Environment.GetEnvironmentVariable("HULL_PHYS") is not { } path)
            return _phys;
        _phys = [];
        var data = File.ReadAllBytes(path);
        for (var at = 0; at < data.Length;)
        {
            var n = BitConverter.ToInt32(data, at);
            var head = System.Text.Json.JsonDocument.Parse(data.AsMemory(at + 4, n)).RootElement;
            at += 4 + n;
            var m = BitConverter.ToInt32(data, at);
            var blob = data.AsSpan(at + 4, m);
            at += 4 + m;
            if (head.GetProperty("ev").GetString() != "phys")
                continue;
            var count = head.GetProperty("n").GetInt32();
            var tris = head.GetProperty("t").GetInt32();
            var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(blob[..(count * 12)]);
            var pts = new Vector3[count];
            for (var i = 0; i < count; i++)
                pts[i] = new Vector3(floats[i * 3], floats[i * 3 + 1], floats[i * 3 + 2]);
            var idx = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(blob.Slice(count * 12, tris * 12)).ToArray();
            var rec = (pts, idx);
            _physAll.Add(rec);
            var key = PhysKey(pts);
            if (!_phys.TryGetValue(key, out var list))
                _phys[key] = list = [];
            list.Add(rec);
        }
        return _phys;
    }

    private static string PhysKey(IEnumerable<Vector3> pts) =>
        string.Join(",", pts.Select(p => $"{BitConverter.SingleToInt32Bits(p.X)}:{BitConverter.SingleToInt32Bits(p.Y)}:{BitConverter.SingleToInt32Bits(p.Z)}"));

    private void ComparePhys(Dictionary<string, List<(Vector3[] Points, int[] Triangles)>> phys, Dictionary<string, int> tally,
        (List<Vector3> Points, List<(int A, int B, int C)> Triangles) ours, string label, ref int shown, int show)
    {
        var tris = ours.Triangles.SelectMany(t => new[] { t.A, t.B, t.C }).ToArray();
        if (phys.TryGetValue(PhysKey(ours.Points), out var hits))
        {
            Count(tally, hits.Any(h => h.Triangles.SequenceEqual(tris)) ? "phys exact" : "phys same points, triangles differ");
            return;
        }
        var set = ours.Points.ToHashSet();
        var best = _physAll.OrderByDescending(r => r.Points.Count(set.Contains) * 2 - r.Points.Length).First();
        var overlap = best.Points.Count(set.Contains);
        var verdict = overlap == best.Points.Length && overlap == set.Count ? "phys same set, order differs" : "phys points differ";
        Count(tally, verdict);
        if (shown++ < show)
        {
            output.WriteLine($"PHYS {label}: {verdict}; ours {ours.Points.Count} points, nearest valve piece {best.Points.Length}, shared {overlap}");
            var vs = best.Points.ToHashSet();
            foreach (var p in ours.Points.Where(p => !vs.Contains(p)).Take(4))
                output.WriteLine($"   ours only {p:R}  nearest valve {best.Points.OrderBy(q => Vector3.Distance(p, q)).First():R}");
            if (verdict == "phys same set, order differs")
            {
                var at = Enumerable.Range(0, best.Points.Length).First(i => ours.Points[i] != best.Points[i]);
                var map = best.Points.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);
                output.WriteLine($"   first difference at {at}; ours as valve indices: {string.Join(",", ours.Points.Select(p => map[p]))}");
                output.WriteLine($"   ours tris  {string.Join(" ", ours.Triangles.Take(12).Select(t => $"{map[ours.Points[t.A]]}/{map[ours.Points[t.B]]}/{map[ours.Points[t.C]]}"))}");
                output.WriteLine($"   valve tris {string.Join(" ", Enumerable.Range(0, Math.Min(12, best.Triangles.Length / 3)).Select(t => $"{best.Triangles[t * 3]}/{best.Triangles[t * 3 + 1]}/{best.Triangles[t * 3 + 2]}"))}");
            }
        }
    }

    // HULL_CORNERS=<capture_weld.py output>: the per-corner meshes Valve's
    // map builder welded, keyed by their corner positions.
    private static Dictionary<string, List<(string[] Names, int Stride, float[] V)>>? _corners;

    private static Dictionary<string, List<(string[] Names, int Stride, float[] V)>>? Corners()
    {
        if (_corners != null || Environment.GetEnvironmentVariable("HULL_CORNERS") is not { } path)
            return _corners;
        _corners = [];
        var data = File.ReadAllBytes(path);
        for (var at = 0; at < data.Length;)
        {
            var n = BitConverter.ToInt32(data, at);
            var head = System.Text.Json.JsonDocument.Parse(data.AsMemory(at + 4, n)).RootElement;
            at += 4 + n;
            var m = BitConverter.ToInt32(data, at);
            var blob = data.AsSpan(at + 4, m);
            at += 4 + m;
            if (head.GetProperty("ev").GetString() != "in")
                continue;
            var nv = head.GetProperty("nv").GetInt32();
            var stride = head.GetProperty("stride").GetInt32();
            var v = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(blob[..(nv * stride * 4)]).ToArray();
            var names = head.GetProperty("streams").EnumerateArray().Select(x => x.GetProperty("name").GetString() ?? "").ToArray();
            var key = CornerKey(v, stride, nv);
            if (!_corners.TryGetValue(key, out var list))
                _corners[key] = list = [];
            list.Add((names, stride, v));
        }
        return _corners;
    }

    private static string CornerKey(float[] v, int stride, int count) =>
        string.Join(",", Enumerable.Range(0, count).Select(i => $"{BitConverter.SingleToInt32Bits(v[i * stride])}:{BitConverter.SingleToInt32Bits(v[i * stride + 1])}:{BitConverter.SingleToInt32Bits(v[i * stride + 2])}"));

    private void CompareCorners(Dictionary<string, List<(string[] Names, int Stride, float[] V)>> weldIns, Dictionary<string, int> tally,
        DmxBinary.Element mesh, string label, ref int shown, int show)
    {
        foreach (var piece in Source2.Compiler.Maps.MapMeshCorners.Build(mesh))
        {
            var count = piece.Indices.Length;
            if (!weldIns.TryGetValue(CornerKey(piece.Vertices, piece.Stride, count), out var hits))
            {
                Count(tally, "corners: no weld with these positions");
                continue;
            }
            var names = piece.Streams.Select(x => x.Name).ToArray();
            // Copies of a mesh share positions; take the capture that agrees most.
            var hit = hits.MinBy(h => h.Stride != piece.Stride ? int.MaxValue
                : Enumerable.Range(0, piece.Vertices.Length).Count(i => BitConverter.SingleToInt32Bits(piece.Vertices[i]) != BitConverter.SingleToInt32Bits(h.V[i])));
            if (!hit.Names.SequenceEqual(names) || hit.Stride != piece.Stride)
            {
                Count(tally, "corners: other streams");
                if (Environment.GetEnvironmentVariable("HULL_CORNERS_STREAMS") == "1")
                    output.WriteLine($"CORNERS {label} material {piece.Material} {(mesh.Get<DmxBinary.Element>("meshData")?.Get<object?[]>("materials") is { } mats && piece.Material < mats.Length ? mats[piece.Material] : "?")}: valve [{string.Join(",", hit.Names)}] ours [{string.Join(",", names)}]");
                continue;
            }
            var bad = new SortedSet<string>();
            for (var i = 0; i < piece.Vertices.Length; i++)
            {
                if (BitConverter.SingleToInt32Bits(piece.Vertices[i]) != BitConverter.SingleToInt32Bits(hit.V[i]))
                    bad.Add(piece.Streams.Last(x => x.First <= i % piece.Stride).Name + (MathF.Abs(piece.Vertices[i] - hit.V[i]) < 1e-4f ? " ulps" : ""));
            }
            Count(tally, bad.Count == 0 ? "corners: exact" : "corners: " + string.Join("+", bad) + " differ");
            if (Environment.GetEnvironmentVariable("HULL_CORNERS_PIECE") is { } pieceOf && label.EndsWith("mesh " + pieceOf, StringComparison.Ordinal))
                for (var c = 0; c < count; c++)
                    output.WriteLine($"PIECE {c} ours [{string.Join(" ", piece.Vertices.Skip(c * piece.Stride).Take(piece.Stride).Select(x => x.ToString("R")))}] valve [{string.Join(" ", hit.V.Skip(c * piece.Stride).Take(piece.Stride).Select(x => x.ToString("R")))}]");
            if (bad.Count > 0 && shown++ < show)
            {
                var i = Enumerable.Range(0, piece.Vertices.Length).First(k => BitConverter.SingleToInt32Bits(piece.Vertices[k]) != BitConverter.SingleToInt32Bits(hit.V[k]));
                var c = i / piece.Stride;
                output.WriteLine($"CORNERS {label} material {piece.Material}: corner {c} ours [{string.Join(" ", piece.Vertices.Skip(c * piece.Stride).Take(piece.Stride).Select(x => x.ToString("R")))}] valve [{string.Join(" ", hit.V.Skip(c * piece.Stride).Take(piece.Stride).Select(x => x.ToString("R")))}]");
            }
        }
    }

    private int _bruteShown;
    private int _svmShown;
    private int _svmDiffShown;
    private string? _lastThrow;

    // For a small piece whose hull does not come out exact, every ordering of
    // its point list through both stages, printed as vmap vertex indices.
    private void Brute(PackageEntry entry, Package package, Vector3[] positions, int[][] piece, BrushHulls.PhysicsType type, Vector3[] local)
    {
        if (_bruteShown >= (int.TryParse(Environment.GetEnvironmentVariable("HULL_BRUTE_MAX"), out var bm) ? bm : 6))
            return;
        package.ReadEntry(entry, out var bytes);
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        if (resource.DataBlock is not Model model || model.GetEmbeddedPhys() is not { } phys)
            return;
        var shipped = phys.Parts.SelectMany(p => p.Shape.Hulls).Select(h => h.Shape).ToList();
        foreach (var input in BrushHulls.Inputs(positions, piece, type, local))
        {
            if (input.Length > 8 || input.Length < 5)
                continue;
            RnHull? Run(Vector3[] pts)
            {
                try
                {
                    var qh = RnHullBuilder.BuildHull(pts, RnHullBuilder.Options.MapBuilder, out _);
                    var sp = qh == null ? null : BrushHulls.ShapePoints(qh.HullVertices.Select(v => new Vector3(v.X, v.Y, v.Z)).ToArray());
                    var hh = sp == null ? null : RnHullBuilder.Create(sp, null, out _);
                    if (hh != null) RnHullBuilder.Transform(hh, RnHullBuilder.Identity);
                    return hh;
                }
                catch (NotSupportedException ex) { _lastThrow = ex.Message; return null; }
            }
            bool Exact(RnHull? h) => h != null && shipped.Any(v => Differences(v, h).Count == 0);
            _lastThrow = null;
            var mine = Run(input);
            if (Exact(mine))
                continue;
            if (_lastThrow != null)
            {
                output.WriteLine($"THROW {entry.GetFullPath()}: {_lastThrow}");
                continue;
            }
            if (mine != null)
            {
                var near = shipped.OrderBy(v => Vector3.Distance(v.Centroid, mine.Centroid)).First();
                output.WriteLine($"DIFF {entry.GetFullPath()}: {string.Join("; ", Differences(near, mine).Take(4))}");
            }
            if (Environment.GetEnvironmentVariable("HULL_BRUTE_NOPERM") == "1")
                continue;
            string Label(Vector3 p) => string.Join("/", Enumerable.Range(0, positions.Length).Where(i => positions[i] == p));
            var hits = new List<string>();
            foreach (var perm in Perms(input.Length))
            {
                var pts = perm.Select(i => input[i]).ToArray();
                if (Exact(Run(pts)))
                    hits.Add(string.Join(" ", pts.Select(Label)));
            }
            _bruteShown++;
            if (Environment.GetEnvironmentVariable("HULL_BRUTE_DUMP") is { Length: > 0 } dump)
                File.AppendAllLines(dump, [$"# {entry.GetFullPath()} ours {string.Join(" ", input.Select(Label))}", "# faces " + string.Join(" ", piece.Select(f => string.Join(",", f))), "# pos " + string.Join(" ", positions.Select((q, i) => $"{i}:{q.X:R},{q.Y:R},{q.Z:R}")), .. hits]);
            output.WriteLine($"BRUTE {entry.GetFullPath()}: ours [{string.Join(" ", input.Select(Label))}], {hits.Count} exact orderings: {string.Join(" | ", hits.Take(3))}");
            output.WriteLine($"  faces: {string.Join(" ", piece.Select(f => "(" + string.Join(",", f) + ")"))}");
        }
    }

    private static IEnumerable<int[]> Perms(int n)
    {
        var a = Enumerable.Range(0, n).ToArray();
        var c = new int[n];
        yield return (int[])a.Clone();
        var i = 0;
        while (i < n)
        {
            if (c[i] < i)
            {
                if (i % 2 == 0) (a[0], a[i]) = (a[i], a[0]);
                else (a[c[i]], a[i]) = (a[i], a[c[i]]);
                yield return (int[])a.Clone();
                c[i]++;
                i = 0;
            }
            else
            {
                c[i] = 0;
                i++;
            }
        }
    }

    private static int[] MaterialIndices(DmxBinary.Element mesh, int count)
    {
        var data = mesh.Get<DmxBinary.Element>("meshData")!;
        var stream = data.Get<DmxBinary.Element>("faceData")?.GetElements("streams").FirstOrDefault(x => x.Name.StartsWith("materialindex:", StringComparison.Ordinal));
        var raw = stream?.Get<object?[]>("data") ?? [];
        return [.. Enumerable.Range(0, count).Select(i => i < raw.Length && raw[i] is int v ? v : 0)];
    }

    // Meshes under the entity, depth first in children order.
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

    private static void Count(Dictionary<string, int> tally, string key) => tally[key] = tally.GetValueOrDefault(key) + 1;

    private static BrushHulls.PhysicsType PhysicsTypeOf(DmxBinary.Element mesh)
    {
        if (!mesh.Attributes.TryGetValue("physicsType", out var v))
            return BrushHulls.PhysicsType.Default;
        return v switch
        {
            int i => (BrushHulls.PhysicsType)i,
            string t => t switch
            {
                "none" => BrushHulls.PhysicsType.None,
                "convex_single" => BrushHulls.PhysicsType.ConvexSingle,
                "convex_multi" => BrushHulls.PhysicsType.ConvexMulti,
                "mesh" => BrushHulls.PhysicsType.Mesh,
                _ => BrushHulls.PhysicsType.Default,
            },
            _ => BrushHulls.PhysicsType.Default,
        };
    }

    // A material piece as the map builder hulls it (BrushHulls.Pieces);
    // HULL_WELD=0 goes back to the unwelded .vmap faces.
    private static (Vector3[] Positions, int[][] Faces, Vector3[] Local) Welded(DmxBinary.Element mesh, DmxBinary.Element entity, int material)
    {
        foreach (var (m, positions, faces, local) in BrushHulls.Pieces(mesh, entity))
            if (m == material)
                return (positions, faces, local);
        return ([], [], []);
    }

    // Entity-local positions (an unrotated mesh: scaled, moved to its origin,
    // less the entity's origin) and each face's vertex loop.
    private static (Vector3[] Positions, int[][] Faces, Vector3[] Local) Read(DmxBinary.Element mesh, Vector3 entityOrigin, bool fromStart, DmxBinary.Element? entity = null)
    {
        var data = mesh.Get<DmxBinary.Element>("meshData")!;
        var stream = data.Get<DmxBinary.Element>("vertexData")!.GetElements("streams")
            .First(x => x.Name.StartsWith("position:", StringComparison.Ordinal));
        var raw = stream.Get<object?[]>("data")!;
        var scales = mesh.GetValue<Vector3>("scales") ?? Vector3.One;
        var origin = mesh.GetValue<Vector3>("origin") ?? Vector3.Zero;
        var mode = Environment.GetEnvironmentVariable("HULL_XFORM") ?? "3";
        Vector3[] positions;
        if (mode == "0")
            positions = raw.Select(p => ((Vector3)p! * scales) + origin - entityOrigin).ToArray();
        else if (mode == "3")
        {
            // The map builder's CTransforms (FUN_18020b230): the mesh node's
            // into the world, then the entity's inverse.
            var toWorld = Source2.Compiler.Maps.CTransform.FromNode(mesh).Matrix();
            var toEntity = Source2.Compiler.Maps.CTransform.FromNode(entity!).Inverse().Matrix();
            positions = raw.Select(p => Source2.Compiler.Maps.MapMeshes.Transform(toEntity, Source2.Compiler.Maps.MapMeshes.Transform(toWorld, (Vector3)p! * scales))).ToArray();
        }
        else
        {
            var e = Source2.Compiler.Maps.MapMeshes.Local(entity!);
            var m = Source2.Compiler.Maps.MapMeshes.Local(mesh);
            var inv = Source2.Compiler.Maps.MapMeshes.Invert(e);
            if (mode == "1")
            {
                var em = Source2.Compiler.Maps.MapMeshes.Concat(inv, m);
                positions = raw.Select(p => Source2.Compiler.Maps.MapMeshes.Transform(em, (Vector3)p! * scales)).ToArray();
            }
            else
                positions = raw.Select(p => Source2.Compiler.Maps.MapMeshes.Transform(inv, Source2.Compiler.Maps.MapMeshes.Transform(m, (Vector3)p! * scales))).ToArray();
        }
        int[] Ints(string name) => (data.Get<object?[]>(name) ?? []).Select(x => x is int i ? i : -1).ToArray();
        var next = Ints("edgeNextIndices");
        var to = Ints("edgeVertexIndices");
        var first = Ints("faceEdgeIndices");
        var faces = new int[first.Length][];
        for (var f = 0; f < first.Length; f++)
        {
            var loop = new List<int>();
            var e = first[f];
            do
            {
                loop.Add(to[e]);
                e = next[e];
            } while (e != first[f] && loop.Count <= next.Length);
            if (fromStart && loop.Count > 0)
                loop.Insert(0, loop[^1]);
            if (fromStart && loop.Count > 0)
                loop.RemoveAt(loop.Count - 1);
            faces[f] = [.. loop];
        }
        return (positions, faces, [.. raw.Select(p => (Vector3)p! * scales)]);
    }

    // The region SVM against Valve's: null when every plane float and node matches.
    private static string? SvmDifference(ValveResourceFormat.ResourceTypes.RubikonPhysics.Shapes.Hull valve, RnHull ours)
    {
        if (valve.RegionSVM is not { } svm)
            return ours.RegionSvm == null ? null : "extra (valve has none)";
        if (ours.RegionSvm is not { } mine)
            return "missing";
        var planes = svm.Data.GetArray<byte>("m_Planes");
        var nodes = svm.Data.GetArray<byte>("m_Nodes");
        if (planes.Length != mine.Planes.Length * 16)
            return $"planes valve {planes.Length / 16} ours {mine.Planes.Length} (hull {ours.Faces.Length} faces, {ours.Edges.Length} half-edges)";
        for (var i = 0; i < mine.Planes.Length; i++)
        {
            var (n, d) = mine.Planes[i];
            float[] ourFloats = [n.X, n.Y, n.Z, d];
            for (var k = 0; k < 4; k++)
            {
                var theirs = BitConverter.ToInt32(planes, (i * 16) + (k * 4));
                if (theirs != BitConverter.SingleToInt32Bits(ourFloats[k]))
                    return $"plane {i}.{k} valve {BitConverter.Int32BitsToSingle(theirs):R} ours {ourFloats[k]:R}";
            }
        }
        if (nodes.Length != mine.Nodes.Length * 4)
            return $"nodes valve {nodes.Length / 4} ours {mine.Nodes.Length}";
        for (var i = 0; i < mine.Nodes.Length; i++)
            if (BitConverter.ToUInt32(nodes, i * 4) != mine.Nodes[i])
                return $"node {i} valve {BitConverter.ToUInt32(nodes, i * 4):x8} ours {mine.Nodes[i]:x8}";
        return null;
    }

    private static List<string> Differences(ValveResourceFormat.ResourceTypes.RubikonPhysics.Shapes.Hull valve, RnHull? ours)
    {
        if (ours == null)
            return [$"failed (no hull of ours with these {valve.GetVertexPositions().Length} positions)"];
        var diffs = new List<string>();
        var points = valve.GetVertexPositions().ToArray();
        if (points.Length != ours.VertexPositions.Length)
            return [$"vertices valve {points.Length} ours {ours.VertexPositions.Length}"];
        var set = new HashSet<Vector3>(points);
        var moved = ours.VertexPositions.Count(p => !set.Contains(p));
        if (moved > 0)
            return [$"positions {moved} of {points.Length} differ"];
        if (!points.SequenceEqual(ours.VertexPositions))
            diffs.Add("order");
        var d = valve.Data;
        void F(string name, float a, float b)
        {
            if (BitConverter.SingleToInt32Bits(a) != BitConverter.SingleToInt32Bits(b))
                diffs.Add($"float {name} valve {a:R} ours {b:R}");
        }
        var mass = d.GetArray<double>("m_MassProperties");
        for (var i = 0; i < 12; i++)
            F($"mass[{i}]", (float)mass[i], ours.MassProperties[i]);
        F("volume", valve.Volume, ours.Volume);
        F("area", d.GetFloatProperty("m_flSurfaceArea"), ours.SurfaceArea);
        F("radius", valve.MaxAngularRadius, ours.MaxAngularRadius);
        var edges = valve.GetEdges().ToArray();
        if (edges.Length != ours.Edges.Length || Enumerable.Range(0, edges.Length).Any(i => (edges[i].Next, edges[i].Twin, edges[i].Origin, edges[i].Face) != ours.Edges[i]))
            diffs.Add("topology");
        return diffs;
    }
}
