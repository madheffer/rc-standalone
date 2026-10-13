namespace Source2.Compiler.RcTexture;

/// <summary>
/// resourcecompiler's <c>AlphaWeighted</c> mip algorithm (<c>GenerateMips_AlphaWeighted</c>
/// 18014e680), what <c>ArtistDefinedOrAlphaWeighted</c> runs when no artist mips are given
/// (docs/MATERIALS.md): RGB averaged weighted by a second input's alpha, then transparent pixels'
/// colour filled from their neighbours on every level.
/// </summary>
public static class RcAlphaWeighted
{
    /// <summary>
    /// Every level down to 1x1 of <paramref name="image"/>, whose channel 3 holds the weights
    /// (RC copies the weighting input's channel 0 there). Power-of-two sizes only, as RC requires.
    /// </summary>
    public static List<RcMipChain.Level> Mips(RcMipChain.Level image)
    {
        int w = image.Width, h = image.Height;
        if ((w & (w - 1)) != 0 || (h & (h - 1)) != 0)
            throw new InvalidOperationException($"RC cannot alpha-weight a non-power-of-two texture ({w}x{h}).");
        var levels = new List<RcMipChain.Level> { image };
        while (levels[^1].Width > 1 || levels[^1].Height > 1)
            levels.Add(Half(levels[^1]));
        return levels.Select(l => FillTransparent(l, 2)).ToList();
    }

    // The 2x2 step: taps (x0,y0) (x1,y0) (x0,y1) (x1,y1), clamped to the image; with a positive
    // weight sum, colour = (((c00*a00 + c10*a10) + c01*a01) + c11*a11) * (1/sum) and alpha = sum*0.25,
    // else colour = (((c00 + c10) + c01) + c11) * 0.25 and alpha 0.
    private static RcMipChain.Level Half(RcMipChain.Level src)
    {
        int nw = Math.Max(1, src.Width / 2), nh = Math.Max(1, src.Height / 2);
        var o = new float[nw * nh * 4];
        float P(int x, int y, int c) => src.Rgba[(y * src.Width + x) * 4 + c];
        for (int y = 0; y < nh; y++)
        {
            int y0 = Math.Min(2 * y, src.Height - 1), y1 = Math.Min(2 * y + 1, src.Height - 1);
            for (int x = 0; x < nw; x++)
            {
                int x0 = Math.Min(2 * x, src.Width - 1), x1 = Math.Min(2 * x + 1, src.Width - 1);
                float a00 = P(x0, y0, 3), a10 = P(x1, y0, 3), a01 = P(x0, y1, 3), a11 = P(x1, y1, 3);
                float sum = ((a00 + a10) + a01) + a11;
                int d = (y * nw + x) * 4;
                if (sum <= 0f)
                {
                    for (int c = 0; c < 3; c++)
                        o[d + c] = (((P(x0, y0, c) + P(x1, y0, c)) + P(x0, y1, c)) + P(x1, y1, c)) * 0.25f;
                    o[d + 3] = 0f;
                }
                else
                {
                    float inv = 1f / sum;
                    for (int c = 0; c < 3; c++)
                        o[d + c] = (((P(x0, y0, c) * a00 + P(x1, y0, c) * a10) + P(x0, y1, c) * a01) + P(x1, y1, c) * a11) * inv;
                    o[d + 3] = sum * 0.25f;
                }
            }
        }
        return new RcMipChain.Level(nw, nh, o);
    }

    // 180c3c6b0: a pixel whose alpha is not positive takes the alpha-weighted mean colour of the
    // pixels within `radius` (rows outer, columns inner) whose alpha is positive; computed from
    // the unmodified level, written after.
    private static RcMipChain.Level FillTransparent(RcMipChain.Level level, int radius)
    {
        int w = level.Width, h = level.Height;
        var src = level.Rgba;
        var o = (float[])src.Clone();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (src[(y * w + x) * 4 + 3] > 0f)
                    continue;
                float sa = 0f, sr = 0f, sg = 0f, sb = 0f;
                for (int yy = y - radius; yy <= y + radius; yy++)
                {
                    if (yy < 0 || yy >= h)
                        continue;
                    for (int xx = x - radius; xx <= x + radius; xx++)
                    {
                        if (xx < 0 || xx >= w)
                            continue;
                        int s = (yy * w + xx) * 4;
                        float a = src[s + 3];
                        if (!(a > 0f))
                            continue;
                        sa += a;
                        sr += a * src[s];
                        sg += a * src[s + 1];
                        sb += a * src[s + 2];
                    }
                }
                if (sa > 0f)
                {
                    float inv = 1f / sa;
                    int d = (y * w + x) * 4;
                    o[d] = sr * inv;
                    o[d + 1] = sg * inv;
                    o[d + 2] = sb * inv;
                }
            }
        return new RcMipChain.Level(w, h, o);
    }
}
