using Source2.Compiler.Io;
using ValveResourceFormat.Blocks;

namespace Source2.Compiler.Lighting;

/// <summary>
/// How meshsystem's geometry info for a draw call (0923 18002af80, read
/// back through CMeshSystem vf 0xf0 at render mesh +0x200) sorts a vertex
/// buffer's fields:
/// <list type="bullet">
/// <item>A field's shader semantic, when it has one, is looked up in the
/// semantic table (<see cref="Lookup"/>: posxyz is position, LowPrecisionUv
/// texcoord 0, VertexPaintTintColor itself, ...); texcoord 0 and 1 and
/// color (any index) are standard, any other name is not.</item>
/// <item>Without one, or one the table does not give, the field's semantic
/// decides: color is the vertex colour, the texcoords of two components
/// take texcoord 0 and 1 by ascending semantic index.</item>
/// <item>The vertex colour is the color field of the lowest semantic index
/// (draw record +0x60). Position, normal, tangent, blend indices and weights
/// are standard by semantic.</item>
/// <item>Every field left over is an extra field (+0x40), unpacked to floats
/// per component (180029c10). A float texcoord 0 or 1 (R32G32_FLOAT) sets
/// +0x66, a float tangent +0x67: their streams are not compressed.</item>
/// </list>
/// </summary>
internal static class MeshGeometry
{
    /// <summary>An extra field: its name (the shader semantic) and component count.</summary>
    public sealed record Extra(VBIB.RenderInputLayoutField Field, string Name, int Components);

    public sealed record Result(VBIB.RenderInputLayoutField Color, IReadOnlyList<Extra> Extras, bool FloatTexcoords, bool FloatTangents);

    // 180174d50: shader semantic, standard name, index, and the index the name itself stands for (+0x14).
    private static readonly (string Name, string Standard, int Index, int NameIndex)[] Semantics =
    [
        ("posxyz", "position", 0, 0), ("normal", "normal", 0, 0), ("compressedtangentframe", "normal", 0, 0), ("optionallycompressedtangentframe", "normal", 0, 0),
        ("lowprecisionuv", "texcoord", 0, 0), ("lowprecisionuv1", "texcoord", 1, 1), ("uv", "texcoord", 0, 0), ("uv1", "texcoord", 1, 1),
        ("tangentu_signv", "tangent", 0, 0), ("color", "color", 0, 0), ("blendweight", "blendweights", 0, 0), ("blendweights", "blendweights", 0, 0),
        ("blendindices", "blendindices", 0, 0), ("VertexPaintBlendParams", "VertexPaintBlendParams", 0, 0), ("VertexPaintBlendParams1", "VertexPaintBlendParams", 1, 1),
        ("VertexGenericIntegerData", "VertexGenericIntegerData", 0, 0), ("OverlayProjectionDirection", "OverlayProjectionDirection", 0, 0),
        ("VertexPaintTintColor", "VertexPaintTintColor", 0, 0), ("FoliageAnimation", "FoliageAnimation", 0, 0), ("PivotPaint", "PivotPaint", 0, 0),
        ("Data_Branch1xy", "Data_Branch1xy", 0, 0), ("Data_Branch1zBranch2x", "Data_Branch1zBranch2x", 0, 0), ("Data_Branch2yz", "Data_Branch2yz", 0, 0),
        ("PerVertexLighting", "color", 1, 0), ("curvature", "texcoord", 2, 0), ("LightmapUV", "texcoord", 3, 0), ("LightmapUVW", "texcoord", 3, 0),
        ("None", "texcoord", 3, 0), ("PropWorldOrigin", "texcoord", 6, 0), ("VertexElementAnnotation", "texcoord", 4, 0), ("TanFrameQuat", "TanFrameQuat", 0, 0),
    ];

    /// <summary>
    /// 180036aa0 as 18002af80 calls it (index 0): the first entry whose name
    /// matches with a name index of 0, or whose standard name matches at index
    /// 0; null when none does (LowPrecisionUv1 and uv1 among them).
    /// </summary>
    public static (string Standard, int Index)? Lookup(string semantic)
    {
        foreach (var e in Semantics)
            if ((Tier0Strings.EqualsIgnoreCase(semantic, e.Name) && e.NameIndex == 0) || (Tier0Strings.EqualsIgnoreCase(semantic, e.Standard) && e.Index == 0))
                return (e.Standard, e.Index);
        return null;
    }

    public static Result Classify(VBIB.OnDiskBufferData vb)
    {
        var fields = vb.InputLayoutFields;
        var consumed = new bool[fields.Length];
        // The texcoords of two components, by semantic index: the first two are texcoord 0 and 1.
        var pairs = fields.Where(f => Is(f.SemanticName, "texcoord") && Components(f) == 2).Select(f => f.SemanticIndex).Order().ToList();
        var color = -1;
        bool floatTexcoords = false, floatTangents = false;
        for (var i = 0; i < fields.Length; i++)
        {
            var f = fields[i];
            var semantic = f.SemanticName;
            if (Is(semantic, "position") || Is(semantic, "normal") || Is(semantic, "blendindices") || Is(semantic, "blendweight") || Is(semantic, "blendweights"))
            {
                consumed[i] = true;
                continue;
            }
            if (Is(semantic, "tangent"))
            {
                consumed[i] = true;
                floatTangents |= f.Format.ToString() is "R32G32_FLOAT" or "R32G32B32_FLOAT" or "R32G32B32A32_FLOAT";
                continue;
            }
            string standard;
            int index;
            if (!string.IsNullOrEmpty(f.ShaderSemantic) && Lookup(f.ShaderSemantic) is { } entry)
                (standard, index) = entry;
            else if (Is(semantic, "texcoord") && Components(f) == 2)
            {
                (standard, index) = ("texcoord", pairs.IndexOf(f.SemanticIndex));
            }
            else if (Is(semantic, "texcoord") && f.Format.ToString() == "R8G8B8A8_UNORM")
                throw new NotSupportedException("an unnamed RGBA8 texcoord (meshsystem's VertexPaintBlendParams rename) is not ported");
            else
                (standard, index) = (semantic.ToLowerInvariant(), f.SemanticIndex);
            if (Is(standard, "texcoord") && index is 0 or 1)
            {
                consumed[i] = true;
                floatTexcoords |= f.Format.ToString() == "R32G32_FLOAT";
            }
            else if (Is(standard, "color"))
            {
                if (color < 0 || fields[color].SemanticIndex > f.SemanticIndex)
                    color = i;
            }
        }
        if (color >= 0)
            consumed[color] = true;
        var extras = new List<Extra>();
        for (var i = 0; i < fields.Length; i++)
        {
            if (consumed[i])
                continue;
            var f = fields[i];
            if (string.IsNullOrEmpty(f.ShaderSemantic))
                throw new NotSupportedException($"an extra vertex field without a shader semantic ({f.SemanticName}{f.SemanticIndex}: the material's attribute name, 1801d3050) is not ported");
            extras.Add(new Extra(f, f.ShaderSemantic, Components(f)));
        }
        return new Result(color >= 0 ? fields[color] : default, extras, floatTexcoords, floatTangents);
    }

    /// <summary>180029c10 for the formats measured: RGBA8 as unorm over 255, floats as they are.</summary>
    public static void Unpack(VBIB.OnDiskBufferData vb, VBIB.RenderInputLayoutField f, int vertex, Span<float> into)
    {
        var at = vertex * (int)vb.ElementSizeInBytes + (int)f.Offset;
        switch (f.Format.ToString())
        {
            case "R8G8B8A8_UNORM":
                for (var c = 0; c < 4; c++)
                    into[c] = vb.Data[at + c] / 255f;
                break;
            case "R32G32_FLOAT" or "R32G32B32_FLOAT" or "R32G32B32A32_FLOAT" or "R32_FLOAT":
                for (var c = 0; c < into.Length; c++)
                    into[c] = BitConverter.ToSingle(vb.Data, at + c * 4);
                break;
            default:
                throw new NotSupportedException($"unpacking a {f.Format} vertex field (180029c10) is not ported");
        }
    }

    // FUN_180036b70's component count.
    private static int Components(VBIB.RenderInputLayoutField f) => f.Format.ToString() switch
    {
        "R32_FLOAT" or "R32_UINT" or "R32_SINT" => 1,
        "R32G32_FLOAT" or "R16G16_FLOAT" or "R16G16_SNORM" or "R16G16_UNORM" or "R16G16_SINT" or "R16G16_UINT" or "R32G32_SINT" or "R32G32_UINT" => 2,
        "R32G32B32_FLOAT" or "R32G32B32_SINT" or "R32G32B32_UINT" => 3,
        _ => 4,
    };

    private static bool Is(string? a, string b) => Tier0Strings.EqualsIgnoreCase(a ?? "", b);
}
