using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// A map mesh as the map builder hands it to its weld: one triangle mesh per
/// material, one vertex per triangle corner (FUN_18020b230 copies these out
/// of the node's mesh data before welding them at 1/32).
///
/// <para>Faces are taken in order and cut by <see cref="PolygonTriangulator"/>
/// on the mesh's own positions. A corner carries its position (the
/// vertexData stream through vertexDataIndices, times the mesh's scales),
/// then its texcoord, normal and per-vertex lighting through
/// edgeVertexDataIndices.</para>
///
/// <para>Hammer's ConvertMeshForBuilder (FUN_1810dff20) writes the mesh the
/// builder reads. Checked against those meshes dumped from a compile, a brush
/// entity's mesh stays in its own space with its stored streams, but for the
/// texcoord shift (<see cref="ShiftTexcoords"/>) and some re-projected faces
/// off by about 1e-5. A material whose shader reads a second texcoord or
/// vertex paint keeps those streams too; they hold defaults, so they are left
/// out (docs/HULLS.md).</para>
/// </summary>
internal static class MapMeshCorners
{
    /// <summary>One material's triangle mesh: vertices of <paramref name="Stride"/> floats, three corners a triangle.</summary>
    public sealed record Piece(int Material, int Stride, float[] Vertices, int[] Indices, IReadOnlyList<Physics.MeshWeld.Stream> Streams)
    {
        /// <summary>The .vmap vertex each corner comes from, corner for corner.</summary>
        public int[] VertexIds { get; init; } = [];
    }

    public static List<Piece> Build(DmxBinary.Element mesh)
    {
        var data = mesh.Get<DmxBinary.Element>("meshData") ?? throw new InvalidDataException("CMapMesh without meshData.");
        var scales = mesh.GetValue<Vector3>("scales") ?? Vector3.One;
        var next = Ints(data, "edgeNextIndices");
        var to = Ints(data, "edgeVertexIndices");
        var first = Ints(data, "faceEdgeIndices");
        var vertexData = Ints(data, "vertexDataIndices");
        var cornerData = Ints(data, "edgeVertexDataIndices");
        var faceData = Ints(data, "faceDataIndices");
        var positions = StreamData(data, "vertexData", "position");
        var materials = (StreamData(data, "faceData", "materialindex") ?? []).Select(x => x is int i ? i : 0).ToArray();

        // The streams each corner carries, in the .vmap's order.
        var streams = new List<(string Name, object?[] Data, int Count)>();
        foreach (var s in data.Get<DmxBinary.Element>("faceVertexData")?.GetElements("streams") ?? [])
        {
            var name = s.Name.Split(':')[0];
            if (name == "tangent")
                continue;
            var values = s.Get<object?[]>("data") ?? [];
            streams.Add((name, values, Width(values)));
        }
        var texcoords = streams.Select((x, i) => (x, i)).Where(x => x.x.Name == "texcoord").ToList();
        var uvData = texcoords.Select(x => x.x.Data).ToList();
        ShiftTexcoords(uvData, first, next, cornerData, Ints(data, "edgeOppositeIndices"));
        for (var t = 0; t < texcoords.Count; t++)
            streams[texcoords[t].i] = (streams[texcoords[t].i].Name, uvData[t], streams[texcoords[t].i].Count);
        // The short layout: first texcoord, normal, per-vertex lighting.
        streams = [.. new[] { "texcoord", "normal", "PerVertexLighting" }
            .Select(n => streams.FirstOrDefault(x => x.Name == n)).Where(x => x.Name != null)];
        var layout = new List<Physics.MeshWeld.Stream> { new("position", 0, 3, false, 42) };
        var stride = 3;
        foreach (var (name, _, count) in streams)
        {
            layout.Add(new(name, stride, count, false, 0));
            stride += count;
        }

        var byMaterial = new SortedDictionary<int, (List<float> V, List<int> I, List<int> Ids)>();
        for (var f = 0; f < first.Length; f++)
        {
            var loop = new List<int>();
            var e = first[f];
            do
            {
                loop.Add(e);
                e = next[e];
            } while (e != first[f] && loop.Count <= next.Length);
            if (loop.Count < 3)
                continue;
            var local = loop.Select(x => (Vector3)positions![vertexData[to[x]]]! * scales).ToArray();
            int[] cut = loop.Count == 3 ? [0, 1, 2] : PolygonTriangulator.Triangulate(local);
            if (cut.Length < 3)
                continue;
            var material = materials.Length == 0 ? 0 : materials[faceData[f]];
            if (!byMaterial.TryGetValue(material, out var piece))
                byMaterial[material] = piece = ([], [], []);
            foreach (var j in cut)
            {
                piece.I.Add(piece.I.Count);
                piece.Ids.Add(to[loop[j]]);
                var p = local[j];
                piece.V.Add(p.X);
                piece.V.Add(p.Y);
                piece.V.Add(p.Z);
                for (var k = 0; k < streams.Count; k++)
                {
                    Append(piece.V, streams[k].Data[cornerData[loop[j]]], streams[k].Count);
                }
            }
        }
        return [.. byMaterial.Select(kv => new Piece(kv.Key, stride, [.. kv.Value.V], [.. kv.Value.I], layout) { VertexIds = [.. kv.Value.Ids] })];
    }

    /// <summary>
    /// FUN_1810d93c0, run when some texcoord lies outside +-1.03125
    /// (FUN_1810e7020): per texcoord stream, each UV island whose box leaves
    /// [0, 1] loses its box centre rounded half away from zero
    /// (FUN_1813cc880). Faces share an island across an edge whose two ends
    /// carry texcoords within a squared distance of 1e-6 on both sides
    /// (FUN_1813b6190). Each shifted stream is replaced by a shifted copy.
    /// </summary>
    internal static void ShiftTexcoords(List<object?[]> texcoords, int[] first, int[] next, int[] cornerData, int[] opposite)
    {
        if (!texcoords.Any(t => t.Any(v => v is Vector2 uv && (uv.X < -1.03125f || 1.03125f < uv.X || uv.Y < -1.03125f || 1.03125f < uv.Y))))
            return;
        var faceOf = Enumerable.Repeat(-1, next.Length).ToArray();
        var prev = new int[next.Length];
        for (var f = 0; f < first.Length; f++)
        {
            var e = first[f];
            do
            {
                faceOf[e] = f;
                prev[next[e]] = e;
                e = next[e];
            } while (e != first[f]);
        }
        for (var t = 0; t < texcoords.Count; t++)
        {
            var uvs = texcoords[t];
            bool Close(int a, int b)
            {
                var x = (Vector2)uvs[a]!;
                var y = (Vector2)uvs[b]!;
                return !((((x.Y - y.Y) * (x.Y - y.Y)) + ((x.X - y.X) * (x.X - y.X))) > 1e-6f);
            }
            var parent = Enumerable.Range(0, first.Length).ToArray();
            int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
            for (var e = 0; e < next.Length; e++)
            {
                var o = e < opposite.Length ? opposite[e] : -1;
                if (faceOf[e] < 0 || o < 0 || faceOf[o] < 0)
                    continue;
                if (Close(cornerData[e], cornerData[prev[o]]) && Close(cornerData[prev[e]], cornerData[o]))
                    parent[Find(faceOf[o])] = Find(faceOf[e]);
            }
            var shifted = (object?[])uvs.Clone();
            foreach (var island in Enumerable.Range(0, first.Length).GroupBy(Find))
            {
                var members = new HashSet<int>();
                foreach (var f in island)
                {
                    var e = first[f];
                    do
                    {
                        members.Add(cornerData[e]);
                        e = next[e];
                    } while (e != first[f]);
                }
                float minU = float.MaxValue, minV = float.MaxValue, maxU = -float.MaxValue, maxV = -float.MaxValue;
                foreach (var i in members)
                {
                    var uv = (Vector2)uvs[i]!;
                    if (uv.X <= minU) minU = uv.X;
                    if (maxU <= uv.X) maxU = uv.X;
                    if (uv.Y <= minV) minV = uv.Y;
                    if (maxV <= uv.Y) maxV = uv.Y;
                }
                if (!(minU < 0f || minV < 0f || 1f < maxU || 1f < maxV))
                    continue;
                var cu = (maxU + minU) * 0.5f;
                var cv = (maxV + minV) * 0.5f;
                var su = (float)(int)(cu < 0f ? cu - 0.5f : cu + 0.5f);
                var sv = (float)(int)(cv < 0f ? cv - 0.5f : cv + 0.5f);
                if (su == 0f && sv == 0f)
                    continue;
                foreach (var i in members)
                {
                    var uv = (Vector2)uvs[i]!;
                    shifted[i] = new Vector2(uv.X - su, uv.Y - sv);
                }
            }
            texcoords[t] = shifted;
        }
    }

    private static int Width(object?[] values) => values.FirstOrDefault() switch
    {
        Vector2 => 2,
        Vector3 => 3,
        Vector4 => 4,
        Quaternion => 4,
        _ => 1,
    };

    private static void Append(List<float> v, object? value, int count)
    {
        switch (value)
        {
            case Vector2 a: v.Add(a.X); v.Add(a.Y); break;
            case Vector3 a: v.Add(a.X); v.Add(a.Y); v.Add(a.Z); break;
            case Vector4 a: v.Add(a.X); v.Add(a.Y); v.Add(a.Z); v.Add(a.W); break;
            case Quaternion a: v.Add(a.X); v.Add(a.Y); v.Add(a.Z); v.Add(a.W); break;
            case float a: v.Add(a); break;
            case int a: v.Add(a); break;
            default: for (var i = 0; i < count; i++) v.Add(0f); break;
        }
    }

    private static object?[]? StreamData(DmxBinary.Element data, string holder, string name) =>
        data.Get<DmxBinary.Element>(holder)?.GetElements("streams")
            .FirstOrDefault(s => s.Name.StartsWith(name + ":", StringComparison.Ordinal))?.Get<object?[]>("data");

    private static int[] Ints(DmxBinary.Element e, string name)
        => (e.Get<object?[]>(name) ?? []).Select(x => x is int i ? i : -1).ToArray();
}
