namespace Source2.Compiler.RcTexture;

/// <summary>
/// BC6H_UF16 decoding as the D3D11 format specifies it, to half floats. The decode is integer math
/// throughout, so any correct decoder gives the same halves; resourcecompiler's (180c57ae0 to RGBA16F,
/// then to float in 180c33150) is one. Reserved modes decode to zero.
/// </summary>
public static class Bc6hDecoder
{
    // Header fields: endpoints w, x, y, z (0..3) per channel (r, g, b), the mode bits and the partition.
    private const byte M = 12, D = 13;

    private static byte F(int endpoint, int channel) => (byte)(endpoint * 3 + channel);

    private sealed record Mode(int Bits, bool Transformed, int Regions, int Precision, int DeltaR, int DeltaG, int DeltaB, (byte Field, byte Bit)[] Layout);

    private static readonly Dictionary<int, Mode> Modes = Build();

    // A run of bits of one field: (field, first bit, count), lowest bit first, or descending when count < 0.
    private static (byte, byte)[] L(params (byte Field, int First, int Count)[] runs)
    {
        var list = new List<(byte, byte)>();
        foreach (var (field, first, count) in runs)
            if (count >= 0)
                for (var i = 0; i < count; i++)
                    list.Add((field, (byte)(first + i)));
            else
                for (var i = 0; i < -count; i++)
                    list.Add((field, (byte)(first - i)));
        return [.. list];
    }

    private static Dictionary<int, Mode> Build()
    {
        byte RW = F(0, 0), GW = F(0, 1), BW = F(0, 2), RX = F(1, 0), GX = F(1, 1), BX = F(1, 2);
        byte RY = F(2, 0), GY = F(2, 1), BY = F(2, 2), RZ = F(3, 0), GZ = F(3, 1), BZ = F(3, 2);
        return new Dictionary<int, Mode>
        {
            [0x00] = new(2, true, 2, 10, 5, 5, 5, L((M, 0, 2), (GY, 4, 1), (BY, 4, 1), (BZ, 4, 1), (RW, 0, 10), (GW, 0, 10), (BW, 0, 10), (RX, 0, 5), (GZ, 4, 1),
                (GY, 0, 4), (GX, 0, 5), (BZ, 0, 1), (GZ, 0, 4), (BX, 0, 5), (BZ, 1, 1), (BY, 0, 4), (RY, 0, 5), (BZ, 2, 1), (RZ, 0, 5), (BZ, 3, 1), (D, 0, 5))),
            [0x01] = new(2, true, 2, 7, 6, 6, 6, L((M, 0, 2), (GY, 5, 1), (GZ, 4, 1), (GZ, 5, 1), (RW, 0, 7), (BZ, 0, 1), (BZ, 1, 1), (BY, 4, 1), (GW, 0, 7), (BY, 5, 1),
                (BZ, 2, 1), (GY, 4, 1), (BW, 0, 7), (BZ, 3, 1), (BZ, 5, 1), (BZ, 4, 1), (RX, 0, 6), (GY, 0, 4), (GX, 0, 6), (GZ, 0, 4), (BX, 0, 6), (BY, 0, 4), (RY, 0, 6),
                (RZ, 0, 6), (D, 0, 5))),
            [0x02] = new(5, true, 2, 11, 5, 4, 4, L((M, 0, 5), (RW, 0, 10), (GW, 0, 10), (BW, 0, 10), (RX, 0, 5), (RW, 10, 1), (GY, 0, 4), (GX, 0, 4), (GW, 10, 1),
                (BZ, 0, 1), (GZ, 0, 4), (BX, 0, 4), (BW, 10, 1), (BZ, 1, 1), (BY, 0, 4), (RY, 0, 5), (BZ, 2, 1), (RZ, 0, 5), (BZ, 3, 1), (D, 0, 5))),
            [0x06] = new(5, true, 2, 11, 4, 5, 4, L((M, 0, 5), (RW, 0, 10), (GW, 0, 10), (BW, 0, 10), (RX, 0, 4), (RW, 10, 1), (GZ, 4, 1), (GY, 0, 4), (GX, 0, 5),
                (GW, 10, 1), (GZ, 0, 4), (BX, 0, 4), (BW, 10, 1), (BZ, 1, 1), (BY, 0, 4), (RY, 0, 4), (BZ, 0, 1), (BZ, 2, 1), (RZ, 0, 4), (GY, 4, 1), (BZ, 3, 1), (D, 0, 5))),
            [0x0a] = new(5, true, 2, 11, 4, 4, 5, L((M, 0, 5), (RW, 0, 10), (GW, 0, 10), (BW, 0, 10), (RX, 0, 4), (RW, 10, 1), (BY, 4, 1), (GY, 0, 4), (GX, 0, 4),
                (GW, 10, 1), (BZ, 0, 1), (GZ, 0, 4), (BX, 0, 5), (BW, 10, 1), (BY, 0, 4), (RY, 0, 4), (BZ, 1, 1), (BZ, 2, 1), (RZ, 0, 4), (BZ, 4, 1), (BZ, 3, 1), (D, 0, 5))),
            [0x0e] = new(5, true, 2, 9, 5, 5, 5, L((M, 0, 5), (RW, 0, 9), (BY, 4, 1), (GW, 0, 9), (GY, 4, 1), (BW, 0, 9), (BZ, 4, 1), (RX, 0, 5), (GZ, 4, 1), (GY, 0, 4),
                (GX, 0, 5), (BZ, 0, 1), (GZ, 0, 4), (BX, 0, 5), (BZ, 1, 1), (BY, 0, 4), (RY, 0, 5), (BZ, 2, 1), (RZ, 0, 5), (BZ, 3, 1), (D, 0, 5))),
            [0x12] = new(5, true, 2, 8, 6, 5, 5, L((M, 0, 5), (RW, 0, 8), (GZ, 4, 1), (BY, 4, 1), (GW, 0, 8), (BZ, 2, 1), (GY, 4, 1), (BW, 0, 8), (BZ, 3, 1), (BZ, 4, 1),
                (RX, 0, 6), (GY, 0, 4), (GX, 0, 5), (BZ, 0, 1), (GZ, 0, 4), (BX, 0, 5), (BZ, 1, 1), (BY, 0, 4), (RY, 0, 6), (RZ, 0, 6), (D, 0, 5))),
            [0x16] = new(5, true, 2, 8, 5, 6, 5, L((M, 0, 5), (RW, 0, 8), (BZ, 0, 1), (BY, 4, 1), (GW, 0, 8), (GY, 5, 1), (GY, 4, 1), (BW, 0, 8), (GZ, 5, 1), (BZ, 4, 1),
                (RX, 0, 5), (GZ, 4, 1), (GY, 0, 4), (GX, 0, 6), (GZ, 0, 4), (BX, 0, 5), (BZ, 1, 1), (BY, 0, 4), (RY, 0, 5), (BZ, 2, 1), (RZ, 0, 5), (BZ, 3, 1), (D, 0, 5))),
            [0x1a] = new(5, true, 2, 8, 5, 5, 6, L((M, 0, 5), (RW, 0, 8), (BZ, 1, 1), (BY, 4, 1), (GW, 0, 8), (BY, 5, 1), (GY, 4, 1), (BW, 0, 8), (BZ, 5, 1), (BZ, 4, 1),
                (RX, 0, 5), (GZ, 4, 1), (GY, 0, 4), (GX, 0, 5), (BZ, 0, 1), (GZ, 0, 4), (BX, 0, 6), (BY, 0, 4), (RY, 0, 5), (BZ, 2, 1), (RZ, 0, 5), (BZ, 3, 1), (D, 0, 5))),
            [0x1e] = new(5, false, 2, 6, 6, 6, 6, L((M, 0, 5), (RW, 0, 6), (GZ, 4, 1), (BZ, 0, 1), (BZ, 1, 1), (BY, 4, 1), (GW, 0, 6), (GY, 5, 1), (BY, 5, 1), (BZ, 2, 1),
                (GY, 4, 1), (BW, 0, 6), (GZ, 5, 1), (BZ, 3, 1), (BZ, 5, 1), (BZ, 4, 1), (RX, 0, 6), (GY, 0, 4), (GX, 0, 6), (GZ, 0, 4), (BX, 0, 6), (BY, 0, 4), (RY, 0, 6),
                (RZ, 0, 6), (D, 0, 5))),
            [0x03] = new(5, false, 1, 10, 10, 10, 10, L((M, 0, 5), (RW, 0, 10), (GW, 0, 10), (BW, 0, 10), (RX, 0, 10), (GX, 0, 10), (BX, 0, 10))),
            [0x07] = new(5, true, 1, 11, 9, 9, 9, L((M, 0, 5), (RW, 0, 10), (GW, 0, 10), (BW, 0, 10), (RX, 0, 9), (RW, 10, 1), (GX, 0, 9), (GW, 10, 1), (BX, 0, 9), (BW, 10, 1))),
            [0x0b] = new(5, true, 1, 12, 8, 8, 8, L((M, 0, 5), (RW, 0, 10), (GW, 0, 10), (BW, 0, 10), (RX, 0, 8), (RW, 11, -2), (GX, 0, 8), (GW, 11, -2), (BX, 0, 8), (BW, 11, -2))),
            [0x0f] = new(5, true, 1, 16, 4, 4, 4, L((M, 0, 5), (RW, 0, 10), (GW, 0, 10), (BW, 0, 10), (RX, 0, 4), (RW, 15, -6), (GX, 0, 4), (GW, 15, -6), (BX, 0, 4), (BW, 15, -6))),
        };
    }

    /// <summary>The 32 two-region partitions: bit i set puts pixel i in region 1.</summary>
    public static readonly ushort[] Partitions =
    [
        0xCCCC, 0x8888, 0xEEEE, 0xECC8, 0xC880, 0xFEEC, 0xFEC8, 0xEC80, 0xC800, 0xFFEC, 0xFE80, 0xE800, 0xFFE8, 0xFF00, 0xFFF0, 0xF000,
        0xF710, 0x008E, 0x7100, 0x08CE, 0x008C, 0x7310, 0x3100, 0x8CCE, 0x088C, 0x3110, 0x6666, 0x366C, 0x17E8, 0x0FF0, 0x718E, 0x399C,
    ];

    /// <summary>Region 1's anchor pixel per partition (its index has one bit fewer).</summary>
    private static readonly byte[] Anchor2 =
        [15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2];

    private static readonly int[] Weights3 = [0, 9, 18, 27, 37, 46, 55, 64];
    private static readonly int[] Weights4 = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    /// <summary>Decode one 16-byte block to 16 pixels of three half floats (r, g, b), pixel order row by row.</summary>
    public static void DecodeBlock(ReadOnlySpan<byte> block, Span<ushort> rgb)
    {
        var lo = BitConverter.ToUInt64(block);
        var hi = BitConverter.ToUInt64(block[8..]);
        int Bit(int i) => (int)((i < 64 ? lo >> i : hi >> (i - 64)) & 1);
        var modeKey = (lo & 2) == 0 ? (int)(lo & 3) : (int)(lo & 0x1f);
        if (!Modes.TryGetValue(modeKey, out var mode))
        {
            rgb[..48].Clear();
            return;
        }
        var e = new int[4, 3];
        var partition = 0;
        var at = 0;
        foreach (var (field, bit) in mode.Layout)
        {
            var v = Bit(at++);
            if (field == D)
                partition |= v << bit;
            else if (field != M)
                e[field / 3, field % 3] |= v << bit;
        }
        var endpoints = mode.Regions * 2;
        int[] delta = [mode.DeltaR, mode.DeltaG, mode.DeltaB];
        var mask = (1 << mode.Precision) - 1;
        if (mode.Transformed)
            for (var i = 1; i < endpoints; i++)
                for (var c = 0; c < 3; c++)
                    e[i, c] = (e[0, c] + SignExtend(e[i, c], delta[c])) & mask;
        for (var i = 0; i < endpoints; i++)
            for (var c = 0; c < 3; c++)
                e[i, c] = Unquantize(e[i, c], mode.Precision);
        var indexBits = mode.Regions == 2 ? 3 : 4;
        for (var p = 0; p < 16; p++)
        {
            var region = mode.Regions == 2 ? (Partitions[partition] >> p) & 1 : 0;
            var anchor = p == 0 || (mode.Regions == 2 && p == Anchor2[partition]);
            var bits = anchor ? indexBits - 1 : indexBits;
            var index = 0;
            for (var b = 0; b < bits; b++)
                index |= Bit(at++) << b;
            var w = mode.Regions == 2 ? Weights3[index] : Weights4[index];
            for (var c = 0; c < 3; c++)
            {
                int a = e[region * 2, c], z = e[region * 2 + 1, c];
                var v = (a * (64 - w) + z * w + 32) >> 6;
                rgb[p * 3 + c] = (ushort)((v * 31) >> 6);
            }
        }
    }

    private static int SignExtend(int v, int bits) => (v & (1 << (bits - 1))) != 0 ? v - (1 << bits) : v;

    private static int Unquantize(int x, int bits)
    {
        if (bits >= 15)
            return x;
        if (x == 0)
            return 0;
        if (x == (1 << bits) - 1)
            return 0xffff;
        return ((x << 16) + 0x8000) >> bits;
    }
}
