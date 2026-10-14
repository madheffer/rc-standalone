using System.Buffers.Binary;

namespace Source2.Compiler.Textures.Decoding;

/// <summary>The block-compressed formats <see cref="BlockDecoders"/> decodes.</summary>
internal enum BlockFormat
{
    /// <summary>BC1 (DXT1), alpha forced opaque.</summary>
    Bc1NoAlpha,

    /// <summary>BC3 (DXT5).</summary>
    Bc3,

    /// <summary>BC4 unsigned (ATI1N): one channel.</summary>
    Bc4U,

    /// <summary>BC5 unsigned (ATI2N): two channels.</summary>
    Bc5U,

    /// <summary>BC6H unsigned half floats.</summary>
    Bc6HU,

    /// <summary>BC7.</summary>
    Bc7,
}

/// <summary>
/// Software decoders for the D3D11 block-compressed formats, written from the specification. A
/// texture of <c>width x height</c> is stored as <c>ceil(width/4) x ceil(height/4)</c> blocks; the
/// decoders write the pixels that fall inside <c>outWidth x outHeight</c> (a texture's display
/// rect may be smaller than its stored size) as RGBA, 4 bytes a pixel, or for BC6H as 4 half
/// floats a pixel with alpha 1.
/// </summary>
internal static class BlockDecoders
{
    /// <summary>The bytes one 4x4 block of <paramref name="format"/> takes.</summary>
    public static int BlockBytes(BlockFormat format) => format is BlockFormat.Bc1NoAlpha or BlockFormat.Bc4U ? 8 : 16;

    /// <summary>The bytes a <paramref name="width"/> x <paramref name="height"/> image of blocks takes.</summary>
    public static long SourceSize(int width, int height, BlockFormat format)
        => (long)((width + 3) / 4) * ((height + 3) / 4) * BlockBytes(format);

    /// <summary>
    /// Decode an 8-bit format (everything but BC6H) to RGBA bytes.
    /// </summary>
    /// <exception cref="ArgumentException">The source or destination is too small.</exception>
    public static void Decode(ReadOnlySpan<byte> source, int width, int height, Span<byte> rgba, int outWidth, int outHeight, BlockFormat format)
    {
        if (format == BlockFormat.Bc6HU)
            throw new ArgumentException("BC6H decodes to half floats; use DecodeBc6H.", nameof(format));
        CheckSizes(source.Length, width, height, rgba.Length / 4, outWidth, outHeight, format);
        var blockBytes = BlockBytes(format);
        var blocksWide = (width + 3) / 4;
        Span<byte> block = stackalloc byte[64];
        for (var by = 0; by * 4 < outHeight; by++)
        {
            for (var bx = 0; bx * 4 < outWidth; bx++)
            {
                var input = source.Slice((by * blocksWide + bx) * blockBytes, blockBytes);
                switch (format)
                {
                    case BlockFormat.Bc1NoAlpha:
                        Bc1(input, block, threeColorMode: true);
                        break;
                    case BlockFormat.Bc3:
                        Bc1(input[8..], block, threeColorMode: false);
                        Bc4(input, block, 3);
                        break;
                    case BlockFormat.Bc4U:
                        Bc4(input, block, 0);
                        for (var i = 0; i < 16; i++)
                        {
                            block[i * 4 + 1] = block[i * 4 + 2] = block[i * 4];
                            block[i * 4 + 3] = 255;
                        }
                        break;
                    case BlockFormat.Bc5U:
                        Bc4(input, block, 0);
                        Bc4(input[8..], block, 1);
                        for (var i = 0; i < 16; i++)
                        {
                            block[i * 4 + 2] = 0;
                            block[i * 4 + 3] = 255;
                        }
                        break;
                    default:
                        Bc7(input, block);
                        break;
                }
                CopyBlock(block, 4, rgba, bx, by, outWidth, outHeight);
            }
        }
    }

    /// <summary>Decode BC6H (unsigned) to RGBA half floats, alpha 1.</summary>
    /// <exception cref="ArgumentException">The source or destination is too small.</exception>
    public static void DecodeBc6H(ReadOnlySpan<byte> source, int width, int height, Span<Half> rgba, int outWidth, int outHeight)
    {
        CheckSizes(source.Length, width, height, rgba.Length / 4, outWidth, outHeight, BlockFormat.Bc6HU);
        var blocksWide = (width + 3) / 4;
        Span<Half> block = stackalloc Half[64];
        for (var by = 0; by * 4 < outHeight; by++)
        {
            for (var bx = 0; bx * 4 < outWidth; bx++)
            {
                Bc6H(source.Slice((by * blocksWide + bx) * 16, 16), block);
                CopyBlock(block, 4, rgba, bx, by, outWidth, outHeight);
            }
        }
    }

    private static void CheckSizes(int sourceLength, int width, int height, int outPixels, int outWidth, int outHeight, BlockFormat format)
    {
        if (outWidth > ((width + 3) & ~3) || outHeight > ((height + 3) & ~3) || outWidth < 0 || outHeight < 0)
            throw new ArgumentException($"A {outWidth}x{outHeight} output does not fit a {width}x{height} image of blocks.");
        if (sourceLength < SourceSize(width, height, format))
            throw new ArgumentException($"A {width}x{height} {format} image needs {SourceSize(width, height, format)} bytes; got {sourceLength}.");
        if (outPixels < (long)outWidth * outHeight)
            throw new ArgumentException($"The output holds {outPixels} pixels, not {outWidth}x{outHeight}.");
    }

    private static void CopyBlock<T>(ReadOnlySpan<T> block, int channels, Span<T> output, int bx, int by, int outWidth, int outHeight)
    {
        var columns = Math.Min(4, outWidth - bx * 4);
        for (var y = 0; y < 4 && by * 4 + y < outHeight; y++)
            block.Slice(y * 4 * channels, columns * channels).CopyTo(output[((((by * 4 + y) * outWidth) + bx * 4) * channels)..]);
    }

    // BC1's colour block: two RGB565 endpoints and 2-bit indices. In the block's own format a first
    // endpoint not above the second selects three colours and black; BC2/BC3 always use four. Each
    // palette colour is interpolated on the 5- or 6-bit values and rounded once to 8 bits.
    private static void Bc1(ReadOnlySpan<byte> input, Span<byte> block, bool threeColorMode)
    {
        var c0 = BinaryPrimitives.ReadUInt16LittleEndian(input);
        var c1 = BinaryPrimitives.ReadUInt16LittleEndian(input[2..]);
        var indices = BinaryPrimitives.ReadUInt32LittleEndian(input[4..]);
        Span<byte> palette = stackalloc byte[16];
        for (var c = 0; c < 3; c++)
        {
            var (shift, max) = c switch { 0 => (11, 31), 1 => (5, 63), _ => (0, 31) };
            int a = (c0 >> shift) & max, b = (c1 >> shift) & max;
            palette[c] = Scale(a, 1, max);
            palette[4 + c] = Scale(b, 1, max);
            if (!threeColorMode || c0 > c1)
            {
                palette[8 + c] = Scale(2 * a + b, 3, max);
                palette[12 + c] = Scale(a + 2 * b, 3, max);
            }
            else
            {
                palette[8 + c] = Scale(a + b, 2, max);
                palette[12 + c] = 0;
            }
        }
        palette[3] = palette[7] = palette[11] = palette[15] = 255;
        for (var i = 0; i < 16; i++)
            palette.Slice((int)((indices >> (2 * i)) & 3) * 4, 4).CopyTo(block[(i * 4)..]);
    }

    // round(sum / count * 255 / max)
    private static byte Scale(int sum, int count, int max) => (byte)((sum * 510 + count * max) / (2 * count * max));

    // BC4's single channel: two 8-bit endpoints and 3-bit indices; endpoint 0 above endpoint 1 gives
    // six interpolated values, otherwise four plus 0 and 255. Written into channel `channel`.
    private static void Bc4(ReadOnlySpan<byte> input, Span<byte> block, int channel)
    {
        int a0 = input[0], a1 = input[1];
        Span<byte> palette = stackalloc byte[8];
        palette[0] = (byte)a0;
        palette[1] = (byte)a1;
        if (a0 > a1)
        {
            for (var i = 1; i < 7; i++)
                palette[i + 1] = (byte)(((7 - i) * a0 + i * a1 + 3) / 7);
        }
        else
        {
            for (var i = 1; i < 5; i++)
                palette[i + 1] = (byte)(((5 - i) * a0 + i * a1 + 2) / 5);
            palette[6] = 0;
            palette[7] = 255;
        }
        ulong indices = 0;
        for (var i = 0; i < 6; i++)
            indices |= (ulong)input[2 + i] << (8 * i);
        for (var i = 0; i < 16; i++)
            block[i * 4 + channel] = palette[(int)((indices >> (3 * i)) & 7)];
    }

    // A 128-bit block read least significant bit first.
    private ref struct Bits(ReadOnlySpan<byte> block)
    {
        private readonly ulong _low = BinaryPrimitives.ReadUInt64LittleEndian(block);
        private readonly ulong _high = BinaryPrimitives.ReadUInt64LittleEndian(block[8..]);
        private int _at;

        public int Read(int count)
        {
            if (count == 0)
                return 0;
            ulong value;
            if (_at >= 64)
                value = _high >> (_at - 64);
            else if (_at + count <= 64)
                value = _low >> _at;
            else
                value = (_low >> _at) | (_high << (64 - _at));
            _at += count;
            return (int)(value & ((1UL << count) - 1));
        }

        public int Bit() => Read(1);
    }

    // BC7 modes: subsets, partition bits, rotation bits, index-selection bits, colour bits,
    // alpha bits, per-endpoint p-bits, shared p-bits, index bits, second index bits.
    private static ReadOnlySpan<byte> Bc7Modes =>
    [
        3, 4, 0, 0, 4, 0, 1, 0, 3, 0,
        2, 6, 0, 0, 6, 0, 0, 1, 3, 0,
        3, 6, 0, 0, 5, 0, 0, 0, 2, 0,
        2, 6, 0, 0, 7, 0, 1, 0, 2, 0,
        1, 0, 2, 1, 5, 6, 0, 0, 2, 3,
        1, 0, 2, 0, 7, 8, 0, 0, 2, 2,
        1, 0, 0, 0, 7, 7, 1, 0, 4, 0,
        2, 6, 0, 0, 5, 5, 1, 0, 2, 0,
    ];

    private static void Bc7(ReadOnlySpan<byte> input, Span<byte> block)
    {
        var bits = new Bits(input);
        var mode = 0;
        while (mode < 8 && bits.Bit() == 0)
            mode++;
        if (mode == 8)
        {
            block.Clear();   // reserved mode: transparent black
            return;
        }
        var m = Bc7Modes.Slice(mode * 10, 10);
        int subsets = m[0], colorBits = m[4], alphaBits = m[5], indexBits = m[8], indexBits2 = m[9];
        var partition = bits.Read(m[1]);
        var rotation = bits.Read(m[2]);
        var indexSelection = bits.Read(m[3]);

        Span<int> endpoints = stackalloc int[6 * 4];   // [endpoint][channel]
        var count = subsets * 2;
        for (var c = 0; c < 3; c++)
            for (var e = 0; e < count; e++)
                endpoints[e * 4 + c] = bits.Read(colorBits);
        for (var e = 0; e < count; e++)
            endpoints[e * 4 + 3] = alphaBits > 0 ? bits.Read(alphaBits) : 255;

        int colorPrecision = colorBits, alphaPrecision = alphaBits;
        if (m[6] != 0 || m[7] != 0)
        {
            Span<int> pbits = stackalloc int[6];
            if (m[6] != 0)
            {
                for (var e = 0; e < count; e++)
                    pbits[e] = bits.Bit();
            }
            else
            {
                for (var s = 0; s < subsets; s++)
                    pbits[s * 2] = pbits[s * 2 + 1] = bits.Bit();
            }
            for (var e = 0; e < count; e++)
            {
                for (var c = 0; c < 3; c++)
                    endpoints[e * 4 + c] = (endpoints[e * 4 + c] << 1) | pbits[e];
                if (alphaBits > 0)
                    endpoints[e * 4 + 3] = (endpoints[e * 4 + 3] << 1) | pbits[e];
            }
            colorPrecision++;
            if (alphaBits > 0)
                alphaPrecision++;
        }
        for (var e = 0; e < count; e++)
        {
            for (var c = 0; c < 3; c++)
                endpoints[e * 4 + c] = Unquantize(endpoints[e * 4 + c], colorPrecision);
            if (alphaBits > 0)
                endpoints[e * 4 + 3] = Unquantize(endpoints[e * 4 + 3], alphaPrecision);
        }

        var shape = subsets switch
        {
            2 => BcTables.Partition2.Slice(partition * 16, 16),
            3 => BcTables.Partition3.Slice(partition * 16, 16),
            _ => default,
        };
        Span<int> anchors = [0, subsets == 2 ? BcTables.Anchor2Of2[partition] : subsets == 3 ? BcTables.Anchor2Of3[partition] : -1,
            subsets == 3 ? BcTables.Anchor3Of3[partition] : -1];

        Span<byte> colorIndex = stackalloc byte[16];
        Span<byte> alphaIndex = stackalloc byte[16];
        for (var i = 0; i < 16; i++)
            colorIndex[i] = (byte)bits.Read(anchors.Contains(i) ? indexBits - 1 : indexBits);
        if (indexBits2 > 0)
            for (var i = 0; i < 16; i++)
                alphaIndex[i] = (byte)bits.Read(i == 0 ? indexBits2 - 1 : indexBits2);

        for (var i = 0; i < 16; i++)
        {
            var subset = shape.IsEmpty ? 0 : shape[i];
            var e0 = endpoints.Slice(subset * 8, 4);
            var e1 = endpoints.Slice(subset * 8 + 4, 4);
            int ci, ai, cb, ab;
            if (indexBits2 == 0)
            {
                ci = ai = colorIndex[i];
                cb = ab = indexBits;
            }
            else if (indexSelection == 0)
            {
                (ci, cb, ai, ab) = (colorIndex[i], indexBits, alphaIndex[i], indexBits2);
            }
            else
            {
                (ci, cb, ai, ab) = (alphaIndex[i], indexBits2, colorIndex[i], indexBits);
            }
            var cw = Weights(cb)[ci];
            var aw = Weights(ab)[ai];
            var pixel = block.Slice(i * 4, 4);
            for (var c = 0; c < 3; c++)
                pixel[c] = (byte)((e0[c] * (64 - cw) + e1[c] * cw + 32) >> 6);
            pixel[3] = (byte)((e0[3] * (64 - aw) + e1[3] * aw + 32) >> 6);
            if (rotation != 0)
                (pixel[3], pixel[rotation - 1]) = (pixel[rotation - 1], pixel[3]);
        }
    }

    private static int Unquantize(int value, int bits) => (value << (8 - bits)) | ((value << (8 - bits)) >> bits);

    private static ReadOnlySpan<byte> Weights(int bits) => bits switch
    {
        2 => BcTables.Weights2,
        3 => BcTables.Weights3,
        _ => BcTables.Weights4,
    };

    // BC6H modes by their 2- or 5-bit code: endpoint bits, then delta bits for R, G, B
    // (equal to the endpoint bits for the untransformed modes 10.10 and 6.6.6.6).
    private static ReadOnlySpan<byte> Bc6Precision =>
    [
        10, 5, 5, 5,   7, 6, 6, 6,   11, 5, 4, 4,   11, 4, 5, 4,   11, 4, 4, 5,   9, 5, 5, 5,   8, 6, 5, 5,
        8, 5, 6, 5,    8, 5, 5, 6,   6, 6, 6, 6,    10, 10, 10, 10, 11, 9, 9, 9,  12, 8, 8, 8,  16, 4, 4, 4,
    ];

    private static int Bc6ModeIndex(int code) => code switch
    {
        0x00 => 0, 0x01 => 1, 0x02 => 2, 0x06 => 3, 0x0A => 4, 0x0E => 5, 0x12 => 6,
        0x16 => 7, 0x1A => 8, 0x1E => 9, 0x03 => 10, 0x07 => 11, 0x0B => 12, 0x0F => 13,
        _ => -1,
    };

    private static void Bc6H(ReadOnlySpan<byte> input, Span<Half> block)
    {
        var bits = new Bits(input);
        var code = bits.Read(2);
        if (code > 1)
            code |= bits.Read(3) << 2;
        var mode = Bc6ModeIndex(code);
        if (mode < 0)
        {
            for (var i = 0; i < 16; i++)
            {
                block[i * 4] = block[i * 4 + 1] = block[i * 4 + 2] = Half.Zero;
                block[i * 4 + 3] = Half.One;
            }
            return;
        }

        // Endpoints [w, x, y, z] per channel, filled field by field in the mode's bit layout.
        Span<int> r = stackalloc int[4];
        Span<int> g = stackalloc int[4];
        Span<int> b = stackalloc int[4];
        var partition = 0;
        ReadBc6Endpoints(mode, ref bits, r, g, b, ref partition);

        var p = Bc6Precision.Slice(mode * 4, 4);
        var endpointBits = p[0];
        var regions = mode >= 10 ? 1 : 2;
        var transformed = mode != 9 && mode != 10;
        if (transformed)
        {
            for (var e = 1; e < regions * 2; e++)
            {
                r[e] = (SignExtend(r[e], p[1]) + r[0]) & ((1 << endpointBits) - 1);
                g[e] = (SignExtend(g[e], p[2]) + g[0]) & ((1 << endpointBits) - 1);
                b[e] = (SignExtend(b[e], p[3]) + b[0]) & ((1 << endpointBits) - 1);
            }
        }
        for (var e = 0; e < regions * 2; e++)
        {
            r[e] = Bc6Unquantize(r[e], endpointBits);
            g[e] = Bc6Unquantize(g[e], endpointBits);
            b[e] = Bc6Unquantize(b[e], endpointBits);
        }

        var weights = regions == 1 ? BcTables.Weights4 : BcTables.Weights3;
        var indexBits = regions == 1 ? 4 : 3;
        var shape = BcTables.Partition2.Slice(partition * 16, 16);
        var anchor = regions == 2 ? BcTables.Anchor2Of2[partition] : -1;
        for (var i = 0; i < 16; i++)
        {
            var index = bits.Read(i == 0 || i == anchor ? indexBits - 1 : indexBits);
            var subset = regions == 1 ? 0 : shape[i];
            var w = weights[index];
            block[i * 4] = Bc6Finish(r[subset * 2], r[subset * 2 + 1], w);
            block[i * 4 + 1] = Bc6Finish(g[subset * 2], g[subset * 2 + 1], w);
            block[i * 4 + 2] = Bc6Finish(b[subset * 2], b[subset * 2 + 1], w);
            block[i * 4 + 3] = Half.One;
        }
    }

    private static int SignExtend(int value, int bits) => (value << (32 - bits)) >> (32 - bits);

    private static int Bc6Unquantize(int value, int bits)
    {
        if (bits >= 15)
            return value;
        if (value == 0)
            return 0;
        if (value == (1 << bits) - 1)
            return 0xFFFF;
        return ((value << 16) + 0x8000) >> bits;
    }

    private static Half Bc6Finish(int e0, int e1, int weight)
    {
        var value = (e0 * (64 - weight) + e1 * weight + 32) >> 6;
        return BitConverter.UInt16BitsToHalf((ushort)((value * 31) >> 6));
    }

    // The fields of each BC6H mode in stream order, from the D3D11 specification's layout table:
    // (channel 0-2 = r,g,b, endpoint 0-3 = w,x,y,z, high bit, low bit); a field whose high bit is
    // below its low bit is stored reversed. Channel 3 is the partition (shape) index.
    private static readonly (byte Channel, byte Endpoint, byte From, byte To)[][] Bc6Layouts = BuildBc6Layouts();

    private static void ReadBc6Endpoints(int mode, ref Bits bits, scoped Span<int> r, scoped Span<int> g, scoped Span<int> b, ref int partition)
    {
        r.Clear();
        g.Clear();
        b.Clear();
        foreach (var (channel, endpoint, from, to) in Bc6Layouts[mode])
        {
            int value;
            if (from >= to)
            {
                value = bits.Read(from - to + 1) << to;
            }
            else
            {
                // Reversed: the first bit read is the highest numbered.
                value = 0;
                for (var bit = to; bit >= from; bit--)
                    value |= bits.Bit() << bit;
            }
            if (channel == 3)
                partition |= value;
            else
                (channel switch { 0 => r, 1 => g, _ => b })[endpoint] |= value;
        }
    }

    private static (byte, byte, byte, byte)[][] BuildBc6Layouts()
    {
        // One string per mode: fields "c e hi lo" where c is r/g/b/d and e is w/x/y/z.
        string[] layouts =
        [
            "gy4 by4 bz4 rw9:0 gw9:0 bw9:0 rx4:0 gz4 gy3:0 gx4:0 bz0 gz3:0 bx4:0 bz1 by3:0 ry4:0 bz2 rz4:0 bz3 d4:0",
            "gy5 gz4 gz5 rw6:0 bz0 bz1 by4 gw6:0 by5 bz2 gy4 bw6:0 bz3 bz5 bz4 rx5:0 gy3:0 gx5:0 gz3:0 bx5:0 by3:0 ry5:0 rz5:0 d4:0",
            "rw9:0 gw9:0 bw9:0 rx4:0 rw10 gy3:0 gx3:0 gw10 bz0 gz3:0 bx3:0 bw10 bz1 by3:0 ry4:0 bz2 rz4:0 bz3 d4:0",
            "rw9:0 gw9:0 bw9:0 rx3:0 rw10 gz4 gy3:0 gx4:0 gw10 gz3:0 bx3:0 bw10 bz1 by3:0 ry3:0 bz0 bz2 rz3:0 gy4 bz3 d4:0",
            "rw9:0 gw9:0 bw9:0 rx3:0 rw10 by4 gy3:0 gx3:0 gw10 bz0 gz3:0 bx4:0 bw10 by3:0 ry3:0 bz1 bz2 rz3:0 bz4 bz3 d4:0",
            "rw8:0 by4 gw8:0 gy4 bw8:0 bz4 rx4:0 gz4 gy3:0 gx4:0 bz0 gz3:0 bx4:0 bz1 by3:0 ry4:0 bz2 rz4:0 bz3 d4:0",
            "rw7:0 gz4 by4 gw7:0 bz2 gy4 bw7:0 bz3 bz4 rx5:0 gy3:0 gx4:0 bz0 gz3:0 bx4:0 bz1 by3:0 ry5:0 rz5:0 d4:0",
            "rw7:0 bz0 by4 gw7:0 gy5 gy4 bw7:0 gz5 bz4 rx4:0 gz4 gy3:0 gx5:0 gz3:0 bx4:0 bz1 by3:0 ry4:0 bz2 rz4:0 bz3 d4:0",
            "rw7:0 bz1 by4 gw7:0 by5 gy4 bw7:0 bz5 bz4 rx4:0 gz4 gy3:0 gx4:0 bz0 gz3:0 bx5:0 by3:0 ry4:0 bz2 rz4:0 bz3 d4:0",
            "rw5:0 gz4 bz0 bz1 by4 gw5:0 gy5 by5 bz2 gy4 bw5:0 gz5 bz3 bz5 bz4 rx5:0 gy3:0 gx5:0 gz3:0 bx5:0 by3:0 ry5:0 rz5:0 d4:0",
            "rw9:0 gw9:0 bw9:0 rx9:0 gx9:0 bx9:0",
            "rw9:0 gw9:0 bw9:0 rx8:0 rw10 gx8:0 gw10 bx8:0 bw10",
            "rw9:0 gw9:0 bw9:0 rx7:0 rw10:11 gx7:0 gw10:11 bx7:0 bw10:11",
            "rw9:0 gw9:0 bw9:0 rx3:0 rw10:15 gx3:0 gw10:15 bx3:0 bw10:15",
        ];
        var result = new (byte, byte, byte, byte)[layouts.Length][];
        for (var mode = 0; mode < layouts.Length; mode++)
        {
            var fields = layouts[mode].Split(' ');
            result[mode] = new (byte, byte, byte, byte)[fields.Length];
            for (var f = 0; f < fields.Length; f++)
            {
                var field = fields[f];
                var channel = (byte)"rgbd".IndexOf(field[0]);
                var at = channel == 3 ? 1 : 2;
                var endpoint = channel == 3 ? (byte)0 : (byte)"wxyz".IndexOf(field[1]);
                var range = field[at..].Split(':');
                var hi = byte.Parse(range[0], System.Globalization.CultureInfo.InvariantCulture);
                var lo = range.Length > 1 ? byte.Parse(range[1], System.Globalization.CultureInfo.InvariantCulture) : hi;
                result[mode][f] = (channel, endpoint, hi, lo);
            }
        }
        return result;
    }
}
