namespace Source2.Compiler.RcTexture;

/// <summary>
/// Image processors <c>CTextureFrame::ApplyImageProcessor</c> (18013f300, resourcecompiler
/// 2026-09-23) runs on an input before its mip algorithm, besides <c>NormalizeNormals</c> and
/// <c>Inverse</c> (<see cref="RcNormalMaps"/>). Each takes the input as RC loads it (four
/// channels; an RGBA8888 load sets every channel's bit in the bitmap's mask, +0x8a0).
/// </summary>
public static class RcImageProcessors
{
    /// <summary>
    /// <c>Mod2XCenter</c> (18013fee0, registering "Texture Compiler Version Image Mod2X"): per
    /// channel, the sum of every pixel in row order, then each pixel <c>(0.5 - sum / (w*h)) + v</c>,
    /// which centres the channel's mean on 0.5 (detail textures).
    /// </summary>
    public static RcMipChain.Level Mod2XCenter(RcMipChain.Level level)
    {
        int n = level.Width * level.Height;
        var o = (float[])level.Rgba.Clone();
        for (int c = 0; c < 4; c++)
        {
            float sum = 0f;
            for (int p = 0; p < n; p++)
                sum += o[p * 4 + c];
            float shift = 0.5f - sum / (float)n;
            for (int p = 0; p < n; p++)
                o[p * 4 + c] = shift + o[p * 4 + c];
        }
        return new RcMipChain.Level(level.Width, level.Height, o);
    }

    /// <summary>
    /// <c>EncodeRGBM</c> (180142040, "Image Encode RGBM" 1): the image copied, then every pixel
    /// RGBM-encoded from the copy as the RGBM mip algorithm encodes its levels
    /// (<see cref="RcMipAlgorithms.EncodeRgbm"/>), alpha taking the multiplier.
    /// </summary>
    public static RcMipChain.Level EncodeRgbm(RcMipChain.Level level)
    {
        var o = (float[])level.Rgba.Clone();
        RcMipAlgorithms.EncodeRgbm(o);
        return new RcMipChain.Level(level.Width, level.Height, o);
    }

    /// <summary>
    /// <c>ConvertToYCoCg</c> (180141720, "Image YCoCg Conversion" 1): the frame written to 8-bit
    /// RGBA (180c45cf0 -> 180c41d70), each pixel turned to integer YCoCg, Co
    /// <c>((2R + 2 - 2B) &gt;&gt; 2) + 128</c> in R, Cg <c>((2G - B - R + 2) &gt;&gt; 2) + 128</c> in G
    /// (arithmetic shifts, both clamped to [0, 255]), 0 in B and Y <c>(R + 2G + B + 2) &gt;&gt; 2</c>
    /// (at most 255) in A; then per 4x4 block (cut at the edges), the largest
    /// <c>|Co - 128|</c> or <c>|Cg - 128|</c>: under 32 the chroma scales by 4 about 128 (clamped)
    /// and B becomes 24, under 64 by 2 with B 8, otherwise the block is left. The bytes are then
    /// loaded back (180c3f4e0). Both ways are linear whatever the input's colour space: the write
    /// is 180c41d70 with no encode, and the load's last argument is 1 (2 would pick the gamma
    /// table, 180c34560). Fewer than three channels refuse; RC's loads here have four.
    /// </summary>
    public static RcMipChain.Level ConvertToYCoCg(RcMipChain.Level level)
    {
        int w = level.Width, h = level.Height;
        var b = RcMipChain.WriteRgba8(level, srgb: false);
        static byte Clamp(int v) => (byte)(v < 0 ? 0 : (v > 255 ? 255 : v));
        for (int i = 0; i < w * h; i++)
        {
            int r = b[i * 4], g = b[i * 4 + 1], bl = b[i * 4 + 2];
            uint y = (uint)(bl + 2 + 2 * g + r) >> 2;
            int cg = ((2 * g - bl - r + 2) >> 2) + 0x80;
            int co = ((r * 2 + 2 - 2 * bl) >> 2) + 0x80;
            b[i * 4] = Clamp(co);
            b[i * 4 + 1] = Clamp(cg);
            b[i * 4 + 2] = 0;
            b[i * 4 + 3] = (byte)Math.Min(y, 255u);
        }
        for (int by = 0; by < (h + 3) / 4; by++)
            for (int bx = 0; bx < (w + 3) / 4; bx++)
            {
                int cols = Math.Min(4, w - bx * 4), rows = Math.Min(4, h - by * 4);
                int max = 0;
                for (int ry = 0; ry < rows; ry++)
                    for (int rx = 0; rx < cols; rx++)
                    {
                        int at = ((by * 4 + ry) * w + bx * 4 + rx) * 4;
                        max = Math.Max(Math.Max(Math.Abs(b[at] - 0x80), max), Math.Abs(b[at + 1] - 0x80));
                    }
                int scale;
                byte blue;
                if (max < 0x20)
                    (scale, blue) = (4, 0x18);
                else if (max < 0x40)
                    (scale, blue) = (2, 8);
                else
                    continue;
                for (int ry = 0; ry < rows; ry++)
                    for (int rx = 0; rx < cols; rx++)
                    {
                        int at = ((by * 4 + ry) * w + bx * 4 + rx) * 4;
                        b[at] = Clamp((b[at] - 0x80) * scale + 0x80);
                        b[at + 1] = Clamp((b[at + 1] - 0x80) * scale + 0x80);
                        b[at + 2] = blue;
                    }
            }
        return RcMipChain.Load(b, w, h, srgb: false);
    }

    /// <summary>
    /// <c>ScaleToPowerOfTwo</c> (inline in 18013f300, "Image ScaleToPowerOfTwo" 1 registered on the
    /// first frame it changes): a 2D frame with a side that is not a power of two is resampled to the
    /// next powers of two with clamping (180c43790 with 1, <see cref="RcMipChain.Resize"/>).
    /// </summary>
    public static RcMipChain.Level ScaleToPowerOfTwo(RcMipChain.Level level)
    {
        static int Next(int v) => (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)v);
        int w = level.Width, h = level.Height;
        if ((w & (w - 1)) == 0 && (h & (h - 1)) == 0)
            return level;
        return RcMipChain.Resize(level, Next(w), Next(h), clamp: true);
    }

    /// <summary>
    /// The size FILL_TO_POWER_OF_TWO compares the texture with, for a <paramref name="width"/> x
    /// <paramref name="height"/> input after <paramref name="processor"/>: ScaleToPowerOfTwo's new
    /// size (it sets no fill flag); the input's own for the fill processors, which set the flag
    /// (FillToMultipleOfFour not for a 1x1 input, so that one is its padded size).
    /// </summary>
    internal static (int Width, int Height) SizeAfter(string processor, int width, int height)
    {
        if (processor.Equals("ScaleToPowerOfTwo", StringComparison.OrdinalIgnoreCase))
            return (NextPowerOfTwo(width), NextPowerOfTwo(height));
        if (processor.Equals("FillToMultipleOfFour", StringComparison.OrdinalIgnoreCase) && width == 1 && height == 1)
            return (4, 4);
        return (width, height);
    }

    private static int NextPowerOfTwo(int v) => (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)v);

    /// <summary>
    /// <c>FillToPowerOfTwo</c> (180140550, "Image FillToPowerOfTwo" 1 on the first frame it
    /// changes): a 2D frame with a side that is not a power of two goes into the top left of a
    /// frame of the next powers of two (181277780), each row's last pixel copied right, the last
    /// row copied down, the corner pixel filling the rest. The compile then writes a fill record
    /// with the original size.
    /// </summary>
    public static RcMipChain.Level FillToPowerOfTwo(RcMipChain.Level level)
    {
        int w = level.Width, h = level.Height;
        if ((w & (w - 1)) == 0 && (h & (h - 1)) == 0)
            return level;
        return Extend(level, NextPowerOfTwo(w), NextPowerOfTwo(h));
    }

    /// <summary>
    /// <c>FillToMultipleOfFour</c> (180140ae0, registering the same "Image FillToPowerOfTwo" 1): a
    /// side that is not a multiple of 4 grows by 3, 2 or 1 (its remainder 1, 2, 3), edges extended
    /// as FillToPowerOfTwo extends them.
    /// </summary>
    public static RcMipChain.Level FillToMultipleOfFour(RcMipChain.Level level)
    {
        int w = level.Width, h = level.Height;
        if (w % 4 == 0 && h % 4 == 0)
            return level;
        int[] pad = [0, 3, 2, 1];
        return Extend(level, w + pad[w % 4], h + pad[h % 4]);
    }

    private static RcMipChain.Level Extend(RcMipChain.Level level, int nw, int nh)
    {
        int w = level.Width, h = level.Height;
        var o = new float[nw * nh * 4];
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
                Array.Copy(level.Rgba, (Math.Min(y, h - 1) * w + Math.Min(x, w - 1)) * 4, o, (y * nw + x) * 4, 4);
        return new RcMipChain.Level(nw, nh, o);
    }

    /// <summary>
    /// <c>HeightToNormal</c> (180141080, "Image HeightToNormal" 1): from channel 0 of a copy, a
    /// 3x3 Sobel with wrapping per pixel, summed as the disassembly does: dx from the columns
    /// either side, dy from the rows; a gradient longer than 1 is made unit, z = sqrt(1 - |g|^2),
    /// and the frame becomes RGB only (180c3f140 with mask 7, alpha its constant 1):
    /// <c>(dx, dy, z) * 0.5 + 0.5</c>.
    /// </summary>
    public static RcMipChain.Level HeightToNormal(RcMipChain.Level level)
    {
        int w = level.Width, h = level.Height;
        float H(int x, int y) => level.Rgba[((y % h) * w + (x % w)) * 4];
        var o = new float[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int xl = x + w - 1, xr = x + w + 1, xc = x + w, yu = y + h - 1, yd = y + h + 1, yc = y + h;
                float a = 0f - H(xl, yu);
                float lc = H(xl, yc), ld = H(xl, yd);
                float ru = H(xr, yu), rc = H(xr, yc), rd = H(xr, yd);
                // The disassembly doubles a term once and adds it (addss x, x), which Ghidra prints as two adds.
                float dx = ((((a - (lc + lc)) - ld) + ru) + (rc + rc)) + rd;
                float cu = H(xc, yu), cd = H(xc, yd);
                float dy = ((((a - (cu + cu)) - ru) + ld) + (cd + cd)) + rd;
                float len = dy * dy + dx * dx;
                if (1f < len)
                {
                    float l = MathF.Sqrt(len);
                    dx /= l;
                    dy /= l;
                    len = 1f;
                }
                float z = MathF.Sqrt(1f - len);
                int at = (y * w + x) * 4;
                (o[at], o[at + 1], o[at + 2], o[at + 3]) = (dx * 0.5f + 0.5f, dy * 0.5f + 0.5f, z * 0.5f + 0.5f, 1f);
            }
        return new RcMipChain.Level(w, h, o);
    }
}
