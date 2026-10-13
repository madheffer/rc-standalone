namespace Source2.Compiler.RcTexture;

/// <summary>
/// resourcecompiler's mip chain (<c>CTextureFrame::GenerateMips_Box</c> over <c>FloatBitMap_t</c>),
/// value for value: decode through tier0's gamma table, downsample in float (never re-quantized),
/// encode with the SIMD <c>x^(53/128)</c> sequence and write with RC's two rounding paths.
/// docs/TEXTURES.md has the measurements and addresses.
/// </summary>
public static class RcMipChain
{
    /// <summary>
    /// <c>tier0!g_Mathlib_GammaToLinearByteTable</c>: the 128/53 power curve as tier0 ships it,
    /// six-decimal floats that no formula reproduces, carried as bit patterns.
    /// </summary>
    private static readonly uint[] GammaToLinearBits =
    [
        0x00000000, 0x399f6230, 0x3a1f1f15, 0x3a6ed02d, 0x3a9f1f15, 0x3ac6f7a1, 0x3aeeae9f, 0x3b0b4396,
        0x3b1f1f15, 0x3b330b5b, 0x3b46e6da, 0x3b539da1, 0x3b689657, 0x3b7ecdd1, 0x3b8b1141, 0x3b975afb,
        0x3ba44418, 0x3bb1cc96, 0x3bbff477, 0x3bcebbba, 0x3bde2ac3, 0x3bee49f5, 0x3bff0889, 0x3c083ba3,
        0x3c114b16, 0x3c1ab29e, 0x3c24723b, 0x3c2e89ec, 0x3c38fde3, 0x3c43ce21, 0x3c4efaa4, 0x3c5a836f,
        0x3c666cb1, 0x3c72b23a, 0x3c7f5c6c, 0x3c863172, 0x3c8ce704, 0x3c93ccd1, 0x3c9ae4f3, 0x3ca22d51,
        0x3ca9aa1d, 0x3cb1593e, 0x3cb93ab4, 0x3cc152b1, 0x3cc99ae9, 0x3cd219a8, 0x3cdaccd5, 0x3ce3b689,
        0x3cecd291, 0x3cf62739, 0x3cffb04f, 0x3d04b902, 0x3d09b414, 0x3d0ecb75, 0x3d13ff26, 0x3d194e1a,
        0x3d1eb95e, 0x3d2440f2, 0x3d29e4d6, 0x3d2fa616, 0x3d3583a5, 0x3d3b7d85, 0x3d4195cd, 0x3d47ca64,
        0x3d4e1c58, 0x3d548cb5, 0x3d5b1a6d, 0x3d61c68f, 0x3d68900c, 0x3d6f77f2, 0x3d767f4e, 0x3d7da405,
        0x3d827419, 0x3d862563, 0x3d89e5e2, 0x3d8db61c, 0x3d919610, 0x3d958644, 0x3d9985ad, 0x3d9d94d1,
        0x3da1b435, 0x3da5e354, 0x3daa22b4, 0x3dae7254, 0x3db2d235, 0x3db74257, 0x3dbbc2b9, 0x3dc0535d,
        0x3dc4f4c7, 0x3dc9a672, 0x3dce68e4, 0x3dd33c1d, 0x3dd81f97, 0x3ddd145e, 0x3de219eb, 0x3de730c6,
        0x3dec5868, 0x3df190d1, 0x3df6da88, 0x3dfc358b, 0x3e00d0ee, 0x3e038fbd, 0x3e065732, 0x3e092791,
        0x3e0c0097, 0x3e0ee243, 0x3e11ccd9, 0x3e14c059, 0x3e17bcc3, 0x3e1ac216, 0x3e1dd053, 0x3e20e779,
        0x3e240789, 0x3e2730c6, 0x3e2a6330, 0x3e2d9e84, 0x3e30e304, 0x3e3430b1, 0x3e37878b, 0x3e3ae792,
        0x3e3e5109, 0x3e41c3ad, 0x3e453f7d, 0x3e48c4bd, 0x3e4c532a, 0x3e4feb07, 0x3e538c97, 0x3e573754,
        0x3e5aeb81, 0x3e5ea961, 0x3e6270b0, 0x3e664170, 0x3e6a1be3, 0x3e6dffc5, 0x3e71ed9e, 0x3e75e4e7,
        0x3e79e5e2, 0x3e7df091, 0x3e810279, 0x3e8311a5, 0x3e8525ab, 0x3e873e8a, 0x3e895c64, 0x3e8b7f17,
        0x3e8da6e7, 0x3e8fd391, 0x3e920536, 0x3e943bd6, 0x3e967792, 0x3e98b828, 0x3e9afddb, 0x3e9d48aa,
        0x3e9f9874, 0x3ea1ed39, 0x3ea4473d, 0x3ea6a63b, 0x3ea90a57, 0x3eab738e, 0x3eade204, 0x3eb05575,
        0x3eb2ce25, 0x3eb54bf1, 0x3eb7cefb, 0x3eba5721, 0x3ebce486, 0x3ebf7729, 0x3ec20f0a, 0x3ec4ac2a,
        0x3ec74e66, 0x3ec9f601, 0x3ecca2db, 0x3ecf5515, 0x3ed20c8d, 0x3ed4c943, 0x3ed78b59, 0x3eda52ce,
        0x3edd1fa3, 0x3edff1b7, 0x3ee2c92a, 0x3ee5a61e, 0x3ee88851, 0x3eeb7004, 0x3eee5d18, 0x3ef14f8b,
        0x3ef447a1, 0x3ef744f6, 0x3efa47cb, 0x3efd5022, 0x3f002efd, 0x3f01b8aa, 0x3f034517, 0x3f04d445,
        0x3f066634, 0x3f07fae3, 0x3f099264, 0x3f0b2ca5, 0x3f0cc9a7, 0x3f0e698c, 0x3f100c31, 0x3f11b196,
        0x3f1359de, 0x3f1504f7, 0x3f16b2d1, 0x3f18638d, 0x3f1a171a, 0x3f1bcd79, 0x3f1d86ba, 0x3f1f42bb,
        0x3f2101b0, 0x3f22c376, 0x3f24881e, 0x3f264f98, 0x3f281a05, 0x3f29e743, 0x3f2bb763, 0x3f2d8a65,
        0x3f2f605b, 0x3f313932, 0x3f3314ec, 0x3f34f399, 0x3f36d528, 0x3f38b999, 0x3f3aa10e, 0x3f3c8b54,
        0x3f3e789e, 0x3f4068dc, 0x3f425bfb, 0x3f44521e, 0x3f464b34, 0x3f48473d, 0x3f4a4639, 0x3f4c4838,
        0x3f4e4d1a, 0x3f505511, 0x3f525ffa, 0x3f546de7, 0x3f567ec8, 0x3f5892bc, 0x3f5aa9a4, 0x3f5cc38f,
        0x3f5ee07e, 0x3f610071, 0x3f632379, 0x3f654973, 0x3f677293, 0x3f699ea5, 0x3f6bcdcd, 0x3f6dfff8,
        0x3f703548, 0x3f726d8b, 0x3f74a8f4, 0x3f76e760, 0x3f7928f2, 0x3f7b6d87, 0x3f7db542, 0x3f800000,
    ];

    private static readonly float[] GammaToLinear = GammaToLinearBits.Select(BitConverter.UInt32BitsToSingle).ToArray();

    /// <summary>One mip level: RGBA floats, interleaved, row-major.</summary>
    public sealed record Level(int Width, int Height, float[] Rgba);

    /// <summary>
    /// The full chain down to 1x1. RGB goes through the gamma table when <paramref name="srgb"/>;
    /// see the loader rules below for everything else.
    /// </summary>
    public static List<Level> Build(ReadOnlySpan<byte> rgba, int width, int height, bool srgb)
    {
        // FloatBitMap_t::LoadFromBuffer, RGBA8888. sRGB (180c34560): the gamma table, alpha v / 255f.
        // Linear (180c32920): a width that is a multiple of 4 goes through SSE as v * (1/255f),
        // any other width is scalar v / 255f, all four channels.
        bool simd = !srgb && width >= 4 && width % 4 == 0;
        var top = new float[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            for (int c = 0; c < 4; c++)
            {
                byte v = rgba[i * 4 + c];
                top[i * 4 + c] = srgb && c < 3 ? GammaToLinear[v]
                    : simd ? v * (1f / 255f)
                    : v / 255f;
            }
        }
        var levels = new List<Level> { new(width, height, top) };
        while (levels[^1].Width > 1 || levels[^1].Height > 1)
            levels.Add(QuarterSize(levels[^1]));
        return levels;
    }

    /// <summary>
    /// <c>FloatBitMap_t::QuarterSize</c>: the new size is <c>w &gt; 3 ? w / 2 : 1</c> by
    /// <c>max(1, h / 2)</c>. Both sides even: a 2x2 box. A side that is odd and halves to
    /// <c>n</c> or rounds to it: a 3-tap filter per axis. Anything else (a side of 1): a clamped box.
    /// Every level is float for float what RC holds (Frida captures of QuarterSize on 256x256,
    /// 300x200, 7x5 and a linear ATI1N compile); the 1x1 level is the header's reflectivity.
    /// </summary>
    public static Level QuarterSize(Level src)
    {
        int W = src.Width, H = src.Height;
        int n = W > 3 ? W / 2 : 1, m = Math.Max(1, H / 2);
        var a = src.Rgba;
        var o = new float[n * m * 4];
        float P(int y, int x, int c) => a[(y * W + x) * 4 + c];

        if (W % 2 == 0 && H % 2 == 0)
        {
            // 1812764c0: the SSE loop (an output width that is a multiple of 4, at least 8) adds the
            // two column pairs; the scalar loop adds the left pair, then TR, then BR.
            bool sse = n % 4 == 0 && n >= 8;
            for (int y = 0; y < m; y++)
                for (int x = 0; x < n; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        float tl = P(2 * y, 2 * x, c), tr = P(2 * y, 2 * x + 1, c);
                        float bl = P(2 * y + 1, 2 * x, c), br = P(2 * y + 1, 2 * x + 1, c);
                        o[(y * n + x) * 4 + c] = (sse ? (tl + bl) + (tr + br) : ((tl + bl) + tr) + br) * 0.25f;
                    }
            return new Level(n, m, o);
        }

        bool tapsX = n * 2 + 1 == W || n * 2 == W, tapsY = m * 2 + 1 == H || m * 2 == H;
        if (!tapsX || !tapsY)
        {
            for (int y = 0; y < m; y++)
            {
                int r0 = Math.Min(2 * y, H - 1), r1 = Math.Min(2 * y + 1, H - 1);
                for (int x = 0; x < n; x++)
                {
                    int c0 = Math.Min(2 * x, W - 1), c1 = Math.Min(2 * x + 1, W - 1);
                    for (int c = 0; c < 4; c++)
                        o[(y * n + x) * 4 + c] = (((P(r0, c1, c) + P(r0, c0, c)) + P(r1, c0, c)) + P(r1, c1, c)) * 0.25f;
                }
            }
            return new Level(n, m, o);
        }

        float iw = 1f / W, ih = 1f / H;
        for (int y = 0; y < m; y++)
        {
            int r0 = Math.Min(2 * y, H - 1), r1 = Math.Min(2 * y + 1, H - 1), r2 = Math.Min(2 * y + 2, H - 1);
            for (int x = 0; x < n; x++)
            {
                int c0 = Math.Min(2 * x, W - 1), c1 = Math.Min(2 * x + 1, W - 1), c2 = Math.Min(2 * x + 2, W - 1);
                float w0 = (n - x) * iw, w1 = n * iw, w2 = (x + 1) * iw;
                // 180c42690: each row adds c0, c1, then c2; each row factor is a single product.
                float ft = (m - y) * ih, fm = m * ih, fb = (y + 1) * ih;
                for (int c = 0; c < 4; c++)
                {
                    float top = (w0 * P(r0, c0, c) + w1 * P(r0, c1, c)) + w2 * P(r0, c2, c);
                    float mid = (w0 * P(r1, c0, c) + w1 * P(r1, c1, c)) + w2 * P(r1, c2, c);
                    float bot = (w0 * P(r2, c0, c) + w1 * P(r2, c1, c)) + w2 * P(r2, c2, c);
                    o[(y * n + x) * 4 + c] = (top * ft + mid * fm) + bot * fb;
                }
            }
        }
        return new Level(n, m, o);
    }

    /// <summary>
    /// <c>FloatBitMap_t</c> bilinear resample (181279940): sample centres at
    /// <c>(i + 0.5) * src / dst</c>, accumulated in float; wrap unless <paramref name="clamp"/>.
    /// </summary>
    public static Level Resize(Level img, int nw, int nh, bool clamp)
    {
        if (nw == img.Width && nh == img.Height)
            return img;
        var o = new float[nw * nh * 4];
        float sx = (float)img.Width / nw, sy = (float)img.Height / nh;
        float fy = (float)img.Height / nh * 0.5f;
        Span<float> px = stackalloc float[4];
        for (int y = 0; y < nh; y++)
        {
            float fx = (float)img.Width / nw * 0.5f;
            for (int x = 0; x < nw; x++)
            {
                Sample(img, fx, fy, clamp, px);
                px.CopyTo(o.AsSpan((y * nw + x) * 4, 4));
                fx += sx;
            }
            fy += sy;
        }
        return new Level(nw, nh, o);
    }

    private static void Sample(Level img, float fx, float fy, bool clamp, Span<float> px)
    {
        int W = img.Width, H = img.Height;
        int x0, x1, y0, y1;
        float tx, ty;
        fx -= 0.5f;
        if (!clamp)
        {
            if (fx < 0) fx += W;
            fy -= 0.5f;
            if (fy < 0) fy += H;
            x0 = (int)MathF.Floor(fx); x1 = x0 + 1; tx = fx - x0;
            if (x1 >= W) x1 -= W;
            y0 = (int)MathF.Floor(fy); y1 = y0 + 1; ty = fy - y0;
            if (y1 >= H) y1 -= H;
        }
        else
        {
            tx = MathF.Min(MathF.Max(0f, fx), W - 1);
            ty = MathF.Min(MathF.Max(0f, fy - 0.5f), H - 1);
            x0 = (int)MathF.Floor(tx); x1 = Math.Min(x0 + 1, W - 1); tx -= x0;
            y0 = (int)MathF.Floor(ty); y1 = Math.Min(y0 + 1, H - 1); ty -= y0;
        }
        var a = img.Rgba;
        for (int c = 0; c < 4; c++)
        {
            float p00 = a[(y0 * W + x0) * 4 + c], p01 = a[(y0 * W + x1) * 4 + c];
            float p10 = a[(y1 * W + x0) * 4 + c], p11 = a[(y1 * W + x1) * 4 + c];
            float top = (p01 - p00) * tx + p00;
            float bot = (p11 - p10) * tx + p10;
            px[c] = (bot - top) * ty + top;
        }
    }

    /// <summary>
    /// The 32x32 <c>FALLBACK_BITS</c> image, as 8-bit RGBA: the smallest mip whose shorter side
    /// is still at least 32 (mip 0 when none is), gamma-encoded when sRGB, resampled to 32x32 and
    /// written. RC then compresses it as DXT5 with perceptual weights.
    /// </summary>
    public static byte[] FallbackRgba8(IReadOnlyList<Level> levels, bool srgb, bool clamp)
    {
        var pick = levels[0];
        foreach (var l in levels)
            if (Math.Min(l.Width, l.Height) >= 32)
                pick = l;
        var encoded = new float[pick.Rgba.Length];
        for (int i = 0; i < encoded.Length; i++)
            encoded[i] = srgb && i % 4 < 3 ? Encode(pick.Rgba[i]) : pick.Rgba[i];
        var small = Resize(new Level(pick.Width, pick.Height, encoded), 32, 32, clamp);
        return WriteRgba8(small, srgb: false);
    }

    /// <summary>
    /// Linear to sRGB as the SIMD encoder computes <c>x^(53/128)</c> (18126a7c0): a sequence of
    /// square roots and products, then <c>1.055 r - 0.055</c>, or <c>12.92 x</c> at or below 0.0031308.
    /// </summary>
    public static float Encode(float v)
    {
        float x = MathF.Min(1f, MathF.Max(0f, v));
        if (x <= 0.0031308f)
            return 12.92f * x;
        float s1 = MathF.Sqrt(x), s2 = MathF.Sqrt(s1), s3 = MathF.Sqrt(s2), s4 = MathF.Sqrt(s3);
        float m = s2 * s3;
        float s5 = MathF.Sqrt(s4);
        m *= s4;
        float s6 = MathF.Sqrt(s5);
        m *= s6;
        float s7 = MathF.Sqrt(s6);
        float r = 1f / (s5 * s7) * m;
        return 1.055f * r - 0.055f;
    }

    /// <summary>
    /// <c>FloatBitMap_t::WriteToBuffer</c> to 8-bit RGBA (180c41d70): a row whose width is a
    /// multiple of 4 goes through SSE (<c>x*255</c>, clamp, round to nearest even); any other
    /// row is scalar <c>(int)(clamp(x, 0, 1) * 255 + 0.5f)</c>. sRGB channels are encoded first.
    /// </summary>
    public static byte[] WriteRgba8(Level level, bool srgb)
    {
        var o = new byte[level.Width * level.Height * 4];
        bool simd = level.Width >= 4 && level.Width % 4 == 0;
        for (int i = 0; i < level.Width * level.Height; i++)
            for (int c = 0; c < 4; c++)
            {
                float v = level.Rgba[i * 4 + c];
                if (srgb && c < 3) v = Encode(v);
                o[i * 4 + c] = simd
                    ? (byte)MathF.Round(MathF.Min(255f, MathF.Max(0f, v * 255f)), MidpointRounding.ToEven)
                    : (byte)(int)(MathF.Min(1f, MathF.Max(0f, v)) * 255f + 0.5f);
            }
        return o;
    }
}
