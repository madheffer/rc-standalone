using System.Globalization;
using System.Numerics;
using Source2.Compiler;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: each mesh DMX the map builder read (<c>tools/hulls/dump_meshbuf.py</c>)
/// against the <c>.vmap</c> mesh it came from, face by face. Meshes pair up by
/// their world positions, faces by their corner positions, corners by
/// position. <c>MESHBUF=&lt;dir&gt;</c>, <c>MESHBUF_VMAP=&lt;vmap&gt;</c>,
/// <c>MESHBUF_SHOW</c> caps the listed differences. <c>MESHBUF_ORDER=1</c>
/// tallies whether each dump lists its faces in the vmap's order (it does,
/// within each face set).
/// </summary>
public class MeshBufferVsVmap(ITestOutputHelper output)
{
    private sealed record Corner(Vector3 Position, Dictionary<string, object?> Streams);

    [Fact]
    public void DumpedMeshesAgainstTheirSource()
    {
        if (Environment.GetEnvironmentVariable("MESHBUF") is not { Length: > 0 } dir
            || Environment.GetEnvironmentVariable("MESHBUF_VMAP") is not { Length: > 0 } vmap)
            return;
        var show = int.TryParse(Environment.GetEnvironmentVariable("MESHBUF_SHOW"), out var s) ? s : 20;
        var meshes = MapMeshes.Read(DmxBinary.Read(File.ReadAllBytes(vmap)));
        var byKey = new Dictionary<string, List<(MapMeshes.Mesh Mesh, bool Local)>>();
        foreach (var m in meshes)
        {
            foreach (var local in new[] { false, true })
            {
                var key = Key(local ? LocalCorners(m) : m.Faces.SelectMany(f => f.Corners));
                if (!byKey.TryGetValue(key, out var list))
                    byKey[key] = list = [];
                list.Add((m, local));
            }
        }
        var tally = new Dictionary<string, int>();
        void Count(string k) => tally[k] = tally.GetValueOrDefault(k) + 1;
        var shown = 0;
        static bool name0(MapMeshes.Mesh m) => (Environment.GetEnvironmentVariable("MESHBUF_STREAM") ?? "") == "position";
        foreach (var file in Directory.GetFiles(dir, "*.dmx").OrderBy(f => int.Parse(Path.GetFileNameWithoutExtension(f), CultureInfo.InvariantCulture)))
        {
            var doc = DmxBinary.Read(File.ReadAllBytes(file));
            var faces = DumpFaces(doc);
            var key = Key(faces.SelectMany(f => f.Select(c => c.Position)));
            if (!byKey.TryGetValue(key, out var candidates))
            {
                Count("mesh unmatched");
                continue;
            }
            if (candidates.Any(c => c.Mesh.NodeId.ToString(CultureInfo.InvariantCulture) == Environment.GetEnvironmentVariable("MESHBUF_NODE")))
                output.WriteLine($"CANDIDATE {Path.GetFileName(file)} nodes {string.Join(",", candidates.Select(c => c.Mesh.NodeId))} first face {string.Join(" ", faces[0].Select(c => $"{c.Position}:{c.Streams.GetValueOrDefault("texcoord$0")}"))}");
            // Copies of one mesh pair up by position alone; keep the one whose corners agree most.
            var (source, isLocal) = candidates.MaxBy(c => Agreement(faces, VmapFaces(c.Mesh, c.Local)));
            Count($"mesh matched{(isLocal ? " in mesh space" : "")} under {source.ParentType}{(candidates.Count > 1 ? " (ambiguous)" : "")}");
            var ours = VmapFaces(source, isLocal);
            if (Environment.GetEnvironmentVariable("MESHBUF_FORMATS") == "1")
            {
                var fmt = doc.Elements.First(e => e.Type == "DmeVertexData").Get<object?[]>("vertexFormat")!.Select(x => (string)x!).ToList();
                var full = fmt.Contains("texcoord$1") || fmt.Any(x => x.StartsWith("VertexPaint", StringComparison.Ordinal));
                var md = source.Element!.Get<DmxBinary.Element>("meshData")!;
                var desc = string.Join(" ", (md.Get<DmxBinary.Element>("faceVertexData")?.GetElements("streams") ?? [])
                    .Where(x => !x.Name.StartsWith("position", StringComparison.Ordinal) && !x.Name.StartsWith("normal", StringComparison.Ordinal) && !x.Name.StartsWith("tangent", StringComparison.Ordinal) && !x.Name.StartsWith("texcoord:0", StringComparison.Ordinal))
                    .Select(x => $"{x.Name}={string.Join("/", (x.Get<object?[]>("data") ?? []).Select(v => v?.ToString()).Distinct().Take(3))}"));
                var mats = string.Join(",", (md.Get<object?[]>("materials") ?? []).Select(x => Path.GetFileNameWithoutExtension(x as string ?? "")));
                Count($"FMT {(full ? "full" : "min")} ");
                if (shown++ < show)
                    output.WriteLine($"FMT {(full ? "full" : "min ")} node {source.NodeId} [{mats}] {desc}");
                if (Environment.GetEnvironmentVariable("MESHBUF_NODE") == source.NodeId.ToString(CultureInfo.InvariantCulture))
                    foreach (var st in md.Get<DmxBinary.Element>("faceVertexData")?.GetElements("streams") ?? [])
                        output.WriteLine($"STREAM {st.Name}: {string.Join(" ", st.Attributes.Where(a => a.Key != "data").Select(a => $"{a.Key}={a.Value}"))}");
            }
            var index = new Dictionary<string, List<Corner[]>>();
            foreach (var f in ours)
            {
                var fk = Key(f.Select(c => c.Position));
                if (!index.TryGetValue(fk, out var l))
                    index[fk] = l = [];
                l.Add(f);
            }
            var order = new List<int>();
            foreach (var f in faces)
            {
                if (!index.TryGetValue(Key(f.Select(c => c.Position)), out var l) || l.Count == 0)
                {
                    Count("face unmatched");
                    continue;
                }
                var g = l[0];
                l.RemoveAt(0);
                order.Add(ours.IndexOf(g));
                if (Environment.GetEnvironmentVariable("MESHBUF_NODE") == source.NodeId.ToString(CultureInfo.InvariantCulture))
                {
                    output.WriteLine($"FACE dmx  {string.Join(" ", f.Select(c => $"{c.Position}:{c.Streams.GetValueOrDefault("texcoord$0")}"))}");
                    output.WriteLine($"FACE ours {string.Join(" ", g.Select(c => $"{c.Position}:{c.Streams.GetValueOrDefault("texcoord$0")}"))}");
                    output.WriteLine($"FACE data {string.Join(" ", g[0].Streams.Where(kv => kv.Key.StartsWith("fd:", StringComparison.Ordinal)).Select(kv => $"{kv.Key}={kv.Value}"))}");
                }
                foreach (var c in f)
                {
                    var d = g.FirstOrDefault(x => Vector3.Distance(x.Position, c.Position) < 0.01f);
                    if (d == null)
                    {
                        Count("corner unmatched");
                        continue;
                    }
                    Count($"position: {(c.Position == d.Position ? "same" : "ulps")} instanced={source.Instances.Length > 0} angles={source.Angles != Vector3.Zero}");
                    if (c.Position != d.Position && name0(source) && shown++ < show)
                        output.WriteLine($"{Path.GetFileName(file)} node {source.NodeId} position dmx {c.Position:R} ours {d.Position:R}");
                    foreach (var (name, value) in c.Streams)
                    {
                        var theirs = d.Streams.GetValueOrDefault(name);
                        var verdict = Equals(value, theirs) ? "same" : theirs == null ? "absent in vmap" : Differ(value, theirs);
                        Count($"{name}: {verdict}");
                        if (name.StartsWith("normal", StringComparison.Ordinal))
                            Count($"  normal {verdict} rotated={source.Angles != Vector3.Zero || source.Instances.Length > 0} smooth={source.Element!.GetValue<float>("smoothingAngle")}");
                        if (verdict != "same" && theirs != null && name.StartsWith(Environment.GetEnvironmentVariable("MESHBUF_STREAM") ?? "", StringComparison.Ordinal) && shown++ < show)
                            output.WriteLine($"{Path.GetFileName(file)} node {source.NodeId} {name} dmx {value} vmap {theirs}");
                    }
                }
            }
            if (Environment.GetEnvironmentVariable("MESHBUF_ORDER") == "1" && order.Count > 1)
            {
                var kind = order.SequenceEqual(order.Order()) ? "ascending" : order.SequenceEqual(order.OrderDescending()) ? "descending" : "other";
                Count($"face order {kind}");
                if (kind == "other" && shown++ < show)
                    output.WriteLine($"ORDER node {source.NodeId} sets {doc.Elements.First(e => e.Type == "DmeMesh").GetElements("faceSets").Count()}: {string.Join(" ", order)}");
            }
        }
        foreach (var (k, v) in tally.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            output.WriteLine($"{k}: {v}");
    }

    private static string Differ(object? a, object? b) => (a, b) switch
    {
        (Vector2 x, Vector2 y) when Vector2.Distance(x, y) < 1e-4f => "ulps",
        (Vector2 x, Vector2 y) when IsWhole(x - y) => "whole-number shift",
        (Vector3 x, Vector3 y) when Vector3.Distance(x, y) < 1e-4f => "ulps",
        (Vector4 x, Vector4 y) when Vector4.Distance(x, y) < 1e-4f => "ulps",
        _ => "differs",
    };

    private static bool IsWhole(Vector2 v) => MathF.Abs(v.X - MathF.Round(v.X)) < 1e-4f && MathF.Abs(v.Y - MathF.Round(v.Y)) < 1e-4f;

    private static string Key(IEnumerable<Vector3> points) => string.Join(";", points
        .Select(p => $"{MathF.Round(p.X * 8) / 8},{MathF.Round(p.Y * 8) / 8},{MathF.Round(p.Z * 8) / 8}")
        .Distinct().OrderBy(x => x, StringComparer.Ordinal));

    // The dump's faces: corner lists from each face set, split at -1.
    private static List<Corner[]> DumpFaces(DmxBinary.Document doc)
    {
        var mesh = doc.Elements.First(e => e.Type == "DmeMesh");
        var data = mesh.Get<DmxBinary.Element>("currentState")!;
        var names = (data.Get<object?[]>("vertexFormat") ?? []).Select(x => (string)x!).ToList();
        var streams = names.Select(n => (Name: n, Values: data.Get<object?[]>(n)!, Indices: data.Get<object?[]>(n + "Indices")!)).ToList();
        var pos = streams.First(x => x.Name.StartsWith("position", StringComparison.Ordinal));
        var faces = new List<Corner[]>();
        foreach (var set in mesh.GetElements("faceSets"))
        {
            var current = new List<Corner>();
            foreach (var o in set.Get<object?[]>("faces") ?? [])
            {
                var c = (int)o!;
                if (c < 0)
                {
                    faces.Add([.. current]);
                    current.Clear();
                    continue;
                }
                current.Add(new Corner((Vector3)pos.Values[(int)pos.Indices[c]!]!,
                    streams.Where(x => x != pos).ToDictionary(x => x.Name, x => x.Values[(int)x.Indices[c]!])));
            }
        }
        return faces;
    }

    // The source mesh's faces in world space with every face-vertex stream.
    private static int Agreement(List<Corner[]> faces, List<Corner[]> ours)
    {
        var values = new HashSet<string>(ours.SelectMany(f => f.Select(c => $"{c.Position}|{string.Join("|", c.Streams.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value))}")));
        return faces.Sum(f => f.Count(c => values.Contains($"{c.Position}|{string.Join("|", c.Streams.Where(kv => ours[0][0].Streams.ContainsKey(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value))}")));
    }

    private static IEnumerable<Vector3> LocalCorners(MapMeshes.Mesh mesh)
    {
        var data = mesh.Element!.Get<DmxBinary.Element>("meshData")!;
        var positions = data.Get<DmxBinary.Element>("vertexData")!.GetElements("streams").First(x => x.Name.StartsWith("position:", StringComparison.Ordinal)).Get<object?[]>("data")!;
        var vertexData = (data.Get<object?[]>("vertexDataIndices") ?? []).Select(x => (int)x!).ToArray();
        var to = (data.Get<object?[]>("edgeVertexIndices") ?? []).Select(x => (int)x!).ToArray();
        return to.Select(v => (Vector3)positions[vertexData[v]]! * mesh.Scales);
    }

    private static List<Corner[]> VmapFaces(MapMeshes.Mesh mesh, bool local)
    {
        var localCorners = local ? LocalCorners(mesh).ToArray() : null;
        var data = mesh.Element!.Get<DmxBinary.Element>("meshData")!;
        int[] Ints(string n) => (data.Get<object?[]>(n) ?? []).Select(x => x is int i ? i : -1).ToArray();
        var next = Ints("edgeNextIndices");
        var cornerData = Ints("edgeVertexDataIndices");
        var first = Ints("faceEdgeIndices");
        var streams = (data.Get<DmxBinary.Element>("faceVertexData")?.GetElements("streams") ?? [])
            .Select(x => (Name: x.Name.Replace(':', '$'), Values: x.Get<object?[]>("data") ?? [])).ToList();
        // Texcoords as FUN_1810d93c0 shifts them (MESHBUF_NOSHIFT=1: as stored).
        if (Environment.GetEnvironmentVariable("MESHBUF_NOSHIFT") != "1")
        {
            var uv = streams.Where(x => x.Name.StartsWith("texcoord", StringComparison.Ordinal)).ToList();
            var data2 = uv.Select(x => x.Values).ToList();
            MapMeshCorners.ShiftTexcoords(data2, first, next, cornerData, Ints("edgeOppositeIndices"));
            for (var t = 0; t < uv.Count; t++)
                streams[streams.IndexOf(uv[t])] = (uv[t].Name, data2[t]);
        }
        var result = new List<Corner[]>();
        for (var f = 0; f < first.Length; f++)
        {
            var corners = new List<Corner>();
            var e = first[f];
            var k = 0;
            do
            {
                corners.Add(new Corner(localCorners != null ? localCorners[e] : mesh.Faces[f].Corners[k], streams.ToDictionary(x => x.Name,
                    x => local ? x.Values[cornerData[e]] : Rotated(mesh.World, x.Name, x.Values[cornerData[e]]))));
                k++;
                e = next[e];
            } while (e != first[f] && k < mesh.Faces[f].Corners.Length);
            var fdi = Ints("faceDataIndices");
            foreach (var fs in data.Get<DmxBinary.Element>("faceData")?.GetElements("streams") ?? [])
                if (f < fdi.Length && fs.Get<object?[]>("data") is { } fv && fdi[f] < fv.Length)
                    corners[0].Streams["fd:" + fs.Name] = fv[fdi[f]];
            result.Add([.. corners]);
        }
        return result;
    }

    // FUN_1810c4e50: normals and tangents turned by the world matrix
    // (FUN_18125d1b0, (x r0 + y r1) + z r2 per row), not renormalised.
    private static object? Rotated(float[] m, string name, object? value)
    {
        static Vector3 Rotate(float[] m, Vector3 v) => new(
            ((v.X * m[0]) + (v.Y * m[1])) + (v.Z * m[2]),
            ((v.X * m[4]) + (v.Y * m[5])) + (v.Z * m[6]),
            ((v.X * m[8]) + (v.Y * m[9])) + (v.Z * m[10]));
        return (name.Split('$')[0], value) switch
        {
            ("normal", Vector3 n) => Rotate(m, n),
            ("tangent", Vector4 t) => new Vector4(Rotate(m, new Vector3(t.X, t.Y, t.Z)), t.W),
            _ => value,
        };
    }
}
