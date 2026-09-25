using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: for every subdivided world mesh of a map, the builder's
/// exported triangle order (<c>tools/hulls/dump_meshbuf.py</c> dumps) against
/// <see cref="MeshTessellation"/>'s, per source face: its level, its triangle
/// count, the triangle that kept the face's own slot, the order of the rest,
/// and the rotation of each exported triangle's corners against ours.
/// <c>SUBDIVALL=&lt;vmap&gt;|&lt;dump dir&gt;</c>.
/// </summary>
public class SubdivisionOrderSurvey(ITestOutputHelper output)
{
    [Fact]
    public void OrdersOfAllSubdividedMeshes()
    {
        if (Environment.GetEnvironmentVariable("SUBDIVALL") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var dumps = Directory.GetFiles(parts[1], "*.dmx").Select(f => (File: f, Doc: DmxBinary.Read(File.ReadAllBytes(f)))).ToList();
        var signatures = new Dictionary<string, int>();
        foreach (var mesh in MapMeshes.Read(DmxBinary.ReadFile(parts[0])))
        {
            var data = mesh.Element!.Get<DmxBinary.Element>("meshData")!;
            var levels = data.Get<DmxBinary.Element>("subdivisionData")?.Get<object?[]>("subdivisionLevels");
            if (levels == null || !levels.Any(x => x is int i && i > 0) || mesh.ParentType is not ("CMapWorld" or "CMapGroup"))
                continue;
            var cut = MeshTessellation.Triangulate(data);
            var ours = cut.Positions.Select(p => MapMeshes.Transform(mesh.World, p * mesh.Scales)).ToList();
            var ourKey = new Dictionary<string, int>();
            for (var t = 0; t < cut.Faces.Count; t++)
                ourKey.TryAdd(Key(ours[cut.Indices[t * 3]], ours[cut.Indices[(t * 3) + 1]], ours[cut.Indices[(t * 3) + 2]]), t);
            // The dump whose triangles are ours.
            foreach (var (file, doc) in dumps)
            {
                var tris = Exported(doc);
                if (tris.Count == 0 || !ourKey.ContainsKey(Key(tris[0][0], tris[0][1], tris[0][2])))
                    continue;
                var seq = tris.Select(c => (T: ourKey.GetValueOrDefault(Key(c[0], c[1], c[2]), -1), C: c)).ToList();
                var missing = seq.Count(x => x.T < 0);
                var firstOf = new Dictionary<int, int>();
                for (var t = 0; t < cut.Faces.Count; t++)
                    firstOf.TryAdd(cut.Faces[t], t);
                var perFace = seq.Where(x => x.T >= 0).GroupBy(x => cut.Faces[x.T]).ToList();
                output.WriteLine($"node {mesh.NodeId} dump {Path.GetFileNameWithoutExtension(file)}: {tris.Count} exported, {cut.Faces.Count} ours, {missing} not found, {perFace.Count} faces");
                // Rotation: where our first corner sits in the exported triangle.
                var rotations = seq.Where(x => x.T >= 0).Select(x =>
                {
                    var a = ours[cut.Indices[x.T * 3]];
                    return Array.FindIndex(x.C, v => Near(v, a));
                }).GroupBy(r => r).Select(g => $"{g.Key}:{g.Count()}");
                output.WriteLine($"  rotation of our first corner in the exported triangle: {string.Join(" ", rotations)}");
                foreach (var g in perFace)
                {
                    var f = g.Key;
                    var local = g.Select(x => x.T - firstOf[f]).ToList();
                    var level = cut.Faces.Count(x => x == f);
                    var sig = $"tris {level}: kept {local[0]} then {string.Join(",", local.Skip(1))}";
                    signatures[sig] = signatures.GetValueOrDefault(sig) + 1;
                }
                // Whether the first pass is one triangle per face, in face order.
                var headFaces = seq.Take(perFace.Count).Select(x => x.T < 0 ? -1 : cut.Faces[x.T]).ToList();
                output.WriteLine($"  first {perFace.Count} exported come from faces {(headFaces.SequenceEqual(headFaces.Order()) && headFaces.Distinct().Count() == headFaces.Count ? "one each, in order" : string.Join(",", headFaces.Take(20)))}");
                break;
            }
        }
        foreach (var (sig, n) in signatures.OrderByDescending(kv => kv.Value))
            output.WriteLine($"SIG x{n} {sig}");
    }

    private static List<Vector3[]> Exported(DmxBinary.Document doc)
    {
        var vd = doc.Elements.First(e => e.Type == "DmeVertexData");
        var pos = (vd.Get<object?[]>("position$0") ?? []).Select(x => (Vector3)x!).ToArray();
        var pidx = (vd.Get<object?[]>("position$0Indices") ?? []).Select(x => (int)x!).ToArray();
        var result = new List<Vector3[]>();
        foreach (var fs in doc.Elements.First(e => e.Type == "DmeMesh").GetElements("faceSets"))
        {
            var poly = new List<int>();
            foreach (var c in (fs.Get<object?[]>("faces") ?? []).Select(x => (int)x!))
            {
                if (c != -1)
                {
                    poly.Add(c);
                    continue;
                }
                if (poly.Count == 3)
                    result.Add([.. poly.Select(i => pos[pidx[i]])]);
                poly.Clear();
            }
        }
        return result;
    }

    private static bool Near(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b) < 1e-4f;

    private static string Key(Vector3 a, Vector3 b, Vector3 c)
        => string.Join(";", new[] { a, b, c }.Select(v => $"{MathF.Round(v.X, 2)},{MathF.Round(v.Y, 2)},{MathF.Round(v.Z, 2)}").Order(StringComparer.Ordinal));
}
