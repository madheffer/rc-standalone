using System.Buffers.Binary;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using ValveResourceFormat.Utils;

namespace Source2.Compiler;

/// <summary>
/// <see cref="ResourceBuilder"/>'s operations on ValveResourceFormat's <see cref="Resource"/>, for a
/// host that already holds VRF objects (vpkstuff). The compiler itself works on bytes
/// (<see cref="CompiledResource"/>, <see cref="ResourceFile"/>) and needs none of this; a host that
/// can move to <see cref="CompiledResource"/> drops this assembly and VRF with it.
/// </summary>
public static class VrfResourceBuilder
{
    /// <summary>Get the KV3 root object from a resource's DATA block.
    /// Internal so <c>another caller</c> shares the
    /// same unwrap instead of growing another copy.</summary>
    public static KVObject GetDataRoot(Resource resource)
    {
        var dataBlock = resource.GetBlockByType(BlockType.DATA)
            ?? throw new InvalidOperationException("Template resource has no DATA block.");

        // vmat_c DATA blocks are typed as Material (a KeyValuesOrNTRO subclass) which
        // wraps an inner BinaryKV3 - AsKeyValueCollection() throws for these.
        // vmdl_c DATA blocks may be raw BinaryKV3 or also KeyValuesOrNTRO.
        // KeyValuesOrNTRO.Data IS the mutatable KVObject; Serialize() delegates to the
        // inner BinaryKV3, so mutating Data is reflected in serialization.
        if (dataBlock is KeyValuesOrNTRO kvn)
            return kvn.Data;

        return dataBlock.AsKeyValueCollection()
            ?? throw new InvalidOperationException($"Template DATA block ({dataBlock.GetType().Name}) is not KV3-backed.");
    }

    /// <summary>
    /// Serialize a mutated resource to a byte array. Public so callers
    /// outside this class (e.g. <c>WeaponsVData.SaveCompiled</c>,
    /// <c>HitmarkerService</c>) can route their writes through the same
    /// canonical path the Build* helpers use.
    /// </summary>
    /// <remarks>
    /// Written as resourcecompiler writes a resource: every v5 KV3 block, RED2 and typed DATA
    /// included, by <see cref="Kv3V5Writer"/>, and the container by <see cref="Layout"/>
    /// (zero padding, RC's RERL). VRF's own container writer pads with an "S2V" marker, and its
    /// KV3 writer turns Valve's typed arrays into generic ones, the encoding CS2 silently
    /// misread in a re-serialized .vnmgraph_c (2026-09-26). Measured unchanged-round-trip with
    /// <c>s2c reserialize-parity</c>.
    /// </remarks>
    public static byte[] Serialize(Resource resource)
    {
        KeepUnchangedKv3Blocks(resource);
        AuthoredKv3.RouteTypedBlocks(resource);
        return Layout(resource);
    }

    /// <summary>
    /// A KV3 block whose tree is still exactly what was read goes out as Valve's own bytes. VRF's
    /// reader does not keep every wire type (an 8-bit int comes back as a 32-bit one: stock
    /// models' <c>m_cRenderColor</c> is UINT8 in MDAT), so even a perfect re-encode of its tree
    /// can differ; an edit then changes only the blocks it touches. Needs the resource's source
    /// stream still open and seekable; otherwise every block is re-encoded as before.
    /// </summary>
    private static void KeepUnchangedKv3Blocks(Resource resource)
    {
        Stream stream;
        try
        {
            if (resource.Reader?.BaseStream is not { CanSeek: true } s)
                return;
            stream = s;
            _ = stream.Length;
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        for (var i = 0; i < resource.Blocks.Count; i++)
        {
            var block = resource.Blocks[i];
            var current = block switch
            {
                ResourceEditInfo2 { Data: { } doc } => doc.Root,
                KeyValuesOrNTRO { Format: not null } typed => typed.Data,
                BinaryKV3 { Data: { } doc } when block is not AuthoredKv3.ValveKv3Block => doc.Root,
                _ => null,
            };
            if (current is null || block.Size < 4 || block.Offset + block.Size > stream.Length)
                continue;
            var original = new byte[block.Size];
            stream.Position = block.Offset;
            stream.ReadExactly(original);
            if ((BitConverter.ToUInt32(original) & 0xFFFFFF00) != 0x4B563300)
                continue;
            KVObject read;
            try
            {
                read = Kv3Binary.Read(original).Root;
            }
            catch (InvalidDataException)
            {
                continue;
            }
            if (ResourceBuilder.SameTree(current, read))
                resource.Blocks[i] = new TypedRawBlock(block.Type, original) { Resource = resource };
            else
                Kv3WireTypes.Inherit(read, current);   // VRF's reader dropped the 8-bit widths; an edit keeps Valve's
        }
    }

    /// <summary>
    /// Re-serialize a model resource re-encoding ONLY the DATA block; every other
    /// block (PHYS, ANIM/ASEQ/AGRP, meshes, CTRL, RERL, RED2) is written
    /// byte-identical to <paramref name="originalBytes"/> rather than letting VRF
    /// re-encode it.
    ///
    /// <para><b>Why this exists.</b> VRF's <c>PhysAggregateData</c> (PHYS) and
    /// animation writers do NOT round-trip CS2's data: a plain
    /// <see cref="Serialize(Resource)"/> silently rewrites the physics aggregate
    /// (observed: a character model's PHYS grew 11,907 → 13,689 B), producing a
    /// ragdoll CS2 cannot build - at runtime the physics-body handle resolves to
    /// null and the client crashes (access violation in vphysics2). A DATA-only
    /// edit (mesh-group fold / mask patch) must therefore leave the physics and
    /// animation blocks untouched. The DATA block still re-encodes from its KV3 so
    /// the edit lands.</para>
    /// </summary>
    public static byte[] SerializeModelDataOnly(Resource resource, byte[] originalBytes) =>
        SerializeModelSingleBlock(resource, originalBytes, BlockType.DATA);

    /// <summary>Generalization of <see cref="SerializeModelDataOnly"/>: re-encode ONLY
    /// <paramref name="keepLive"/> (carrying the caller's KV3 mutation); every other
    /// block is written byte-identical from <paramref name="originalBytes"/>. Used with
    /// <c>BlockType.CTRL</c> for controller edits (e.g. unbinding a mesh's morph block).</summary>
    public static byte[] SerializeModelSingleBlock(Resource resource, byte[] originalBytes, BlockType keepLive) =>
        SerializeModelKeepBlocksLive(resource, originalBytes, keepLive);

    /// <summary>Like <see cref="SerializeModelSingleBlock"/> but keeps SEVERAL block types live
    /// (re-encoded from their parsed state); every other block is written byte-identical from
    /// <paramref name="originalBytes"/>. Used for edits that span e.g. CTRL (mesh name / buffer
    /// descriptors) AND MDAT (draw calls) while leaving MVTX/MIDX/DATA/PHYS/anim untouched.</summary>
    public static byte[] SerializeModelKeepBlocksLive(Resource resource, byte[] originalBytes, params BlockType[] liveTypes)
    {
        var live = new HashSet<BlockType>(liveTypes);
        for (var i = 0; i < resource.Blocks.Count; i++)
        {
            var b = resource.Blocks[i];
            if (live.Contains(b.Type))
                continue;             // re-encodes (carries the edit)
            long off = b.Offset, len = b.Size;
            if (off < 0 || len < 0 || off + len > originalBytes.Length)
                continue; // unexpected - let VRF handle it
            resource.Blocks[i] = new TypedRawBlock(b.Type, originalBytes.AsSpan((int)off, (int)len).ToArray()) { Resource = resource };
        }
        return Serialize(resource);
    }

    /// <summary>
    /// A Block implementation that writes pre-built raw bytes as its serialized form.
    /// Used to embed a custom vtex_c DATA payload into a Resource container without
    /// going through Texture.Serialize() (which throws NotImplementedException).
    /// </summary>
    private sealed class RawDataBlock : Block
    {
        private readonly byte[] _data;

        public RawDataBlock(byte[] data) => _data = data;

        public override BlockType Type => BlockType.DATA;

        public override void Read(BinaryReader reader) { }

        public override void Serialize(Stream stream) => stream.Write(_data);

        public override void WriteText(IndentedTextWriter writer) { }
    }

    /// <summary>
    /// <see cref="ResourceBuilder.RebuildModelRaw"/> for blocks typed with VRF's <see cref="BlockType"/>,
    /// whose value is the block's four-character tag read as a little-endian u32 (an unknown tag
    /// cast to <see cref="BlockType"/> included).
    /// </summary>
    public static byte[] RebuildModelRaw(byte[] template, IReadOnlyList<(BlockType Type, byte[] Bytes)> blocks)
        => ResourceBuilder.RebuildModelRaw(template, [.. blocks.Select(b => (Tag(b.Type), b.Bytes))]);

    /// <summary>The four-character tag of <paramref name="type"/> (<c>BlockType.PHYS</c> is <c>"PHYS"</c>).</summary>
    public static string Tag(BlockType type)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)type);
        return System.Text.Encoding.ASCII.GetString(bytes);
    }

    /// <summary>
    /// A block whose payload is already laid out. The types whose DATA is not KV3
    /// need this, because VRF writes no serializer for them: a texture's pixels, and
    /// a resource manifest's string table.
    /// </summary>
    public static Block RawBlock(BlockType type, byte[] bytes, Resource resource)
        => new TypedRawBlock(type, bytes) { Resource = resource };

    /// <summary>A block written verbatim from captured bytes under an arbitrary
    /// <see cref="BlockType"/> - used by <see cref="SerializeModelDataOnly"/> to
    /// pass PHYS/anim blocks through untouched.</summary>
    private sealed class TypedRawBlock : Block
    {
        private readonly byte[] _data;
        private readonly BlockType _type;

        public TypedRawBlock(BlockType type, byte[] data) { _type = type; _data = data; }

        public override BlockType Type => _type;

        public override void Read(BinaryReader reader) { }

        public override void Serialize(Stream stream) => stream.Write(_data);

        public override void WriteText(IndentedTextWriter writer) { }
    }

    /// <summary>
    /// The resource's bytes in resourcecompiler's layout (<see cref="ResourceFile"/>: blocks padded
    /// with zeros to 16, RERL to 4 with its entry array on an 8-byte boundary), KV3 compression
    /// chosen as binary_auto chooses it.
    /// </summary>
    private static byte[] Layout(Resource resource)
    {
        AuthoredKv3.ChooseCompression(resource);
        var count = resource.Blocks.Count;
        var output = new MemoryStream();
        var header = new byte[16 + 12 * count];
        output.Write(header);
        var table = new List<(BlockType Type, long Offset, long Size)>();
        foreach (var block in resource.Blocks)
        {
            var align = block.Type == BlockType.RERL ? 4 : 16;
            while (output.Position % align != 0)
                output.WriteByte(0);
            var start = output.Position;
            if (block is ResourceExtRefList rerl)
                output.Write(ResourceRefs.Write([.. rerl.ResourceRefInfoList.Select(r => (r.Id, r.Name))], start));
            else
                block.Serialize(output);
            table.Add((block.Type, start, output.Position - start));
        }
        var bytes = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), resource.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)count);
        for (var i = 0; i < count; i++)
        {
            var at = 16 + 12 * i;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), (uint)table[i].Type);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 4), (uint)(table[i].Offset - (at + 4)));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 8), (uint)table[i].Size);
        }
        return bytes;
    }
}
