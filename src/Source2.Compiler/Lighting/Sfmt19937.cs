namespace Source2.Compiler.Lighting;

/// <summary>
/// SFMT-19937 as resourcecompiler's lightmap packer draws its debug chart
/// colours (0923 181c473c0 seed, 181c46e60 refill): 156 128-bit words, POS1
/// 122, shifts SL1 18 / SR1 11 / SL2 and SR2 one byte, masks 1fffef 1ecb7f
/// 1affff 1ffff6, parity 1 0 0 13c9e684. The packer reads the state words
/// themselves, 624 at a time, without tempering.
/// </summary>
internal sealed class Sfmt19937
{
    private const int N32 = 624;
    private readonly uint[] _s = new uint[N32];
    private int _index;

    public Sfmt19937(uint seed)
    {
        _s[0] = seed;
        for (var i = 1; i < N32; i++)
            _s[i] = (uint)(1812433253u * (_s[i - 1] ^ (_s[i - 1] >> 30)) + (uint)i);
        _index = N32;
        // Period certification against the parity words.
        uint[] parity = [1u, 0u, 0u, 0x13c9e684u];
        var inner = 0u;
        for (var i = 0; i < 4; i++)
            inner ^= _s[i] & parity[i];
        for (var i = 16; i > 0; i >>= 1)
            inner ^= inner >> i;
        if ((inner & 1) == 1)
            return;
        for (var i = 0; i < 4; i++)
        {
            var work = 1u;
            for (var j = 0; j < 32; j++, work <<= 1)
                if ((work & parity[i]) != 0)
                {
                    _s[i] ^= work;
                    return;
                }
        }
    }

    public uint Next()
    {
        if (_index > N32 - 1)
        {
            Refill();
            _index = 0;
        }
        return _s[_index++];
    }

    private void Refill()
    {
        const int n = 156, pos1 = 122;
        Span<uint> r1 = stackalloc uint[4], r2 = stackalloc uint[4], o = stackalloc uint[4];
        _s.AsSpan((n - 2) * 4, 4).CopyTo(r1);
        _s.AsSpan((n - 1) * 4, 4).CopyTo(r2);
        for (var i = 0; i < n; i++)
        {
            var b = (i + pos1) % n;
            Recursion(o, _s.AsSpan(i * 4, 4), _s.AsSpan(b * 4, 4), r1, r2);
            o.CopyTo(_s.AsSpan(i * 4, 4));
            r2.CopyTo(r1);
            o.CopyTo(r2);
        }
    }

    private static void Recursion(Span<uint> r, ReadOnlySpan<uint> a, ReadOnlySpan<uint> b, ReadOnlySpan<uint> c, ReadOnlySpan<uint> d)
    {
        // x = a << 8 bits (128-bit), y = c >> 8 bits (128-bit).
        var ah = ((ulong)a[3] << 32) | a[2];
        var al = ((ulong)a[1] << 32) | a[0];
        var xh = (ah << 8) | (al >> 56);
        var xl = al << 8;
        var ch = ((ulong)c[3] << 32) | c[2];
        var cl = ((ulong)c[1] << 32) | c[0];
        var yl = (cl >> 8) | (ch << 56);
        var yh = ch >> 8;
        uint[] mask = [0xdfffffefu, 0xddfecb7fu, 0xbffaffffu, 0xbffffff6u];
        uint X(int k) => (uint)(k < 2 ? xl >> (32 * k) : xh >> (32 * (k - 2)));
        uint Y(int k) => (uint)(k < 2 ? yl >> (32 * k) : yh >> (32 * (k - 2)));
        for (var k = 0; k < 4; k++)
            r[k] = a[k] ^ X(k) ^ ((b[k] >> 11) & mask[k]) ^ Y(k) ^ (d[k] << 18);
    }
}
