using Source2.Compiler.Lighting.Bake.Glsl;
using Source2.Compiler.Maps;
using Signature = System.Collections.Generic.IReadOnlyList<Source2.Compiler.Source2ContainerAuthor.MaterialAuthoring.InputSignatureElement>;

namespace Source2.Compiler.Lighting.Bake;

/// <summary>
/// The vertex buffer vrad3's hit shaders read, as scenesystem's world loader
/// (2026-10-15 scenesystem, 1800912d0) builds it from a scene_meshes.kv3 mesh:
/// <list type="number">
/// <item>The packed input layout (1804aaf20, called through 1804abf10 with
/// flags 0 and a blend weight limit of 4): one field per CMesh attribute, in
/// attribute order, its format from the semantic table at 18066d400 (the
/// first entry whose mesh name and index match), else from the attribute's
/// size (18066d7e0: 1 R32_FLOAT, 2 R32G32_FLOAT, 3 R32G32B32_FLOAT, 4
/// R32G32B32A32_FLOAT). A no_compression attribute takes the size format. A
/// normal without no_compression is packed with its tangent into one R32_UINT,
/// and the tangent that follows it is dropped; a field whose format stays 0
/// (a tangent with neither) is dropped too.</item>
/// <item>The material's input signature (1804aa730, the mesh's own
/// material_name, INSG m_elems): each element's Semantic names a mesh field
/// through the same table (or by itself, a trailing digit as the index); the
/// field found takes the element's D3D semantic name and index. Every field
/// no element asked for is removed. Offsets are then the running sum of the
/// format sizes; the stride is their total.</item>
/// <item>The vertices (1804abf60, 1804ac280): each kept field converted from
/// the attribute's floats at a running offset (1804aa4e0 per channel: half
/// floats truncated by 18015e140, UNORM / SNORM truncated after clamping, as
/// cvttss2si: 0x80000000 for a NaN or out of range); the normal and the first
/// size-4 attribute containing "tangent" packed by 180567b00; PerVertexLighting
/// as four halves, lightmapuv as UNORM16, TanFrameQuat as 10:10:10 and a sign,
/// VertexGenericIntegerData copied. 180567b00 and 18015e140 are the same code
/// as resourcecompiler's 182067e80 and 1802ea750, so this uses their ports
/// (TangentFrame.Compress, WorldNodeModels.ToHalf; checked equal over every
/// float and 2M frames).</item>
/// <item>GeometryInfo_t's field infos (180093f10, stream -1): position
/// R32G32B32(A32)_FLOAT at offset 0, normal R32G32B32_FLOAT or the packed
/// R32_UINT, tangent R32G32B32A32_FLOAT, texcoord and color per index below
/// 8 in the formats the hit shaders decode.</item>
/// </list>
/// A PerVertexLighting attribute is kept by 1804aa730's second loop as color 1
/// (R16G16B16A16_FLOAT): the hit shaders never read it, but it is in the
/// stride. Not ported (they throw): blend weights and indices (scene meshes
/// carry no skinning), and a mesh whose material is missing (1800912d0 then
/// makes no vertex buffer, 180092bff).
/// </summary>
public static class SceneVertexLayout
{
    /// <summary>A mesh's layout fields, stride, vertex buffer as words and GeometryInfo_t.</summary>
    public sealed record Result(IReadOnlyList<Field> Fields, int Stride, uint[] Words, GeometryInfo_t_natural Geometry, IReadOnlyList<string> Notes);

    /// <summary>The whole step for one mesh drawn with its own material's input signature.</summary>
    public static Result Build(BakeScene.Mesh mesh, int index, Signature? signature)
    {
        if (signature == null)
            throw new InvalidOperationException($"mesh {index}: material {mesh.Material} has no input signature (scenesystem 1800912d0 makes no vertex buffer without it)");
        var attributes = Attributes(mesh);
        var layout = Build(attributes, signature);
        var bytes = Write(mesh, attributes, layout);
        return new Result(layout.Fields, layout.Stride, System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bytes).ToArray(),
                          Geometry(layout, signature, index), []);
    }

    /// <summary>One RenderInputLayoutField_t (0x4c bytes): name, semantic index (a signed byte), DXGI format, offset, slot, instanced, and the second name (the signature's Semantic).</summary>
    public sealed record Field(string Name, int SemanticIndex, int Format, int Offset, int Slot, bool Instanced, string Semantic = "");

    /// <summary>
    /// A built layout: the fields, the CMesh attribute each one reads, the stride in bytes, and
    /// 1804aaf20's flag bytes 0 (the normal is packed with its tangent) and 1 (a PerVertexLighting
    /// attribute was laid out as half floats).
    /// </summary>
    public sealed record Layout(IReadOnlyList<Field> Fields, IReadOnlyList<int> Attributes, int Stride, bool PackedNormal, bool PerVertexLighting);

    // 18066d400, 0x20 bytes each: Valve's semantic (+0), the mesh attribute it reads (+8) and that
    // attribute's semantic index (+0x10), the semantic's own index (+0x14), the packed format (+0x18).
    private static readonly (string? Semantic, string Mesh, int Index, int SemanticIndex, int Format)[] Semantics =
    [
        ("posxyz", "position", 0, 0, 0x6),
        ("normal", "normal", 0, 0, 0x0),
        ("compressedtangentframe", "normal", 0, 0, 0x2a),
        ("optionallycompressedtangentframe", "normal", 0, 0, 0x0),
        ("lowprecisionuv", "texcoord", 0, 0, 0x22),
        ("lowprecisionuv1", "texcoord", 1, 1, 0x22),
        ("uv", "texcoord", 0, 0, 0x22),
        ("uv1", "texcoord", 1, 1, 0x22),
        ("tangentu_signv", "tangent", 0, 0, 0x0),
        ("color", "color", 0, 0, 0x1c),
        ("blendweight", "blendweights", 0, 0, 0x0),
        ("blendweights", "blendweights", 0, 0, 0x0),
        ("blendindices", "blendindices", 0, 0, 0x0),
        ("VertexPaintBlendParams", "VertexPaintBlendParams", 0, 0, 0x1c),
        ("VertexPaintBlendParams1", "VertexPaintBlendParams", 1, 1, 0x1c),
        ("VertexGenericIntegerData", "VertexGenericIntegerData", 0, 0, 0x2a),
        ("OverlayProjectionDirection", "OverlayProjectionDirection", 0, 0, 0x1c),
        ("VertexPaintTintColor", "VertexPaintTintColor", 0, 0, 0x1c),
        ("FoliageAnimation", "FoliageAnimation", 0, 0, 0x1c),
        ("PivotPaint", "PivotPaint", 0, 0, 0xa),
        ("Data_Branch1xy", "Data_Branch1xy", 0, 0, 0x22),
        ("Data_Branch1zBranch2x", "Data_Branch1zBranch2x", 0, 0, 0x22),
        ("Data_Branch2yz", "Data_Branch2yz", 0, 0, 0x22),
        ("PerVertexLighting", "color", 1, 0, 0x1c),
        ("curvature", "texcoord", 2, 0, 0x29),
        ("LightmapUV", "texcoord", 3, 0, 0x23),
        ("LightmapUVW", "texcoord", 3, 0, 0x23),
        (null, "texcoord", 3, 0, 0x6),
        ("PropWorldOrigin", "texcoord", 6, 0, 0x6),
        ("VertexElementAnnotation", "texcoord", 4, 0, 0xa),
        ("TanFrameQuat", "TanFrameQuat", 0, 0, 0x2a),
    ];

    // 18066d7e0: the float format for an attribute size 0-4.
    private static readonly int[] SizeFormats = [0x0, 0x29, 0x10, 0x6, 0x2];

    // 1805ad070, 0x18 bytes per DXGI format: channels; bytes +4 (integer), +5 (signed range), +6 (float); channel size; total size.
    private readonly record struct FormatInfo(int Channels, bool Integer, bool SignedRange, bool Float, int ChannelSize, int Size);

    private static FormatInfo Info(int format) => format switch
    {
        0x2 => new(4, true, false, true, 4, 16),   // R32G32B32A32_FLOAT
        0x6 => new(3, true, false, true, 4, 12),   // R32G32B32_FLOAT
        0xa => new(4, true, false, true, 2, 8),    // R16G16B16A16_FLOAT
        0xb => new(4, false, true, false, 2, 8),   // R16G16B16A16_UNORM
        0x10 => new(2, true, false, true, 4, 8),   // R32G32_FLOAT
        0x1c => new(4, false, false, false, 1, 4), // R8G8B8A8_UNORM
        0x22 => new(2, true, false, true, 2, 4),   // R16G16_FLOAT
        0x23 => new(2, false, true, false, 2, 4),  // R16G16_UNORM
        0x29 => new(1, true, false, true, 4, 4),   // R32_FLOAT
        0x2a => new(1, false, false, false, 4, 4), // R32_UINT
        _ => throw new NotSupportedException($"vertex format {format} is not in the ported part of scenesystem's table 1805ad070"),
    };

    private static readonly string[] Special =
        ["PerVertexLighting", "LightmapUV", "PropWorldOrigin", "curvature", "bentnormal", "blendindices", "blendweights", "blendweight"];

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // V_strncpy into a 0x20-byte name: at most 31 characters.
    private static string Name32(string s) => s.Length > 31 ? s[..31] : s;

    // The layout field's semantic index is a signed byte.
    private static int SByte(int v) => (sbyte)(byte)v;

    /// <summary>The CMesh attributes after 1804aaf20's rename: a trailing digit becomes the semantic index.</summary>
    public static BakeScene.MeshField[] Attributes(BakeScene.Mesh mesh)
    {
        var fields = mesh.Fields.ToArray();
        for (var i = 0; i < fields.Length; i++)
            if (fields[i].Name is { Length: > 0 } n && n[^1] is >= '0' and <= '9')
                fields[i] = fields[i] with { Name = n[..^1], SemanticIndex = n[^1] - '0' };
        return fields;
    }

    /// <summary>1804aaf20 with flags 0: the packed layout, then 1804aa730's signature step, the removal and the offsets.</summary>
    public static Layout Build(BakeScene.MeshField[] attributes, Signature signature)
    {
        var fields = new List<Field>();
        var source = new List<int>();
        bool packedNormal = false, perVertexLighting = false;
        for (var a = 0; a < attributes.Length; a++)
        {
            var attr = attributes[a];
            var format = -1;
            foreach (var s in Semantics)
                if (Eq(attr.Name, s.Mesh) && s.Index == attr.SemanticIndex)
                {
                    format = s.Format;
                    break;
                }
            var sizeFormat = attr.Size < 5 ? SizeFormats[attr.Size] : 0;
            if (format < 0)
                format = sizeFormat;
            if (format == 0 && Eq(attr.Name, "normal"))
                format = 0x2a;
            if (Eq(attr.Name, "blendweight") || Eq(attr.Name, "blendweights") || Eq(attr.Name, "blendindices"))
                throw new NotSupportedException($"{attr.Name}: 1804aaf20's blend weight and index layout is not ported");
            int fieldFormat;
            if (Eq(attr.Name, "pervertexlighting"))
            {
                fieldFormat = 0xa;
                perVertexLighting = true;
            }
            else if (Eq(attr.Name, "lightmapuv"))
            {
                fieldFormat = attr.NoCompression ? sizeFormat : 0x23;
                if (fieldFormat == 0)
                    continue;
            }
            else
            {
                if (attr.NoCompression && attr.Size >= 5)
                    throw new NotSupportedException($"{attr.Name}: a no_compression attribute of size {attr.Size} (1804aaf20 reads a stale register)");
                fieldFormat = attr.NoCompression ? sizeFormat : format;
                if (Eq(attr.Name, "normal") && !attr.NoCompression)
                    packedNormal = true;
                else if (Eq(attr.Name, "tangent") && packedNormal)
                    continue;
                if (fieldFormat == 0)
                    continue;
            }
            fields.Add(new Field(Name32(attr.Name), SByte(attr.SemanticIndex), fieldFormat, 0, 0, false));
            source.Add(a);
        }

        // 1804aa730 (up to 16 fields, no split-stream signature, flags 0).
        var output = new List<Field>();
        var matched = new List<int>();
        bool Known(Field f) => output.Any(o => o.SemanticIndex == f.SemanticIndex && o.Format == f.Format && o.Offset == f.Offset
                                              && o.Slot == f.Slot && o.Instanced == f.Instanced && Eq(o.Name, f.Name));
        void Add(Field f, int index)
        {
            if (Known(f))
                return;
            if (output.Count == 16)
                throw new NotSupportedException("more than 16 layout fields (1804aa730 writes past its array)");
            output.Add(f);
            matched.Add(index);
        }
        foreach (var e in signature)
        {
            string name = e.Semantic;
            var index = 0;
            var entry = Array.FindIndex(Semantics, s => s.Semantic != null && Eq(e.Semantic, s.Semantic));
            if (entry >= 0)
                (name, index) = (Semantics[entry].Mesh, Semantics[entry].Index);
            else if (e.Semantic is { Length: > 0 } sem && sem[^1] is >= '0' and <= '9')
                (name, index) = (sem[..^1], sem[^1] - '0');
            var j = fields.FindIndex(f => Eq(f.Name, name) && f.SemanticIndex == index);
            if (j < 0)
                continue;
            var f = fields[j];
            Add(new Field(Name32(e.D3DSemanticName), SByte(e.D3DSemanticIndex), f.Format, f.Offset, f.Slot, f.Instanced, Name32(e.Semantic)), j);
        }
        for (var i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            // This loop matches the field's name against the table's Semantic and its own index (+0x14).
            var entry = Array.FindIndex(Semantics, s => s.Semantic != null && Eq(f.Name, s.Semantic) && (f.SemanticIndex == -1 || s.SemanticIndex == f.SemanticIndex));
            if (entry < 0)
                continue;
            var (_, mesh, index, _, _) = Semantics[entry];
            if (output.Any(o => Eq(o.Name, mesh) && o.SemanticIndex == index))
                continue;
            if (!Special.Any(s => Eq(f.Name, s)))
                continue;
            Add(new Field(Eq(mesh, "blendweights") ? "blendweight" : mesh, SByte(index), f.Format, f.Offset, f.Slot, f.Instanced), i);
        }
        for (var i = 0; i < output.Count; i++)
            fields[matched[i]] = output[i];

        // Every field the signature step did not keep is removed, last first.
        for (var i = fields.Count - 1; i >= 0; i--)
            if (!matched.Contains(i))
            {
                fields.RemoveAt(i);
                source.RemoveAt(i);
            }

        // Offsets: the running size within a slot (each slot restarts at 0 when it changes).
        var stride = 0;
        int previousSlot = 0, running = 0;
        for (var i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            var offset = f.Slot == previousSlot ? running : 0;
            fields[i] = f with { Offset = (short)offset };
            var size = Info(f.Format).Size;
            stride += size;
            previousSlot = f.Slot;
            running = offset + size;
        }
        return new Layout(fields, source, stride, packedNormal, perVertexLighting);
    }

    /// <summary>1804abf60 / 1804ac280: the vertex buffer, every field written in turn at a running offset.</summary>
    public static byte[] Write(BakeScene.Mesh mesh, BakeScene.MeshField[] attributes, Layout layout)
    {
        var count = mesh.Positions.Length;
        var buffer = new byte[count * layout.Stride];
        // 1804aa130(mesh, "tangent", 4): the first attribute whose name contains "tangent" and whose size is 4.
        var tangent = Array.FindIndex(attributes, a => a.Name.Contains("tangent", StringComparison.OrdinalIgnoreCase) && a.Size == 4);
        var at = 0;
        var channels = new float[4];
        for (var v = 0; v < count; v++)
        {
            var vertex = v * mesh.Stride;
            for (var i = 0; i < layout.Fields.Count; i++)
            {
                var field = layout.Fields[i];
                var attr = attributes[layout.Attributes[i]];
                var src = vertex + attr.Offset;
                if (Eq(attr.Name, "blendindices") || Eq(attr.Name, "blendweight") || Eq(attr.Name, "blendweights"))
                    throw new NotSupportedException($"{attr.Name}: 1804ac280's blend paths are not ported");
                if (layout.PackedNormal && Eq(attr.Name, "normal"))
                {
                    if (field.Format == 0x2a)
                    {
                        var n = new System.Numerics.Vector3(mesh.Vertices[src], mesh.Vertices[src + 1], mesh.Vertices[src + 2]);
                        var t = tangent >= 0
                            ? new System.Numerics.Vector4(mesh.Vertices[vertex + attributes[tangent].Offset], mesh.Vertices[vertex + attributes[tangent].Offset + 1],
                                                          mesh.Vertices[vertex + attributes[tangent].Offset + 2], mesh.Vertices[vertex + attributes[tangent].Offset + 3])
                            : new System.Numerics.Vector4(0f, 0f, 1f, 1f);
                        BitConverter.TryWriteBytes(buffer.AsSpan(at), TangentFrame.Compress(n, t));
                    }
                    at += 4;
                    continue;
                }
                if (layout.PerVertexLighting && Eq(attr.Name, "pervertexlighting"))
                {
                    // Four halves of the attribute's first four floats (8 bytes, whatever the field's format).
                    for (var c = 0; c < 4; c++)
                        BitConverter.TryWriteBytes(buffer.AsSpan(at + c * 2), WorldNodeModels.ToHalf(mesh.Vertices[src + c]));
                    at += 8;
                    continue;
                }
                if (Eq(attr.Name, "lightmapuv"))
                {
                    // Two UNORM16: maxss(v * 65535, 0), minss(, 65535), truncated.
                    for (var c = 0; c < 2; c++)
                        BitConverter.TryWriteBytes(buffer.AsSpan(at + c * 2), (ushort)Cvtt32(MinSs(MaxSs(mesh.Vertices[src + c] * 65535f, 0f), 65535f)));
                    at += 4;
                    continue;
                }
                if (Eq(attr.Name, "VertexGenericIntegerData"))
                {
                    BitConverter.TryWriteBytes(buffer.AsSpan(at), mesh.Vertices[src]);
                    at += 4;
                    continue;
                }
                if (Eq(attr.Name, "TanFrameQuat"))
                {
                    // ((x | sign 0x400) << 10 | y) << 10 | z, each channel clamped to -1..1, remapped to 0..1, times 1023, truncated.
                    static uint Q(float f) => (uint)Cvtt64((MinSs(MaxSs(f, -1f), 1f) * 0.5f + 0.5f) * 1023f);
                    var w = mesh.Vertices[src + 3];
                    var q = ((Q(mesh.Vertices[src]) | (0f > w ? 0x400u : 0u)) << 10 | Q(mesh.Vertices[src + 1])) << 10 | Q(mesh.Vertices[src + 2]);
                    BitConverter.TryWriteBytes(buffer.AsSpan(at), q);
                    at += 4;
                    continue;
                }
                var info = Info(field.Format);
                // The buffer is zeroed already (1804ac280 memsets the field's bytes first).
                int n4;
                ReadOnlySpan<float> values;
                if (Eq(attr.Name, "color"))
                {
                    // A colour starts as (0, 0, 0, 1) under the attribute's first min(size, 4) channels.
                    channels[0] = channels[1] = channels[2] = 0f;
                    channels[3] = 1f;
                    for (var c = 0; c < Math.Min(attr.Size, 4); c++)
                        channels[c] = mesh.Vertices[src + c];
                    values = channels.AsSpan();
                    n4 = Math.Min(info.Channels, 4);
                }
                else
                {
                    values = mesh.Vertices.AsSpan(src, Math.Min(attr.Size, info.Channels));
                    n4 = attr.Size <= info.Channels ? attr.Size : info.Channels;
                }
                for (var c = 0; c < n4; c++)
                    ConvertChannel(buffer.AsSpan(at + c * info.ChannelSize), info.ChannelSize, info.Integer, info.SignedRange, info.Float, values[c]);
                at += info.Size;
            }
        }
        if (at != buffer.Length)
            throw new InvalidOperationException($"wrote {at} vertex bytes for a {buffer.Length}-byte buffer");
        return buffer;
    }

    /// <summary>180093f10 (stream -1): GeometryInfo_t's normal, tangent, texcoord and color infos from the layout.</summary>
    public static GeometryInfo_t_natural Geometry(Layout layout, Signature signature, int buffer)
    {
        var g = new GeometryInfo_t_natural
        {
            // y and z are -1 in every capture (capture_host.py on ze_hold_em_p and probe01; no shader reads them).
            m_nVertexAndIndexBuffers = new ivec4(buffer, -1, -1, buffer),
            m_nBufferBindOffsetBytes = new ivec4(0),
            m_nBufferStrideInBytes = new ivec4(layout.Stride, 0, 0, 4),
            m_vTexCoordFieldInfo = new _Array_natural_vector_int_4_8 { data = new ivec4[8] },
            m_vColorFieldInfo = new _Array_natural_vector_int_4_8 { data = new ivec4[8] },
        };
        var position = false;
        foreach (var f in layout.Fields)
        {
            // One vertex stream: slot 0, not instanced.
            if (f.Slot < 0 || f.Slot >= 1 || f.Instanced)
                continue;
            var (name, index) = (f.Name, f.SemanticIndex);
            if (index == -1)
                foreach (var e in signature)
                    if (string.Compare(f.Name, 0, e.Semantic, 0, 0x1f, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        (name, index) = (e.D3DSemanticName, e.D3DSemanticIndex);
                        break;
                    }
            var info = new ivec4(f.Offset, f.Format, f.Slot, 0);
            var aligned = (f.Offset & 3) == 0;
            if (Eq(name, "position") && index == 0 && f.Offset == 0 && f.Format is 0x2 or 0x6)
                position = true;
            else if (Eq(name, "normal") && index == 0 && aligned && f.Format is 0x6 or 0x2a)
                g.m_vNormalFieldInfo = info;
            else if (Eq(name, "tangent") && index == 0 && aligned && f.Format == 0x2)
                g.m_vTangentFieldInfo = info;
            else if (Eq(name, "texcoord") && aligned && f.Format < 0x26 && (0x2410010004UL >> f.Format & 1) != 0 && index is >= 0 and < 8)
                g.m_vTexCoordFieldInfo.data[index] = info;
            else if (Eq(name, "color") && aligned && f.Format is 0x2 or 0x1c && index is >= 0 and < 8)
                g.m_vColorFieldInfo.data[index] = info;
        }
        if (!position)
            throw new InvalidOperationException("the layout has no R32G32B32 position at offset 0 (180093f10 makes no geometry)");
        return g;
    }

    /// <summary>
    /// 1804aa4e0 (ConvertFloatToChannel): one channel. Float: as is (4 bytes) or 18015e140's half (WorldNodeModels.ToHalf)
    /// (2). Otherwise clamped (to -1..1 with the signed range flag, else 0..1; a NaN passes) and
    /// scaled, truncating; with the integer flag and no signed range, the value is truncated and
    /// saturated as an integer; with the signed range but no integer flag it is remapped to 0..1 first.
    /// </summary>
    public static void ConvertChannel(Span<byte> dst, int size, bool integer, bool signedRange, bool isFloat, float v)
    {
        if (isFloat)
        {
            if (size == 4)
                BitConverter.TryWriteBytes(dst, v);
            else if (size == 2)
                BitConverter.TryWriteBytes(dst, WorldNodeModels.ToHalf(v));
            else
                throw new NotSupportedException($"ConvertFloatToChannel to {size} bytes");
            return;
        }
        if (signedRange)
        {
            // comiss -1, v; ja -> -1; else minss(1, v).
            var c = -1f > v ? -1f : (1f < v ? 1f : v);
            if (integer)
            {
                switch (size)
                {
                    case 4: BitConverter.TryWriteBytes(dst, Cvtt32(c * 2147483648f)); return;
                    case 2: BitConverter.TryWriteBytes(dst, (short)Cvtt32(c * 32767f)); return;
                    case 1: dst[0] = (byte)Cvtt32(c * 127f); return;
                }
            }
            else
            {
                c = c * 0.5f + 0.5f;
                switch (size)
                {
                    case 4: BitConverter.TryWriteBytes(dst, (uint)Cvtt64(c * 4294967296f)); return;
                    case 2: BitConverter.TryWriteBytes(dst, (ushort)Cvtt32(c * 65535f)); return;
                    case 1: dst[0] = (byte)Cvtt32(c * 255f); return;
                }
            }
        }
        else if (integer)
        {
            var i = Cvtt32(v);
            switch (size)
            {
                case 4: BitConverter.TryWriteBytes(dst, i <= int.MinValue ? int.MinValue : i >= int.MaxValue ? int.MaxValue : i); return;
                case 2: BitConverter.TryWriteBytes(dst, (short)(i <= -0x8000 ? -0x8000 : i >= 0x7fff ? 0x7fff : i)); return;
                case 1: dst[0] = (byte)(i <= -0x80 ? -0x80 : i >= 0x7f ? 0x7f : i); return;
            }
        }
        else
        {
            // comiss 0, v; ja -> 0; else minss(1, v).
            var c = 0f > v ? 0f : (1f < v ? 1f : v);
            switch (size)
            {
                case 4: BitConverter.TryWriteBytes(dst, (uint)Cvtt64(c * 4294967296f)); return;
                case 2: BitConverter.TryWriteBytes(dst, (ushort)Cvtt32(c * 65535f)); return;
                case 1: dst[0] = (byte)Cvtt32(c * 255f); return;
            }
        }
        throw new NotSupportedException($"ConvertFloatToChannel: a normalised channel of {size} bytes");
    }

    /// <summary>cvttss2si to 32 bits: truncation, 0x80000000 for NaN and out of range.</summary>
    public static int Cvtt32(float v) => float.IsNaN(v) || v >= 2147483648f || v < -2147483648f ? int.MinValue : (int)v;

    /// <summary>cvttss2si to 64 bits: truncation, 0x8000000000000000 for NaN and out of range.</summary>
    public static long Cvtt64(float v) => float.IsNaN(v) || v >= 9.223372e18f || v < -9.223372e18f ? long.MinValue : (long)v;

    // maxss / minss: the first operand when the comparison holds, else the second (so a NaN first operand gives the second).
    private static float MaxSs(float a, float b) => a > b ? a : b;
    private static float MinSs(float a, float b) => a < b ? a : b;
}
