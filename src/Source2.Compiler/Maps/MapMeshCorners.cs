using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// A map mesh as the map builder hands it to its weld: one triangle mesh per
/// material, one vertex per triangle corner (FUN_18020b230 copies these out
/// of the node's mesh data before welding them at 1/32).
///
/// <para>Faces are taken in order and cut by <see cref="PolygonTriangulator"/>
/// on the mesh's own positions. A corner carries its position (the
/// vertexData stream through vertexDataIndices, times the mesh's scales)
/// and every faceVertexData stream through edgeVertexDataIndices, in the
/// .vmap's stream order, less the tangent the map builder drops before
/// welding. Texcoords are the stored ones less a whole number per face.</para>
///
/// <para>Not yet Valve's: it builds the mesh from the stored .vmap streams,
/// where the map builder recomputes texcoords from the texture projection,
/// smooths normals and keeps only the streams the material uses. Positions
/// and corner order match all 1330 of Mako's captured pieces and 549 match
/// in every float (docs/HULLS.md). The whole number taken off each face's
/// texcoords (its rounded mean) is measured, not read.</para>
/// </summary>
internal static class MapMeshCorners
{
    /// <summary>One material's triangle mesh: vertices of <paramref name="Stride"/> floats, three corners a triangle.</summary>
    public sealed record Piece(int Material, int Stride, float[] Vertices, int[] Indices, IReadOnlyList<Physics.MeshWeld.Stream> Streams);

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
        var layout = new List<Physics.MeshWeld.Stream> { new("position", 0, 3, false, 42) };
        var stride = 3;
        foreach (var (name, _, count) in streams)
        {
            layout.Add(new(name, stride, count, false, 0));
            stride += count;
        }

        var byMaterial = new SortedDictionary<int, (List<float> V, List<int> I)>();
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
                byMaterial[material] = piece = ([], []);
            var shift = new Dictionary<int, Vector2>();
            foreach (var j in cut)
            {
                piece.I.Add(piece.I.Count);
                var p = local[j];
                piece.V.Add(p.X);
                piece.V.Add(p.Y);
                piece.V.Add(p.Z);
                for (var k = 0; k < streams.Count; k++)
                {
                    var value = streams[k].Data[cornerData[loop[j]]];
                    if (streams[k].Name == "texcoord")
                    {
                        if (!shift.TryGetValue(k, out var s))
                            shift[k] = s = Shift(loop.Select(x => (Vector2)streams[k].Data[cornerData[x]]!));
                        var uv = (Vector2)value!;
                        piece.V.Add(uv.X - s.X);
                        piece.V.Add(uv.Y - s.Y);
                    }
                    else
                        Append(piece.V, value, streams[k].Count);
                }
            }
        }
        return [.. byMaterial.Select(kv => new Piece(kv.Key, stride, [.. kv.Value.V], [.. kv.Value.I], layout))];
    }

    // The whole number a face's texcoords lose: their mean, rounded.
    private static Vector2 Shift(IEnumerable<Vector2> uvs)
    {
        var list = uvs.ToList();
        float u = 0f, v = 0f;
        foreach (var uv in list)
        {
            u += uv.X;
            v += uv.Y;
        }
        return new Vector2(MathF.Round(u / list.Count, MidpointRounding.AwayFromZero), MathF.Round(v / list.Count, MidpointRounding.AwayFromZero));
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
