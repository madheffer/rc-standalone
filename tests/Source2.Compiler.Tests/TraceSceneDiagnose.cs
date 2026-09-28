using System.Buffers.Binary;
using System.Numerics;
using Source2.Compiler.Maps;
using ValvePak;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Where <see cref="TraceScene.Triangles"/> and the .rte part: each of our
/// triangles not found, by what its mesh is (instanced, rotated, subdivided,
/// parent), and each file triangle not produced, as a near miss of one of ours
/// (every corner within 0.01) or with nothing of ours there.
/// <c>TRACEDIAG=&lt;addon&gt;;&lt;map&gt;;&lt;compiled in&gt;</c>.
/// </summary>
public class TraceSceneDiagnose(ITestOutputHelper output)
{
    [Fact]
    public void Where()
    {
        if (Environment.GetEnvironmentVariable("TRACEDIAG") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split(';');
        var (addon, map, compiledIn) = (parts[0], parts[1], parts[2]);
        var source = MapFixtures.VmapSource(addon, map)!;
        var rtePath = Path.Combine(Path.GetTempPath(), "csgo_addons", compiledIn, "maps", map + ".rte");
        var pak = CS2Fixtures.StockPak()!;
        var schema = MapFixtures.GameSchema()!;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        var packages = new[] { "csgo", "core" }.Select(d => { var p = new Package(); p.Read(Path.Combine(game, d, "pak01_dir.vpk")); return p; }).ToList();
        var visFlags = new MaterialVisFlags.Source([Path.Combine(game, "csgo_addons", addon)], packages);
        bool RendersAsWorld(string c) => schema.IsSolidClass(c) && schema.HasFlag(c, "render_as_world_but_physics_as_entity");
        var document = DmxBinary.ReadFile(source);
        var meshes = MapMeshes.Read(document);
        var ours = new List<(TraceScene.Triangle T, MapMeshes.Mesh Mesh)>();
        foreach (var mesh in meshes)
            foreach (var t in TraceScene.Triangles([mesh], content.Material, m => visFlags[m], RendersAsWorld))
                ours.Add((t, mesh));

        var rte = RayTraceEnvironment.ReadFile(rtePath);
        var file = new Dictionary<string, Queue<int>>();
        for (var i = 0; i < rte.TriangleCount; i++)
        {
            var key = Key(rte.FileRecord(i));
            if (!file.TryGetValue(key, out var q))
                file[key] = q = new Queue<int>();
            q.Enqueue(i);
        }
        var record = new byte[48];
        var taken = new bool[rte.TriangleCount];
        var missed = new List<(TraceScene.Triangle T, MapMeshes.Mesh Mesh)>();
        var degenerate = 0;
        foreach (var (t, mesh) in ours)
        {
            if (!RayTraceEnvironment.RecordFor(t.A, t.B, t.C, 0, record))
            {
                degenerate++;
                missed.Add((t, mesh));
                continue;
            }
            if (file.TryGetValue(Key(record), out var q) && q.Count > 0)
                taken[q.Dequeue()] = true;
            else
                missed.Add((t, mesh));
        }
        output.WriteLine($"{map}: ours {ours.Count}, found {ours.Count - missed.Count}, degenerate {degenerate}; file {rte.TriangleCount}, not produced {taken.Count(x => !x)}");

        string Kind(MapMeshes.Mesh m)
        {
            var sub = m.Element?.Get<DmxBinary.Element>("meshData")?.Get<DmxBinary.Element>("subdivisionData")?.Get<object?[]>("subdivisionLevels")?.Any(x => x is int i && i > 0) == true;
            var rotatedPath = m.Instances.Length > 0 && (m.Path[1] != 0 || m.Path[2] != 0 || m.Path[4] != 0 || m.Path[6] != 0 || m.Path[8] != 0 || m.Path[9] != 0);
            return $"{m.ParentType}{(m.ParentClass is { } c ? "/" + c : "")}{(m.Instances.Length > 0 ? rotatedPath ? " inst-rot" : " inst" : "")}{(m.Angles != Vector3.Zero ? " angled" : "")}{(m.Scales != Vector3.One ? " scaled" : "")}{(sub ? " subdiv" : "")}";
        }

        // Our leftovers by what their mesh is.
        var byKind = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, mesh) in missed)
            byKind[Kind(mesh)] = byKind.GetValueOrDefault(Kind(mesh)) + 1;
        foreach (var (k, n) in byKind)
            output.WriteLine($"  ours not found: {k} x{n}");

        // Each file leftover against our leftovers, corner for corner.
        var grid = new Dictionary<(int, int, int), List<int>>();
        static (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y), (int)MathF.Floor(p.Z));
        for (var i = 0; i < missed.Count; i++)
        {
            var c = Cell(missed[i].T.A);
            if (!grid.TryGetValue(c, out var l))
                grid[c] = l = [];
            l.Add(i);
        }
        var used = new bool[missed.Count];
        var near = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var alone = new List<int>();
        var ulps = new SortedDictionary<int, int>();
        for (var i = 0; i < rte.TriangleCount; i++)
        {
            if (taken[i])
                continue;
            var v = rte.Vertices(i);
            if (v is null)
            {
                alone.Add(i);
                continue;
            }
            var hit = -1;
            foreach (var p in v)
            {
                for (var dx = -1; dx <= 1 && hit < 0; dx++)
                    for (var dy = -1; dy <= 1 && hit < 0; dy++)
                        for (var dz = -1; dz <= 1 && hit < 0; dz++)
                        {
                            var c = Cell(p);
                            if (!grid.TryGetValue((c.Item1 + dx, c.Item2 + dy, c.Item3 + dz), out var l))
                                continue;
                            foreach (var j in l)
                            {
                                if (used[j])
                                    continue;
                                Vector3[] mine = [missed[j].T.A, missed[j].T.B, missed[j].T.C];
                                if (v.All(q => mine.Any(o => Vector3.Distance(o, q) < 0.01f)))
                                {
                                    hit = j;
                                    break;
                                }
                            }
                        }
                if (hit >= 0)
                    break;
            }
            if (hit < 0)
            {
                alone.Add(i);
                continue;
            }
            used[hit] = true;
            var k = Kind(missed[hit].Mesh);
            near[k] = near.GetValueOrDefault(k) + 1;
        }
        foreach (var (k, n) in near)
            output.WriteLine($"  near miss: {k} x{n}");
        output.WriteLine($"  file triangles with nothing of ours: {alone.Count}");

        // Where the lone ones lie: the nearest mesh of ours by centroid, and flags.
        var centroids = ours.Select(o => (C: (o.T.A + o.T.B + o.T.C) / 3f, o.Mesh)).ToList();
        var coarse = new Dictionary<(int, int, int), List<int>>();
        static (int, int, int) Coarse(Vector3 p) => ((int)MathF.Floor(p.X / 64f), (int)MathF.Floor(p.Y / 64f), (int)MathF.Floor(p.Z / 64f));
        for (var j = 0; j < centroids.Count; j++)
        {
            var c = Coarse(centroids[j].C);
            if (!coarse.TryGetValue(c, out var l))
                coarse[c] = l = [];
            l.Add(j);
        }
        var byFlags = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var byMesh = new Dictionary<int, int>();
        var dumped = 0;
        foreach (var i in alone)
        {
            var key = $"0x{rte.RawFlags(i):x4}";
            byFlags[key] = byFlags.GetValueOrDefault(key) + 1;
            var v = rte.Vertices(i);
            if (v is null)
                continue;
            var c = (v[0] + v[1] + v[2]) / 3f;
            var cc = Coarse(c);
            var candidates = new List<int>();
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                    for (var dz = -1; dz <= 1; dz++)
                        if (coarse.TryGetValue((cc.Item1 + dx, cc.Item2 + dy, cc.Item3 + dz), out var l))
                            candidates.AddRange(l);
            var best = candidates.Count == 0 ? (C: new Vector3(1e9f), Mesh: meshes[0]) : centroids[candidates.MinBy(j => Vector3.DistanceSquared(centroids[j].C, c))];
            var d = Vector3.Distance(best.C, c);
            if (d < 64f)
                byMesh[best.Mesh.NodeId] = byMesh.GetValueOrDefault(best.Mesh.NodeId) + 1;
            else
                byMesh[-1] = byMesh.GetValueOrDefault(-1) + 1;
            if (dumped++ < 20)
                output.WriteLine($"    lone {i} flags {key}: {v[0]} {v[1]} {v[2]} nearest mesh {best.Mesh.NodeId} ({Kind(best.Mesh)}) at {d:F2}");
        }
        foreach (var (k, n) in byFlags)
            output.WriteLine($"  lone flags {k} x{n}");
        foreach (var (k, n) in byMesh.OrderByDescending(kv => kv.Value).Take(40))
            output.WriteLine($"  lone near mesh {k}{(meshes.FirstOrDefault(m => m.NodeId == k) is { } mm ? " " + Kind(mm) : "")} x{n}");

        // Our leftovers with no file counterpart, by mesh.
        var extra = new Dictionary<int, int>();
        for (var j = 0; j < missed.Count; j++)
            if (!used[j])
                extra[missed[j].Mesh.NodeId] = extra.GetValueOrDefault(missed[j].Mesh.NodeId) + 1;
        output.WriteLine($"  ours with nothing in the file: {extra.Values.Sum()}");
        foreach (var (k, n) in extra.OrderByDescending(kv => kv.Value).Take(40))
        {
            var mm = meshes.First(m => m.NodeId == k);
            output.WriteLine($"    mesh {k} {Kind(mm)} x{n} of {ours.Count(o => o.Mesh == mm)}; flags 0x{missed.First(x => x.Mesh.NodeId == k).T.Flags:x4}");
        }
        if (Environment.GetEnvironmentVariable("TRACEDIAG_EXTRA") is not null)
            for (var j = 0; j < missed.Count; j++)
                if (!used[j])
                {
                    var t = missed[j].T;
                    var area = Vector3.Cross(t.B - t.A, t.C - t.A).Length() / 2f;
                    output.WriteLine(FormattableString.Invariant($"    extra {missed[j].Mesh.NodeId} flags 0x{t.Flags:x4} area {area:G4} {t.A} {t.B} {t.C}"));
                }
        if (Environment.GetEnvironmentVariable("TRACEDIAG_NEAR") is not null)
            for (var j = 0; j < missed.Count; j++)
                if (used[j])
                    output.WriteLine($"    near {missed[j].Mesh.NodeId} {Kind(missed[j].Mesh)} {missed[j].T.A} {missed[j].T.B} {missed[j].T.C}");
    }

    private static string Key(ReadOnlySpan<byte> r)
    {
        var parts = new List<string>(13);
        foreach (var k in new[] { 0, 1, 2, 3, 5, 6, 7, 8, 9, 10 })
            parts.Add(BinaryPrimitives.ReadUInt32LittleEndian(r[(k * 4)..]).ToString("x8"));
        parts.Add(r[0x2c].ToString());
        parts.Add(r[0x2d].ToString());
        return string.Join(",", parts);
    }
}
