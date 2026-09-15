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

    /// <summary>Payload size above which RC compresses. Bracketed by measurement to
    /// (252, 279]; 256 is the only round value in that window.</summary>
    internal const int CompressionThreshold = 256;

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
    /// Give each KV3 block the compression RC would have chosen: LZ4 above
    /// <see cref="CompressionThreshold"/> bytes of payload, stored raw below it.
    ///
    /// <para>RC does not compress uniformly, and it does not decide per resource type
    /// either: of two <c>.vdata_c</c> references, one carries DATA uncompressed and the
    /// other LZ4. It is a payload-size cut, and it is sharp. Measured over ~3,600
    /// Valve-compiled v5 blocks (cs2-paint-assets, cs2stock, weaponviewer, and the
    /// RcReference probes): the largest block RC left uncompressed is 252 bytes and the
    /// smallest it compressed is 279, with nothing in between and no uncompressed block
    /// anywhere above 252.</para>
    ///
    /// <para>"Whichever is smaller" is NOT the rule and was measured wrong: LZ4 does
    /// shrink a 109-byte KV3 buffer, yet RC still stores it raw.</para>
    /// </summary>
    internal static void ChooseCompression(Resource resource)
    {
        foreach (var block in resource.Blocks)
        {
            if (block is not BinaryKV3 kv3)
            {
                continue;
            }

            kv3.SerializationCompressionMethod = PayloadBytes(kv3) > CompressionThreshold
                ? KV3BinaryCompressionMethod.Lz4
                : KV3BinaryCompressionMethod.Uncompressed;
        }
    }

    /// <summary>Uncompressed payload size, read back from the block's own v5 header
    /// (offset 48 is <c>buffer1 + buffer2</c>), which is the figure RC's cut is made on.</summary>
    private static int PayloadBytes(BinaryKV3 kv3)
    {
        var restore = kv3.SerializationCompressionMethod;
        try
        {
            kv3.SerializationCompressionMethod = KV3BinaryCompressionMethod.Uncompressed;
            using var ms = new MemoryStream();
            kv3.Serialize(ms);
            var bytes = ms.GetBuffer();
            return ms.Length >= 52 ? BitConverter.ToInt32(bytes, 48) : 0;
        }
        finally
        {
            kv3.SerializationCompressionMethod = restore;
        }
    }
}
