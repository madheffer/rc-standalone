namespace Source2.Compiler.Lighting.Bake;

/// <summary>
/// The random generator vrad3's light probe volume passes draw their cell
/// offsets from: SFMT-19937 (Saito and Matsumoto's SIMD-oriented Fast Mersenne
/// Twister, the reference parameters), 32-bit output. vrad3's init (1801ff9a0)
/// is init_gen_rand (1812433253 recurrence over 624 words, then the period
/// certification with parity 1, 0, 0, 0x13c9e684); its refill (1801ff440) is
/// gen_rand_all with SL1 18, SR1 11, one-byte SL2/SR2, POS1 122 and the masks
/// 0xdfffffef 0xddfecb7f 0xbffaffff 0xbffffff6; a draw refills once 624 are
/// used (the state starts used up).
/// </summary>
public sealed class Sfmt19937
{
    private const int N = 156, N32 = 624, Pos1 = 122, Sl1 = 18, Sr1 = 11;
    private static readonly uint[] Mask = [0xdfffffefu, 0xddfecb7fu, 0xbffaffffu, 0xbffffff6u];
    private static readonly uint[] Parity = [0x00000001u, 0x00000000u, 0x00000000u, 0x13c9e684u];

    private readonly uint[] _state = new uint[N32];
    private int _index;

    public Sfmt19937(uint seed)
    {
        _state[0] = seed;
        for (var i = 1; i < N32; i++)
            _state[i] = 1812433253u * (_state[i - 1] ^ (_state[i - 1] >> 30)) + (uint)i;
        _index = N32;
        CertifyPeriod();
    }

    public uint Next()
    {
        if (_index >= N32)
        {
            GenerateAll();
            _index = 0;
        }
        return _state[_index++];
    }

    private void CertifyPeriod()
    {
        uint inner = 0;
        for (var i = 0; i < 4; i++)
            inner ^= _state[i] & Parity[i];
        for (var i = 16; i > 0; i >>= 1)
            inner ^= inner >> i;
        if ((inner & 1) == 1)
            return;
        for (var i = 0; i < 4; i++)
        {
            var work = 1u;
            for (var j = 0; j < 32; j++, work <<= 1)
                if ((work & Parity[i]) != 0)
                {
                    _state[i] ^= work;
                    return;
                }
        }
    }

    private void GenerateAll()
    {
        int r1 = N - 2, r2 = N - 1;
        for (var i = 0; i < N; i++)
        {
            var b = i < N - Pos1 ? i + Pos1 : i + Pos1 - N;
            Recursion(i, i, b, r1, r2);
            r1 = r2;
            r2 = i;
        }
    }

    // r = a ^ (a << 8 bits as 128) ^ ((b >> SR1) & MSK) ^ (c >> 8 bits as 128) ^ (d << SL1), on 128-bit words.
    private void Recursion(int r, int a, int b, int c, int d)
    {
        var s = _state;
        ulong ah = ((ulong)s[a * 4 + 3] << 32) | s[a * 4 + 2], al = ((ulong)s[a * 4 + 1] << 32) | s[a * 4];
        ulong xh = (ah << 8) | (al >> 56), xl = al << 8;
        ulong ch = ((ulong)s[c * 4 + 3] << 32) | s[c * 4 + 2], cl = ((ulong)s[c * 4 + 1] << 32) | s[c * 4];
        ulong yh = ch >> 8, yl = (cl >> 8) | (ch << 56);
        Span<uint> x = [(uint)xl, (uint)(xl >> 32), (uint)xh, (uint)(xh >> 32)];
        Span<uint> y = [(uint)yl, (uint)(yl >> 32), (uint)yh, (uint)(yh >> 32)];
        for (var k = 0; k < 4; k++)
            s[r * 4 + k] = s[a * 4 + k] ^ x[k] ^ ((s[b * 4 + k] >> Sr1) & Mask[k]) ^ y[k] ^ (s[d * 4 + k] << Sl1);
    }
}
