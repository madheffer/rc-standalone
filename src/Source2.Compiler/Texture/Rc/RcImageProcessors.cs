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
}
