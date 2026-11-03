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
}
