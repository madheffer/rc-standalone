using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// What a world node model (<c>worldnodes/*.vmdl_c</c>) holds, before it is
/// written: its buffers, its scene objects and draw calls, and the compile
/// arguments its RED2 records. <see cref="WorldNodeModelTrees"/> turns it into
/// the MDAT, CTRL, RED2 and DATA trees. <see cref="Aggregate"/> marks an
/// aggregate model (<c>agg_*</c>): it has per-draw bounds and meshlets, and
/// three more compile arguments.
/// </summary>
public sealed record WorldNodeModel(
    string Name,
    IReadOnlyList<WorldNodeModel.VertexBuffer> VertexBuffers,
    IReadOnlyList<WorldNodeModel.IndexBuffer> IndexBuffers,
    IReadOnlyList<WorldNodeModel.SceneObject> SceneObjects,
    bool Aggregate,
    int CompileWarnings)
{
    /// <summary>One input layout field of a vertex buffer.</summary>
    public sealed record LayoutField(string Semantic, int SemanticIndex, uint Format, int Offset, string ShaderSemantic);

    /// <summary>A vertex buffer: element count and size, meshopt or raw, its layout.</summary>
    public sealed record VertexBuffer(int Count, int Stride, bool Meshopt, IReadOnlyList<LayoutField> Layout);

    /// <summary>An index buffer: element count and size, meshopt or raw, pooled.</summary>
    public sealed record IndexBuffer(int Count, int ElementSize, bool Meshopt, bool Pooled);

    /// <summary>A meshlet: its box packed in the draw's bounds, its culling cone and its ranges.</summary>
    public sealed record Meshlet(uint PackedMin, uint PackedMax, int ConeX, int ConeY, int ConeZ, int ConeCutoff,
                                 int VertexOffset, int TriangleOffset, int VertexCount, int TriangleCount);

    /// <summary>
    /// A draw call. <see cref="VertexEnd"/> is <c>m_nVertexCount</c>: one past the
    /// highest vertex the draw reads, its applied index offset included.
    /// </summary>
    public sealed record Draw(string Material, Vector3 Tint, float UvDensity, int FirstMeshlet, int MeshletCount,
                              int AppliedIndexOffset, int DepthVertexBuffer, int VertexEnd, int StartIndex, int IndexCount,
                              int IndexBufferHandle, IReadOnlyList<int> VertexBufferHandles,
                              bool BakedLightingFromVertexStream, bool NotMatchedToMaterial);

    /// <summary>A scene object: its bounds, draw calls, per-draw bounds and meshlets.</summary>
    public sealed record SceneObject(Vector3 Min, Vector3 Max, IReadOnlyList<Draw> Draws,
                                     IReadOnlyList<(Vector3 Min, Vector3 Max)> DrawBounds, IReadOnlyList<Meshlet> Meshlets);

    /// <summary>
    /// The RED2 argument list, in Valve's order: name, type, default
    /// fingerprint. Every node model sets <c>embedded_map_mesh</c> and
    /// <c>preserve_tangents</c>; an aggregate also sets
    /// <c>embedded_aggregate_mesh</c>, <c>generate_draw_bounds</c> and
    /// <c>generate_meshlets</c> (all 401 models of probe01, cardtest, atixref).
    /// </summary>
    public static readonly (string Name, string Type, long Default)[] ArgumentList =
    [
        ("___OverrideInputData___", "BinaryBlobArg", 0),
        ("create_pooled_vb", "IntArg", 0),
        ("embedded_aggregate_mesh", "IntArg", 0),
        ("embedded_map_mesh", "IntArg", 0),
        ("force_up_to_date_materials", "FloatArg", 0),
        ("generate_draw_bounds", "IntArg", 0),
        ("generate_meshlets", "IntArg", 0),
        ("keep_vertices", "IntArg", 0),
        ("mapbuilder_entity_classname", "StringArg", 0),
        ("meshlets_max_tri_count", "IntArg", 4294967295),
        ("meshlets_max_vertex_count", "IntArg", 4294967295),
        ("preserve_tangents", "IntArg", 0),
    ];

    /// <summary>This model's fingerprint for one argument.</summary>
    public long Argument(string name, long fallback) => name switch
    {
        "embedded_map_mesh" or "preserve_tangents" => 1,
        "embedded_aggregate_mesh" or "generate_draw_bounds" or "generate_meshlets" => Aggregate ? 1 : 0,
        _ => fallback,
    };
}
