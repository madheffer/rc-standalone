using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Source2.Compiler.Physics;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: the meshes physicsbuilder's blend split was handed (a
/// <c>capture_physshapes.py --blend</c> capture) against one .vmap mesh node,
/// vertex by vertex matched on exact position. For a plain mesh, each
/// captured vertex's paint is looked up among the paint of the .vmap corners
/// at that vertex (first, last, other). For a subdivided mesh, it is compared
/// with <see cref="WorldCollision"/>'s tessellated paint.
/// <c>BLENDINPUT=&lt;vmap&gt;|&lt;node id&gt;|&lt;capture json&gt;</c>.
/// </summary>
public class BlendInputCompare(ITestOutputHelper output)
{
    [Fact]
    public void PaintAsHandedToTheSplit()
    {
        if (Environment.GetEnvironmentVariable("BLENDINPUT") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var doc = DmxBinary.ReadFile(parts[0]);
        var world = doc.OfType("CMapWorld").First();
        var id = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        var node = Maps.MapMeshes.Read(doc).First(m => m.NodeId == id);
        var mesh = node.Element!;
        var paint = WorldCollision.PaintStream(mesh)!;
        var path = node.Instances.Length > 0 ? node.Path : null;

        // Every candidate paint per position: plain pieces list each corner's
        // paint in corner order; tessellated pieces give our one value.
        var candidates = new Dictionary<Vector3, List<Vector4>>();
        var idAt = new Dictionary<Vector3, int>();
        var idsAt = new Dictionary<Vector3, HashSet<int>>();
        var ours = new Dictionary<Vector3, Vector4>();
        var ourPieces = new List<(Vector3[] Points, Vector4[] Paint)>();
        var subdivided = mesh.Get<DmxBinary.Element>("meshData")?.Get<DmxBinary.Element>("subdivisionData")?.Get<object?[]>("subdivisionLevels") is { } lv && lv.Any(x => x is int i && i > 0);
        if (subdivided)
        {
            foreach (var (material, points, pieceIndices, piecePaint) in WorldCollision.TessellatedPieces(mesh, world, path, out _))
            {
                if (points.Length <= 4)
                    output.WriteLine($"  piece material {material}: {string.Join(" ", points.Select((q, i) => $"{q}={piecePaint?[i].X}"))} tris {string.Join(",", pieceIndices)}");
                if (piecePaint != null)
                    ourPieces.Add((points, piecePaint));
            }
        }
        else
        {
            foreach (var (_, positions, faces, _, cornerIds, cornerData, _) in BrushHulls.PiecesWithCorners(mesh, world, path))
                for (var f = 0; f < faces.Length; f++)
                    for (var j = 0; j < faces[f].Length; j++)
                    {
                        var p = positions[faces[f][j]];
                        if (!candidates.TryGetValue(p, out var list))
                            candidates[p] = list = [];
                        list.Add(paint[cornerData[f][j]]);
                        idAt.TryAdd(p, cornerIds[f][j]);
                        if (!idsAt.TryGetValue(p, out var ids))
                            idsAt[p] = ids = [];
                        ids.Add(cornerIds[f][j]);
                    }
        }

        // Candidate rules over the .vmap's half-edges, keyed by vertex id: the
        // first and last corner met walking faces in order and each loop in
        // order, and the corners around the vertex's stored half-edge.
        var data = mesh.Get<DmxBinary.Element>("meshData")!;
        int[] Ints(string name) => (data.Get<object?[]>(name) ?? []).Select(x => x is int i ? i : -1).ToArray();
        var next = Ints("edgeNextIndices");
        var to = Ints("edgeVertexIndices");
        var opposite = Ints("edgeOppositeIndices");
        var cornerOf = Ints("edgeVertexDataIndices");
        var faceFirst = Ints("faceEdgeIndices");
        var vertexEdge = Ints("vertexEdgeIndices");
        var vertexData = Ints("vertexDataIndices");
        var positionsStream = (data.Get<DmxBinary.Element>("vertexData")!.GetElements("streams").First(s => s.Name.Split(':')[0] == "position").Get<object?[]>("data") ?? []);
        // Per face set (lightmap scale bias, material), as the export splits them.
        var faceDataIdx = Ints("faceDataIndices");
        int[] FaceStream(string name) => (data.Get<DmxBinary.Element>("faceData")?.GetElements("streams").FirstOrDefault(x => x.Name.Split(':')[0] == name)?.Get<object?[]>("data") ?? [])
            .Select(x => x is int i ? i : 0).ToArray();
        var faceMat = FaceStream("materialindex");
        var faceBias = FaceStream("lightmapScaleBias");
        (int, int) SetOf(int f) => (faceBias.Length == 0 ? 0 : faceBias[faceDataIdx[f]], faceMat.Length == 0 ? 0 : faceMat[faceDataIdx[f]]);
        var firstMetBy = new Dictionary<(int, int), Dictionary<int, Vector4>>();
        var lastMetBy = new Dictionary<(int, int), Dictionary<int, Vector4>>();
        for (var f = 0; f < faceFirst.Length; f++)
        {
            var set = SetOf(f);
            if (!firstMetBy.TryGetValue(set, out var fm))
            {
                firstMetBy[set] = fm = [];
                lastMetBy[set] = [];
            }
            var h = faceFirst[f];
            do
            {
                fm.TryAdd(to[h], paint[cornerOf[h]]);
                lastMetBy[set][to[h]] = paint[cornerOf[h]];
                h = next[h];
            } while (h != faceFirst[f]);
        }
        int Prev(int h)
        {
            var p = h;
            while (next[p] != h)
                p = next[p];
            return p;
        }
        var scales = mesh.GetValue<Vector3>("scales") ?? Vector3.One;
        // A vertex id by its local position (scaled), for the flat mesh.
        var byLocal = new Dictionary<Vector3, int>();
        for (var vtx = 0; vtx < vertexData.Length; vtx++)
            if (vertexData[vtx] >= 0)
                byLocal.TryAdd((Vector3)positionsStream[vertexData[vtx]]! * scales, vtx);
        var rules = new Dictionary<string, int> { ["face order first"] = 0, ["face order last"] = 0, ["stored edge, opposite"] = 0, ["stored edge, prev"] = 0 };
        foreach (var set in firstMetBy.Keys)
            rules[$"set {set} first"] = rules[$"set {set} last"] = 0;
        // The whole mesh, face sets in (bias, material) order.
        var setOrder = new Dictionary<int, Vector4>();
        foreach (var f in Enumerable.Range(0, faceFirst.Length).OrderBy(f => SetOf(f)).ThenBy(f => f))
        {
            var h = faceFirst[f];
            do
            {
                setOrder.TryAdd(to[h], paint[cornerOf[h]]);
                h = next[h];
            } while (h != faceFirst[f]);
        }
        rules["whole mesh, sets in order, first"] = 0;
        // Per material, its face sets in bias order.
        var byMaterial = new Dictionary<int, Dictionary<int, Vector4>>();
        foreach (var f in Enumerable.Range(0, faceFirst.Length).OrderBy(f => SetOf(f)).ThenBy(f => f))
        {
            var mat = SetOf(f).Item2;
            if (!byMaterial.TryGetValue(mat, out var dm))
                byMaterial[mat] = dm = [];
            var h = faceFirst[f];
            do
            {
                dm.TryAdd(to[h], paint[cornerOf[h]]);
                h = next[h];
            } while (h != faceFirst[f]);
        }
        rules["material 2, biases in order, first"] = 0;
        var firstMet = new Dictionary<int, Vector4>();
        var lastMet = new Dictionary<int, Vector4>();
        for (var f = faceFirst.Length - 1; f >= 0; f--)
        {
            var h = faceFirst[f];
            do
            {
                firstMet[to[h]] = paint[cornerOf[h]];
                h = next[h];
            } while (h != faceFirst[f]);
        }
        for (var f = 0; f < faceFirst.Length; f++)
        {
            var h = faceFirst[f];
            do
            {
                lastMet[to[h]] = paint[cornerOf[h]];
                h = next[h];
            } while (h != faceFirst[f]);
        }

        using var capture = JsonDocument.Parse(File.ReadAllBytes(parts[2]));
        foreach (var e in capture.RootElement.EnumerateArray().Where(e => e.TryGetProperty("blend", out var k) && k.GetString() == "in"))
        {
            var m = e.GetProperty("mesh");
            var stride = m.GetProperty("stride").GetInt32();
            var streams = m.GetProperty("streams").EnumerateArray().ToList();
            var at = streams.First(x => x.GetProperty("name").GetString() == "VertexPaintBlendParams").GetProperty("offset").GetInt32();
            var v = MemoryMarshal.Cast<byte, float>(Convert.FromHexString(m.GetProperty("vdata").GetString()!)).ToArray();
            var n = v.Length / stride;
            int unmatched = 0, same = 0, first = 0, last = 0, other = 0, none = 0;
            var shown = 0;
            if (subdivided)
            {
                // The piece with this vertex count and first vertex, vertex for vertex.
                var p0 = new Vector3(v[0], v[1], v[2]);
                var piece = ourPieces.FirstOrDefault(x => x.Points.Length == n && x.Points[0] == p0);
                if (piece.Points == null && ourPieces.Count > 0 && n > 100)
                    piece = ourPieces.MaxBy(x => x.Points.Length);
                ours = piece.Points == null ? [] : piece.Points.Select((q, i) => (q, i)).GroupBy(x => x.q).ToDictionary(g => g.Key, g => piece.Paint[g.First().i]);
            }
            for (var k = 0; k < n; k++)
            {
                var p = new Vector3(v[k * stride], v[(k * stride) + 1], v[(k * stride) + 2]);
                var c = new Vector4(v[(k * stride) + at], v[(k * stride) + at + 1], v[(k * stride) + at + 2], v[(k * stride) + at + 3]);
                if (subdivided)
                {
                    if (!ours.TryGetValue(p, out var o))
                        unmatched++;
                    else if (o == c)
                        same++;
                    else if (shown++ < 8)
                        output.WriteLine($"  vertex {k} at {p}: captured {c}, ours {o}");
                    continue;
                }
                if (!candidates.TryGetValue(p, out var list))
                {
                    unmatched++;
                    continue;
                }
                if (idAt.TryGetValue(p, out var vid))
                {
                    if (byMaterial.TryGetValue(2, out var mm) && mm.TryGetValue(vid, out var want) && want != c)
                    {
                        var met = new List<string>();
                        for (var f = 0; f < faceFirst.Length; f++)
                        {
                            if (SetOf(f).Item2 != 2)
                                continue;
                            var h = faceFirst[f];
                            var j = 0;
                            do
                            {
                                if (to[h] == vid)
                                    met.Add($"f{f}{SetOf(f)}c{j}/h{h}/d{cornerOf[h]}:{paint[cornerOf[h]].X}");
                                h = next[h];
                                j++;
                            } while (h != faceFirst[f]);
                        }
                        output.WriteLine($"  MISS vertex {vid} at {p}: captured {c.X}, corners {string.Join(" ", met)}");
                    }
                    foreach (var set in firstMetBy.Keys)
                    {
                        if (idsAt[p].Any(x => firstMetBy[set].TryGetValue(x, out var a) && a == c))
                            rules[$"set {set} first"]++;
                        else if (set == (1, 2) && n > 4)
                            output.WriteLine($"  SETMISS {p}: ids {string.Join(",", idsAt[p])} captured {c.X} set values {string.Join(",", idsAt[p].Select(x => firstMetBy[set].TryGetValue(x, out var a) ? a.X.ToString() : "-"))}");
                        if (lastMetBy[set].TryGetValue(vid, out var b) && b == c)
                            rules[$"set {set} last"]++;
                    }
                    if (byMaterial.TryGetValue(2, out var m2) && m2.GetValueOrDefault(vid) == c)
                        rules["material 2, biases in order, first"]++;
                    if (setOrder.GetValueOrDefault(vid) == c)
                        rules["whole mesh, sets in order, first"]++;
                    if (firstMet.GetValueOrDefault(vid) == c)
                        rules["face order first"]++;
                    if (lastMet.GetValueOrDefault(vid) == c)
                        rules["face order last"]++;
                    var e0 = vertexEdge.ElementAtOrDefault(vid);
                    if (e0 >= 0 && opposite[e0] >= 0 && paint[cornerOf[opposite[e0]]] == c)
                        rules["stored edge, opposite"]++;
                    if (e0 >= 0 && paint[cornerOf[Prev(e0)]] == c)
                        rules["stored edge, prev"]++;
                }
                if (list[0] == c)
                    first++;
                else if (list[^1] == c)
                    last++;
                else if (list.Contains(c))
                    other++;
                else
                {
                    none++;
                    if (shown++ < 8)
                        output.WriteLine($"  vertex {k} at {p}: captured {c}, corners {string.Join(" ", list)}");
                }
                if (list.All(x => x == list[0]))
                    same++;
            }
            output.WriteLine(subdivided
                ? $"mesh of {n} vertices: {unmatched} unmatched, {same} paint equal to ours"
                : $"mesh of {n} vertices: {unmatched} unmatched; paint from first corner {first}, last {last}, other {other}, none {none}; {same} vertices whose corners agree; rules {string.Join(", ", rules.Select(r => $"{r.Key} {r.Value}"))}");
            foreach (var key in rules.Keys.ToList())
                rules[key] = 0;
        }
    }
}
