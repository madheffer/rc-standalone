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
                }
                var stored = PhysicsTypeOf(mesh);
                var matNames = (mesh.Get<DmxBinary.Element>("meshData")?.Get<object?[]>("materials") ?? []).Select(x => Path.GetFileNameWithoutExtension(x as string ?? "")).ToArray();
                if (Environment.GetEnvironmentVariable("HULL_NOCLIP") == "1" && matNames.Length > 0 && matNames.All(m => m.Contains("clip", StringComparison.OrdinalIgnoreCase)))
                    continue;
                var type = BrushHulls.Resolve(stored, true, className == "func_shatterglass", false, false);
                var (positions, faces) = Read(mesh, origin, fromStart, entity);
                var materialOf = MaterialIndices(mesh, faces.Length);
                var runs = HullSimplifier.Runs;
                try
                {
                    foreach (var m in materialOf.Distinct().OrderBy(x => x))
                    {
                        if (m >= 0 && m < matNames.Length && Environment.GetEnvironmentVariable("HULL_NOCLIP") == "1" && matNames[m].Contains("clip", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var piece = faces.Where((_, f) => materialOf[f] == m).ToArray();
                        // BrushHulls.Build one input at a time, to see which
                        // hulls went through the simplifier.
                        foreach (var input in BrushHulls.Inputs(positions, piece, type))
                        {
                            var before = HullSimplifier.Runs;
                            var qh = RnHullBuilder.BuildHull(input, RnHullBuilder.Options.MapBuilder, out _);
                            var points = qh == null ? null : BrushHulls.ShapePoints([.. qh.HullVertices.Select(v => new Vector3(v.X, v.Y, v.Z))]);
                            var hull = points == null ? null : RnHullBuilder.Create(points, RnHullBuilder.Options.Compile, out _);
                            if (hull != null)
                            {
                                RnHullBuilder.Transform(hull, RnHullBuilder.Identity);
                                if (HullSimplifier.Runs != before)
                                    simplifiedHulls.Add(hull);
                            }
                            ours.Add(hull);
                        }
                        if (Environment.GetEnvironmentVariable("HULL_BRUTE") == "1")
                            Brute(entry, package, positions, piece, type);
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
                    output.WriteLine($"{entry.GetFullPath()} ({className}): shipped {shipped.Count} hulls, ours {ours.Count}");
                    foreach (var mesh in Meshes(entity))
                    {
                        mesh.Attributes.TryGetValue("physicsType", out var pt);
                        var (positions, faces) = Read(mesh, origin, fromStart, entity);
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
                var diff = Differences(shipped[i], match);
                var verdict = diff.Count == 0 ? "hull exact" : diff[0].Split(' ')[0];
                Count(tally, verdict);
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

    private int _bruteShown;
    private string? _lastThrow;

    // For a small piece whose hull does not come out exact, every ordering of
    // its point list through both stages, printed as vmap vertex indices.
    private void Brute(PackageEntry entry, Package package, Vector3[] positions, int[][] piece, BrushHulls.PhysicsType type)
    {
        if (_bruteShown >= (int.TryParse(Environment.GetEnvironmentVariable("HULL_BRUTE_MAX"), out var bm) ? bm : 6))
            return;
        package.ReadEntry(entry, out var bytes);
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        if (resource.DataBlock is not Model model || model.GetEmbeddedPhys() is not { } phys)
            return;
        var shipped = phys.Parts.SelectMany(p => p.Shape.Hulls).Select(h => h.Shape).ToList();
        foreach (var input in BrushHulls.Inputs(positions, piece, type))
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

    // Entity-local positions (an unrotated mesh: scaled, moved to its origin,
    // less the entity's origin) and each face's vertex loop.
    private static (Vector3[] Positions, int[][] Faces) Read(DmxBinary.Element mesh, Vector3 entityOrigin, bool fromStart, DmxBinary.Element? entity = null)
    {
        var data = mesh.Get<DmxBinary.Element>("meshData")!;
        var stream = data.Get<DmxBinary.Element>("vertexData")!.GetElements("streams")
            .First(x => x.Name.StartsWith("position:", StringComparison.Ordinal));
        var raw = stream.Get<object?[]>("data")!;
        var scales = mesh.GetValue<Vector3>("scales") ?? Vector3.One;
        var origin = mesh.GetValue<Vector3>("origin") ?? Vector3.Zero;
        var mode = Environment.GetEnvironmentVariable("HULL_XFORM") ?? "2";
        Vector3[] positions;
        if (mode == "0")
            positions = raw.Select(p => ((Vector3)p! * scales) + origin - entityOrigin).ToArray();
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
        return (positions, faces);
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
