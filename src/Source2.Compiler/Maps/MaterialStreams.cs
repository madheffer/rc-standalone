using Source2.Compiler.Io;

namespace Source2.Compiler.Maps;

/// <summary>
/// The mesh streams a material reads, and which of an entry's streams
/// WRBMeshList_PrepareForMerge (1802b52d0 in the 09-23 build) keeps.
/// <list type="bullet">
/// <item>A material's inputs: each element of its vertex input signature
/// (INSG m_pSemantic) through FUN_181366d90's table (0x183014ae0): the
/// shader semantic (case-blind) names a CMesh stream and its semantic
/// index (PosXyz is position 0, LowPrecisionUv1 texcoord 1); a semantic the
/// table lacks is a stream of its own name at index 0 (FUN_1802b4d50).</item>
/// <item>A stream stays when an input has its name (case-blind) and
/// semantic index. PerVertexLighting always stays, unless the list keeps
/// baked lighting (the list's setting byte +4 and attribute bit 29), when it
/// goes and any LightmapUV stream stays instead. Every other stream is
/// removed (FUN_1812e2d50), last to first.</item>
/// </list>
/// </summary>
internal static class MaterialStreams
{
    // FUN_181366d90's table: shader semantic, stream name, stream index. The
    // one row with no semantic (texcoord 3) is only found by stream name.
    private static readonly (string Semantic, string Stream, int Index)[] Table =
    [
        ("posxyz", "position", 0), ("normal", "normal", 0), ("compressedtangentframe", "normal", 0),
        ("optionallycompressedtangentframe", "normal", 0), ("lowprecisionuv", "texcoord", 0), ("lowprecisionuv1", "texcoord", 1),
        ("uv", "texcoord", 0), ("uv1", "texcoord", 1), ("tangentu_signv", "tangent", 0), ("color", "color", 0),
        ("blendweight", "blendweights", 0), ("blendweights", "blendweights", 0), ("blendindices", "blendindices", 0),
        ("VertexPaintBlendParams", "VertexPaintBlendParams", 0), ("VertexPaintBlendParams1", "VertexPaintBlendParams", 1),
        ("VertexGenericIntegerData", "VertexGenericIntegerData", 0), ("OverlayProjectionDirection", "OverlayProjectionDirection", 0),
        ("VertexPaintTintColor", "VertexPaintTintColor", 0), ("FoliageAnimation", "FoliageAnimation", 0), ("PivotPaint", "PivotPaint", 0),
        ("Data_Branch1xy", "Data_Branch1xy", 0), ("Data_Branch1zBranch2x", "Data_Branch1zBranch2x", 0), ("Data_Branch2yz", "Data_Branch2yz", 0),
        ("PerVertexLighting", "color", 1), ("curvature", "texcoord", 2), ("LightmapUV", "texcoord", 3), ("LightmapUVW", "texcoord", 3),
        ("PropWorldOrigin", "texcoord", 6), ("VertexElementAnnotation", "texcoord", 4), ("TanFrameQuat", "TanFrameQuat", 0),
    ];

    /// <summary>The streams a material's input signature reads, as (name, semantic index).</summary>
    public static List<(string Name, int Index)> Inputs(IEnumerable<string> semantics)
    {
        var inputs = new List<(string, int)>();
        foreach (var semantic in semantics)
        {
            var row = Array.FindIndex(Table, t => Io.Tier0Strings.EqualsIgnoreCase(t.Semantic, semantic));
            inputs.Add(row >= 0 ? (Table[row].Stream, Table[row].Index) : (semantic, 0));
        }
        return inputs;
    }

    /// <summary>
    /// Which of <paramref name="streams"/> stay, in order: each stream's
    /// semantic index is its place among the streams of its name.
    /// </summary>
    public static List<int> Kept(IReadOnlyList<Physics.MeshWeld.Stream> streams, IReadOnlyList<(string Name, int Index)> inputs, bool keepLighting)
    {
        var kept = new List<int>();
        for (var i = 0; i < streams.Count; i++)
        {
            var name = streams[i].Name;
            var index = 0;
            for (var j = 0; j < i; j++)
                if (streams[j].Name == name)
                    index++;
            bool stays;
            if (keepLighting && name.ContainsAscii("LightmapUV"))
                stays = true;
            else if (Io.Tier0Strings.EqualsIgnoreCase(name, "PerVertexLighting"))
                stays = !keepLighting;
            else
                stays = inputs.Any(x => Io.Tier0Strings.EqualsIgnoreCase(x.Name, name) && x.Index == index);
            if (stays)
                kept.Add(i);
        }
        return kept;
    }
}
