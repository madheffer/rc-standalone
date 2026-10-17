// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// Resource/ResourceTypes/BinaryKV3.cs at d9926fcb with this project's guard patches applied, so the compiler reads KV3 without
// depending on VRF (docs/INDEPENDENCE.md). Changes: no Block base class (the input is the block's
// bytes), Kv3Formats for KV3IDLookup, InvalidDataException for UnexpectedMagicException, and
// the writing half dropped (Kv3V5Writer and Kv3Tree write).
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ValveKeyValue;
using ValveKeyValue.KeyValues3;

namespace Source2.Compiler.Kv3.Reading;

/// <summary>
/// Represents a binary KeyValues3 data block.
/// </summary>
internal sealed partial class Kv3BinaryReader
{
    /// <summary>
    /// Magic number for VKV3 format.
    /// </summary>
    public const int MAGIC0 = 0x03564B56; // VKV3 (3 isn't ascii, it's 0x03)

    /// <summary>
    /// Magic number for KV3 version 1.
    /// </summary>
    public const int MAGIC1 = 0x4B563301; // KV3\x01

    /// <summary>
    /// Magic number for KV3 version 2.
    /// </summary>
    public const int MAGIC2 = 0x4B563302; // KV3\x02

    /// <summary>
    /// Magic number for KV3 version 3.
    /// </summary>
    public const int MAGIC3 = 0x4B563303; // KV3\x03

    /// <summary>
    /// Magic number for KV3 version 4.
    /// </summary>
    public const int MAGIC4 = 0x4B563304; // KV3\x04

    /// <summary>
    /// Magic number for KV3 version 5.
    /// </summary>
    public const int MAGIC5 = 0x4B563305; // KV3\x05

    /// <summary>
    /// Checks if the given magic number represents a binary KV3 format.
    /// </summary>
    /// <param name="magic">The magic number to check.</param>
    /// <returns>True if the magic number is a valid binary KV3 format.</returns>
    public static bool IsBinaryKV3(uint magic) => magic is MAGIC0 or MAGIC1 or MAGIC2 or MAGIC3 or MAGIC4 or MAGIC5;

    /// <summary>
    /// Gets the deserialized KeyValues3 data.
    /// </summary>
    public KVDocument Data { get; private set; } = null!;

    /// <summary>
    /// Gets or sets the binary KV3 version used when serializing this block. Supported values are 4 and 5.
    /// </summary>
    public int SerializationVersion { get; set; } = 5;

    /// <summary>The block's size; the guards measure declared sizes against it.</summary>
    public long Size { get; set; }

    /// <summary>Where the block starts in the reader's stream.</summary>
    public long Offset { get; set; }

    /// <summary>The LZ4 frame size every v2+ block uses.</summary>
    private const int CompressionFrameSize = 16384;

    /// <summary>
    /// Gets or sets the compression method used when serializing this block.
    /// </summary>
    public Kv3Compression SerializationCompressionMethod { get; set; } = Kv3Compression.Uncompressed;

    private class Buffers
    {
        public ArraySegment<byte> Bytes1;
        public ArraySegment<byte> Bytes2;
        public ArraySegment<byte> Bytes4;
        public ArraySegment<byte> Bytes8;
    }

    private class Context
    {
        public int Version;
        // Nesting and value count, against the block's size (Kv3Limits).
        public Kv3Limits.Budget Budget = new(long.MaxValue);
        public ArraySegment<byte> Types;
        public ArraySegment<byte> ObjectLengths;
        public ArraySegment<byte> BinaryBlobs;
        public ArraySegment<byte> BinaryBlobLengths;
        public string[] Strings = null!;
        public Buffers Buffer = null!;
        public Buffers AuxiliaryBuffer = null!;
    }

    /// <summary>Read the block the reader is positioned at (its <see cref="Offset"/>).</summary>
    public void Read(BinaryReader reader)
    {
        var magic = reader.ReadUInt32();

        if (magic == MAGIC0)
        {
            ReadVersion0(reader);
            return;
        }

        var version = magic & 0xFF;
        magic &= 0xFFFFFF00;

        if (magic != 0x4B563300)
        {
            throw new InvalidDataException($"Unsupported KV3 signature: {magic}");
        }

        if (version < 1 || version > 5)
        {
            throw new InvalidDataException($"Unsupported KV3 version: {version}");
        }

        ReadBuffer((int)version, reader);
    }

    internal static void DecompressLZ4(BinaryReader reader, Span<byte> output, int compressedSize)
    {
        var inputBuf = ArrayPool<byte>.Shared.Rent(compressedSize);

        try
        {
            var input = inputBuf.AsSpan(0, compressedSize);
            reader.BaseStream.ReadExactly(input);

            var written = Compression.Lz4.Decode(input, output);

            if (written != output.Length)
            {
                throw new InvalidDataException($"Failed to decompress LZ4 (expected {output.Length} bytes, got {written})");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(inputBuf);
        }
    }

    internal static void DecompressZSTD(ZstdSharp.Decompressor zstdDecompressor, BinaryReader reader, Span<byte> output, int compressedSize)
    {
        var inputBuf = ArrayPool<byte>.Shared.Rent(compressedSize);

        try
        {
            var input = inputBuf.AsSpan(0, compressedSize);
            reader.BaseStream.ReadExactly(input);

            if (!zstdDecompressor.TryUnwrap(input, output, out var written) || output.Length != written)
            {
                throw new InvalidDataException($"Failed to decompress ZSTD (expected {output.Length} bytes, got {written})");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(inputBuf);
        }
    }

    private void ReadBuffer(int version, BinaryReader reader)
    {
        var context = new Context
        {
            Version = version,
        };

        var format = Kv3Formats.GetByValue(new Guid(reader.ReadBytes(16)));

        var compressionMethod = (Kv3Compression)reader.ReadUInt32();

        if (!Enum.IsDefined(compressionMethod))
        {
            throw new InvalidDataException($"Unknown compression method: {(uint)compressionMethod}");
        }

        // Versions before 4 cannot be written, they will be serialized as version 5
        SerializationVersion = version == 4 ? 4 : 5;
        SerializationCompressionMethod = compressionMethod;

        ushort compressionDictionaryId = 0;
        ushort compressionFrameSize = 0;
        var countBytes1 = 0;
        var countBytes4 = 0;
        var countBytes8 = 0;
        var countTypes = 0;
        var countObjects = 0;
        var countArrays = 0;
        var sizeUncompressedTotal = 0;
        var sizeCompressedTotal = 0;
        var countBlocks = 0;
        var sizeBinaryBlobsBytes = 0;

        if (version == 1)
        {
            // Version 1 did not have extra compression data
            countBytes1 = reader.ReadInt32();
            countBytes4 = reader.ReadInt32();
            countBytes8 = reader.ReadInt32();
            sizeUncompressedTotal = reader.ReadInt32();

            sizeCompressedTotal = (int)(Size - (reader.BaseStream.Position - Offset));
        }
        else
        {
            compressionDictionaryId = reader.ReadUInt16();
            compressionFrameSize = reader.ReadUInt16();
            countBytes1 = reader.ReadInt32();
            countBytes4 = reader.ReadInt32();
            countBytes8 = reader.ReadInt32();
            countTypes = reader.ReadInt32();
            countObjects = reader.ReadUInt16();
            countArrays = reader.ReadUInt16();
            sizeUncompressedTotal = reader.ReadInt32();
            sizeCompressedTotal = reader.ReadInt32();
            countBlocks = reader.ReadInt32();
            sizeBinaryBlobsBytes = reader.ReadInt32();
        }

        var countBytes2 = 0;
        var sizeBlockCompressedSizesBytes = 0;

        if (version >= 4)
        {
            countBytes2 = reader.ReadInt32();
            sizeBlockCompressedSizesBytes = reader.ReadInt32();
        }

        var sizeUncompressedBuffer1 = 0;
        var sizeCompressedBuffer1 = 0;
        var sizeUncompressedBuffer2 = 0;
        var sizeCompressedBuffer2 = 0;
        var countBytes1_buffer2 = 0;
        var countBytes2_buffer2 = 0;
        var countBytes4_buffer2 = 0;
        var countBytes8_buffer2 = 0;
        var countObjects_buffer2 = 0;
        var countArrays_buffer2 = 0;

        if (version >= 5)
        {
            sizeUncompressedBuffer1 = reader.ReadInt32();
            sizeCompressedBuffer1 = reader.ReadInt32();
            sizeUncompressedBuffer2 = reader.ReadInt32();
            sizeCompressedBuffer2 = reader.ReadInt32();
            countBytes1_buffer2 = reader.ReadInt32();
            countBytes2_buffer2 = reader.ReadInt32();
            countBytes4_buffer2 = reader.ReadInt32();
            countBytes8_buffer2 = reader.ReadInt32();
            // The node and element counts are only allocation hints, the data itself is self-describing
            var countNodes = reader.ReadInt32();
            countObjects_buffer2 = reader.ReadInt32();
            countArrays_buffer2 = reader.ReadInt32();
            var countArrayElements = reader.ReadInt32();

            Debug.Assert(sizeUncompressedTotal == sizeUncompressedBuffer1 + sizeUncompressedBuffer2);
        }
        else
        {
            sizeCompressedBuffer1 = sizeCompressedTotal;
            sizeUncompressedBuffer1 = sizeUncompressedTotal;
        }

        // Every size above comes from the file: check them against the block before renting
        // buffers sized by them, so a few hundred hostile bytes cannot ask for gigabytes (found by
        // s2c fuzz). The decompressed size also bounds how many values the block may hold.
        {
            var remaining = Size > 0 ? Offset + Size - reader.BaseStream.Position
                : reader.BaseStream.CanSeek ? reader.BaseStream.Length - reader.BaseStream.Position : long.MaxValue;
            var declared = (long)sizeUncompressedBuffer1 + sizeUncompressedBuffer2 + sizeBinaryBlobsBytes;
            var compressed = compressionMethod != Kv3Compression.Uncompressed;
            var stored = !compressed ? declared
                : version >= 5 ? (long)sizeCompressedBuffer1 + sizeCompressedBuffer2 : sizeCompressedTotal;
            Kv3Limits.CheckSizes(declared, stored, remaining, sizeUncompressedBuffer1, sizeUncompressedBuffer2,
                sizeBinaryBlobsBytes, sizeCompressedBuffer1, sizeCompressedBuffer2, sizeCompressedTotal, countBlocks);
            context.Budget = new Kv3Limits.Budget(Kv3Limits.NodeBudget(declared));
        }

        // Before version 5, zstd stores the buffer and the binary blobs as two consecutive frames,
        // and the compressed size covers both, so they are decompressed together
        var sizeDecompressedBuffer1 = version < 5 && compressionMethod == Kv3Compression.Zstd
            ? sizeUncompressedBuffer1 + sizeBinaryBlobsBytes
            : sizeUncompressedBuffer1;
        var buffer1Raw = ArrayPool<byte>.Shared.Rent(sizeDecompressedBuffer1);
        byte[]? buffer2Raw = null;
        byte[]? binaryBlobsRaw = null;
        ZstdSharp.Decompressor? zstdDecompressor = null;

        try
        {
            ArraySegment<byte> bufferWithBinaryBlobSizes = default;

            // Buffer 1
            {
                var buffer1Span = new ArraySegment<byte>(buffer1Raw, 0, sizeUncompressedBuffer1);

                if (compressionMethod == Kv3Compression.Uncompressed)
                {
                    if (compressionDictionaryId != 0)
                    {
                        throw new InvalidDataException($"Unhandled: {compressionDictionaryId}");
                    }

                    if (compressionFrameSize != 0)
                    {
                        throw new InvalidDataException($"Unhandled: {compressionFrameSize}");
                    }

                    if (version >= 5)
                    {
                        Debug.Assert(sizeCompressedBuffer1 == 0);
                    }
                    else
                    {
                        Debug.Assert(sizeCompressedBuffer1 == sizeUncompressedBuffer1);
                    }

                    reader.Read(buffer1Span);
                }
                else if (compressionMethod == Kv3Compression.Lz4)
                {
                    if (compressionDictionaryId != 0)
                    {
                        throw new InvalidDataException($"Unhandled: {compressionDictionaryId}");
                    }

                    if (compressionFrameSize != CompressionFrameSize && version >= 2)
                    {
                        throw new InvalidDataException($"Unhandled: {compressionFrameSize}");
                    }

                    Debug.Assert(sizeCompressedBuffer1 > 0);

                    DecompressLZ4(reader, buffer1Span, sizeCompressedBuffer1);
                }
                else if (compressionMethod == Kv3Compression.Zstd)
                {
                    Debug.Assert(version >= 2);

                    if (compressionDictionaryId != 0)
                    {
                        throw new InvalidDataException($"Unhandled: {compressionDictionaryId}");
                    }

                    if (compressionFrameSize != 0)
                    {
                        throw new InvalidDataException($"Unhandled: {compressionFrameSize}");
                    }

                    Debug.Assert(sizeCompressedBuffer1 > 0);

                    zstdDecompressor = new ZstdSharp.Decompressor();

                    DecompressZSTD(zstdDecompressor, reader, buffer1Raw.AsSpan(0, sizeDecompressedBuffer1), sizeCompressedBuffer1);
                }
                var buffer1 = new Buffers();

                var offset = 0;

                if (countBytes1 > 0)
                {
                    var end = offset + countBytes1;
                    buffer1.Bytes1 = buffer1Span[offset..end];
                    offset = end;
                }

                if (countBytes2 > 0)
                {
                    Align(ref offset, 2);

                    var end = offset + countBytes2 * 2;
                    buffer1.Bytes2 = buffer1Span[offset..end];
                    offset = end;
                }

                if (countBytes4 > 0)
                {
                    Align(ref offset, 4);

                    var end = offset + countBytes4 * 4;
                    buffer1.Bytes4 = buffer1Span[offset..end];
                    offset = end;
                }

                if (countBytes8 > 0)
                {
                    Align(ref offset, 8);

                    var end = offset + countBytes8 * 8;
                    buffer1.Bytes8 = buffer1Span[offset..end];
                    offset = end;
                }
                else if (version < 5)
                {
                    // Before version 5 empty lanes are still aligned, empty 2 and 4 byte lanes are covered by this too
                    Align(ref offset, 8);
                }

                Debug.Assert(countBytes4 > 0); // should be guaranteed to be at least 1 for the strings count

                var countStrings = MemoryMarshal.Read<int>(buffer1.Bytes4);
                buffer1.Bytes4 = buffer1.Bytes4[sizeof(int)..];
                // Each string takes at least its terminator in buffer 1, so a count past that is
                // hostile; check it before allocating the table.
                if (countStrings < 0 || countStrings > buffer1Span.Count)
                {
                    throw Kv3Limits.Hostile($"{countStrings} strings in {buffer1Span.Count} bytes");
                }

                context.Strings = new string[countStrings];

                // Before version 5 there is only one buffer, so auxiliary reads use it as well
                context.AuxiliaryBuffer = buffer1;

                if (version >= 5)
                {
                    var readStringBytes = 0;

                    for (var i = 0; i < countStrings; i++)
                    {
                        context.Strings[i] = ReadNullTermUtf8String(ref buffer1.Bytes1, ref readStringBytes);
                    }

                    Debug.Assert(buffer1Span.Count == offset);
                }
                else
                {
                    context.Buffer = buffer1;

                    var stringsBuffer = buffer1Span[offset..];
                    var stringsStartOffset = offset;

                    for (var i = 0; i < countStrings; i++)
                    {
                        context.Strings[i] = ReadNullTermUtf8String(ref stringsBuffer, ref offset);
                    }

                    // Types before v5
                    int typesLength;

                    if (version == 1)
                    {
                        typesLength = sizeUncompressedTotal - offset - 4;
                    }
                    else
                    {
                        typesLength = countTypes - offset + stringsStartOffset;
                    }

                    context.Types = buffer1Span[offset..(offset + typesLength)];
                    offset += typesLength;

                    if (countBlocks == 0)
                    {
                        var trailer = MemoryMarshal.Read<uint>(buffer1Span[offset..]);
                        offset += 4;
                        if (!(trailer == 0xFFEEDD00)) throw new InvalidDataException($"Unexpected value {trailer}");

                        Debug.Assert(buffer1Span.Count == offset);
                    }
                    else
                    {
                        bufferWithBinaryBlobSizes = buffer1Span[offset..];
                    }
                }
            }

            // Buffer 2
            if (version >= 5)
            {
                buffer2Raw = ArrayPool<byte>.Shared.Rent(sizeUncompressedBuffer2);
                var buffer2Span = new ArraySegment<byte>(buffer2Raw, 0, sizeUncompressedBuffer2);

                if (compressionMethod == Kv3Compression.Uncompressed)
                {
                    Debug.Assert(sizeCompressedBuffer2 == 0);

                    reader.Read(buffer2Span);
                }
                else if (compressionMethod == Kv3Compression.Lz4)
                {
                    Debug.Assert(sizeCompressedBuffer2 > 0);

                    DecompressLZ4(reader, buffer2Span, sizeCompressedBuffer2);
                }
                else if (compressionMethod == Kv3Compression.Zstd)
                {
                    Debug.Assert(sizeCompressedBuffer2 > 0);

                    zstdDecompressor ??= new ZstdSharp.Decompressor();

                    DecompressZSTD(zstdDecompressor, reader, buffer2Span, sizeCompressedBuffer2);
                }
                var buffer2 = new Buffers();
                context.Buffer = buffer2;

                var end = countObjects_buffer2 * sizeof(int);
                var offset = end;

                context.ObjectLengths = buffer2Span[..end];

                if (countBytes1_buffer2 > 0)
                {
                    end = offset + countBytes1_buffer2;
                    buffer2.Bytes1 = buffer2Span[offset..end];
                    offset = end;
                }

                if (countBytes2_buffer2 > 0)
                {
                    Align(ref offset, 2);

                    end = offset + countBytes2_buffer2 * 2;
                    buffer2.Bytes2 = buffer2Span[offset..end];
                    offset = end;
                }

                if (countBytes4_buffer2 > 0)
                {
                    Align(ref offset, 4);

                    end = offset + countBytes4_buffer2 * 4;
                    buffer2.Bytes4 = buffer2Span[offset..end];
                    offset = end;
                }

                if (countBytes8_buffer2 > 0)
                {
                    Align(ref offset, 8);

                    end = offset + countBytes8_buffer2 * 8;
                    buffer2.Bytes8 = buffer2Span[offset..end];
                    offset = end;
                }

                // Types in v5
                context.Types = buffer2Span[offset..(offset + countTypes)];
                offset += countTypes;

                if (countBlocks == 0)
                {
                    var trailer = MemoryMarshal.Read<uint>(buffer2Span[offset..]);
                    offset += 4;
                    if (!(trailer == 0xFFEEDD00)) throw new InvalidDataException($"Unexpected value {trailer}");

                    Debug.Assert(buffer2Span.Count == offset + sizeBlockCompressedSizesBytes);
                }
                else
                {
                    bufferWithBinaryBlobSizes = buffer2Span[offset..];
                }
            }

            if (countBlocks > 0)
            {
                Debug.Assert(version >= 2);
                Debug.Assert(bufferWithBinaryBlobSizes.Array != null);

                {
                    var end = countBlocks * sizeof(int);
                    context.BinaryBlobLengths = bufferWithBinaryBlobSizes[..end];
                    bufferWithBinaryBlobSizes = bufferWithBinaryBlobSizes[end..];

                    var trailer = MemoryMarshal.Read<uint>(bufferWithBinaryBlobSizes);
                    bufferWithBinaryBlobSizes = bufferWithBinaryBlobSizes[sizeof(int)..];
                    if (!(trailer == 0xFFEEDD00)) throw new InvalidDataException($"Unexpected value {trailer}");
                }

                if (compressionMethod == Kv3Compression.Uncompressed)
                {
                    binaryBlobsRaw = ArrayPool<byte>.Shared.Rent(sizeBinaryBlobsBytes);
                    context.BinaryBlobs = new ArraySegment<byte>(binaryBlobsRaw, 0, sizeBinaryBlobsBytes);
                    reader.Read(context.BinaryBlobs);
                }
                else if (compressionMethod == Kv3Compression.Lz4)
                {
                    binaryBlobsRaw = ArrayPool<byte>.Shared.Rent(sizeBinaryBlobsBytes);
                    context.BinaryBlobs = new ArraySegment<byte>(binaryBlobsRaw, 0, sizeBinaryBlobsBytes);

                    // Each blob is split into frames of up to compressionFrameSize bytes, a frame never spans two blobs,
                    // and the frames are chained so that later frames can reference earlier blobs
                    var lz4decoder = new Compression.Lz4ChainDecoder(compressionFrameSize);
                    var inputBuf = ArrayPool<byte>.Shared.Rent(ushort.MaxValue);

                    try
                    {
                        var blobOffset = 0;

                        foreach (var blobLength in MemoryMarshal.Cast<byte, int>(context.BinaryBlobLengths.AsSpan()))
                        {
                            var blobEnd = blobOffset + blobLength;

                            while (blobOffset < blobEnd)
                            {
                                var compressedBlockLength = MemoryMarshal.Read<ushort>(bufferWithBinaryBlobSizes);
                                bufferWithBinaryBlobSizes = bufferWithBinaryBlobSizes[sizeof(ushort)..];

                                var input = inputBuf.AsSpan(0, compressedBlockLength);
                                reader.Read(input);

                                var decoded = lz4decoder.Decode(input, context.BinaryBlobs.AsSpan(blobOffset, blobEnd - blobOffset));
                                if (decoded < 1)
                                {
                                    throw new InvalidDataException("Failed to decompress LZ4 binary blob frame");
                                }

                                blobOffset += decoded;
                            }
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(inputBuf);
                    }

                    Debug.Assert(bufferWithBinaryBlobSizes.Count == 0);
                }
                else if (compressionMethod == Kv3Compression.Zstd)
                {
                    if (version >= 5)
                    {
                        if (!(sizeBlockCompressedSizesBytes == 0)) throw new InvalidDataException($"Unexpected value {sizeBlockCompressedSizesBytes}");

                        var sizeCompressedBinaryBlobs = sizeCompressedTotal - sizeCompressedBuffer1 - sizeCompressedBuffer2;

                        binaryBlobsRaw = ArrayPool<byte>.Shared.Rent(sizeBinaryBlobsBytes);
                        context.BinaryBlobs = new ArraySegment<byte>(binaryBlobsRaw, 0, sizeBinaryBlobsBytes);

                        zstdDecompressor ??= new ZstdSharp.Decompressor();

                        DecompressZSTD(zstdDecompressor, reader, context.BinaryBlobs, sizeCompressedBinaryBlobs);
                    }
                    else
                    {
                        // The blob frame was already decompressed together with the buffer frame above
                        context.BinaryBlobs = new ArraySegment<byte>(buffer1Raw, sizeUncompressedBuffer1, sizeBinaryBlobsBytes);
                    }
                }
                {
                    var trailer = reader.ReadUInt32();
                    if (!(trailer == 0xFFEEDD00)) throw new InvalidDataException($"Unexpected value {trailer}");
                }
            }

            var (rootType, rootFlag) = ReadType(context);
            var root = ReadBinaryValue(context, rootType, rootFlag, context.Buffer);
            Data = new KVDocument(new KVHeader { Format = format }, null, root);

            Debug.Assert(context.Types.Count == 0);
            Debug.Assert(context.ObjectLengths.Count == 0);
            Debug.Assert(context.BinaryBlobs.Count == 0);
            Debug.Assert(context.BinaryBlobLengths.Count == 0);
            Debug.Assert(context.Buffer.Bytes1.Count == 0);
            Debug.Assert(context.Buffer.Bytes2.Count == 0);
            Debug.Assert(context.Buffer.Bytes4.Count == 0);
            Debug.Assert(context.Buffer.Bytes8.Count == 0);

        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer1Raw);

            if (buffer2Raw != null)
            {
                ArrayPool<byte>.Shared.Return(buffer2Raw);
            }

            if (binaryBlobsRaw != null)
            {
                ArrayPool<byte>.Shared.Return(binaryBlobsRaw);
            }

            zstdDecompressor?.Dispose();
        }
    }

    private static (Kv3Type Type, KVFlag Flag) ReadType(Context context)
    {
        var databyte = context.Types[0];
        context.Types = context.Types[1..];
        var flagInfo = KVFlag.None;

        if (context.Version >= 3)
        {
            if ((databyte & 0x80) != 0)
            {
                flagInfo = (KVFlag)context.Types[0];
                context.Types = context.Types[1..];

                if (flagInfo == KVFlag.None || !Enum.IsDefined(flagInfo))
                {
                    throw new InvalidDataException($"Unexpected kv3 flag: {(int)flagInfo}");
                }
            }

            // No known writer sets this bit, readers skip the extra byte that follows
            if ((databyte & 0x40) != 0)
            {
                context.Types = context.Types[1..];
            }

            databyte &= 0x3F;
        }
        else
        {
            if ((databyte & 0x80) != 0)
            {
                flagInfo = ConvertLegacyFlags(context.Types[0]);
                context.Types = context.Types[1..];
            }

            databyte &= 0x7F;
        }

        // Flags are not validated against the value type: subclass is only valid on objects,
        // and every other flag is only valid on strings
        return ((Kv3Type)databyte, flagInfo);
    }

    private static string GetString(Context context, int id)
    {
        // Negative and out of range ids, including -1 for an empty string, resolve to an empty string
        return (uint)id < (uint)context.Strings.Length ? context.Strings[id] : string.Empty;
    }

    private static void ParseBinaryKV3(Context context, KVObject parent)
    {
        var (datatype, flagInfo) = ReadType(context);

        if (parent.IsArray)
        {
            parent.Add(ReadBinaryValue(context, datatype, flagInfo, context.Buffer));
            return;
        }

        var name = GetString(context, ReadLane<int>(ref context.Buffer.Bytes4));

        // A repeated member name replaces the earlier value
        parent[name] = ReadBinaryValue(context, datatype, flagInfo, context.Buffer);
    }

    private static T ReadLane<T>(ref ArraySegment<byte> lane) where T : unmanaged
    {
        var value = MemoryMarshal.Read<T>(lane);
        lane = lane[Unsafe.SizeOf<T>()..];
        return value;
    }

    private static KVObject ReadBinaryValue(Context context, Kv3Type datatype, KVFlag flagInfo, Buffers lane)
    {
        // Nesting goes through here alone, so one budget caps both the call-stack depth of a
        // deeply nested buffer (a stack overflow ends the process where no catch can stop it) and
        // the values a zero-width typed array could otherwise claim by the billion.
        context.Budget.Enter();
        try
        {
            var result = ReadValue(context, datatype, lane);

            if (flagInfo != KVFlag.None)
            {
                result.Flag = flagInfo;
            }

            return result;
        }
        finally
        {
            context.Budget.Leave();
        }
    }

    // Primitive values are read from the given lane, which is the auxiliary buffer for elements of
    // ARRAY_TYPE_AUXILIARY_BUFFER. Strings, blobs and container lengths always come from the main buffer.
    private static KVObject ReadValue(Context context, Kv3Type datatype, Buffers lane)
    {
        var buffer = context.Buffer;

        switch (datatype)
        {
            // Hardcoded values
            case Kv3Type.Null:
                return KVObject.Null();
            case Kv3Type.BooleanTrue:
                return true;
            case Kv3Type.BooleanFalse:
                return false;
            case Kv3Type.Int64Zero:
                return 0L;
            case Kv3Type.Int64One:
                return 1L;
            case Kv3Type.DoubleZero:
                return 0.0D;
            case Kv3Type.DoubleOne:
                return 1.0D;

            // 1 byte values
            case Kv3Type.Boolean:
                return ReadLane<byte>(ref lane.Bytes1) != 0;
            case Kv3Type.Int8:
            {
                Debug.Assert(context.Version >= 4);
                // KVValueType has no 8-bit types; the width is recorded so a write keeps it.
                var int8 = new KVObject((int)ReadLane<sbyte>(ref lane.Bytes1));
                Kv3WireTypes.Set(int8, Kv3WireTypes.Int8);
                return int8;
            }
            case Kv3Type.UInt8:
            {
                Debug.Assert(context.Version >= 4);
                var uint8 = new KVObject((uint)ReadLane<byte>(ref lane.Bytes1));
                Kv3WireTypes.Set(uint8, Kv3WireTypes.UInt8);
                return uint8;
            }

            // 2 byte values
            case Kv3Type.Int16:
                Debug.Assert(context.Version >= 4);
                return ReadLane<short>(ref lane.Bytes2);
            case Kv3Type.UInt16:
                Debug.Assert(context.Version >= 4);
                return ReadLane<ushort>(ref lane.Bytes2);

            // 4 byte values
            case Kv3Type.Int32:
                return ReadLane<int>(ref lane.Bytes4);
            case Kv3Type.UInt32:
                return ReadLane<uint>(ref lane.Bytes4);
            case Kv3Type.Float:
                Debug.Assert(context.Version >= 4);
                return ReadLane<float>(ref lane.Bytes4);

            // 8 byte values
            case Kv3Type.Int64:
                return ReadLane<long>(ref lane.Bytes8);
            case Kv3Type.UInt64:
                return ReadLane<ulong>(ref lane.Bytes8);
            case Kv3Type.Double:
                return ReadLane<double>(ref lane.Bytes8);

            // Custom types
            case Kv3Type.String:
                return GetString(context, ReadLane<int>(ref buffer.Bytes4));
            case Kv3Type.BinaryBlob when context.Version < 2:
            {
                var blockLength = ReadLane<int>(ref buffer.Bytes4);
                byte[] output;

                if (blockLength > 0)
                {
                    output = [.. buffer.Bytes1[..blockLength]]; // explicit copy
                    buffer.Bytes1 = buffer.Bytes1[blockLength..];
                }
                else
                {
                    output = [];
                }

                return output;
            }
            case Kv3Type.BinaryBlob:
            {
                var blockLength = ReadLane<int>(ref context.BinaryBlobLengths);
                byte[] output;

                if (blockLength > 0)
                {
                    output = [.. context.BinaryBlobs[..blockLength]]; // explicit copy
                    context.BinaryBlobs = context.BinaryBlobs[blockLength..];
                }
                else
                {
                    output = [];
                }

                return output;
            }
            case Kv3Type.Array:
            {
                var arrayLength = ReadLane<int>(ref buffer.Bytes4);
                var array = KVObject.Array(Kv3Limits.Capacity(arrayLength));

                for (var i = 0; i < arrayLength; i++)
                {
                    ParseBinaryKV3(context, array);
                }

                return array;
            }
            case Kv3Type.ArrayTyped:
            case Kv3Type.ArrayTypeByteLength:
            case Kv3Type.ArrayTypeAuxiliaryBuffer:
            {
                var arrayLength = datatype == Kv3Type.ArrayTyped
                    ? ReadLane<int>(ref buffer.Bytes4)
                    : ReadLane<byte>(ref buffer.Bytes1);

                if (arrayLength == 0 && context.Version >= 2)
                {
                    throw new InvalidDataException("Typed KV3 arrays can not be empty");
                }

                var (subType, subFlagInfo) = ReadType(context);
                var typedArray = KVObject.Array(Kv3Limits.Capacity(arrayLength));
                var elementLane = datatype == Kv3Type.ArrayTypeAuxiliaryBuffer ? context.AuxiliaryBuffer : buffer;

                for (var i = 0; i < arrayLength; i++)
                {
                    typedArray.Add(ReadBinaryValue(context, subType, subFlagInfo, elementLane));
                }

                return typedArray;
            }

            case Kv3Type.Object:
            {
                var objectLength = context.Version >= 5
                    ? ReadLane<int>(ref context.ObjectLengths)
                    : ReadLane<int>(ref buffer.Bytes4);
                var newObject = KVObject.Collection(Kv3Limits.Capacity(objectLength));

                for (var i = 0; i < objectLength; i++)
                {
                    ParseBinaryKV3(context, newObject);
                }

                return newObject;
            }
            default:
                throw new InvalidDataException($"Unknown KVType: {(int)datatype}");
        }
    }

    private static string ReadNullTermUtf8String(ref ArraySegment<byte> buffer, ref int offset)
    {
        var nullByte = buffer.AsSpan().IndexOf((byte)0);
        if (nullByte < 0)
        {
            throw new InvalidDataException("A KV3 string table entry has no terminator.");
        }
        var str = buffer[..nullByte];
        buffer = buffer[(nullByte + 1)..];

        offset += nullByte + 1;

        return System.Text.Encoding.UTF8.GetString(str);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Align(ref int offset, int alignment)
    {
        offset = (offset + alignment - 1) / alignment * alignment;
    }

}
