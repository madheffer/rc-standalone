// Per-format decode and codec conversions follow ValveResourceFormat's TextureDecoders (MIT,
// https://github.com/ValveResourceFormat/ValveResourceFormat), so the pixels match its GenerateBitmap.

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Source2.Compiler.Textures.Decoding;

/// <summary>
/// Decodes one face of a mip into an <see cref="SKBitmap"/>: <see cref="SKColorType.Bgra8888"/>
/// bytes, or <see cref="SKColorType.RgbaF32"/> floats for a high dynamic range decode.
/// </summary>
internal static class PixelDecoders
{
    /// <summary>
    /// Decode <paramref name="input"/>, one face of a mip stored <paramref name="storedWidth"/> x
    /// <paramref name="storedHeight"/>, into <paramref name="bitmap"/> (which may be smaller: a display rect).
    /// </summary>
    public static void Decode(VTexFormat format, ReadOnlySpan<byte> input, int storedWidth, int storedHeight, SKBitmap bitmap, bool hdr)
    {
        int width = bitmap.Width, height = bitmap.Height;
        var bytes = bitmap.GetPixelSpan();
        var floats = hdr ? MemoryMarshal.Cast<byte, float>(bytes) : default;

        BlockFormat? block = format switch
        {
            VTexFormat.DXT1 => BlockFormat.Bc1NoAlpha,
            VTexFormat.DXT5 => BlockFormat.Bc3,
            VTexFormat.ATI1N => BlockFormat.Bc4U,
            VTexFormat.ATI2N => BlockFormat.Bc5U,
            VTexFormat.BC7 => BlockFormat.Bc7,
            _ => null,
        };
        if (block is { } blockFormat)
        {
            BlockDecoders.Decode(input, storedWidth, storedHeight, bytes, width, height, blockFormat);
            SwapRB(bytes);
            return;
        }
        if (format == VTexFormat.BC6H)
        {
            var halves = new Half[width * height * 4];
            BlockDecoders.DecodeBc6H(input, storedWidth, storedHeight, halves, width, height);
            if (hdr)
            {
                for (var i = 0; i < halves.Length; i++)
                    floats[i] = (float)halves[i];
            }
            else
            {
                for (var i = 0; i < halves.Length; i++)
                    bytes[i] = ToClampedLdrColor((float)halves[i]);
                SwapRB(bytes);
            }
            return;
        }

        var pixelBytes = format switch
        {
            VTexFormat.I8 => 1,
            VTexFormat.IA88 or VTexFormat.R16 or VTexFormat.R16F => 2,
            VTexFormat.RGBA8888 or VTexFormat.BGRA8888 or VTexFormat.RG1616 or VTexFormat.RG1616F or VTexFormat.R32F => 4,
            VTexFormat.RGBA16161616 or VTexFormat.RGBA16161616F or VTexFormat.RG3232F => 8,
            VTexFormat.RGB323232F => 12,
            VTexFormat.RGBA32323232F => 16,
            VTexFormat.ETC2 or VTexFormat.ETC2_EAC or VTexFormat.R11_EAC or VTexFormat.RG11_EAC =>
                throw new NotSupportedException($"Decoding {format} is not supported yet (docs/INDEPENDENCE.md)."),
            _ => throw new NotSupportedException($"Texture format {format} has no decoder."),
        };
        if (input.Length < (long)storedWidth * (height - 1) * pixelBytes + (long)width * pixelBytes)
            throw new InvalidDataException($"A {storedWidth}x{storedHeight} {format} face needs more than the {input.Length} bytes stored.");

        // Rows are stored at the full stored width; the bitmap may be a narrower display rect.
        if (format is VTexFormat.RGBA16161616 or VTexFormat.RGBA16161616F && !hdr)
        {
            Tonemap(format, input, storedWidth, width, height, bytes);
            return;
        }
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var p = input.Slice((y * storedWidth + x) * pixelBytes, pixelBytes);
                var o = y * width + x;
                if (hdr)
                {
                    var (r, g, b, a) = HdrPixel(format, p);
                    floats[o * 4] = r;
                    floats[o * 4 + 1] = g;
                    floats[o * 4 + 2] = b;
                    floats[o * 4 + 3] = a;
                }
                else
                {
                    var (r, g, b, a) = LdrPixel(format, p);
                    bytes[o * 4] = b;
                    bytes[o * 4 + 1] = g;
                    bytes[o * 4 + 2] = r;
                    bytes[o * 4 + 3] = a;
                }
            }
        }
    }

    private static ushort U16(ReadOnlySpan<byte> p, int i) => BinaryPrimitives.ReadUInt16LittleEndian(p[(i * 2)..]);

    private static float F16(ReadOnlySpan<byte> p, int i) => (float)BinaryPrimitives.ReadHalfLittleEndian(p[(i * 2)..]);

    private static float F32(ReadOnlySpan<byte> p, int i) => BinaryPrimitives.ReadSingleLittleEndian(p[(i * 4)..]);

    private static (float R, float G, float B, float A) HdrPixel(VTexFormat format, ReadOnlySpan<byte> p) => format switch
    {
        VTexFormat.R16 => (U16(p, 0) / (float)ushort.MaxValue, 0f, 0f, 1f),
        VTexFormat.RG1616 => (U16(p, 0) / (float)ushort.MaxValue, U16(p, 1) / (float)ushort.MaxValue, 0f, 1f),
        VTexFormat.RGBA16161616 => (U16(p, 0) / (float)ushort.MaxValue, U16(p, 1) / (float)ushort.MaxValue,
            U16(p, 2) / (float)ushort.MaxValue, U16(p, 3) / (float)ushort.MaxValue),
        VTexFormat.R16F => (F16(p, 0), 0f, 0f, 1f),
        VTexFormat.RG1616F => (F16(p, 0), F16(p, 1), 0f, 1f),
        VTexFormat.RGBA16161616F => (F16(p, 0), F16(p, 1), F16(p, 2), F16(p, 3)),
        VTexFormat.R32F => (F32(p, 0), 0f, 0f, 1f),
        VTexFormat.RG3232F => (F32(p, 0), F32(p, 1), 0f, 1f),
        VTexFormat.RGB323232F => (F32(p, 0), F32(p, 1), F32(p, 2), 1f),
        _ => (F32(p, 0), F32(p, 1), F32(p, 2), F32(p, 3)),
    };

    private static (byte R, byte G, byte B, byte A) LdrPixel(VTexFormat format, ReadOnlySpan<byte> p) => format switch
    {
        VTexFormat.I8 => (p[0], p[0], p[0], 255),
        VTexFormat.IA88 => (p[0], p[0], p[0], p[1]),
        VTexFormat.RGBA8888 => (p[0], p[1], p[2], p[3]),
        VTexFormat.BGRA8888 => (p[2], p[1], p[0], p[3]),
        VTexFormat.R16 => ((byte)(U16(p, 0) / 256), 0, 0, 255),
        VTexFormat.RG1616 => ((byte)(U16(p, 0) / 256), (byte)(U16(p, 1) / 256), 0, 255),
        VTexFormat.R16F => (ToClampedLdrColor(F16(p, 0)), 0, 0, 255),
        VTexFormat.RG1616F => (ToClampedLdrColor(F16(p, 0)), ToClampedLdrColor(F16(p, 1)), 0, 255),
        VTexFormat.R32F => (ToClampedLdrColor(F32(p, 0)), 0, 0, 255),
        VTexFormat.RG3232F => (ToClampedLdrColor(F32(p, 0)), ToClampedLdrColor(F32(p, 1)), 0, 255),
        VTexFormat.RGB323232F => (ToClampedLdrColor(F32(p, 0)), ToClampedLdrColor(F32(p, 1)), ToClampedLdrColor(F32(p, 2)), 255),
        _ => (ToClampedLdrColor(F32(p, 0)), ToClampedLdrColor(F32(p, 1)), ToClampedLdrColor(F32(p, 2)), ToClampedLdrColor(F32(p, 3))),
    };

    // RGBA16161616(F) to 8 bits: a Reinhard-style curve on luminance keyed to the log-average
    // luminance of the whole face, then gamma 2.25. Unsigned 16-bit channels count in 1/256ths.
    private static void Tonemap(VTexFormat format, ReadOnlySpan<byte> input, int storedWidth, int width, int height, Span<byte> output)
    {
        var scale = format == VTexFormat.RGBA16161616;
        var log = 0f;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var lum = Channel(input, storedWidth, scale, x, y, 0) * 0.299f + Channel(input, storedWidth, scale, x, y, 1) * 0.587f + Channel(input, storedWidth, scale, x, y, 2) * 0.114f;
                log += MathF.Log(MathF.Max(float.Epsilon, lum));
            }
        }
        log = MathF.Exp(log / (width * height));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                float hr = Channel(input, storedWidth, scale, x, y, 0), hg = Channel(input, storedWidth, scale, x, y, 1), hb = Channel(input, storedWidth, scale, x, y, 2), ha = Channel(input, storedWidth, scale, x, y, 3);
                var luma = (hr * 0.299f) + (hg * 0.587f) + (hb * 0.114f);
                var u = (hb - luma) * 0.565f;
                var v = (hr - luma) * 0.713f;
                var mul = 4.0f * luma / log;
                mul /= 1.0f + mul;
                mul /= luma;
                hr = MathF.Pow((luma + (1.403f * v)) * mul, 2.25f);
                hg = MathF.Pow((luma - (0.344f * u) - (0.714f * v)) * mul, 2.25f);
                hb = MathF.Pow((luma + (1.770f * u)) * mul, 2.25f);
                var o = (y * width + x) * 4;
                output[o] = ToClampedLdrColor(hb);
                output[o + 1] = ToClampedLdrColor(hg);
                output[o + 2] = ToClampedLdrColor(hr);
                output[o + 3] = ToClampedLdrColor(ha);
            }
        }
    }

    private static float Channel(ReadOnlySpan<byte> input, int storedWidth, bool scale, int x, int y, int c)
    {
        var p = input[((y * storedWidth + x) * 8)..];
        return scale ? U16(p, c) / 256f : F16(p, c);
    }

    /// <summary>A float in 0..1 to a byte, clamped and rounded.</summary>
    public static byte ToClampedLdrColor(float value)
    {
        var clamped = value > 1f ? 1f : value < 0f ? 0f : value;
        return (byte)(clamped * 255 + 0.5f);
    }

    private static byte ClampColor(int value) => value > 255 ? (byte)255 : value < 0 ? (byte)0 : (byte)value;

    private static void SwapRB(Span<byte> pixels)
    {
        for (var i = 0; i + 3 < pixels.Length; i += 4)
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
    }

    /// <summary>
    /// Apply <paramref name="codec"/>'s conversions to BGRA pixels: DXT5nm's swap of red and alpha,
    /// then per pixel YCoCg to RGB (from sRGB first when flagged), hemi-octahedral normal to XYZ
    /// (roughness to alpha), and reconstruction of a normal's blue.
    /// </summary>
    public static void ApplyCodec(Span<byte> bgra, TextureCodec codec)
    {
        var swapRA = codec.HasFlag(TextureCodec.Dxt5nm);
        var yCoCg = codec.HasFlag(TextureCodec.YCoCg);
        var linearize = yCoCg && codec.HasFlag(TextureCodec.ColorSpaceSrgb);
        var hemiOct = codec.HasFlag(TextureCodec.HemiOctRB);
        var reconstructZ = codec.HasFlag(TextureCodec.NormalizeNormals);
        if (!swapRA && !yCoCg && !hemiOct && !reconstructZ)
            return;
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var pixel = bgra.Slice(i, 4);
            if (swapRA)
                (pixel[2], pixel[3]) = (pixel[3], pixel[2]);
            if (yCoCg)
            {
                var input = new Vector3(pixel[2], pixel[1], pixel[0]) / 255f;
                if (linearize)
                    input = SrgbGammaToLinear(input);
                var rgb = DecodeYCoCg(new Vector4(input, pixel[3] / 255f));
                pixel[2] = ToClampedLdrColor(rgb.X);
                pixel[1] = ToClampedLdrColor(rgb.Y);
                pixel[0] = ToClampedLdrColor(rgb.Z);
                pixel[3] = 255;
            }
            if (hemiOct)
                DecodeHemiOct(pixel);
            if (reconstructZ)
                ReconstructNormal(pixel);
        }
    }

    private static Vector3 DecodeYCoCg(Vector4 color)
    {
        var scale = (color.Z * (255f / 8f)) + 1f;
        var co = (color.X - (128f / 255f)) / scale;
        var cg = (color.Y - (128f / 255f)) / scale;
        var y = color.W;
        return new Vector3(y + co - cg, y + cg, y - co - cg);
    }

    // sRGB gamma to linear as VRF computes it (pow through exp/log, less a hair for exp(0)).
    private static Vector3 SrgbGammaToLinear(Vector3 gamma)
    {
        var linear = gamma / 12.92f;
        var curve = Vector3.FusedMultiplyAdd(gamma, new Vector3(1f / 1.055f), new Vector3(0.055f / 1.055f));
        curve = Vector3.Exp(2.4f * Vector3.Log(curve));
        curve -= new Vector3(0.0000001f);
        return new Vector3(
            gamma.X <= 0.04045f ? linear.X : curve.X,
            gamma.Y <= 0.04045f ? linear.Y : curve.Y,
            gamma.Z <= 0.04045f ? linear.Z : curve.Z);
    }

    // BGRA pixel: R and G hold a normal's X and Y premultiplied; B becomes sqrt(1 - x² - y²).
    private static void ReconstructNormal(Span<byte> pixel)
    {
        var x = (pixel[2] * 2) - 255;
        var y = (pixel[1] * 2) - 255;
        var z = (int)MathF.Sqrt((255 * 255) - (x * x) - (y * y));
        pixel[2] = ClampColor((x / 2) + 128);
        pixel[1] = ClampColor((y / 2) + 128);
        pixel[0] = ClampColor((z / 2) + 128);
    }

    // BGRA pixel: R+G and R-G give a hemi-octahedral normal; B (roughness) moves to alpha.
    private static void DecodeHemiOct(Span<byte> pixel)
    {
        var nx = ((pixel[2] + pixel[1]) / 255.0f) - 1.003922f;
        var ny = (pixel[2] - pixel[1]) / 255.0f;
        var nz = 1 - MathF.Abs(nx) - MathF.Abs(ny);
        var l = MathF.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
        pixel[3] = pixel[0];
        pixel[2] = (byte)(((nx / l * 0.5f) + 0.5f) * 255);
        pixel[1] = (byte)(((ny / l * 0.5f) + 0.5f) * 255);
        pixel[0] = (byte)(((nz / l * 0.5f) + 0.5f) * 255);
    }
}
