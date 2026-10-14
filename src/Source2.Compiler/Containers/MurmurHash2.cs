using System.Buffers.Binary;

namespace Source2.Compiler;

/// <summary>
/// MurmurHash2 (Austin Appleby), as tier0 and resourcecompiler compute it: the 32-bit hash behind
/// string tokens (a shader parameter's name, seed 0x31415926; a generated texture's logical CRC, seed
/// 0x24871466) and the 64-bit "B" variant behind resource ids (<see cref="Source2ResourceId"/>).
/// </summary>
public static class MurmurHash2
{
    private const uint M = 0x5bd1e995;

    /// <summary>The 32-bit hash of <paramref name="data"/>.</summary>
    public static uint Hash32(ReadOnlySpan<byte> data, uint seed)
    {
        uint h = seed ^ (uint)data.Length;
        int i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            uint k = BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
            k *= M;
            k ^= k >> 24;
            k *= M;
            h *= M;
            h ^= k;
        }
        switch (data.Length - i)
        {
            case 3: h ^= (uint)data[i + 2] << 16; goto case 2;
            case 2: h ^= (uint)data[i + 1] << 8; goto case 1;
            case 1: h ^= data[i]; h *= M; break;
        }
        h ^= h >> 13;
        h *= M;
        h ^= h >> 15;
        return h;
    }

    /// <summary>The 64-bit hash of <paramref name="data"/>, "B" variant (two 32-bit halves).</summary>
    public static ulong Hash64B(ReadOnlySpan<byte> data, ulong seed)
    {
        uint h1 = (uint)seed ^ (uint)data.Length;
        uint h2 = (uint)(seed >> 32);
        int i = 0, len = data.Length;

        static uint Mix(uint k)
        {
            k *= M;
            k ^= k >> 24;
            return k * M;
        }

        for (; len >= 8; i += 8, len -= 8)
        {
            h1 = (h1 * M) ^ Mix(BinaryPrimitives.ReadUInt32LittleEndian(data[i..]));
            h2 = (h2 * M) ^ Mix(BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 4)..]));
        }
        if (len >= 4)
        {
            h1 = (h1 * M) ^ Mix(BinaryPrimitives.ReadUInt32LittleEndian(data[i..]));
            i += 4;
            len -= 4;
        }
        switch (len)
        {
            case 3: h2 ^= (uint)data[i + 2] << 16; goto case 2;
            case 2: h2 ^= (uint)data[i + 1] << 8; goto case 1;
            case 1: h2 ^= data[i]; h2 *= M; break;
        }

        h1 ^= h2 >> 18;
        h1 *= M;
        h2 ^= h1 >> 22;
        h2 *= M;
        h1 ^= h2 >> 17;
        h1 *= M;
        h2 ^= h1 >> 19;
        h2 *= M;
        return ((ulong)h1 << 32) | h2;
    }
}
