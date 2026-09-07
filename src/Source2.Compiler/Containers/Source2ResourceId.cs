using System.Text;

namespace Source2.Compiler;

/// <summary>
/// Computes the 64-bit resource identifier Source 2 stores in a compiled
/// resource's RERL block (<c>ResourceReferenceInfo.Id</c>) for every external
/// reference.
///
/// <para>Reverse-engineered 2026-05-15 by brute-forcing 25 known
/// (path, Id) pairs lifted from stock CS2 <c>vmat_c</c>/<c>vmdl_c</c> RERL
/// blocks. The function is <b>MurmurHash64B</b> over the lowercase resource
/// path — forward slashes, and the <em>uncompiled</em> extension as RERL
/// stores it (<c>.vtex</c> / <c>.vmat</c>, never <c>.vtex_c</c>) — with seed
/// <c>0xEDABCDEF</c>. All 25 pairs matched exactly.</para>
///
/// <para>This is what lets <see cref="ResourceBuilder"/> emit a working
/// <c>.vmat_c</c> that references brand-new texture paths without routing
/// through Valve's <c>resourcecompiler.exe</c> — the engine looks resources up
/// by this 64-bit id and FATAL-errors on a wrong/zero one.</para>
/// </summary>
public static class Source2ResourceId
{
    private const ulong Seed = 0xEDABCDEF;

    /// <summary>
    /// The RERL id for a resource path. The path is lowercased and slash-
    /// normalized; a trailing compiled <c>_c</c> suffix is stripped (RERL
    /// stores the uncompiled extension).
    /// </summary>
    public static ulong ForPath(string resourcePath)
    {
        var p = resourcePath.Replace('\\', '/').ToLowerInvariant();
        if (p.EndsWith("_c", StringComparison.Ordinal))
            p = p[..^2];
        return MurmurHash64B(Encoding.UTF8.GetBytes(p), Seed);
    }

    /// <summary>MurmurHash2 64-bit ("B" variant, 32-bit operations).</summary>
    private static ulong MurmurHash64B(ReadOnlySpan<byte> data, ulong seed)
    {
        const uint m = 0x5bd1e995;
        const int r = 24;

        uint h1 = (uint)seed ^ (uint)data.Length;
        uint h2 = (uint)(seed >> 32);
        int len = data.Length, i = 0;

        while (len >= 8)
        {
            uint k1 = BitConverter.ToUInt32(data.Slice(i, 4)); i += 4; len -= 4;
            k1 *= m; k1 ^= k1 >> r; k1 *= m; h1 *= m; h1 ^= k1;

            uint k2 = BitConverter.ToUInt32(data.Slice(i, 4)); i += 4; len -= 4;
            k2 *= m; k2 ^= k2 >> r; k2 *= m; h2 *= m; h2 ^= k2;
        }

        if (len >= 4)
        {
            uint k1 = BitConverter.ToUInt32(data.Slice(i, 4)); i += 4; len -= 4;
            k1 *= m; k1 ^= k1 >> r; k1 *= m; h1 *= m; h1 ^= k1;
        }

        switch (len)
        {
            case 3: h2 ^= (uint)data[i + 2] << 16; goto case 2;
            case 2: h2 ^= (uint)data[i + 1] << 8;  goto case 1;
            case 1: h2 ^= data[i]; h2 *= m; break;
        }

        h1 ^= h2 >> 18; h1 *= m;
        h2 ^= h1 >> 22; h2 *= m;
        h1 ^= h2 >> 17; h1 *= m;
        h2 ^= h1 >> 19; h2 *= m;

        return ((ulong)h1 << 32) | h2;
    }
}
