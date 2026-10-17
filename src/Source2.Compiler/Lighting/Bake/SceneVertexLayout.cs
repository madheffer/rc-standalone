using Source2.Compiler.Lighting.Bake.Glsl;
using Source2.Compiler.Maps;
using Signature = System.Collections.Generic.IReadOnlyList<Source2.Compiler.Source2ContainerAuthor.MaterialAuthoring.InputSignatureElement>;

namespace Source2.Compiler.Lighting.Bake;

/// <summary>
/// The vertex buffer and input layout scenesystem's ray trace scene loader
/// (1800912d0, scenesystem 1015) makes for one scene_meshes.kv3 mesh, and the
/// GeometryInfo_t its builder (180093f10) reads from that layout. vrad3 hands
/// the meshes over as CMeshes (1801fd4e0) and does not set the layout itself.
/// <list type="number">
/// <item>CMesh_CreateInputLayout (1804aaf20, flags 0, at most four blend
/// weights) gives each CMesh attribute a format: a trailing digit of the name
/// is its semantic index; the format is the attribute table's (31 rows at
/// 18066d400: alias, name, index, alias index, format) for that name and
/// index, else by size (R32, R32G32, R32G32B32, R32G32B32A32_FLOAT). An
/// attribute flagged no_compression takes the size format. A compressed
/// normal is R32_UINT, the packed tangent frame, and drops the tangent that
/// follows it; a field left with format 0 is dropped.</item>
/// <item>The material's input signature, in order (1804aa730): each element's
/// Semantic, through the alias table or split at a trailing digit, picks the
/// field of that name and index; the field takes the element's D3D semantic
/// name and index. Identical results are kept once. Then PerVertexLighting,
/// LightmapUV, PropWorldOrigin, curvature, bentnormal and the blend fields are
/// kept under their table name if no element took them.</item>
/// <item>Fields no element and no rule took are removed; the rest keep the
/// CMesh order, packed by format size from offset 0 (1804aaf20's tail).</item>
/// <item>Each vertex is written field by field (1804abf60 / 1804ac280): the
/// compressed normal as CompressTangentFrame (180567b00, the same code as
/// resourcecompiler's 182067e80) of the normal and the first attribute
/// containing "tangent" of size 4; colour fields from (0, 0, 0, 1) filled
/// with the attribute's components; every other component through
/// ConvertFloatToChannel (1804aa4e0, as resourcecompiler's 181363b90) with
/// the format table at 1805ad070; halves by 18015e140 (resourcecompiler's
/// 1802ea750, truncating).</item>
/// </list>
/// So the blend weights csgo_simple_2way_blend reads from TEXCOORD 4 are the
/// mesh's VertexPaintBlendParams (as R8G8B8A8_UNORM), and the scene's prop
/// meshes carry half texcoords and a packed tangent frame.
/// </summary>
public static class SceneVertexLayout
{
    /// <summary>One layout field: name and semantic index (D3D after the signature), format, byte offset, the CMesh attribute it reads.</summary>
    public readonly record struct Field(string Name, int SemanticIndex, int Format, int Offset, int Source);

    public sealed record Result(IReadOnlyList<Field> Fields, int Stride, uint[] Words, GeometryInfo_t_natural Geometry, IReadOnlyList<string> Notes);

    // 18066d400: alias, name, index, alias index, format.
    private static readonly (string? Alias, string Name, int Index, int AliasIndex, int Format)[] Attributes =
    [
        ("posxyz", "position", 0, 0, 6), ("normal", "normal", 0, 0, 0), ("compressedtangentframe", "normal", 0, 0, 42),
        ("optionallycompressedtangentframe", "normal", 0, 0, 0), ("lowprecisionuv", "texcoord", 0, 0, 34),
        ("lowprecisionuv1", "texcoord", 1, 1, 34), ("uv", "texcoord", 0, 0, 34), ("uv1", "texcoord", 1, 1, 34),
        ("tangentu_signv", "tangent", 0, 0, 0), ("color", "color", 0, 0, 28), ("blendweight", "blendweights", 0, 0, 0),
        ("blendweights", "blendweights", 0, 0, 0), ("blendindices", "blendindices", 0, 0, 0),
        ("VertexPaintBlendParams", "VertexPaintBlendParams", 0, 0, 28), ("VertexPaintBlendParams1", "VertexPaintBlendParams", 1, 1, 28),
        ("VertexGenericIntegerData", "VertexGenericIntegerData", 0, 0, 42), ("OverlayProjectionDirection", "OverlayProjectionDirection", 0, 0, 28),
        ("VertexPaintTintColor", "VertexPaintTintColor", 0, 0, 28), ("FoliageAnimation", "FoliageAnimation", 0, 0, 28),
        ("PivotPaint", "PivotPaint", 0, 0, 10), ("Data_Branch1xy", "Data_Branch1xy", 0, 0, 34),
        ("Data_Branch1zBranch2x", "Data_Branch1zBranch2x", 0, 0, 34), ("Data_Branch2yz", "Data_Branch2yz", 0, 0, 34),
        ("PerVertexLighting", "color", 1, 0, 28), ("curvature", "texcoord", 2, 0, 41), ("LightmapUV", "texcoord", 3, 0, 35),
        ("LightmapUVW", "texcoord", 3, 0, 35), (null, "texcoord", 3, 0, 6), ("PropWorldOrigin", "texcoord", 6, 0, 6),
        ("VertexElementAnnotation", "texcoord", 4, 0, 10), ("TanFrameQuat", "TanFrameQuat", 0, 0, 42),
    ];

    // 18066d7e0: the format by component count.
    private static readonly int[] SizeFormats = [0, 41, 16, 6, 2];

    // 1805ad070, per DXGI format: components, the converter's three flags (+4..+6), bytes per component, size.
    private static readonly (int Components, bool A, bool B, bool Float, int ComponentBytes, int Size)[] Formats = BuildFormats();

    private static (int, bool, bool, bool, int, int)[] BuildFormats()
    {
        var t = new (int, bool, bool, bool, int, int)[50];
        void Row(int f, int c, int a, int b, int fl, int cb, int s) => t[f] = (c, a != 0, b != 0, fl != 0, cb, s);
        for (var f = 1; f <= 4; f++) Row(f, 4, f is 1 or 2 or 4 ? 1 : 0, 0, f is 1 or 2 ? 1 : 0, 4, 16);
        for (var f = 5; f <= 8; f++) Row(f, 3, f is 5 or 6 or 8 ? 1 : 0, 0, f is 5 or 6 ? 1 : 0, 4, 12);
        Row(9, 4, 1, 0, 1, 2, 8); Row(10, 4, 1, 0, 1, 2, 8); Row(11, 4, 0, 1, 0, 2, 8); Row(12, 4, 0, 0, 0, 2, 8);
        Row(13, 4, 1, 1, 0, 2, 8); Row(14, 4, 1, 0, 0, 2, 8);
        for (var f = 15; f <= 18; f++) Row(f, 2, f is 15 or 16 or 18 ? 1 : 0, 0, f is 15 or 16 ? 1 : 0, 4, 8);
        for (var f = 19; f <= 22; f++) Row(f, 0, 0, 0, 0, 0, 8);
        for (var f = 23; f <= 26; f++) Row(f, 0, 0, 0, 0, 0, 4);
        Row(27, 4, 0, 1, 0, 1, 4); Row(28, 4, 0, 0, 0, 1, 4); Row(29, 4, 0, 0, 0, 1, 4); Row(30, 4, 0, 0, 0, 1, 4);
        Row(31, 4, 1, 1, 0, 1, 4); Row(32, 4, 1, 0, 0, 1, 4);
        Row(33, 2, 1, 0, 1, 2, 4); Row(34, 2, 1, 0, 1, 2, 4); Row(35, 2, 0, 1, 0, 2, 4); Row(36, 2, 0, 0, 0, 2, 4);
        Row(37, 2, 1, 1, 0, 2, 4); Row(38, 2, 1, 0, 0, 2, 4);
        Row(39, 1, 1, 0, 1, 4, 4); Row(40, 1, 1, 0, 1, 4, 4); Row(41, 1, 1, 0, 1, 4, 4); Row(42, 1, 0, 0, 0, 4, 4); Row(43, 1, 1, 0, 0, 4, 4);
        for (var f = 44; f <= 47; f++) Row(f, 0, 0, 0, 0, 0, 4);
        Row(48, 2, 0, 1, 0, 1, 2); Row(49, 2, 0, 1, 0, 1, 2);
        return t;
    }

    private static bool Is(string a, string? b) => b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static int Size(int format) => format is > 0 and < 50 ? Formats[format].Size : 0;

    /// <summary>
    /// The layout, vertex words and GeometryInfo_t for one mesh drawn with a material of the given
    /// input signature (null when the material has none: then only the kept-by-rule fields remain).
    /// </summary>
    public static Result Build(BakeScene.Mesh mesh, int index, Signature? signature)
    {
        var notes = new List<string>();
        var attrs = mesh.Fields;
        // 1. CMesh_CreateInputLayout (1804aaf20) with flags 0: name, index, format per attribute.
        var layout = new List<(string Name, int Index, int Format)>();
        var source = new List<int>();
        var packedNormal = false;
        for (var a = 0; a < attrs.Count; a++)
        {
            var name = attrs[a].Name;
            var semantic = attrs[a].SemanticIndex;
            if (name.Length > 0 && char.IsAsciiDigit(name[^1]))
            {
                semantic = name[^1] - '0';
                name = name[..^1];
            }
            var size = attrs[a].Size;
            var row = Array.FindIndex(Attributes, r => Is(name, r.Name) && r.Index == semantic);
            var format = row >= 0 ? Attributes[row].Format : size < 5 ? SizeFormats[Math.Max(size, 0)] : 0;
            if (format == 0 && Is(name, "normal"))
                format = 42;
            int kept;
            if (Is(name, "blendweight") || Is(name, "blendweights") || Is(name, "blendindices"))
            {
                notes.Add($"{mesh.Material}: blend field {name} not ported, dropped");
                continue;
            }
            if (Is(name, "pervertexlighting"))
                kept = 10;
            else if (Is(name, "lightmapuv"))
                kept = attrs[a].NoCompression ? (size < 5 ? SizeFormats[Math.Max(size, 0)] : 0) : 35;
            else
            {
                kept = attrs[a].NoCompression ? (size < 5 ? SizeFormats[Math.Max(size, 0)] : 0) : format;
                if (Is(name, "normal") && !attrs[a].NoCompression)
                    packedNormal = true;
                else if (Is(name, "tangent") && packedNormal)
                    continue;
            }
            if (kept == 0)
                continue;
            layout.Add((name, semantic, kept));
            source.Add(a);
        }

        // 2. The input signature (1804aa730, 16 fields at most).
        var output = new List<(string Name, int Index, int Format, int Layout)>();
        void Add(string name, int semanticIndex, int format, int at)
        {
            if (output.Exists(o => o.Index == semanticIndex && o.Format == format && Is(o.Name, name)))
                return;
            if (output.Count <= 16)
                output.Add((name, semanticIndex, format, at));
        }
        foreach (var element in signature ?? [])
        {
            var row = Array.FindIndex(Attributes, r => Is(element.Semantic, r.Alias));
            string name;
            int semanticIndex;
            if (row >= 0)
                (name, semanticIndex) = (Attributes[row].Name, Attributes[row].Index);
            else if (element.Semantic.Length > 0 && char.IsAsciiDigit(element.Semantic[^1]))
                (name, semanticIndex) = (element.Semantic[..^1], element.Semantic[^1] - '0');
            else
                (name, semanticIndex) = (element.Semantic, 0);
            var at = layout.FindIndex(f => Is(f.Name, name) && f.Index == semanticIndex);
            if (at >= 0)
                Add(element.D3DSemanticName, (sbyte)element.D3DSemanticIndex, layout[at].Format, at);
        }
        string[] byRule = ["PerVertexLighting", "LightmapUV", "PropWorldOrigin", "curvature", "bentnormal", "blendindices", "blendweights", "blendweight"];
        for (var at = 0; at < layout.Count; at++)
        {
            var f = layout[at];
            var row = Array.FindIndex(Attributes, r => Is(f.Name, r.Alias) && (f.Index == -1 || r.AliasIndex == f.Index));
            if (row < 0 || output.Exists(o => o.Index == Attributes[row].Index && Is(o.Name, Attributes[row].Name)))
                continue;
            if (!byRule.Any(n => Is(f.Name, n)))
                continue;
            var name = Is(Attributes[row].Name, "blendweights") ? "blendweight" : Attributes[row].Name;
            Add(name, Attributes[row].Index, f.Format, at);
        }

        // 3. Write back over the layout, drop what nothing took, pack the offsets.
        var fields = new List<Field>();
        var stride = 0;
        for (var at = 0; at < layout.Count; at++)
        {
            // Each result is written over its layout field in turn (1804aaf20 after 1804aa730): the last one stands.
            var o = output.FindLastIndex(x => x.Layout == at);
            if (o < 0)
                continue;
            var (name, semanticIndex, format, _) = output[o];
            fields.Add(new Field(name, semanticIndex, format, stride, source[at]));
            stride += Size(format);
        }

        // 4. The vertices (1804abf60 / 1804ac280).
        var bytes = new byte[mesh.Positions.Length * stride + 3 & ~3];
        var tangent = -1;
        for (var a = 0; a < attrs.Count; a++)
            if (attrs[a].Name.Contains("tangent", StringComparison.OrdinalIgnoreCase) && attrs[a].Size == 4)
            {
                tangent = attrs[a].Offset;
                break;
            }
        var v = mesh.Vertices;
        for (var vertex = 0; vertex < mesh.Positions.Length; vertex++)
        {
            var b = vertex * mesh.Stride;
            foreach (var f in fields)
            {
                var attr = attrs[f.Source];
                var dst = bytes.AsSpan(vertex * stride + f.Offset);
                var at = b + attr.Offset;
                if (packedNormal && Is(attr.Name, "normal"))
                {
                    var n = new System.Numerics.Vector3(v[at], v[at + 1], v[at + 2]);
                    var t = tangent >= 0 ? new System.Numerics.Vector4(v[b + tangent], v[b + tangent + 1], v[b + tangent + 2], v[b + tangent + 3])
                        : new System.Numerics.Vector4(0f, 0f, 1f, 1f);
                    if (f.Format == 42)
                        BitConverter.TryWriteBytes(dst, TangentFrame.Compress(n, t));
                    continue;
                }
                if (Is(attr.Name, "pervertexlighting"))
                {
                    for (var k = 0; k < 4; k++)
                        BitConverter.TryWriteBytes(dst[(k * 2)..], WorldNodeModels.ToHalf(v[at + k]));
                    continue;
                }
                if (Is(attr.Name, "lightmapuv"))
                {
                    for (var k = 0; k < 2; k++)
                    {
                        var x = v[at + k] * 65535f;
                        x = x <= 0f ? 0f : x;
                        x = 65535f <= x ? 65535f : x;
                        BitConverter.TryWriteBytes(dst[(k * 2)..], (short)(int)x);
                    }
                    continue;
                }
                if (Is(attr.Name, "VertexGenericIntegerData") || Is(attr.Name, "TanFrameQuat"))
                {
                    notes.Add($"{mesh.Material}: {attr.Name} not ported");
                    continue;
                }
                var row = Formats[f.Format];
                var values = new float[4];
                int count;
                if (Is(attr.Name, "color"))
                {
                    values[3] = 1f;
                    for (var k = 0; k < Math.Min(attr.Size, 4); k++)
                        values[k] = v[at + k];
                    count = Math.Min(row.Components, 4);
                }
                else
                {
                    count = Math.Min(row.Components, attr.Size);
                    for (var k = 0; k < count; k++)
                        values[k] = v[at + k];
                }
                for (var k = 0; k < count; k++)
                    ConvertFloatToChannel(dst[(k * row.ComponentBytes)..], row.ComponentBytes, row.A, row.B, row.Float, values[k]);
            }
        }
        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
        return new Result(fields, stride, words, Geometry(fields, stride, index, notes), notes);
    }

    // ConvertFloatToChannel (1804aa4e0): a float, half, or clamped and truncated integer channel.
    private static void ConvertFloatToChannel(Span<byte> dst, int bytes, bool a, bool b, bool isFloat, float v)
    {
        if (isFloat)
        {
            if (bytes == 4)
                BitConverter.TryWriteBytes(dst, v);
            else if (bytes == 2)
                BitConverter.TryWriteBytes(dst, WorldNodeModels.ToHalf(v));
            return;
        }
        if (!b)
        {
            if (!a)
            {
                var f = 0f;
                if (0f <= v)
                    f = v <= 1f ? v : 1f;
                switch (bytes)
                {
                    case 4: BitConverter.TryWriteBytes(dst, (uint)(long)(f * 4.2949673e+09f)); break;
                    case 2: BitConverter.TryWriteBytes(dst, (short)(int)(f * 65535f)); break;
                    case 1: dst[0] = (byte)(int)(f * 255f); break;
                }
                return;
            }
            var i = (int)v;
            switch (bytes)
            {
                case 4: BitConverter.TryWriteBytes(dst, i); break;
                case 2: BitConverter.TryWriteBytes(dst, (short)(i < -0x7fff ? -0x8000 : 0x7ffe < i ? 0x7fff : i)); break;
                case 1: dst[0] = i < -0x7f ? (byte)0x80 : 0x7e < i ? (byte)0x7f : (byte)i; break;
            }
            return;
        }
        var c = -1f;
        if (-1f <= v)
            c = v <= 1f ? v : 1f;
        if (!a)
        {
            c = c * 0.5f + 0.5f;
            switch (bytes)
            {
                case 4: BitConverter.TryWriteBytes(dst, (uint)(long)(c * 4.2949673e+09f)); break;
                case 2: BitConverter.TryWriteBytes(dst, (short)(int)(c * 65535f)); break;
                case 1: dst[0] = (byte)(int)(c * 255f); break;
            }
            return;
        }
        switch (bytes)
        {
            case 4: BitConverter.TryWriteBytes(dst, (int)(c * 2.1474836e+09f)); break;
            case 2: BitConverter.TryWriteBytes(dst, (short)(int)(c * 32767f)); break;
            case 1: dst[0] = (byte)(sbyte)(int)(c * 127f); break;
        }
    }

    // PathTraceScene_GeometryInfo (180093f10) on the one-stream layout: position (2 or 6, offset 0), normal 0
    // (6 or 42), tangent 0 (2), texcoords 0-7 (formats in mask 0x2410010004) and colours 0-7 (2 or 28), each
    // at a 4-byte aligned offset, as (offset, format, stream 0).
    private static GeometryInfo_t_natural Geometry(IReadOnlyList<Field> fields, int stride, int index, List<string> notes)
    {
        var g = new GeometryInfo_t_natural
        {
            m_nVertexAndIndexBuffers = new ivec4(index, index, index, index),
            m_nBufferBindOffsetBytes = new ivec4(0),
            m_nBufferStrideInBytes = new ivec4(stride, 0, 0, 4),
            m_vTexCoordFieldInfo = new _Array_natural_vector_int_4_8 { data = new ivec4[8] },
            m_vColorFieldInfo = new _Array_natural_vector_int_4_8 { data = new ivec4[8] },
        };
        var position = false;
        foreach (var f in fields)
        {
            var info = new ivec4(f.Offset, f.Format, 0, 0);
            var aligned = (f.Offset & 3) == 0;
            if (Is(f.Name, "position") && f.SemanticIndex == 0 && f.Offset == 0 && f.Format is 2 or 6)
                position = true;
            else if (Is(f.Name, "normal") && f.SemanticIndex == 0 && aligned && f.Format is 6 or 42)
                g.m_vNormalFieldInfo = info;
            else if (Is(f.Name, "tangent") && f.SemanticIndex == 0 && aligned && f.Format == 2)
                g.m_vTangentFieldInfo = info;
            else if (Is(f.Name, "texcoord") && aligned && f.Format < 0x26 && ((0x2410010004UL >> f.Format) & 1) != 0 && f.SemanticIndex is >= 0 and < 8)
                g.m_vTexCoordFieldInfo.data[f.SemanticIndex] = info;
            else if (Is(f.Name, "color") && aligned && f.Format is 2 or 28 && f.SemanticIndex is >= 0 and < 8)
                g.m_vColorFieldInfo.data[f.SemanticIndex] = info;
        }
        if (!position)
            notes.Add("a mesh has no position field scenesystem accepts; its GeometryInfo_t is not built");
        return g;
    }
}
