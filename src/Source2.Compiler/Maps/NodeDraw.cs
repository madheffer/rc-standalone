using Source2.Compiler.Meshopt;

namespace Source2.Compiler.Maps;

/// <summary>
/// One world node draw from its source entry (GROUND_TRUTH 34): what the
/// model compiler does to the in-memory ModelDoc mesh the node builds.
/// <list type="number">
/// <item>CMesh_Weld at 1e-7 over every stream, vertices renumbered by first
/// use (<see cref="Physics.MeshWeld"/>).</item>
/// <item>meshopt optimizeVertexCacheTable, then optimizeOverdraw at 1.03 on
/// the positions (<see cref="MeshoptOptimizers"/>).</item>
/// <item>An aggregate: meshopt 1.0 buildMeshletsFlex (255 vertices, 48
/// triangles, the cone weight given), each meshlet through
/// optimizeMeshletLevel 4, a meshlet with an odd triangle count padded with
/// its last index three times (<see cref="MeshoptMeshlets"/>).</item>
/// <item>The vertices reordered by first use in the final index list
/// (optimizeVertexFetch): every draw of atixref's 1,270 is in that order
/// (VertexOrderProbe).</item>
/// </list>
/// </summary>
internal static class NodeDraw
{
    /// <summary>Meshlet sizes of an aggregate draw (AddDrawDescriptors' meshopt call).</summary>
    public const int MeshletVertices = 255, MeshletTriangles = 48;

    /// <summary>
    /// The cone weight an aggregate's meshlets are built with: 0.15 when the
    /// material culls back faces (AllowBackfaceCulling and not DoubleSided),
    /// else 0.
    /// </summary>
    public static float ConeWeight(bool backfaceCulled) => backfaceCulled ? 0.15f : 0f;

    /// <summary>
    /// A built draw: <see cref="Vertices"/> in fetch order (the source stride),
    /// <see cref="Indices"/> into them, padding triangles included, and for
    /// an aggregate its meshlets (vertex and triangle ranges, triangles
    /// counted with their padding).
    /// </summary>
    public sealed record Result(float[] Vertices, int VertexCount, int[] Indices, IReadOnlyList<MeshoptMeshlets.Meshlet> Meshlets);

    /// <summary>
    /// Builds one draw. <paramref name="vertices"/> holds <paramref name="stride"/>
    /// floats a vertex, the position at <paramref name="positionOffset"/>;
    /// <paramref name="streams"/> describe them for the weld.
    /// </summary>
    public static Result Build(float[] vertices, int stride, int positionOffset, int[] indices, IReadOnlyList<Physics.MeshWeld.Stream> streams,
                               bool aggregate, float coneWeight)
    {
        var (welded, weldedIndices) = Physics.MeshWeld.Weld(vertices, stride, indices, streams, 1e-7f, true);
        var count = welded.Length / stride;
        var positions = new float[count * 3];
        for (var v = 0; v < count; v++)
            for (var k = 0; k < 3; k++)
                positions[(v * 3) + k] = welded[(v * stride) + positionOffset + k];
        var ordered = MeshoptOptimizers.OptimizeOverdraw(MeshoptOptimizers.OptimizeVertexCache(weldedIndices, count), positions, count, 3, 1.03f);
        var meshlets = new List<MeshoptMeshlets.Meshlet>();
        if (aggregate)
        {
            var built = MeshoptMeshlets.Build(ordered, positions, count, 3, MeshletVertices, MeshletTriangles, MeshletTriangles, coneWeight);
            var final = new List<int>(ordered.Length + 3);
            foreach (var m in built.Meshlets)
            {
                MeshoptMeshlets.OptimizeLevel(built.Vertices, m.VertexOffset, m.VertexCount, built.Triangles, m.TriangleOffset, m.TriangleCount, 4);
                var first = final.Count / 3;
                for (var k = 0; k < m.TriangleCount * 3; k++)
                    final.Add(built.Vertices[m.VertexOffset + built.Triangles[m.TriangleOffset + k]]);
                var triangles = m.TriangleCount;
                if ((triangles & 1) != 0)
                {
                    final.AddRange([final[^1], final[^1], final[^1]]);
                    triangles++;
                }
                meshlets.Add(new MeshoptMeshlets.Meshlet(m.VertexOffset, first, m.VertexCount, triangles));
            }
            ordered = [.. final];
        }
        // optimizeVertexFetch: vertices in the order the final list first uses them.
        var (renumbered, remap) = MeshoptOptimizers.RenumberByFirstUse(ordered);
        var fetched = new float[remap.Length * stride];
        for (var v = 0; v < remap.Length; v++)
            Array.Copy(welded, remap[v] * stride, fetched, v * stride, stride);
        return new Result(fetched, remap.Length, renumbered, meshlets);
    }
}
