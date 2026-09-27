using System.Numerics;
using System.Runtime.InteropServices;
using Source2.Compiler.Maps;
using Source2.Compiler.Physics;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: the faces of one world mesh piece whose triangles are not
/// among the captured shape's, with their corners, our cut and the captured
/// triangles over the same corners.
/// <c>EARORDER=&lt;addon&gt;|&lt;map&gt;|&lt;capture .json&gt;|&lt;node&gt;/&lt;material&gt;</c>.
/// </summary>
public class EarOrderProbe(ITestOutputHelper output)
{
    [Fact]
    public void Faces()
    {
        if (Environment.GetEnvironmentVariable("EARORDER") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        var game = Path.Combine(cs2, "game");
        var vmap = Path.Combine(cs2, "content", "csgo_addons", parts[0], "maps", parts[1] + ".vmap");
        using var models = new SettleBuildTests.PakModels(Path.Combine(game, "csgo", "pak01_dir.vpk"), Path.Combine(game, "csgo_addons", parts[0]));
        var doc = DmxBinary.ReadFile(vmap);
        var pieces = WorldCollision.Pieces(doc, n => WorldCollision.ReadMaterial(models.Material(n), models.CollisionProperty));
        var id = parts[3].Split('/').Select(int.Parse).ToArray();
        var piece = pieces.First(p => p.NodeId == id[0] && p.Material == id[1] && p.Hull == null);

        using var capture = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(parts[2]));
        var world = capture.RootElement.EnumerateArray().Where(c => c.TryGetProperty("call", out _)).MaxBy(c => c.GetProperty("count").GetInt32());
        var theirs = world.GetProperty("shapes").EnumerateArray()
            .Where(s => s.TryGetProperty("vdata", out _))
            .Select(s => (V: MemoryMarshal.Cast<byte, Vector3>(Convert.FromHexString(s.GetProperty("vdata").GetString()!)).ToArray(),
                          I: MemoryMarshal.Cast<byte, int>(Convert.FromHexString(s.GetProperty("idata").GetString()!)).ToArray()))
            .First(s => s.V.Length == piece.Points.Length && s.V[0] == piece.Points[0]);
        var theirTriangles = new HashSet<string>();
        for (var t = 0; t < theirs.I.Length; t += 3)
            theirTriangles.Add(Key(theirs.V[theirs.I[t]], theirs.V[theirs.I[t + 1]], theirs.V[theirs.I[t + 2]]));
        output.WriteLine($"piece {parts[3]}: {piece.Points.Length} v, ours {piece.Indices.Length / 3} t, theirs {theirs.I.Length / 3} t");

        var mesh = MapMeshes.Read(doc).First(m => m.NodeId == id[0]).Element!;
        var toWorld = MapMeshes.Local(mesh);
        var data = mesh.Get<DmxBinary.Element>("meshData")!;
        int[] Ints(string name) => (data.Get<object?[]>(name) ?? []).Select(Convert.ToInt32).ToArray();
        object?[] Stream(string array, string name) => data.Get<DmxBinary.Element>(array)!.GetElements("streams").First(s => s.Name.Split(':')[0] == name).Get<object?[]>("data")!;
        var next = Ints("edgeNextIndices");
        var to = Ints("edgeVertexIndices");
        var first = Ints("faceEdgeIndices");
        var vertexData = Ints("vertexDataIndices");
        var faceData = Ints("faceDataIndices");
        var positions = Stream("vertexData", "position");
        var materials = Stream("faceData", "materialindex").Select(Convert.ToInt32).ToArray();
        var scales = mesh.GetValue<Vector3>("scales") ?? Vector3.One;
        var shown = 0;
        for (var f = 0; f < first.Length; f++)
        {
            if (materials[faceData[f]] != id[1])
                continue;
            var loop = new List<int>();
            var e = first[f];
            do
            {
                loop.Add(e);
                e = next[e];
            } while (e != first[f]);
            if (loop.Count < 4)
                continue;
            var local = loop.Select(x => (Vector3)positions[vertexData[to[x]]]! * scales).ToArray();
            // Each corner as the piece's point nearest its moved position.
            var at = local.Select(p => MapMeshes.Transform(toWorld, p))
                          .Select(w => Enumerable.Range(0, piece.Points.Length).MinBy(i => Vector3.DistanceSquared(piece.Points[i], w))).ToArray();
            bool Matches(int[] c)
            {
                var ok = c.Length > 0;
                for (var t = 0; t + 2 < c.Length; t += 3)
                    ok &= theirTriangles.Contains(Key(piece.Points[at[c[t]]], piece.Points[at[c[t + 1]]], piece.Points[at[c[t + 2]]]));
                return ok;
            }
            var cut = PolygonTriangulator.Triangulate(local);
            var inWorld = PolygonTriangulator.Triangulate(local.Select(p => MapMeshes.Transform(toWorld, p)).ToArray());
            var onPoints = PolygonTriangulator.Triangulate(at.Select(i => piece.Points[i]).ToArray());
            if (Matches(cut) || shown++ >= 12)
                continue;
            output.WriteLine($"face {f}: {loop.Count} corners, cut {string.Join(" ", cut)}; world cut matches {Matches(inWorld)}, welded-point cut matches {Matches(onPoints)}");
            for (var j = 0; j < local.Length; j++)
                output.WriteLine($"  c{j}: local ({local[j].X:R},{local[j].Y:R},{local[j].Z:R}) point {at[j]} {piece.Points[at[j]]}");
            var mine = at.ToHashSet();
            var theirsHere = new List<string>();
            for (var t = 0; t < theirs.I.Length; t += 3)
            {
                int[] tri = [theirs.I[t], theirs.I[t + 1], theirs.I[t + 2]];
                var corners = tri.Select(i => Array.FindIndex(at, a => piece.Points[a] == theirs.V[i])).ToArray();
                if (corners.All(c => c >= 0))
                    theirsHere.Add(string.Join(" ", corners));
            }
            output.WriteLine($"  theirs over these corners: {string.Join(", ", theirsHere)}");
        }
    }

    // A triangle by its corner positions, from its least corner on, winding kept.
    private static string Key(Vector3 a, Vector3 b, Vector3 c)
    {
        var r = new[] { a, b, c }.Select(v => v.ToString("R", null)).ToArray();
        var s = Enumerable.Range(0, 3).MinBy(i => r[i], StringComparer.Ordinal);
        return $"{r[s]}|{r[(s + 1) % 3]}|{r[(s + 2) % 3]}";
    }
}
