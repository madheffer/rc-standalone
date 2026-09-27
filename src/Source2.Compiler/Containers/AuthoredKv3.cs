using ValveKeyValue;
using ValveKeyValue.KeyValues3;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler;

/// <summary>
/// Builds KV3 blocks with the wire settings CS2 expects, and picks each block's
/// compression the way Valve's resourcecompiler does.
///
/// <para>Version 5 is what RC emits: measured on stock <c>sleeve_fbi.vmat_c</c>,
/// <c>glove_bloodhound.vmdl_c</c> and every reference output under
/// <c>tests/.../RcReference</c>. VRF's own default is Uncompressed, which CS2's
/// material loader rejects ("attempting to render with error material"), so a block
/// left at the defaults ships a crash nothing but the game will show.</para>
/// </summary>
internal static class AuthoredKv3
{
    /// <summary>Serialization version Valve emits. Stock vmat_c and vmdl_c are both v5.</summary>
    internal const int Version = 5;

    /// <summary>
    /// tier0's binary_auto save (1800c7220 via SaveKV3): below this many bytes
    /// of buffer plus blobs the block is stored raw (1800c8790 / 1800c8d80).
    /// </summary>
    internal const int RawBelow = 0x100;

    /// <summary>binary_auto picks LZ4 while buffer plus blobs is below this, Zstd from it on.</summary>
    internal const int ZstdFrom = 0x80001;

    /// <summary>A KV3 block carrying <paramref name="data"/>, wired to <paramref name="resource"/>.
    /// Compression is chosen later by <see cref="ChooseCompression"/>, once the block can be
    /// measured.</summary>
    internal static BinaryKV3 Block(KVObject data, KV3ID format, BlockType blockType, Resource resource) =>
        new(data, format, blockType)
        {
            Resource = resource,
            SerializationVersion = Version,
            SerializationCompressionMethod = KV3BinaryCompressionMethod.Lz4,
        };

    /// <summary>
    /// Give each KV3 block the compression resourcecompiler's binary_auto
    /// encoding gives it (tier0 1800c7220): with the buffer (trailer included)
    /// plus the blobs under 256 bytes the block is raw, under 0x80001 LZ4,
    /// otherwise Zstd. Most resource block writers save with binary_auto (the
    /// RED2 writer, 181c24a70, among them); a few use plain "binary" or
    /// "binary_bc", not mapped to resource types yet (docs/GROUND_TRUTH.md).
    /// </summary>
    internal static void ChooseCompression(Resource resource)
    {
        foreach (var block in resource.Blocks)
        {
            if (block is not BinaryKV3 kv3)
                continue;
            var total = TotalPayloadBytes(kv3);
            kv3.SerializationCompressionMethod = total < RawBelow ? KV3BinaryCompressionMethod.Uncompressed
                : total < ZstdFrom ? KV3BinaryCompressionMethod.Lz4
                : KV3BinaryCompressionMethod.Zstd;
        }
    }

    /// <summary>
    /// The block's whole uncompressed payload, buffer plus blobs, read back from
    /// its v5 header (offset 48 is the buffer with its trailer, offset 60 the
    /// blobs): the sum binary_auto decides on.
    /// </summary>
    internal static int TotalPayloadBytes(BinaryKV3 kv3)
    {
        var restore = kv3.SerializationCompressionMethod;
        try
        {
            kv3.SerializationCompressionMethod = KV3BinaryCompressionMethod.Uncompressed;
            using var ms = new MemoryStream();
            kv3.Serialize(ms);
            var bytes = ms.GetBuffer();
            return ms.Length >= 64 ? BitConverter.ToInt32(bytes, 48) + BitConverter.ToInt32(bytes, 60) : 0;
        }
        finally
        {
            kv3.SerializationCompressionMethod = restore;
        }
    }
}
