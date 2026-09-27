using System.Numerics;
using System.Runtime.InteropServices;
using Source2.Compiler.Physics;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The world's triangle soups as <see cref="WorldCollision"/> assembles them,
/// against the RnMeshCreate inputs a full compile made
/// (<c>tools/physics/capture_rnmesh.py --full</c>). The soups are compared in
/// order with the captured calls from the first world call on.
/// <c>WORLDCOL=&lt;addon&gt;|&lt;map&gt;|&lt;capture&gt;|&lt;first world call id&gt;</c>, and
/// <c>WORLDCOL_GPU=1</c> to draw new-blending materials' sample points.
/// </summary>
public class WorldCollisionInput(ITestOutputHelper output)
{
    [Fact]
    public void SoupsMatchTheCapturedCalls()
    {
        if (Environment.GetEnvironmentVariable("WORLDCOL") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        var game = Path.Combine(cs2, "game");
        var vmap = Path.Combine(cs2, "content", "csgo_addons", parts[0], "maps", parts[1] + ".vmap");
        using var models = new SettleBuildTests.PakModels(Path.Combine(game, "csgo", "pak01_dir.vpk"), Path.Combine(game, "csgo_addons", parts[0]));
        var cache = new Dictionary<string, WorldCollision.MaterialPhysics>(StringComparer.OrdinalIgnoreCase);
        WorldCollision.MaterialPhysics Lookup(string name)
        {
            if (cache.TryGetValue(name, out var known))
                return known;
            var info = models.Material(name);
            var physics = WorldCollision.ReadMaterial(info, models.CollisionProperty);
            output.WriteLine($"material {name}: {physics}{(physics.Blend is { } b ? $" blend {b.LayerCount} layers, surfaces {string.Join(",", b.Surfaces)}, remap {string.Join(",", b.Remap)}, puddle {b.PuddleChannel}/{b.PuddleLayer}, sampled {b.Sampled}" : "")}{(info == null ? " (not found)" : "")}");
            return cache[name] = physics;
        }

        // WORLDCOL_GPU=1: new-blending pieces take their layers from the GPU sampler.
        using var gpu = Environment.GetEnvironmentVariable("WORLDCOL_GPU") == "1"
            ? new Source2.Compiler.Gpu.GpuMaterialSampler(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"), models.Read)
            : null;
        var notes = new List<string>();
        var pieces = WorldCollision.Pieces(DmxBinary.ReadFile(vmap), Lookup, notes, gpu == null ? null : gpu.For, models.Physics);
        foreach (var note in notes)
            output.WriteLine("PAINT " + note);
        var meshPieces = pieces.Where(p => p.Hull == null).ToList();
        // The part builder registers each shape's attribute and surface as it
        // writes it (FUN_180c25900: spheres, capsules, hulls, then the mesh
        // gatherer), so the tables fill in first-appearance order of the sorted part.
        var attributes = new List<string>();
        var surfaces = new List<uint>();
        int IndexOf<T>(List<T> table, T key)
        {
            var i = table.IndexOf(key);
            if (i < 0)
            {
                table.Add(key);
                i = table.Count - 1;
            }
            return i;
        }
        List<int>? types = null;
        if (parts.Length > 4)
            types = CompareInserts(parts[4], meshPieces);
        List<WorldCollision.Piece> ordered;
        if (pieces.Count != meshPieces.Count)
        {
            // Our prop hulls stand in the walk; the captured insert types check where.
            var ours = string.Concat(pieces.Select(p => p.Type));
            if (types != null)
            {
                output.WriteLine($"insert types {(ours == string.Concat(types) ? "same as" : "differ from")} the capture ({pieces.Count - meshPieces.Count} hulls ours, {types.Count(t => t == WorldCollision.HullType)} captured)");
                // Where the hulls stand: before each matched mesh, as many hulls in ours as in the capture.
                int same = 0, differ = 0;
                var hullsBefore = 0;
                var meshIndex = 0;
                foreach (var p in pieces)
                {
                    if (p.Hull != null)
                    {
                        hullsBefore++;
                        continue;
                    }
                    if (_capturedAt.TryGetValue(meshIndex, out var k))
                    {
                        if (types.Take(k).Count(t => t == WorldCollision.HullType) == hullsBefore)
                            same++;
                        else if (differ++ < 5)
                            output.WriteLine($"  mesh {p.NodeId}/{p.Material}: {hullsBefore} hulls before it in ours, {types.Take(k).Count(t => t == WorldCollision.HullType)} in the capture");
                    }
                    meshIndex++;
                }
                output.WriteLine($"hulls before each matched mesh: {same} same, {differ} differ");
            }
            ordered = WorldCollision.PartOrder(pieces, p => p.Type);
        }
        else
        {
            // Without prop physics: with a capture, hull slots (null here) stand
            // where the captured part had them, so the sort shuffles ties as it did.
            var slots = new List<WorldCollision.Piece?>();
            var next = 0;
            foreach (var type in types ?? [])
                slots.Add(type == WorldCollision.MeshType && next < pieces.Count ? pieces[next++] : null);
            slots.AddRange(pieces.Skip(next));
            if (types != null && types.Count(t => t == WorldCollision.MeshType) != pieces.Count)
                output.WriteLine($"captured part has {types.Count(t => t == WorldCollision.MeshType)} mesh inserts, ours {pieces.Count}");
            ordered = WorldCollision.PartOrder(slots, p => p == null ? WorldCollision.HullType : WorldCollision.MeshType).OfType<WorldCollision.Piece>().ToList();
        }
        output.WriteLine("part order: " + string.Join(" ", ordered.Take(60).Select(p => $"{p.NodeId}/{p.Material}")));
        // Hulls are written before the mesh gatherer runs.
        var shapes = ordered.Where(p => p.Hull != null).Concat(ordered.Where(p => p.Hull == null))
            .Select(p => (Piece: p, Attribute: IndexOf(attributes, p.Physics.AttributeKey), Surface: IndexOf(surfaces, p.Physics.SurfaceKey))).ToList();
        var soups = WorldCollision.Group(shapes.Where(x => x.Piece.Hull == null).Select(x => (x.Attribute, x.Surface, x.Piece.Points, x.Piece.Indices)));
        output.WriteLine($"attributes: {string.Join(" ; ", attributes)}; surfaces: {string.Join(" ", surfaces.Select(h => h.ToString("x8")))}");
        if (shapes.Any(x => x.Piece.Hull != null))
            output.WriteLine("hull attr/surf: " + string.Join(" ", shapes.Where(x => x.Piece.Hull != null).Select(x => $"{x.Attribute}/{x.Surface}")));

        var first = int.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
        var allSame = true;
        for (var s = 0; s < soups.Count; s++)
        {
            var soup = soups[s];
            var (indices, vertices, materials) = Input(parts[2], first + s);
            var sameV = soup.Vertices.SequenceEqual(vertices);
            var sameI = soup.Indices.SequenceEqual(indices);
            var sameM = materials == null ? soup.Materials == null : soup.Materials != null && soup.Materials.SequenceEqual(materials);
            allSame &= sameV && sameI && sameM;
            output.WriteLine($"soup {s} attr {soup.Attribute} surf {soup.SurfaceProperty}: {soup.Indices.Count / 3} triangles, {soup.Vertices.Count} vertices, materials {soup.Materials?.Count.ToString() ?? "none"}; " +
                             $"call {first + s}: {indices.Length / 3} triangles, {vertices.Length} vertices, materials {materials?.Length.ToString() ?? "none"}; " +
                             $"vertices {(sameV ? "same" : "differ")}, indices {(sameI ? "same" : "differ")}, materials {(sameM ? "same" : "differ")}");
            if (!sameM && materials != null && soup.Materials != null)
                output.WriteLine($"  surfaces per triangle: ours {string.Join(" ", soup.Materials.GroupBy(m => m).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}"))}, " +
                                 $"captured {string.Join(" ", materials.GroupBy(m => m).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}"))}; " +
                                 $"runs ours {string.Join(" ", Runs(soup.Materials))}, captured {string.Join(" ", Runs(materials))}");
            if (!sameV)
            {
                var i = Enumerable.Range(0, Math.Min(soup.Vertices.Count, vertices.Length)).FirstOrDefault(k => soup.Vertices[k] != vertices[k], -1);
                if (i >= 0)
                    output.WriteLine($"  first vertex difference at {i}: ours {soup.Vertices[i]:R} captured {vertices[i]:R}");
            }
        }
        Assert.True(allSame);
    }

    /// <summary>
    /// Our pieces against the world part's mesh inserts in a
    /// capture_physshapes.py capture, in insert order: each captured mesh
    /// shape by vertex count and first vertex, and which of our pieces it is.
    /// </summary>
    // Our mesh piece index -> the captured insert it matched.
    private readonly Dictionary<int, int> _capturedAt = [];

    private List<int> CompareInserts(string capture, List<WorldCollision.Piece> pieces)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(capture));
        var all = doc.RootElement.EnumerateArray().ToList();
        System.Text.Json.JsonElement world;
        world = all.Where(c => c.TryGetProperty("call", out _)).MaxBy(c => c.GetProperty("count").GetInt32());
        var count = world.GetProperty("count").GetInt32();
        var byPart = all.Where(c => c.TryGetProperty("insert", out _)).GroupBy(c => c.GetProperty("part").GetString()).ToList();
        var inserts = byPart.Last(g => g.Count() == count).ToList();
        var ours = pieces.Select((p, i) => (p, i)).Where(x => x.p.Points.Length > 0).GroupBy(x => (x.p.Points.Length, x.p.Points[0])).ToDictionary(g => g.Key, g => new Queue<int>(g.Select(x => x.i)));
        var matched = new HashSet<int>();
        var lines = new List<string>();
        var k = 0;
        foreach (var s in inserts)
        {
            var type = s.GetProperty("type").GetInt32();
            if (type != WorldCollision.MeshType)
            {
                k++;
                continue;
            }
            if (!s.TryGetProperty("v0", out var v0e))
            {
                lines.Add($"insert {k} mesh with no soup at insert (built mesh?)");
                k++;
                continue;
            }
            var v0 = v0e.EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
            var key = (s.GetProperty("vertices").GetInt32(), new Vector3(v0[0], v0[1], v0[2]));
            var hit = ours.TryGetValue(key, out var at) && at.Count > 0 ? at.Dequeue() : -1;
            if (hit >= 0)
            {
                matched.Add(hit);
                _capturedAt[hit] = k;
            }
            lines.Add($"insert {k} mesh {key.Item1} v {key.Item2}: " + (hit >= 0 ? $"ours {hit} node {pieces[hit].NodeId}/{pieces[hit].Material} {Path.GetFileNameWithoutExtension(pieces[hit].MaterialName)}" : "not ours"));
            k++;
        }
        output.WriteLine($"captured mesh inserts {lines.Count}, ours {pieces.Count}, matched {matched.Count}; ours unmatched: " +
                         string.Join(" ", Enumerable.Range(0, pieces.Count).Where(i => !matched.Contains(i) && pieces[i].Points.Length > 0).Take(40).Select(i => $"{pieces[i].NodeId}/{pieces[i].Material}:{Path.GetFileNameWithoutExtension(pieces[i].MaterialName)}:{pieces[i].Points.Length}@{pieces[i].Points[0]}")));
        foreach (var l in lines.Where(l => !l.Contains(": ours ")).Take(80))
            output.WriteLine("  " + l);
        // Bit for bit: each of our pieces against the gathered shape with the
        // same vertex count and first vertex, when the capture dumped its data (--dump).
        var dumped = world.GetProperty("shapes").EnumerateArray()
            .Where(s => s.GetProperty("type").GetInt32() == WorldCollision.MeshType && s.TryGetProperty("vdata", out _))
            .Select(s => (V: Convert.FromHexString(s.GetProperty("vdata").GetString()!), I: Convert.FromHexString(s.GetProperty("idata").GetString()!)))
            .ToList();
        if (dumped.Count > 0)
        {
            var byFirst = dumped.GroupBy(d => (d.V.Length / 12, new Vector3(BitConverter.ToSingle(d.V, 0), BitConverter.ToSingle(d.V, 4), BitConverter.ToSingle(d.V, 8))))
                                .ToDictionary(g => g.Key, g => g.ToList());
            int exact = 0, sameFirst = 0;
            var notes = new List<string>();
            foreach (var p in pieces.Where(p => p.Points.Length > 0))
            {
                if (!byFirst.TryGetValue((p.Points.Length, p.Points[0]), out var cands))
                    continue;
                sameFirst++;
                var v = MemoryMarshal.AsBytes(p.Points.AsSpan()).ToArray();
                var ix = MemoryMarshal.AsBytes(p.Indices.AsSpan()).ToArray();
                if (cands.Any(c => c.V.AsSpan().SequenceEqual(v) && c.I.AsSpan().SequenceEqual(ix)))
                    exact++;
                else if (notes.Count < 15)
                {
                    var c = cands[0];
                    var vi = Enumerable.Range(0, p.Points.Length).FirstOrDefault(k => BitConverter.ToSingle(c.V, k * 12) != p.Points[k].X || BitConverter.ToSingle(c.V, (k * 12) + 4) != p.Points[k].Y || BitConverter.ToSingle(c.V, (k * 12) + 8) != p.Points[k].Z, -1);
                    var ii = Enumerable.Range(0, Math.Min(p.Indices.Length, c.I.Length / 4)).FirstOrDefault(k => BitConverter.ToInt32(c.I, k * 4) != p.Indices[k], -1);
                    var detail = vi < 0 ? "" : $" ours {p.Points[vi].X:R},{p.Points[vi].Y:R},{p.Points[vi].Z:R} theirs {BitConverter.ToSingle(c.V, vi * 12):R},{BitConverter.ToSingle(c.V, (vi * 12) + 4):R},{BitConverter.ToSingle(c.V, (vi * 12) + 8):R}";
                    var differing = Enumerable.Range(0, p.Points.Length).Count(k => BitConverter.ToSingle(c.V, k * 12) != p.Points[k].X || BitConverter.ToSingle(c.V, (k * 12) + 4) != p.Points[k].Y || BitConverter.ToSingle(c.V, (k * 12) + 8) != p.Points[k].Z);
                    notes.Add($"{p.NodeId}/{p.Material} {Path.GetFileNameWithoutExtension(p.MaterialName)}: first vertex difference at {vi}{detail}, {differing} of {p.Points.Length} vertices differ, first index difference at {ii} (ours {p.Indices.Length}, theirs {c.I.Length / 4})");
                }
            }
            output.WriteLine($"BITEXACT {exact} of {sameFirst} pieces with a captured shape's count and first vertex are identical, vertices and indices");
            foreach (var n in notes)
                output.WriteLine("  DIFF " + n);
        }
        // Vertex totals per material: ours against the captured shapes' surface names.
        var theirs = inserts.Where(s => s.GetProperty("type").GetInt32() == WorldCollision.MeshType).ToList();
        var gathered = world.GetProperty("shapes").EnumerateArray().Where(s => s.GetProperty("type").GetInt32() == WorldCollision.MeshType)
            .GroupBy(s => s.TryGetProperty("surface", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String ? m.GetString()! : "?")
            .ToDictionary(g => g.Key.Replace((char)92, '/').ToLowerInvariant(), g => (Shapes: g.Count(), Verts: g.Sum(x => x.TryGetProperty("vertices", out var v) ? v.GetInt32() : 0)));
        foreach (var g in pieces.GroupBy(p => p.MaterialName.Replace((char)92, '/').ToLowerInvariant()))
        {
            var ourVerts = g.Sum(p => p.Points.Length);
            var t = gathered.GetValueOrDefault(g.Key);
            if (t.Verts != ourVerts || t.Shapes != g.Count())
                output.WriteLine($"  MAT {g.Key}: ours {g.Count()} pieces {ourVerts} verts, captured {t.Shapes} shapes {t.Verts} verts");
        }
        foreach (var (key, t) in gathered.Where(kv => !pieces.Any(p => p.MaterialName.Replace((char)92, '/').ToLowerInvariant() == kv.Key)))
            output.WriteLine($"  MAT {key}: ours none, captured {t.Shapes} shapes {t.Verts} verts");
        // Each unmatched piece against the nearest captured mesh by first vertex.
        var captured = inserts.Where(s => s.GetProperty("type").GetInt32() == WorldCollision.MeshType && s.TryGetProperty("v0", out _))
            .Select(s => (Count: s.GetProperty("vertices").GetInt32(), V: s.GetProperty("v0").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray())).ToList();
        foreach (var i in Enumerable.Range(0, pieces.Count).Where(i => !matched.Contains(i) && pieces[i].Points.Length > 0))
        {
            var p0 = pieces[i].Points[0];
            var best = captured.MinBy(c => Vector3.DistanceSquared(new Vector3(c.V[0], c.V[1], c.V[2]), p0));
            var d = Vector3.Distance(new Vector3(best.V[0], best.V[1], best.V[2]), p0);
            var bv = new Vector3(best.V[0], best.V[1], best.V[2]);
            output.WriteLine($"  NEAR {pieces[i].NodeId}/{pieces[i].Material} {Path.GetFileNameWithoutExtension(pieces[i].MaterialName)} ours {pieces[i].Points.Length} v {p0:R} | nearest {best.Count} at distance {d:G4}, first {bv:R}, ours at {Array.IndexOf(pieces[i].Points, bv)}");
            // The same triangles in another order? Each triangle as its corner
            // positions from its lowest corner on, against a dumped shape of that size.
            static string Key(Vector3 a, Vector3 b, Vector3 c)
            {
                var r = new[] { a, b, c }.Select(v => v.ToString("R", null)).ToArray();
                var k = Enumerable.Range(0, 3).MinBy(j => r[j], StringComparer.Ordinal);
                return r[k] + r[(k + 1) % 3] + r[(k + 2) % 3];
            }
            var piece = pieces[i];
            var mine = Enumerable.Range(0, piece.Indices.Length / 3).Select(t => Key(piece.Points[piece.Indices[t * 3]], piece.Points[piece.Indices[(t * 3) + 1]], piece.Points[piece.Indices[(t * 3) + 2]])).Order(StringComparer.Ordinal).ToList();
            foreach (var shape in world.GetProperty("shapes").EnumerateArray().Where(x => x.TryGetProperty("vdata", out _) && x.GetProperty("vertices").GetInt32() == piece.Points.Length))
            {
                var v = MemoryMarshal.Cast<byte, Vector3>(Convert.FromHexString(shape.GetProperty("vdata").GetString()!)).ToArray();
                var ix = MemoryMarshal.Cast<byte, int>(Convert.FromHexString(shape.GetProperty("idata").GetString()!)).ToArray();
                var captured3 = Enumerable.Range(0, ix.Length / 3).Select(t => Key(v[ix[t * 3]], v[ix[(t * 3) + 1]], v[ix[(t * 3) + 2]])).Order(StringComparer.Ordinal).ToList();
                output.WriteLine($"    same triangles in another order: {mine.SequenceEqual(captured3)} (ours {mine.Count}, theirs {captured3.Count}, shared {mine.Intersect(captured3).Count()})");
            }
        }
        return [.. inserts.Select(s => s.GetProperty("type").GetInt32())];
    }

    private static IEnumerable<string> Runs(IReadOnlyList<byte> m)
    {
        for (var i = 0; i < m.Count;)
        {
            var j = i;
            while (j < m.Count && m[j] == m[i])
                j++;
            yield return $"{m[i]}x{j - i}";
            i = j;
        }
    }

    private static (int[] Indices, Vector3[] Vertices, byte[]? Materials) Input(string path, int id)
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
            var hasMaterials = head.GetProperty("hasMaterials").GetBoolean();
            var off = (tris * 12) + (hasMaterials ? tris : 0);
            return (MemoryMarshal.Cast<byte, int>(blob[..(tris * 12)]).ToArray(), MemoryMarshal.Cast<byte, Vector3>(blob.Slice(off, vcount * 12)).ToArray(),
                    hasMaterials ? blob.Slice(tris * 12, tris).ToArray() : null);
        }
        throw new InvalidDataException($"no call {id}");
    }
}
