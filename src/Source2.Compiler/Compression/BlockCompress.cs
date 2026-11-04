using System.Buffers.Binary;

namespace Source2.Compiler.Compression;

/// <summary>
/// tier0's <c>CBlockCompress</c> (exports of tier0.dll, the same code from the 0923 to the 1103 build):
/// an LZSS a particle snapshot's SNAP block is written with (rc 18033fe60).
/// <list type="bullet">
/// <item>A u32 header: the uncompressed size, bit 31 set when the bytes follow stored.</item>
/// <item>Otherwise groups of 16 tokens, each led by a u16 of flags, bit i for token i: clear, one
/// literal byte; set, a u16 back reference, distance - 1 in the top 12 bits and length - 3 in the
/// low 4 (3 to 18 bytes, up to 4096 back).</item>
/// </list>
/// </summary>
public static class BlockCompress
{
    private const int HashSize = 0x800;
    private const int MaxMatch = 0x12;
    private const int MaxDistance = 0x1000;

    /// <summary>
    /// <c>CBlockCompress::FastCompress</c> (tier0 180050840) with an output buffer of
    /// <c>input.Length + 4</c>, which is how rc 18033fe60 calls it.
    /// </summary>
    /// <remarks>
    /// Each position with 3 or more bytes left hashes its first three bytes into a 2048-slot table
    /// (cleared at the start) of the last position that hashed there, and takes the match with that
    /// position when it is before this one, no more than 4096 back and at least 3 long (compared up
    /// to 18 bytes, or the bytes left). The slot takes this position either way. Whenever the
    /// output's write position reaches <c>input.Length - 4</c> the whole input is stored instead.
    /// A last group that is full drops the flag word reserved after it.
    /// </remarks>
    public static byte[] FastCompress(ReadOnlySpan<byte> input)
    {
        var n = input.Length;
        if (n > 0x7ffffffe)
            throw new ArgumentOutOfRangeException(nameof(input), "CBlockCompress takes at most 0x7ffffffe bytes");
        // Room for the first token past the header even when the input is shorter than it: with
        // fewer than five bytes FastCompress writes one token at offset 6 and then stores.
        var output = new byte[Math.Max(n + 4, 8)];
        BinaryPrimitives.WriteUInt32LittleEndian(output, (uint)n);
        if (n == 0)
        {
            output[3] = 0x80;
            return output[..4];
        }

        Span<int> table = stackalloc int[HashSize];
        table.Fill(-1);
        uint flags = 0;
        var bits = 16;
        var flagAt = 4;
        var o = 6;
        var p = 0;
        while (p < n)
        {
            var left = n - p;
            var length = 0;
            var distance = 0;
            if (left >= 3)
            {
                var most = Math.Min(left, MaxMatch);
                var h = Hash(input[p], input[p + 1], input[p + 2]);
                var candidate = table[h];
                table[h] = p;
                if (candidate >= 0 && candidate < p && p - candidate <= MaxDistance)
                {
                    while (length < most && input[p + length] == input[candidate + length])
                        length++;
                    distance = p - candidate;
                }
            }
            if (length >= 3)
            {
                flags |= 0x10000;
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(o), (ushort)(((distance - 1) << 4) | (length - 3)));
                o += 2;
                p += length;
            }
            else
            {
                output[o++] = input[p];
                p++;
            }
            flags >>= 1;
            if (--bits == 0)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(flagAt), (ushort)flags);
                bits = 16;
                flags = 0;
                flagAt = o;
                o += 2;
            }
            // param_3 + (size - 4) <= out: with fewer than four bytes the size wraps and this always holds.
            if (o >= (long)n - 4)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(output, (uint)n | 0x80000000);
                input.CopyTo(output.AsSpan(4));
                return output[..(n + 4)];
            }
        }
        if (bits != 16)
        {
            flags >>= bits;
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(flagAt), (ushort)flags);
            return output[..o];
        }
        return output[..(o - 2)];
    }

    // The three bytes as a little-endian 24-bit value through FastCompress's multiply-xorshift mix.
    private static int Hash(byte a, byte b, byte c)
    {
        var x = (uint)(a | (b << 8) | (c << 16)) * unchecked((uint)-0x3f);
        x ^= x >> 17;
        x = (x * unchecked((uint)-0x1ff)) ^ (x * unchecked((uint)-0x1ff0));
        x = (x * unchecked((uint)-7)) ^ (x * unchecked((uint)-0x1c00));
        return (int)(((x >> 15) ^ x) & 0x7ff);
    }

    /// <summary>
    /// <c>CBlockCompress::FastDecompress</c> (tier0 180050ae0). Returns the decoded bytes; a block that
    /// reads past its end, reaches before the output's start or overruns its size is an
    /// <see cref="InvalidDataException"/> (Valve's returns 0).
    /// </summary>
    public static byte[] FastDecompress(ReadOnlySpan<byte> input)
    {
        if (input.Length < 4)
            throw new InvalidDataException("CBlockCompress block shorter than its header");
        var header = BinaryPrimitives.ReadUInt32LittleEndian(input);
        var size = (int)(header & 0x7fffffff);
        if ((header & 0x80000000) != 0)
        {
            if (size > input.Length - 4)
                throw new InvalidDataException("stored CBlockCompress block shorter than its size");
            return input.Slice(4, size).ToArray();
        }
        if (input.Length < 6)
            throw new InvalidDataException("CBlockCompress block without flags");
        var output = new byte[size];
        var o = 0;
        var p = 6;
        int flags = BinaryPrimitives.ReadUInt16LittleEndian(input[4..]);
        var bits = 16;
        while (o < size)
        {
            if (bits == 0)
            {
                if (p + 2 > input.Length)
                    throw new InvalidDataException("CBlockCompress block ends inside a flag word");
                flags = BinaryPrimitives.ReadUInt16LittleEndian(input[p..]);
                p += 2;
                bits = 16;
            }
            if ((flags & 1) == 0)
            {
                if (p + 1 > input.Length)
                    throw new InvalidDataException("CBlockCompress block ends inside a literal");
                output[o++] = input[p++];
            }
            else
            {
                if (p + 2 > input.Length)
                    throw new InvalidDataException("CBlockCompress block ends inside a reference");
                int word = BinaryPrimitives.ReadUInt16LittleEndian(input[p..]);
                p += 2;
                var distance = (word >> 4) + 1;
                var length = (word & 0xf) + 3;
                if (distance > o || o + length > size)
                    throw new InvalidDataException("CBlockCompress reference outside the output");
                for (var i = 0; i < length; i++, o++)
                    output[o] = output[o - distance];
            }
            flags >>= 1;
            bits--;
        }
        return output;
    }
}
