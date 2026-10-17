// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// Resource/ResourceTypes/BinaryKV3.Legacy.cs at d9926fcb with this project's guard patches applied, so the compiler reads KV3 without
// depending on VRF (docs/INDEPENDENCE.md). Changes: no Block base class (the input is the block's
// bytes), Kv3Formats for KV3IDLookup, InvalidDataException for UnexpectedMagicException, and
// the writing half dropped (Kv3V5Writer and Kv3Tree write).
using System.Buffers;
using System.IO;
using ValveKeyValue;

namespace Source2.Compiler.Kv3.Reading;

internal sealed partial class Kv3BinaryReader
{
    private void ReadVersion0(BinaryReader reader)
    {
        var context = new Context
        {
            Version = 0,
        };

        var encoding = Kv3Formats.GetByValue(new Guid(reader.ReadBytes(16)));
        var format = Kv3Formats.GetByValue(new Guid(reader.ReadBytes(16)));

        // Valve's implementation lives in LoadKV3Binary()
        // KV3_ENCODING_BINARY_BLOCK_COMPRESSED calls CBlockCompress::FastDecompress()
        // and then it proceeds to call LoadKV3BinaryUncompressed, which should be the same routine for KV3_ENCODING_BINARY_UNCOMPRESSED
        // Old binary with debug symbols for ref: https://users.alliedmods.net/~asherkin/public/bins/dota_symbols/bin/osx64/libmeshsystem.dylib

        byte[]? outputBuf = null;

        try
        {
            int outBufferLength;

            // What is left of the block bounds every size this header declares (Kv3Limits).
            long Remaining() => Size > 0 ? Offset + Size - reader.BaseStream.Position : reader.BaseStream.Length - reader.BaseStream.Position;

            if (encoding == Kv3Formats.Get("binary_bc"))
            {
                var info = BlockCompress.GetDecompressedSize(reader);
                outBufferLength = info.Size;
                Kv3Limits.CheckSizes(outBufferLength, 0, Remaining(), outBufferLength);
                outputBuf = ArrayPool<byte>.Shared.Rent(outBufferLength);

                BlockCompress.FastDecompress(info, reader, outputBuf.AsSpan(0, outBufferLength));
            }
            else if (encoding == Kv3Formats.Get("binary_lz4"))
            {
                outBufferLength = reader.ReadInt32();
                var compressedSize = (int)Remaining();
                Kv3Limits.CheckSizes(outBufferLength, compressedSize, Remaining(), outBufferLength);
                outputBuf = ArrayPool<byte>.Shared.Rent(outBufferLength);
                DecompressLZ4(reader, outputBuf.AsSpan(0, outBufferLength), compressedSize);
            }
            else if (encoding == Kv3Formats.Get("binary") || encoding == Kv3Formats.Get("binary_zstd") || encoding == Kv3Formats.Get("binary_auto"))
            {
                // Only block compression and LZ4 were ever implemented for this header, other binary encodings are stored as is
                outBufferLength = (int)Remaining();
                outputBuf = ArrayPool<byte>.Shared.Rent(outBufferLength);
                reader.BaseStream.ReadExactly(outputBuf.AsSpan(0, outBufferLength));
            }
            else
            {
                throw new InvalidDataException($"Unrecognised KV3 Encoding: {encoding.ToString()}");
            }

            using var outStream = new MemoryStream(outputBuf, 0, outBufferLength);
            using var outRead = new BinaryReader(outStream, System.Text.Encoding.UTF8, true);

            context.Budget = new Kv3Limits.Budget(Kv3Limits.NodeBudget(outBufferLength));
            var stringCount = outRead.ReadUInt32();
            // Each string takes at least its terminator.
            if (stringCount > outBufferLength)
            {
                throw Kv3Limits.Hostile($"{stringCount} strings in {outBufferLength} bytes");
            }
            context.Strings = new string[stringCount];

            for (var i = 0; i < stringCount; i++)
            {
                context.Strings[i] = ReadNullTermUtf8(outRead);
            }

            var (rootType, rootFlag) = LegacyReadType(outRead);
            var root = LegacyReadBinaryValue(context, rootType, rootFlag, outRead);

            // Do not set encoding on the header here
            Data = new KVDocument(new KVHeader { Format = format }, null, root);

            var trailer = outRead.ReadUInt32();
            if (trailer != 0xFFFFFFFF)
            {
                throw new InvalidDataException($"Invalid trailer: {trailer}");
            }
        }
        finally
        {
            if (outputBuf != null)
            {
                ArrayPool<byte>.Shared.Return(outputBuf);
            }
        }
    }

    private static (Kv3Type Type, KVFlag Flag) LegacyReadType(BinaryReader reader)
    {
        var databyte = reader.ReadByte();
        var flagInfo = KVFlag.None;

        if ((databyte & 0x80) != 0)
        {
            flagInfo = ConvertLegacyFlags(reader.ReadByte());
        }

        return ((Kv3Type)(databyte & 0x7F), flagInfo);
    }

    // Before version 3 flags were a bit field
    private static KVFlag ConvertLegacyFlags(byte flags)
    {
        if ((flags & 0x80) != 0)
        {
            throw new InvalidDataException($"Unexpected kv3 flag: {flags}");
        }

        // Only one flag can be stored, the lowest set bit wins. Bit 4 marked multiline strings and is dropped.
        if ((flags & 1) != 0)
        {
            return KVFlag.Resource;
        }

        if ((flags & 2) != 0)
        {
            return KVFlag.ResourceName;
        }

        if ((flags & 8) != 0)
        {
            return KVFlag.Panorama;
        }

        if ((flags & 16) != 0)
        {
            return KVFlag.SoundEvent;
        }

        if ((flags & 32) != 0)
        {
            return KVFlag.SubClass;
        }

        return KVFlag.None;
    }

    private static void LegacyParseBinaryKV3(Context context, BinaryReader reader, KVObject parent)
    {
        string? name = null;

        if (!parent.IsArray)
        {
            name = GetString(context, reader.ReadInt32());
        }

        var (datatype, flagInfo) = LegacyReadType(reader);
        var result = LegacyReadBinaryValue(context, datatype, flagInfo, reader);

        if (name != null)
        {
            // A repeated member name replaces the earlier value
            parent[name] = result;
        }
        else
        {
            parent.Add(result);
        }
    }

    private static KVObject LegacyReadBinaryValue(Context context, Kv3Type datatype, KVFlag flagInfo, BinaryReader reader)
    {
        context.Budget.Enter();
        try
        {
            var result = LegacyReadValue(context, datatype, reader);

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

    private static KVObject LegacyReadValue(Context context, Kv3Type datatype, BinaryReader reader)
    {
        switch (datatype)
        {
            case Kv3Type.Null:
                return KVObject.Null();
            case Kv3Type.Boolean:
                return reader.ReadBoolean();
            case Kv3Type.BooleanTrue:
                return true;
            case Kv3Type.BooleanFalse:
                return false;
            case Kv3Type.Int64Zero:
                return 0L;
            case Kv3Type.Int64One:
                return 1L;
            case Kv3Type.Int64:
                return reader.ReadInt64();
            case Kv3Type.UInt64:
                return reader.ReadUInt64();
            case Kv3Type.Int32:
                return reader.ReadInt32();
            case Kv3Type.UInt32:
                return reader.ReadUInt32();
            case Kv3Type.Double:
                return reader.ReadDouble();
            case Kv3Type.DoubleZero:
                return 0.0D;
            case Kv3Type.DoubleOne:
                return 1.0D;
            case Kv3Type.String:
                return GetString(context, reader.ReadInt32());
            case Kv3Type.BinaryBlob:
                var length = reader.ReadInt32();
                if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
                {
                    throw new InvalidDataException($"A legacy KV3 blob claims {length} bytes past the end of its block.");
                }
                return reader.ReadBytes(length);
            case Kv3Type.Array:
                var arrayLength = reader.ReadInt32();
                var array = KVObject.Array(Kv3Limits.Capacity(arrayLength));

                for (var i = 0; i < arrayLength; i++)
                {
                    LegacyParseBinaryKV3(context, reader, array);
                }

                return array;
            case Kv3Type.ArrayTyped:
                var typeArrayLength = reader.ReadInt32();
                var (subType, subFlagInfo) = LegacyReadType(reader);
                var typedArray = KVObject.Array(Kv3Limits.Capacity(typeArrayLength));

                for (var i = 0; i < typeArrayLength; i++)
                {
                    typedArray.Add(LegacyReadBinaryValue(context, subType, subFlagInfo, reader));
                }

                return typedArray;
            case Kv3Type.Object:
                var objectLength = reader.ReadInt32();
                var newObject = KVObject.Collection(Kv3Limits.Capacity(objectLength));

                for (var i = 0; i < objectLength; i++)
                {
                    LegacyParseBinaryKV3(context, reader, newObject);
                }

                return newObject;
            default:
                throw new InvalidDataException($"Unknown KV3 type {(int)datatype} at byte {reader.BaseStream.Position}");
        }
    }

    // A NUL-terminated UTF-8 string (VRF's StreamHelpers.ReadNullTermString), bounded by the stream.
    private static string ReadNullTermUtf8(BinaryReader reader)
    {
        using var ms = new MemoryStream(32);
        int b;
        while ((b = reader.BaseStream.ReadByte()) > 0)
            ms.WriteByte((byte)b);
        if (b < 0)
            throw new InvalidDataException("Unterminated string in a legacy KV3 block");
        return System.Text.Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }
}
