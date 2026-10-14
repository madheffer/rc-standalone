// Ported from ValveResourceFormat's VBIB (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat):
// the buffer layouts, the attribute decoders and their quirks are VRF's, checked against it over every
// pak01 mesh (docs/MESHES.md, "Vertex data").

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ValveKeyValue;

namespace Source2.Compiler.Meshes;

/// <summary>One field of a vertex layout: which attribute, in which format, at which byte of a vertex.</summary>
public struct RenderInputLayoutField
{
    /// <summary>The semantic, upper case (<c>POSITION</c>, <c>TEXCOORD</c>, <c>BLENDINDICES</c>...).</summary>
    public string SemanticName;

    /// <summary>Which of several fields with the same semantic (TEXCOORD 0, 1...).</summary>
    public int SemanticIndex;

    /// <summary>The field's format.</summary>
    public DXGI_FORMAT Format;

    /// <summary>Its byte offset within a vertex.</summary>
    public uint Offset;

    /// <summary>The input slot.</summary>
    public int Slot;

    /// <summary>Per vertex or per instance.</summary>
    public RenderSlotType SlotType;

    /// <summary>The instance step rate.</summary>
    public int InstanceStepRate;

    /// <summary>The shader semantic name, when the layout carries one.</summary>
    public string? ShaderSemantic;
}

/// <summary>A vertex or index buffer, decoded: <see cref="ElementCount"/> elements of <see cref="ElementSizeInBytes"/> bytes.</summary>
public struct OnDiskBufferData
{
    /// <summary>Vertices or indices.</summary>
    public uint ElementCount;

    /// <summary>The vertex stride, or 2 or 4 for indices.</summary>
    public uint ElementSizeInBytes;

    /// <summary>The vertex layout (none for an index buffer).</summary>
    public RenderInputLayoutField[] InputLayoutFields;

    /// <summary>The decoded elements.</summary>
    public byte[] Data;

    /// <summary><see cref="ElementCount"/> x <see cref="ElementSizeInBytes"/>.</summary>
    public readonly uint TotalSizeInBytes => ElementCount * ElementSizeInBytes;
}

/// <summary>
/// A mesh's vertex and index buffers, decoded, with the attribute readers VRF's <c>VBIB</c> has. Read
/// from a binary VBIB block (<see cref="Read(ReadOnlySpan{byte})"/>), or from a descriptor whose buffers
/// are inline (<c>m_pData</c>) or in their own blocks (<c>m_nBlockIndex</c>, the MVTX/MIDX layout of CS2
/// models), with <see cref="FromDescriptor"/>.
/// </summary>
public sealed class Vbib
{
    /// <summary>The vertex buffers.</summary>
    public List<OnDiskBufferData> VertexBuffers { get; } = [];

    /// <summary>The index buffers.</summary>
    public List<OnDiskBufferData> IndexBuffers { get; } = [];

    /// <summary>The most bytes one buffer may decode to (2 GiB less a little), a check on hostile counts.</summary>
    private const long MaxBufferBytes = int.MaxValue - 64;

    /// <summary>Read a binary VBIB block.</summary>
    /// <exception cref="InvalidDataException">A count, offset or size runs past the block, or a buffer does not decode.</exception>
    public static Vbib Read(ReadOnlySpan<byte> block)
    {
        var vbib = new Vbib();
        Need(block, 0, 16);
        var vertexOffset = BinaryPrimitives.ReadUInt32LittleEndian(block);
        var vertexCount = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        var indexOffset = BinaryPrimitives.ReadUInt32LittleEndian(block[8..]);
        var indexCount = BinaryPrimitives.ReadUInt32LittleEndian(block[12..]);
        Need(block, vertexOffset, 24L * vertexCount);
        Need(block, 8L + indexOffset, 24L * indexCount);
        for (var i = 0; i < vertexCount; i++)
            vbib.VertexBuffers.Add(ReadBuffer(block, vertexOffset + 24L * i, isVertex: true));
        for (var i = 0; i < indexCount; i++)
            vbib.IndexBuffers.Add(ReadBuffer(block, 8L + indexOffset + 24L * i, isVertex: false));
        return vbib;
    }

    private static void Need(ReadOnlySpan<byte> block, long at, long bytes)
    {
        if (at < 0 || bytes < 0 || at + bytes > block.Length)
            throw new InvalidDataException($"VBIB: {bytes} bytes at {at} run past the {block.Length}-byte block.");
    }

    private static OnDiskBufferData ReadBuffer(ReadOnlySpan<byte> block, long at, bool isVertex)
    {
        var record = block[(int)at..];
        var buffer = new OnDiskBufferData { ElementCount = BinaryPrimitives.ReadUInt32LittleEndian(record) };
        // meshsystem - look for "SceneSystem/ComputeShaderSkinning" string
        var size = BinaryPrimitives.ReadInt32LittleEndian(record[4..]);
        buffer.ElementSizeInBytes = (uint)(size & 0x3FFFFFF);
        // Bits 28 to 31 are GPU buffer creation flags and do not affect parsing.
        var isMeshoptCompressed = (size & 0x4000000) == 0;
        var isZstdCompressed = (size & 0x8000000) != 0;

        var attributesAt = at + 8 + BinaryPrimitives.ReadUInt32LittleEndian(record[8..]);
        var attributeCount = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
        var dataAt = at + 16 + BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
        var totalSize = BinaryPrimitives.ReadInt32LittleEndian(record[20..]);
        Need(block, attributesAt, 56L * attributeCount);
        Need(block, dataAt, totalSize);

        buffer.InputLayoutFields = new RenderInputLayoutField[attributeCount];
        for (var i = 0; i < attributeCount; i++)
        {
            var field = block.Slice((int)(attributesAt + 56 * i), 56);
            var name = field[..32];
            var end = name.IndexOf((byte)0);
            buffer.InputLayoutFields[i] = new RenderInputLayoutField
            {
                SemanticName = Encoding.UTF8.GetString(end < 0 ? name : name[..end]).ToUpperInvariant(),
                SemanticIndex = BinaryPrimitives.ReadInt32LittleEndian(field[32..]),
                Format = (DXGI_FORMAT)BinaryPrimitives.ReadUInt32LittleEndian(field[36..]),
                Offset = BinaryPrimitives.ReadUInt32LittleEndian(field[40..]),
                Slot = BinaryPrimitives.ReadInt32LittleEndian(field[44..]),
                SlotType = (RenderSlotType)BinaryPrimitives.ReadUInt32LittleEndian(field[48..]),
                InstanceStepRate = BinaryPrimitives.ReadInt32LittleEndian(field[52..]),
            };
        }

        var data = block.Slice((int)dataAt, totalSize);
        var decompressedSize = (long)buffer.ElementCount * buffer.ElementSizeInBytes;
        buffer.Data = decompressedSize > totalSize
            ? Decompress(buffer, data, decompressedSize, isVertex, isZstdCompressed, isMeshoptCompressed)
            : data.ToArray();
        return buffer;
    }

    private static byte[] Decompress(OnDiskBufferData buffer, ReadOnlySpan<byte> data, long decompressedSize, bool isVertex, bool isZstd, bool isMeshopt)
    {
        if (decompressedSize > MaxBufferBytes)
            throw new InvalidDataException($"A mesh buffer of {buffer.ElementCount} x {buffer.ElementSizeInBytes} bytes is too large.");
        byte[]? unwrapped = null;
        if (isZstd)
        {
            // No size is stored; a whole buffer of elements bounds it.
            unwrapped = new byte[decompressedSize];
            using var zstd = new ZstdSharp.Decompressor();
            if (!zstd.TryUnwrap(data, unwrapped, out var written))
                throw new InvalidDataException("A mesh buffer does not decompress (zstd).");
            data = unwrapped.AsSpan(0, written);
        }
        if (!isMeshopt)
        {
            if (data.Length != decompressedSize)
                throw new InvalidDataException($"A mesh buffer decompressed to {data.Length} bytes, expected {decompressedSize}.");
            return data.ToArray();
        }
        return isVertex
            ? MeshoptVertexCodec.Decode(data, (int)buffer.ElementCount, (int)buffer.ElementSizeInBytes)
            : MeshoptIndexCodec.Decode(data, (int)buffer.ElementCount, (int)buffer.ElementSizeInBytes);
    }

    /// <summary>
    /// Read the buffers a descriptor lists (<c>m_vertexBuffers</c>, <c>m_indexBuffers</c>): each inline
    /// (<c>m_pData</c>, meshopt-encoded when shorter than its elements) or in the block of
    /// <paramref name="resource"/> its <c>m_nBlockIndex</c> names (meshopt and zstd as flagged).
    /// </summary>
    /// <exception cref="InvalidDataException">A block index is out of range or a buffer does not decode.</exception>
    public static Vbib FromDescriptor(CompiledResource resource, KVObject descriptor)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(descriptor);
        var vbib = new Vbib();
        foreach (var vb in descriptor.GetArray("m_vertexBuffers") ?? [])
            vbib.VertexBuffers.Add(FromBufferDescriptor(resource, vb, isVertex: true));
        foreach (var ib in descriptor.GetArray("m_indexBuffers") ?? [])
            vbib.IndexBuffers.Add(FromBufferDescriptor(resource, ib, isVertex: false));
        return vbib;
    }

    private static OnDiskBufferData FromBufferDescriptor(CompiledResource resource, KVObject data, bool isVertex)
    {
        var buffer = new OnDiskBufferData
        {
            ElementCount = data.GetUInt32Property("m_nElementCount"),
            ElementSizeInBytes = data.GetUInt32Property("m_nElementSizeInBytes"),
        };
        buffer.InputLayoutFields = [.. (data.GetArray("m_inputLayoutFields") ?? []).Select(static il =>
        {
            var semanticName = il["m_pSemanticName"];
            var name = semanticName?.ValueType switch
            {
                KVValueType.String => (string)semanticName,
                KVValueType.BinaryBlob => Encoding.UTF8.GetString(semanticName.AsBlob().AsSpan().TrimEnd((byte)0)),
                _ => string.Empty,
            };
            return new RenderInputLayoutField
            {
                SemanticName = name.ToUpperInvariant(),
                SemanticIndex = il.GetInt32Property("m_nSemanticIndex"),
                Format = (DXGI_FORMAT)il.GetUInt32Property("m_Format"),
                Offset = il.GetUInt32Property("m_nOffset"),
                Slot = il.GetInt32Property("m_nSlot"),
                SlotType = il.GetEnumValue<RenderSlotType>("m_nSlotType"),
                InstanceStepRate = il.GetInt32Property("m_nInstanceStepRate"),
                ShaderSemantic = il.GetStringProperty("m_szShaderSemantic"),
            };
        })];

        var decompressedSize = (long)buffer.ElementCount * buffer.ElementSizeInBytes;
        if (data.ContainsKey("m_pData"))
        {
            var bytes = data.GetArray<byte>("m_pData") ?? [];
            buffer.Data = bytes.Length == decompressedSize
                ? bytes
                : Decompress(buffer, bytes, decompressedSize, isVertex, isZstd: false, isMeshopt: true);
            return buffer;
        }

        // MVTX MIDX update
        var blockIndex = data.GetInt32Property("m_nBlockIndex");
        if (blockIndex < 0 || blockIndex >= resource.File.Blocks.Count)
            throw new InvalidDataException($"A mesh buffer names block {blockIndex}; the file has {resource.File.Blocks.Count}.");
        var stored = resource.File.Blocks[blockIndex].Data;
        var isMeshopt = data.GetByteProperty("m_bMeshoptCompressed") == 1;
        var isZstd = data.GetByteProperty("m_bCompressedZSTD") == 1;
        buffer.Data = isZstd || isMeshopt
            ? Decompress(buffer, stored, decompressedSize, isVertex, isZstd, isMeshopt)
            : stored.ToArray();
        return buffer;
    }

    /// <summary>Whether two buffers have the same stride and fields (semantic, index, format, offset).</summary>
    public static bool HasSameLayout(OnDiskBufferData a, OnDiskBufferData b)
    {
        if (a.ElementSizeInBytes != b.ElementSizeInBytes || a.InputLayoutFields.Length != b.InputLayoutFields.Length)
            return false;
        for (var i = 0; i < a.InputLayoutFields.Length; i++)
        {
            var x = a.InputLayoutFields[i];
            var y = b.InputLayoutFields[i];
            if (x.SemanticName != y.SemanticName || x.SemanticIndex != y.SemanticIndex || x.Format != y.Format || x.Offset != y.Offset)
                return false;
        }
        return true;
    }

    /// <summary>Buffers of one layout, one after another.</summary>
    /// <exception cref="ArgumentException">The layouts differ.</exception>
    public static OnDiskBufferData Concatenate(IReadOnlyList<OnDiskBufferData> buffers)
    {
        ArgumentNullException.ThrowIfNull(buffers);
        var first = buffers[0];
        var totalCount = 0u;
        foreach (var buffer in buffers)
        {
            if (!HasSameLayout(first, buffer))
                throw new ArgumentException("Vertex buffers must share a layout to be concatenated.", nameof(buffers));
            totalCount += buffer.ElementCount;
        }
        var data = new byte[totalCount * first.ElementSizeInBytes];
        var written = 0;
        foreach (var buffer in buffers)
        {
            var length = (int)(buffer.ElementCount * buffer.ElementSizeInBytes);
            Buffer.BlockCopy(buffer.Data, 0, data, written, length);
            written += length;
        }
        return new OnDiskBufferData { ElementCount = totalCount, ElementSizeInBytes = first.ElementSizeInBytes, InputLayoutFields = first.InputLayoutFields, Data = data };
    }

    // Every reader walks ElementCount vertices from the field's offset; this checks that the last one fits.
    private static void CheckField(OnDiskBufferData vertexBuffer, RenderInputLayoutField attribute, int bytes)
    {
        if (vertexBuffer.ElementCount == 0)
            return;
        var last = (long)(vertexBuffer.ElementCount - 1) * vertexBuffer.ElementSizeInBytes + attribute.Offset + bytes;
        if (last > vertexBuffer.Data.Length)
            throw new InvalidDataException($"{attribute.SemanticName} ({attribute.Format} at {attribute.Offset}) runs past the {vertexBuffer.Data.Length}-byte buffer.");
    }

    private static InvalidDataException Unexpected(RenderInputLayoutField attribute) =>
        new($"Unexpected {attribute.SemanticName} attribute format {attribute.Format}");

    /// <summary>A one-float attribute (R32_FLOAT).</summary>
    public static float[] GetScalarAttributeArray(OnDiskBufferData vertexBuffer, RenderInputLayoutField attribute)
    {
        if (attribute.Format != DXGI_FORMAT.R32_FLOAT)
            throw Unexpected(attribute);
        var result = new float[vertexBuffer.ElementCount];
        MarshallAttributeArray(result, sizeof(float), vertexBuffer, attribute);
        return result;
    }

    /// <summary>A two-component attribute: R32G32_FLOAT, R16G16_FLOAT, R16G16_UNORM or R16G16_SNORM.</summary>
    public static Vector2[] GetVector2AttributeArray(OnDiskBufferData vertexBuffer, RenderInputLayoutField attribute)
    {
        var result = new Vector2[vertexBuffer.ElementCount];
        var offset = (int)attribute.Offset;
        var data = vertexBuffer.Data.AsSpan();
        var stride = (int)vertexBuffer.ElementSizeInBytes;
        switch (attribute.Format)
        {
            case DXGI_FORMAT.R32G32_FLOAT:
                MarshallAttributeArray(result, sizeof(float) * 2, vertexBuffer, attribute);
                break;
            case DXGI_FORMAT.R16G16_FLOAT:
                CheckField(vertexBuffer, attribute, 4);
                for (var i = 0; i < result.Length; i++, offset += stride)
                {
                    var halfs = MemoryMarshal.Cast<byte, Half>(data.Slice(offset, 4));
                    result[i] = new Vector2((float)halfs[0], (float)halfs[1]);
                }
                break;
            case DXGI_FORMAT.R16G16_UNORM:
                CheckField(vertexBuffer, attribute, 4);
                for (var i = 0; i < result.Length; i++, offset += stride)
                {
                    var ushorts = MemoryMarshal.Cast<byte, ushort>(data.Slice(offset, 4));
                    result[i] = new Vector2(ushorts[0], ushorts[1]) / 65535f;
                }
                break;
            case DXGI_FORMAT.R16G16_SNORM:
                CheckField(vertexBuffer, attribute, 4);
                for (var i = 0; i < result.Length; i++, offset += stride)
                {
                    var shorts = MemoryMarshal.Cast<byte, short>(data.Slice(offset, 4));
                    result[i] = new Vector2(shorts[0], shorts[1]) / 32767f;
                }
                break;
            default:
                throw Unexpected(attribute);
        }
        return result;
    }

    /// <summary>A three-float attribute (R32G32B32_FLOAT).</summary>
    public static Vector3[] GetVector3AttributeArray(OnDiskBufferData vertexBuffer, RenderInputLayoutField attribute)
    {
        if (attribute.Format != DXGI_FORMAT.R32G32B32_FLOAT)
            throw Unexpected(attribute);
        var result = new Vector3[vertexBuffer.ElementCount];
        MarshallAttributeArray(result, sizeof(float) * 3, vertexBuffer, attribute);
        return result;
    }

    /// <summary>A four-component attribute: R32G32B32A32_FLOAT, R16G16B16A16_FLOAT or R8G8B8A8_UNORM.</summary>
    public static Vector4[] GetVector4AttributeArray(OnDiskBufferData vertexBuffer, RenderInputLayoutField attribute)
    {
        var result = new Vector4[vertexBuffer.ElementCount];
        var offset = (int)attribute.Offset;
        var data = vertexBuffer.Data.AsSpan();
        var stride = (int)vertexBuffer.ElementSizeInBytes;
        switch (attribute.Format)
        {
            case DXGI_FORMAT.R32G32B32A32_FLOAT:
                MarshallAttributeArray(result, sizeof(float) * 4, vertexBuffer, attribute);
                break;
            case DXGI_FORMAT.R16G16B16A16_FLOAT:
                CheckField(vertexBuffer, attribute, 8);
                for (var i = 0; i < result.Length; i++, offset += stride)
                {
                    var halfs = MemoryMarshal.Cast<byte, Half>(data.Slice(offset, 8));
                    result[i] = new Vector4((float)halfs[0], (float)halfs[1], (float)halfs[2], (float)halfs[3]);
                }
                break;
            case DXGI_FORMAT.R8G8B8A8_UNORM:
                CheckField(vertexBuffer, attribute, 4);
                for (var i = 0; i < result.Length; i++, offset += stride)
                    result[i] = new Vector4(data[offset], data[offset + 1], data[offset + 2], data[offset + 3]) / 255f;
                break;
            default:
                throw Unexpected(attribute);
        }
        return result;
    }

    /// <summary>
    /// Normals, and tangents when the format packs them: R32G32B32_FLOAT (normals only), R32_UINT
    /// (CS2's packed frame: octahedral normal, tangent angle, bitangent sign) or R8G8B8A8_UNORM (the
    /// older two-byte normal and tangent).
    /// </summary>
    public static (Vector3[] Normals, Vector4[] Tangents) GetNormalTangentArray(OnDiskBufferData vertexBuffer, RenderInputLayoutField attribute)
    {
        switch (attribute.Format)
        {
            case DXGI_FORMAT.R32G32B32_FLOAT:
            {
                var normals = new Vector3[vertexBuffer.ElementCount];
                MarshallAttributeArray(normals, sizeof(float) * 3, vertexBuffer, attribute);
                return (normals, []);
            }
            case DXGI_FORMAT.R32_UINT: // Version 2 compressed normals (CS2)
            {
                var packedFrames = new uint[vertexBuffer.ElementCount];
                MarshallAttributeArray(packedFrames, sizeof(uint), vertexBuffer, attribute);
                return DecompressNormalTangents2(packedFrames);
            }
            case DXGI_FORMAT.R8G8B8A8_UNORM: // Version 1 compressed normals
            {
                CheckField(vertexBuffer, attribute, 4);
                var normals = new Vector3[vertexBuffer.ElementCount];
                var tangents = new Vector4[vertexBuffer.ElementCount];
                var offset = (int)attribute.Offset;
                for (var i = 0; i < normals.Length; i++, offset += (int)vertexBuffer.ElementSizeInBytes)
                {
                    normals[i] = DecompressNormal(vertexBuffer.Data[offset], vertexBuffer.Data[offset + 1]);
                    tangents[i] = DecompressTangent(vertexBuffer.Data[offset + 2], vertexBuffer.Data[offset + 3]);
                }
                return (normals, tangents);
            }
            default:
                throw Unexpected(attribute);
        }
    }

    /// <summary>
    /// Bone indices, four per vertex (eight for R32G32B32A32_SINT and R16G16B16A16_UINT), through
    /// <paramref name="remapTable"/> when given (a mesh's local bones to the model skeleton). R16G16_SINT
    /// stores two and repeats the second.
    /// </summary>
    /// <exception cref="InvalidDataException">An index is outside <paramref name="remapTable"/>.</exception>
    public static ushort[] GetBlendIndicesArray(OnDiskBufferData vertexBuffer, RenderInputLayoutField attribute, int[]? remapTable = null)
    {
        var numJoints = attribute.Format is DXGI_FORMAT.R32G32B32A32_SINT or DXGI_FORMAT.R16G16B16A16_UINT ? 8 : 4;
        var indices = new ushort[vertexBuffer.ElementCount * numJoints];
        var offset = (int)attribute.Offset;
        ReadOnlySpan<byte> data = vertexBuffer.Data.AsSpan();
        var stride = (int)vertexBuffer.ElementSizeInBytes;
        switch (attribute.Format)
        {
            case DXGI_FORMAT.R16G16_SINT:
                CheckField(vertexBuffer, attribute, 4);
                for (var i = 0; i < vertexBuffer.ElementCount; i++, offset += stride)
                {
                    var ushorts = MemoryMarshal.Cast<byte, ushort>(data.Slice(offset, 4));
                    var joints = indices.AsSpan(i * numJoints, numJoints);
                    joints[0] = ushorts[0];
                    joints[1] = joints[2] = joints[3] = ushorts[1];
                }
                break;
            case DXGI_FORMAT.R16G16B16A16_SINT:
            case DXGI_FORMAT.R32G32B32A32_SINT: // 8 joints
                CheckField(vertexBuffer, attribute, numJoints * sizeof(ushort));
                for (var i = 0; i < vertexBuffer.ElementCount; i++, offset += stride)
                    MemoryMarshal.Cast<byte, ushort>(data.Slice(offset, numJoints * sizeof(ushort))).CopyTo(indices.AsSpan(i * numJoints, numJoints));
                break;
            case DXGI_FORMAT.R8G8B8A8_UINT:
            case DXGI_FORMAT.R16G16B16A16_UINT: // 8 joints
            {
                CheckField(vertexBuffer, attribute, numJoints);
                var inc = 0;
                for (var i = 0; i < vertexBuffer.ElementCount; i++, offset += stride)
                    foreach (var b in data.Slice(offset, numJoints))
                        indices[inc++] = b;
                break;
            }
            default:
                throw Unexpected(attribute);
        }
        if (remapTable != null)
        {
            for (var i = 0; i < indices.Length; i++)
            {
                if (indices[i] >= remapTable.Length || (uint)remapTable[indices[i]] > ushort.MaxValue)
                    throw new InvalidDataException($"Bone index {indices[i]} is outside the mesh's {remapTable.Length}-entry remap table.");
                indices[i] = (ushort)remapTable[indices[i]];
            }
        }
        return indices;
    }

    /// <summary>
    /// Bone weights: one vector per vertex, two for R16G16B16A16_UNORM (read as eight bytes over 255,
    /// as VRF reads it); R16G16_UNORM fills two.
    /// </summary>
    public static Vector4[] GetBlendWeightsArray(OnDiskBufferData vertexBuffer, RenderInputLayoutField attribute)
    {
        var numVectors = attribute.Format is DXGI_FORMAT.R16G16B16A16_UNORM ? 2 : 1;
        var weights = new Vector4[vertexBuffer.ElementCount * numVectors];
        var offset = (int)attribute.Offset;
        var data = vertexBuffer.Data.AsSpan();
        var stride = (int)vertexBuffer.ElementSizeInBytes;
        switch (attribute.Format)
        {
            case DXGI_FORMAT.R8G8B8A8_UNORM:
                CheckField(vertexBuffer, attribute, 4);
                for (var i = 0; i < weights.Length; i++, offset += stride)
                    weights[i] = new Vector4(data[offset], data[offset + 1], data[offset + 2], data[offset + 3]) / 255f;
                break;
            case DXGI_FORMAT.R16G16B16A16_UNORM:
                CheckField(vertexBuffer, attribute, 8);
                for (var i = 0; i < weights.Length; i += 2, offset += stride)
                {
                    weights[i] = new Vector4(data[offset], data[offset + 1], data[offset + 2], data[offset + 3]) / 255f;
                    weights[i + 1] = new Vector4(data[offset + 4], data[offset + 5], data[offset + 6], data[offset + 7]) / 255f;
                }
                break;
            case DXGI_FORMAT.R16G16_UNORM:
                CheckField(vertexBuffer, attribute, 4);
                for (var i = 0; i < weights.Length; i++, offset += stride)
                {
                    var packed = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
                    weights[i] = new Vector4(packed & 0x0000FFFF, packed >> 16, 0f, 0f) / 65535f;
                }
                break;
            default:
                throw Unexpected(attribute);
        }
        return weights;
    }

    private static void MarshallAttributeArray<T>(T[] result, int size, OnDiskBufferData vertexBuffer, RenderInputLayoutField attribute)
        where T : unmanaged
    {
        CheckField(vertexBuffer, attribute, size);
        var offset = (int)attribute.Offset;
        var data = vertexBuffer.Data.AsSpan();
        for (var i = 0; i < vertexBuffer.ElementCount; i++, offset += (int)vertexBuffer.ElementSizeInBytes)
            result[i] = Unsafe.ReadUnaligned<T>(ref MemoryMarshal.GetReference(data.Slice(offset, size)));
    }

    private static Vector3 DecompressNormal(float x, float y)
    {
        x -= 128.0f;
        y -= 128.0f;

        var zSignBit = x < 0 ? 1.0f : 0.0f;    // z and t negative bits (like slt asm instruction)
        var tSignBit = y < 0 ? 1.0f : 0.0f;
        var zSign = -((2 * zSignBit) - 1);     // z and t signs
        var tSign = -((2 * tSignBit) - 1);

        x = (x * zSign) - zSignBit;            // 0..127
        y = (y * tSign) - tSignBit;
        x -= 64;                               // -64..63
        y -= 64;

        var xSignBit = x < 0 ? 1.0f : 0.0f;    // x and y negative bits (like slt asm instruction)
        var ySignBit = y < 0 ? 1.0f : 0.0f;
        var xSign = -((2 * xSignBit) - 1);     // x and y signs
        var ySign = -((2 * ySignBit) - 1);

        x = ((x * xSign) - xSignBit) / 63.0f;  // 0..1 range
        y = ((y * ySign) - ySignBit) / 63.0f;
        var z = 1.0f - x - y;

        var oolen = 1.0f / MathF.Sqrt((x * x) + (y * y) + (z * z)); // Normalize and
        x *= oolen * xSign;                   // Recover signs
        y *= oolen * ySign;
        z *= oolen * zSign;
        return new Vector3(x, y, z);
    }

    private static Vector4 DecompressTangent(float x, float y)
    {
        var normal = DecompressNormal(x, y);
        var tSign = y < 128.0f ? -1.0f : 1.0f;
        return new Vector4(normal.X, normal.Y, normal.Z, tSign);
    }

    private static (Vector3[] Normals, Vector4[] Tangents) DecompressNormalTangents2(uint[] packedFrames)
    {
        var normals = new Vector3[packedFrames.Length];
        var tangents = new Vector4[packedFrames.Length];
        for (var i = 0; i < packedFrames.Length; i++)
        {
            var nPackedFrame = packedFrames[i];
            var signBit = nPackedFrame & 1u;            // LSB bit
            float tBits = (nPackedFrame >> 1) & 0x7ff;  // 11 bits
            float xBits = (nPackedFrame >> 12) & 0x3ff; // 10 bits
            float yBits = (nPackedFrame >> 22) & 0x3ff; // 10 bits

            // Unpack from 0..1 to -1..1
            var nPackedFrameX = (xBits / 1023.0f) * 2.0f - 1.0f;
            var nPackedFrameY = (yBits / 1023.0f) * 2.0f - 1.0f;

            // Z is never given a sign, meaning negative values are caused by abs(packedframexy) adding up to over 1.0
            var derivedNormalZ = 1.0f - MathF.Abs(nPackedFrameX) - MathF.Abs(nPackedFrameY); // Project onto x+y+z=1
            var unpackedNormal = new Vector3(nPackedFrameX, nPackedFrameY, derivedNormalZ);

            // A negative Z folded its excess into X and Y; take it back out.
            var negativeZCompensation = Math.Clamp(-derivedNormalZ, 0.0f, 1.0f);
            var unpackedNormalXPositive = unpackedNormal.X >= 0.0f ? 1.0f : 0.0f;
            var unpackedNormalYPositive = unpackedNormal.Y >= 0.0f ? 1.0f : 0.0f;
            unpackedNormal.X += negativeZCompensation * (1f - unpackedNormalXPositive) + -negativeZCompensation * unpackedNormalXPositive; // mix() - x×(1−a)+y×a
            unpackedNormal.Y += negativeZCompensation * (1f - unpackedNormalYPositive) + -negativeZCompensation * unpackedNormalYPositive;

            var normal = Vector3.Normalize(unpackedNormal);
            normals[i] = normal;

            // Invert tangent when normal Z is negative
            var tangentSign = (normal.Z >= 0.0f) ? 1.0f : -1.0f;
            // equal to tangentSign * (1.0 + abs(normal.z))
            var rcpTangentZ = 1.0f / (tangentSign + normal.Z);

            // Keep the operation order: the compressed data is sensitive to float precision.
            Vector3 unalignedTangent;
            unalignedTangent.X = -tangentSign * (normal.X * normal.X) * rcpTangentZ + 1.0f;
            unalignedTangent.Y = -tangentSign * ((normal.X * normal.Y) * rcpTangentZ);
            unalignedTangent.Z = -tangentSign * normal.X;

            // The normal alone gives a direction on the tangent plane; the stored angle turns it onto the texcoords.
            var nPackedFrameT = tBits / 2047.0f * MathF.Tau;
            var tangent = unalignedTangent * MathF.Cos(nPackedFrameT) + Vector3.Cross(normal, unalignedTangent) * MathF.Sin(nPackedFrameT);
            tangents[i] = new Vector4(tangent, (signBit == 0u) ? -1.0f : 1.0f); // Bitangent sign bit... inverted (0 = negative)
        }
        return (normals, tangents);
    }

    /// <summary>The size of one component and the number of components of an attribute format.</summary>
    /// <exception cref="NotSupportedException">A format no vertex layout uses.</exception>
    public static (int ElementSize, int ElementCount) GetFormatInfo(DXGI_FORMAT format, string? attributeName = null) => format switch
    {
        DXGI_FORMAT.R8G8B8A8_UINT => (1, 4),
        DXGI_FORMAT.R8G8B8A8_UNORM => (1, 4),
        DXGI_FORMAT.R16G16_FLOAT => (2, 2),
        DXGI_FORMAT.R16G16_SINT => (2, 2),
        DXGI_FORMAT.R16G16_SNORM => (2, 2),
        DXGI_FORMAT.R16G16_UNORM => (2, 2),
        DXGI_FORMAT.R16G16B16A16_FLOAT => (2, 4),
        DXGI_FORMAT.R16G16B16A16_SINT => (2, 4),
        DXGI_FORMAT.R16G16B16A16_UINT => (2, 4),
        DXGI_FORMAT.R16G16B16A16_UNORM => (2, 4),
        DXGI_FORMAT.R32_FLOAT => (4, 1),
        DXGI_FORMAT.R32_UINT => (4, 1),
        DXGI_FORMAT.R32G32_FLOAT => (4, 2),
        DXGI_FORMAT.R32G32B32_FLOAT => (4, 3),
        DXGI_FORMAT.R32G32B32A32_FLOAT => (4, 4),
        DXGI_FORMAT.R32G32B32A32_SINT => (4, 4),
        _ => throw new NotSupportedException($"Unsupported \"{attributeName}\" DXGI_FORMAT.{format}"),
    };

    /// <summary>An index buffer's indices, widened to 32 bits.</summary>
    /// <exception cref="InvalidDataException">The element size is not 2 or 4.</exception>
    public static uint[] GetIndices(OnDiskBufferData indexBuffer)
    {
        var indices = new uint[indexBuffer.ElementCount];
        var data = indexBuffer.Data.AsSpan();
        switch (indexBuffer.ElementSizeInBytes)
        {
            case 2:
                for (var i = 0; i < indices.Length; i++)
                    indices[i] = BinaryPrimitives.ReadUInt16LittleEndian(data[(i * 2)..]);
                break;
            case 4:
                MemoryMarshal.Cast<byte, uint>(data[..(indices.Length * 4)]).CopyTo(indices);
                break;
            default:
                throw new InvalidDataException($"An index buffer of {indexBuffer.ElementSizeInBytes}-byte elements.");
        }
        return indices;
    }
}
