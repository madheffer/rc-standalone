using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: a subdivided mesh as the builder exports it (a
/// <c>tools/hulls/dump_meshbuf.py</c> DMX) against <see cref="MeshTessellation"/>.
/// For each exported polygon, where the same triangle (corners as a set, in
/// world space) sits in our list, and which of our source faces it comes from.
/// <c>SUBDIVCMP=&lt;vmap&gt;|&lt;node id&gt;|&lt;dmx&gt;</c>.
/// </summary>
public class SubdivisionOrderProbe(ITestOutputHelper output)
{
    [Fact]
    public void ExportedOrderAgainstOurs()
    {
        if (Environment.GetEnvironmentVariable("SUBDIVCMP") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var node = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        var mesh = MapMeshes.Read(DmxBinary.ReadFile(parts[0])).First(m => m.NodeId == node);
        var world = mesh.World;
        var cut = MeshTessellation.Triangulate(mesh.Element!.Get<DmxBinary.Element>("meshData")!);
        var scales = mesh.Scales;
        var ours = cut.Positions.Select(p => MapMeshes.Transform(world, p * scales)).ToList();
        var ourKey = new Dictionary<string, List<int>>();
        for (var t = 0; t < cut.Faces.Count; t++)
        {
            var k = Key(ours[cut.Indices[t * 3]], ours[cut.Indices[(t * 3) + 1]], ours[cut.Indices[(t * 3) + 2]]);
            if (!ourKey.TryGetValue(k, out var l))
                ourKey[k] = l = [];
            l.Add(t);
        }

        var dmx = DmxBinary.Read(File.ReadAllBytes(parts[2]));
        var vd = dmx.Elements.First(e => e.Type == "DmeVertexData");
        var pos = (vd.Get<object?[]>("position$0") ?? []).Select(x => (Vector3)x!).ToArray();
        var pidx = (vd.Get<object?[]>("position$0Indices") ?? []).Select(x => (int)x!).ToArray();
        output.WriteLine($"exported {pos.Length} positions; ours {ours.Count} positions, {cut.Faces.Count} triangles from {cut.Faces.Distinct().Count()} faces");
        output.WriteLine($"exported first positions: {string.Join(" ", pos.Take(6))}");
        output.WriteLine($"our first positions: {string.Join(" ", ours.Take(6))}");
        var polys = 0;
        var seq = new List<int>();
        var found = 0;
        var lines = new List<string>();
        foreach (var fs in dmx.Elements.First(e => e.Type == "DmeMesh").GetElements("faceSets"))
        {
            var faces = (fs.Get<object?[]>("faces") ?? []).Select(x => (int)x!).ToArray();
            var poly = new List<int>();
            foreach (var c in faces)
            {
                if (c != -1)
                {
                    poly.Add(c);
                    continue;
                }
                var corners = poly.Select(i => pos[pidx[i]]).ToArray();
                var at = corners.Length == 3 && ourKey.TryGetValue(Key(corners[0], corners[1], corners[2]), out var l) ? l[0] : -1;
                if (at >= 0)
                    found++;
                seq.Add(at);
                if (lines.Count < 60)
                    lines.Add($"poly {polys} ({corners.Length} corners, pos idx {string.Join(",", poly.Select(i => pidx[i]))}): ours triangle {at}" + (at >= 0 ? $" face {cut.Faces[at]}" : $" first {corners[0]}"));
                polys++;
                poly.Clear();
            }
        }
        output.WriteLine($"{polys} exported polygons, {found} found among ours");
        output.WriteLine("LOCAL " + string.Join(" ", seq.Select(t => t < 0 ? "?" : $"{cut.Faces[t]}.{t - cut.Faces.IndexOf(cut.Faces[t])}")));
        foreach (var l in lines)
            output.WriteLine("  " + l);
        // SUBDIVCMP_FACE=<n>: face n's corners (world, loop order) and its
        // exported triangles in export order, as JSON for a simulator.
        if (Environment.GetEnvironmentVariable("SUBDIVCMP_FACE") is { Length: > 0 } fn)
        {
            var face = int.Parse(fn, System.Globalization.CultureInfo.InvariantCulture);
            var data = mesh.Element!.Get<DmxBinary.Element>("meshData")!;
            var next = (data.Get<object?[]>("edgeNextIndices") ?? []).Select(x => (int)x!).ToArray();
            var to = (data.Get<object?[]>("edgeVertexIndices") ?? []).Select(x => (int)x!).ToArray();
            var first = (data.Get<object?[]>("faceEdgeIndices") ?? []).Select(x => (int)x!).ToArray();
            var vdi = (data.Get<object?[]>("vertexDataIndices") ?? []).Select(x => (int)x!).ToArray();
            var vpos = (data.Get<DmxBinary.Element>("vertexData")!.GetElements("streams").First(st => st.Name.StartsWith("position", StringComparison.Ordinal)).Get<object?[]>("data") ?? []).Select(x => (Vector3)x!).ToArray();
            var corners = new List<Vector3>();
            var e = first[face];
            do
            {
                corners.Add(MapMeshes.Transform(world, vpos[vdi[to[e]]] * scales));
                e = next[e];
            }
            while (e != first[face]);
            var mine = new List<string>();
            var exportedTris = new List<Vector3[]>();
            foreach (var fs2 in dmx.Elements.First(x => x.Type == "DmeMesh").GetElements("faceSets"))
            {
                var poly = new List<int>();
                foreach (var c in (fs2.Get<object?[]>("faces") ?? []).Select(x => (int)x!))
                {
                    if (c != -1) { poly.Add(c); continue; }
                    exportedTris.Add([.. poly.Select(i => pos[pidx[i]])]);
                    poly.Clear();
                }
            }
            for (var k = 0; k < seq.Count; k++)
            {
                if (seq[k] >= 0 && cut.Faces[seq[k]] == face)
                    mine.Add("[" + string.Join(",", exportedTris[k].Select(v => $"[{v.X:R},{v.Y:R},{v.Z:R}]")) + "]");
            }
            output.WriteLine("FACEJSON {\"corners\":[" + string.Join(",", corners.Select(v => $"[{v.X:R},{v.Y:R},{v.Z:R}]")) + "],\"tris\":[" + string.Join(",", mine) + "]}");
        }
    }

    private static string Key(Vector3 a, Vector3 b, Vector3 c)
        => string.Join(";", new[] { a, b, c }.Select(v => $"{MathF.Round(v.X, 2)},{MathF.Round(v.Y, 2)},{MathF.Round(v.Z, 2)}").Order(StringComparer.Ordinal));
}
