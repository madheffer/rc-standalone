using System.Buffers.Binary;

namespace Source2.Compiler.RcTexture;

/// <summary>
/// The uncompressed output formats of a texture compile beyond RGBA8888: what
/// <c>FloatBitMap_t::WriteToBuffer</c> (resourcecompiler 0923 180c45cf0; 1103 180c46cb0, whose
/// jump table adds A8 and R8_UNORM) writes for each, from a level's linear floats. The names are
/// the <c>m_outputFormat</c> strings; <see cref="VtexCodes"/> holds the byte the vtex header
/// stores for each (180133da0's switch for the 0923 formats, RC's 11-04 compiles for the rest).
/// Every pixel is written in row order, channels as RC numbers them (0 R, 1 G, 2 B, 3 A).
/// </summary>
internal static class RcFormats
{
    /// <summary>The vtex header's format byte, by output format.</summary>
    internal static readonly Dictionary<string, byte> VtexCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["I8"] = 3, ["R16"] = 5, ["RG1616"] = 6, ["RGBA16161616"] = 7, ["R16F"] = 8, ["RG1616F"] = 9,
        ["RGBA16161616F"] = 10, ["R32F"] = 11, ["RG3232F"] = 12, ["RGB323232F"] = 13, ["RGBA32323232F"] = 14,
        ["IA88"] = 22, ["BGRA8888"] = 28, ["A8"] = 32, ["R8_UNORM"] = 33,
    };

    /// <summary>Whether <paramref name="format"/> is one of these writers.</summary>
    internal static bool Handles(string format) => VtexCodes.ContainsKey(format);

    /// <summary>One level in <paramref name="format"/>.</summary>
    internal static byte[] Write(RcMipChain.Level level, string format)
    {
        int n = level.Width * level.Height;
        var px = level.Rgba;
        switch (format.ToUpperInvariant())
        {
            case "I8":
            {
                var o = new byte[n];
                for (int i = 0; i < n; i++)
                    o[i] = Luminance(px, i);
                return o;
            }
            case "IA88":
            {
                // 180c34a30: the I8 luminance, then alpha by the same 1023-step rule.
                var o = new byte[n * 2];
                for (int i = 0; i < n; i++)
                {
                    o[i * 2] = Luminance(px, i);
                    o[i * 2 + 1] = Ten((int)(px[i * 4 + 3] * 1023f + 0.5f));
                }
                return o;
            }
            case "A8":
            case "R8_UNORM":
            {
                // 1103 cases 7 and 67: one channel (alpha, red), 0 under 0 else min(1, v), * 255 + 0.5 truncated.
                int c = format.Equals("A8", StringComparison.OrdinalIgnoreCase) ? 3 : 0;
                var o = new byte[n];
                for (int i = 0; i < n; i++)
                {
                    float v = px[i * 4 + c];
                    o[i] = (byte)(int)((0f <= v ? MathF.Min(1f, v) : 0f) * 255f + 0.5f);
                }
                return o;
            }
            case "BGRA8888":
            {
                // 180c35550: each channel clamped to [0, 1], * 255 + 0.5 truncated, stored B, G, R, A.
                var o = new byte[n * 4];
                for (int i = 0; i < n; i++)
                {
                    o[i * 4 + 2] = Unit8(px[i * 4]);
                    o[i * 4 + 1] = Unit8(px[i * 4 + 1]);
                    o[i * 4] = Unit8(px[i * 4 + 2]);
                    o[i * 4 + 3] = Unit8(px[i * 4 + 3]);
                }
                return o;
            }
            case "R16": return Unorm16(px, n, 1);
            case "RG1616": return Unorm16(px, n, 2);
            case "RGBA16161616": return Unorm16(px, n, 4);
            case "R16F": return Halves(px, n, 1);
            case "RG1616F": return Halves(px, n, 2);
            case "RGBA16161616F": return Halves(px, n, 4);
            case "R32F": return Floats(px, n, 1);
            case "RG3232F": return Floats(px, n, 2);
            case "RGB323232F": return Floats(px, n, 3);
            case "RGBA32323232F": return Floats(px, n, 4);
            default:
                throw new InvalidOperationException($"Output format {format} is not compiled here yet.");
        }
    }

    // 180c45cf0 case 5: (int)((B * 0.114 + (R * 0.299 + G * 0.587)) * 1023 + 0.5), all single
    // precision, clamped to [0, 1023] and shifted down by 2.
    private static byte Luminance(float[] px, int i) =>
        Ten((int)((px[i * 4 + 2] * 0.114f + (px[i * 4] * 0.299f + px[i * 4 + 1] * 0.587f)) * 1023f + 0.5f));

    private static byte Ten(int v) => (byte)((v < 0 ? 0 : (v > 0x3ff ? 0x3ff : v)) >> 2);

    private static byte Unit8(float v) => (byte)(int)((0f <= v ? (v <= 1f ? v : 1f) : 0f) * 255f + 0.5f);

    // 180c48c70 and cases 0x31 / 0x32: (int)(v * 65535 + 0.5), clamped to [0, 65535].
    private static byte[] Unorm16(float[] px, int n, int channels)
    {
        var o = new byte[n * channels * 2];
        for (int i = 0; i < n; i++)
            for (int c = 0; c < channels; c++)
            {
                int v = (int)(px[i * 4 + c] * 65535f + 0.5f);
                BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan((i * channels + c) * 2), (ushort)(v < 0 ? 0 : (v > 0xffff ? 0xffff : v)));
            }
        return o;
    }

    private static byte[] Halves(float[] px, int n, int channels)
    {
        var o = new byte[n * channels * 2];
        for (int i = 0; i < n; i++)
            for (int c = 0; c < channels; c++)
                BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan((i * channels + c) * 2), Half(px[i * 4 + c]));
        return o;
    }

    private static byte[] Floats(float[] px, int n, int channels)
    {
        var o = new byte[n * channels * 4];
        for (int i = 0; i < n; i++)
            for (int c = 0; c < channels; c++)
                BinaryPrimitives.WriteSingleLittleEndian(o.AsSpan((i * channels + c) * 4), px[i * 4 + c]);
        return o;
    }

    /// <summary>
    /// Valve's float to half (1802ea750): the value clamped to [-65504, 65504], the mantissa
    /// truncated (no rounding), a result under 2^-14 made denormal by shifting the 23-bit mantissa
    /// right by <c>126 - e</c> and adding the implicit bit <c>1 &lt;&lt; (e - 103)</c> (exponents
    /// 103..112; smaller ones give a signed zero); infinity the largest finite half, NaN and float
    /// denormals a signed zero. The sign is always the input's.
    /// </summary>
    internal static ushort Half(float f)
    {
        uint bits = BitConverter.SingleToUInt32Bits(f);
        ushort sign = (ushort)((bits >> 16) & 0x8000);
        uint exp = (bits >> 23) & 0xff;
        if (exp == 0)
            return sign;
        if (exp == 0xff)
            return (bits & 0x7fffff) == 0 ? (ushort)(sign | 0x7bff) : sign;
        float c = f;
        if (65504f < f)
            c = 65504f;
        if (f < -65504f)
            c = -65504f;
        uint cb = BitConverter.SingleToUInt32Bits(c);
        uint e = (cb >> 23) & 0xff;
        int ie = (int)e - 127;
        if (ie < -14)
        {
            if ((uint)(0x70 - (int)e) < 10)
            {
                uint m = (cb & 0x7fffff) >> (int)((0x7e - e) & 31);
                return (ushort)(((m + (1u << (int)((e - 0x67) & 31))) & 0x3ff) | sign);
            }
            return sign;
        }
        if (15 < ie)
            return (ushort)(sign | 0x7bff);
        return (ushort)(((cb >> 13) & 0x3ff) | (((uint)(ie + 15) * 0x400) & 0x7c00) | sign);
    }
}
