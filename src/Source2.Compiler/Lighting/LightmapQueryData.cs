using System.Numerics;
using Source2.Compiler.Maps;
using Source2.Compiler.Meshopt;
using ValveKeyValue;

namespace Source2.Compiler.Lighting;

/// <summary>
/// maps/&lt;map&gt;/lightmaps/lightmap_query_data.kv3, as
/// StaticLighting_WriteLightmapQueryData (resourcecompiler 0923 1802163b0)
/// writes it from the packing geometry:
/// <list type="number">
/// <item>A CMesh of five floats per vertex: the position, and the atlas uv as
/// V_roundf(uv / (65535 / a) * 65535) per axis, with a = (float)(int)(floor(32767 /
/// size) size 2) (57344 for 4096; the caller's 1802171b0 setup).</item>
/// <item>180216090: when the map has lightmap resolution volumes, a triangle
/// that overlaps none of their boxes grown by 240 (TriBoxOverlap) becomes
/// 0, 0, 0.</item>
/// <item>CMesh_Weld at 1/32 (the uv stream exact), dropping degenerate
/// triangles and renumbering by first use.</item>
/// <item>meshopt_simplify to half the indices, error 0.05, no options.</item>
/// <item>KV3 (binary_auto, generic): version 1, vertex_count, index_count,
/// vertices (three floats and two u16 per vertex) and indices (u32).</item>
/// </list>
/// The volumes are the importance volumes (<see cref="WorldNodeBuild.ImportanceVolumes"/>):
/// 180215bf0 reads them through the lighting context and boxes each point list.
/// </summary>
public static class LightmapQueryData
{
    /// <summary>The uv quantisation a for a lightmap side: (float)(int)((int)floor(32767 / size) * size * 2).</summary>
    public static float UvScale(int size) => (int)((int)MathF.Floor(32767f / size) * size * 2);

    public static byte[] Build(PackingGeometry g, int lightmapWidth, int lightmapHeight, IReadOnlyList<Vector3[]> volumes)
    {
        // param_9[2] / [3]: 65535 over the scale of each axis (u from the first size, v from the second).
        var pu = 65535f / UvScale(lightmapWidth);
        var pv = 65535f / UvScale(lightmapHeight);
        var count = g.VertexCount;
        const int stride = 5;
        var vertices = new float[count * stride];
        var position = (int)g.Offsets[0];
        for (var i = 0; i < count; i++)
        {
            var at = i * g.VertexStride + position;
            vertices[i * stride] = BitConverter.ToSingle(g.Vertices, at);
            vertices[i * stride + 1] = BitConverter.ToSingle(g.Vertices, at + 4);
            vertices[i * stride + 2] = BitConverter.ToSingle(g.Vertices, at + 8);
            vertices[i * stride + 3] = MathF.Round((g.AtlasUv[i * 2] / pu) * 65535f, MidpointRounding.AwayFromZero);
            vertices[i * stride + 4] = MathF.Round((g.AtlasUv[i * 2 + 1] / pv) * 65535f, MidpointRounding.AwayFromZero);
        }
        var indices = g.Indices.Select(i => (int)i).ToArray();
        Cull(vertices, stride, indices, volumes);

        var (welded, weldedIndices) = Physics.MeshWeld.Weld(vertices, stride, indices,
            [new Physics.MeshWeld.Stream("position", 0, 3, false, 0), new Physics.MeshWeld.Stream("LightmapUV", 3, 2, false, 0x29)], 1f / 32f);
        var vertexCount = welded.Length / stride;
        var source = weldedIndices.Select(i => (uint)i).ToArray();
        var simplified = new uint[source.Length];
        var indexCount = MeshoptSimplify.Simplify(simplified, source, welded, vertexCount, stride, source.Length / 2, 0.05f, 0, out _);

        var vertexBlob = new byte[vertexCount * 16];
        for (var i = 0; i < vertexCount; i++)
        {
            BitConverter.TryWriteBytes(vertexBlob.AsSpan(i * 16), welded[i * stride]);
            BitConverter.TryWriteBytes(vertexBlob.AsSpan(i * 16 + 4), welded[i * stride + 1]);
            BitConverter.TryWriteBytes(vertexBlob.AsSpan(i * 16 + 8), welded[i * stride + 2]);
            BitConverter.TryWriteBytes(vertexBlob.AsSpan(i * 16 + 12), (ushort)(uint)welded[i * stride + 3]);
            BitConverter.TryWriteBytes(vertexBlob.AsSpan(i * 16 + 14), (ushort)(uint)welded[i * stride + 4]);
        }
        var indexBlob = new byte[indexCount * 4];
        for (var i = 0; i < indexCount; i++)
            BitConverter.TryWriteBytes(indexBlob.AsSpan(i * 4), simplified[i]);

        var root = KVObject.Collection();
        root.Add("version", new KVObject(1L));
        root.Add("vertex_count", new KVObject(vertexCount));
        root.Add("index_count", new KVObject(indexCount));
        root.Add("vertices", KVObject.Blob(vertexBlob));
        root.Add("indices", KVObject.Blob(indexBlob));
        return Kv3V5Writer.Write(root, GenericFormat);
    }

    private static readonly Guid GenericFormat = new("7412167c-06e9-4698-aff2-e63eb59037e7");

    // 180216090: triangles away from every resolution volume become 0, 0, 0.
    private static void Cull(float[] vertices, int stride, int[] indices, IReadOnlyList<Vector3[]> volumes)
    {
        if (volumes.Count == 0)
            return;
        var boxes = volumes.Select(points =>
        {
            Vector3 min = new(float.MaxValue), max = new(-float.MaxValue);
            foreach (var p in points)
            {
                min = new Vector3(p.X <= min.X ? p.X : min.X, p.Y <= min.Y ? p.Y : min.Y, p.Z <= min.Z ? p.Z : min.Z);
                max = new Vector3(max.X <= p.X ? p.X : max.X, max.Y <= p.Y ? p.Y : max.Y, max.Z <= p.Z ? p.Z : max.Z);
            }
            return (Centre: new Vector3((min.X + max.X) * 0.5f, (min.Y + max.Y) * 0.5f, (min.Z + max.Z) * 0.5f),
                Half: new Vector3((max.X - min.X) * 0.5f, (max.Y - min.Y) * 0.5f, (max.Z - min.Z) * 0.5f));
        }).ToList();
        Vector3 P(int i) => new(vertices[i * stride], vertices[i * stride + 1], vertices[i * stride + 2]);
        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            var a = P(indices[t]);
            var b = P(indices[t + 1]);
            var c = P(indices[t + 2]);
            if (boxes.Any(box => VisibilityMeshMerger.TriBoxOverlap(box.Centre, box.Half, a, b, c, 240f)))
                continue;
            indices[t] = indices[t + 1] = indices[t + 2] = 0;
        }
    }
}
