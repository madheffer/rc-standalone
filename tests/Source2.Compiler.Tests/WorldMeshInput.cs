using System.Numerics;
using System.Runtime.InteropServices;
using Source2.Compiler;
using Source2.Compiler.Physics;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: the triangles the compile hands RnMeshCreate for the world
/// (<c>tools/physics/capture_rnmesh.py</c>), against the world meshes' welded
/// material pieces (<see cref="BrushHulls.Pieces"/>) laid end to end in node
/// order. <c>WORLDIN=&lt;capture&gt;|&lt;vmap&gt;|&lt;call id&gt;</c>.
/// </summary>
public class WorldMeshInput(ITestOutputHelper output)
{
    [Fact]
    public void WorldPiecesAgainstTheCapturedInput()
    {
        if (Environment.GetEnvironmentVariable("WORLDIN") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var call = int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
        var (indices, vertices) = Input(parts[0], call);
        output.WriteLine($"captured: {indices.Length / 3} triangles, {vertices.Length} vertices");

        var doc = DmxBinary.Read(File.ReadAllBytes(parts[1]));
        var world = doc.OfType("CMapWorld").First();
        var ours = new List<Vector3>();
        var ourTris = new List<int>();
        var pieces = 0;
        foreach (var mesh in Maps.MapMeshes.Read(doc).Where(m => m.ParentType is "CMapWorld" or "CMapGroup"))
        {
            foreach (var (material, positions, faces, local) in BrushHulls.Pieces(mesh.Element!, world))
            {
                pieces++;
                // The half-edge mesh joins corners by position (FUN_181308060).
                var (points, triangles) = BrushHulls.TriangleMesh(positions, faces, local);
                var baseIndex = ours.Count;
                ours.AddRange(points);
                foreach (var (a, b, c) in triangles)
                    ourTris.AddRange([a + baseIndex, b + baseIndex, c + baseIndex]);
                var at = Array.IndexOf(vertices, points[0]);
                var mats = mesh.Element!.Get<DmxBinary.Element>("meshData")?.Get<object?[]>("materials");
                output.WriteLine($"piece {pieces}: node {mesh.NodeId} material {material} {(mats != null && material < mats.Length ? Path.GetFileNameWithoutExtension(mats[material] as string) : "?")}: {triangles.Count} triangles, {points.Count} vertices; first vertex at captured index {at}");
            }
        }
        output.WriteLine($"ours: {ourTris.Count / 3} triangles, {ours.Count} vertices in {pieces} pieces");
        // Which piece each captured triangle came from, as runs.
        var owner = new Dictionary<string, string>();
        var pieceOf = new List<string>();
        foreach (var mesh in Maps.MapMeshes.Read(doc).Where(m => m.ParentType is "CMapWorld" or "CMapGroup"))
            foreach (var (material, positions, faces, local) in BrushHulls.Pieces(mesh.Element!, world))
            {
                var (points, triangles) = BrushHulls.TriangleMesh(positions, faces, local);
                foreach (var (a, b, c) in triangles)
                    owner[Key(points[a], points[b], points[c])] = $"{mesh.NodeId}/{material}";
            }
        var runs = new List<string>();
        for (var t = 0; t < indices.Length / 3; t++)
        {
            var k = Key(vertices[indices[t * 3]], vertices[indices[(t * 3) + 1]], vertices[indices[(t * 3) + 2]]);
            var o = owner.GetValueOrDefault(k, "?");
            if (runs.Count == 0 || !runs[^1].StartsWith(o + "x", StringComparison.Ordinal))
                runs.Add(o + "x1");
            else
                runs[^1] = o + "x" + (int.Parse(runs[^1].Split('x')[1], System.Globalization.CultureInfo.InvariantCulture) + 1);
        }
        output.WriteLine("captured order: " + string.Join(" ", runs));
        foreach (var mesh in Maps.MapMeshes.Read(doc).Where(m => m.ParentType is "CMapWorld" or "CMapGroup"))
            foreach (var (material, positions, faces, local) in BrushHulls.Pieces(mesh.Element!, world))
            {
                var lo = positions.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
                var hi = positions.Aggregate(new Vector3(float.MinValue), Vector3.Max);
                output.WriteLine($"BOUNDS {mesh.NodeId}/{material} {lo} .. {hi} centre {(lo + hi) / 2} parent {mesh.ParentType} layer? {mesh.Element!.Get<DmxBinary.Element>("parent")?.Type}");
            }
        output.WriteLine("dmx element order: " + string.Join(" ", doc.OfType("CMapMesh").Select(m => m.GetValue<int>("nodeID"))));
        output.WriteLine("walk order: " + string.Join(" ", Maps.MapMeshes.Read(doc).Select(m => m.NodeId)));
        var set = vertices.ToHashSet();
        output.WriteLine($"our vertices found in the capture: {ours.Count(set.Contains)} of {ours.Count}");
        var same = ours.Count == vertices.Length && ours.SequenceEqual(vertices);
        output.WriteLine($"vertex lists identical: {same}; triangle lists identical: {ourTris.SequenceEqual(indices)}");
        for (var i = 0; i < Math.Min(ours.Count, vertices.Length); i++)
        {
            if (ours[i] != vertices[i])
            {
                output.WriteLine($"first vertex difference at {i}: ours {ours[i]:R} captured {vertices[i]:R}");
                break;
            }
        }
    }

    private static string Key(Vector3 a, Vector3 b, Vector3 c) => string.Join(";", new[] { a, b, c }.Select(v => v.ToString()).Order(StringComparer.Ordinal));

    private static (int[] Indices, Vector3[] Vertices) Input(string path, int id)
    {
        var data = File.ReadAllBytes(path);
        for (var at = 0; at < data.Length;)
        {
            var n = BitConverter.ToInt32(data, at);
            var head = System.Text.Json.JsonDocument.Parse(data.AsMemory(at + 4, n)).RootElement;
            at += 4 + n;
            var m = BitConverter.ToInt32(data, at);
            var blob = data.AsSpan(at + 4, m);
            at += 4 + m;
            if (head.GetProperty("ev").GetString() != "in" || head.GetProperty("id").GetInt32() != id)
                continue;
            var tris = head.GetProperty("tris").GetInt32();
            var vcount = head.GetProperty("vcount").GetInt32();
            var off = tris * 12 + (head.GetProperty("hasMaterials").GetBoolean() ? tris : 0);
            return (MemoryMarshal.Cast<byte, int>(blob[..(tris * 12)]).ToArray(), MemoryMarshal.Cast<byte, Vector3>(blob.Slice(off, vcount * 12)).ToArray());
        }
        throw new InvalidDataException($"no call {id}");
    }
}
