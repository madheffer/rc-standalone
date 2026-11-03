namespace Source2.Compiler.RcTexture;

/// <summary>
/// The mip algorithms <c>CTextureFrame::GenerateMips</c> (180142990, resourcecompiler 2026-09-23)
/// dispatches to beyond the Box chain, the normal maps and alpha weighting, each read from its
/// decompile and disassembly (docs/TEXTURES.md "Mip algorithms"). Every function takes the
/// group's whole input image as RC loads it and returns the chain RC holds.
/// </summary>
public static class RcMipAlgorithms
{
    /// <summary><c>None</c> (180144200): the input copied as the only level.</summary>
    public static List<RcMipChain.Level> None(RcMipChain.Level input) => [input];

    /// <summary>
    /// <c>BoxInverse</c> (<c>GenerateMips_BoxInverse</c> 1801447c0): level 0 is the input with
    /// every channel negated then raised by one (<c>v * -1 + 1</c>, which is <c>1 - v</c> exactly),
    /// each later level the 2x2 box of the one before (180c43450). Power-of-two sizes only:
    /// "Cannot box filter non-power-of-two texture".
    /// </summary>
    public static List<RcMipChain.Level> BoxInverse(RcMipChain.Level input)
    {
        RequirePowerOfTwo(input, "BoxInverse");
        return RcMipChain.Chain(new RcMipChain.Level(input.Width, input.Height, input.Rgba.Select(v => v * -1f + 1f).ToArray()));
    }

    /// <summary>
    /// <c>GenerateMips_Multiply</c> (180144ce0, for <c>MultiplyBox</c> and <c>MultiplyNoMip</c>):
    /// level 0 is the first input with each channel multiplied by the second input's same channel
    /// (the second resized to the first's size with clamping, 180c43790), then the 2x2 box chain
    /// (180c43450) for MultiplyBox, one level for MultiplyNoMip. When the first input is the
    /// smaller, RC sizes the levels by the larger but copies the first input unresized; refused.
    /// MultiplyBox needs power-of-two sides ("Cannot filter non-power-of-two texture when creating mips").
    /// </summary>
    public static List<RcMipChain.Level> Multiply(RcMipChain.Level a, RcMipChain.Level b, bool mips)
    {
        if (b.Width > a.Width || b.Height > a.Height)
            throw new InvalidOperationException($"Multiply with a first input smaller than the second ({a.Width}x{a.Height} and {b.Width}x{b.Height}) is not measured.");
        if (mips)
            RequirePowerOfTwo(a, "MultiplyBox");
        if (b.Width != a.Width || b.Height != a.Height)
            b = RcMipChain.Resize(b, a.Width, a.Height, clamp: true);
        var top = new float[a.Rgba.Length];
        for (int i = 0; i < top.Length; i++)
            top[i] = b.Rgba[i] * a.Rgba[i];
        var level = new RcMipChain.Level(a.Width, a.Height, top);
        return mips ? RcMipChain.Chain(level) : [level];
    }

    /// <summary>
    /// <c>GenerateMips_MipSize</c> (18014e260, for <c>MipWidth</c>, <c>MipHeight</c>, <c>MipDepth</c>):
    /// the input's pixels are not read; every level of the full chain is a red-only frame
    /// (180c3f140 with mask 1, alpha 1) filled with <c>logf(side) / logf(2) * 0.0625</c> for that
    /// level's width, height or depth (sides halved, at least 1). Power-of-two sizes only.
    /// </summary>
    public static List<RcMipChain.Level> MipSize(RcMipChain.Level input, int axis)
    {
        RequirePowerOfTwo(input, axis switch { 0 => "MipWidth", 1 => "MipHeight", _ => "MipDepth" });
        var levels = new List<RcMipChain.Level>();
        int w = input.Width, h = input.Height, d = 1;
        // 180c49440: the full chain's count, down to 1x1.
        int n = 1 + System.Numerics.BitOperations.Log2((uint)Math.Max(w, h));
        for (int k = 0; k < n; k++)
        {
            float v = MathF.Log(axis switch { 0 => w, 1 => h, _ => d }) / MathF.Log(2f) * 0.0625f;
            var px = new float[w * h * 4];
            for (int i = 0; i < w * h; i++)
                (px[i * 4], px[i * 4 + 3]) = (v, 1f);
            levels.Add(new RcMipChain.Level(w, h, px));
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }
        return levels;
    }

    /// <summary>
    /// <c>GenerateMips_RGBM</c> (180151310) on a 2D texture: level 0 is a four-channel frame
    /// (180c3f140, mask 0xf) taking the input's channels 0..2 (181277690) with alpha 1, each later
    /// level the 2x2 box of the one before (180c43450), then every level RGBM-encoded (180150f60).
    /// The six-face irradiance pass that follows for cubemaps is not ported. Power-of-two only.
    /// </summary>
    public static List<RcMipChain.Level> Rgbm(RcMipChain.Level input)
    {
        RequirePowerOfTwo(input, "RGBM");
        var top = (float[])input.Rgba.Clone();
        for (int i = 3; i < top.Length; i += 4)
            top[i] = 1f;
        var levels = RcMipChain.Chain(new RcMipChain.Level(input.Width, input.Height, top));
        foreach (var level in levels)
            EncodeRgbm(level.Rgba);
        return levels;
    }

    /// <summary>
    /// 180150f60, per pixel: each colour channel clamped to [0, 36] (<c>v &lt;= 0</c> gives +0),
    /// its root times 1/6 clamped to [0, 1]; M = <c>ceilf(max(r, g, max(b, 1/6)) * 255) / 255</c>
    /// (the maxima taken as RC compares them); the pixel becomes (r / M, g / M, b / M, M).
    /// </summary>
    internal static void EncodeRgbm(float[] px)
    {
        const float Sixth = 0.16666667f;
        static float Clamp(float v, float hi) => v <= 0f ? 0f : (hi <= v ? hi : v);
        for (int i = 0; i < px.Length; i += 4)
        {
            float r = Clamp(MathF.Sqrt(Clamp(px[i], 36f)) * Sixth, 1f);
            float g = Clamp(MathF.Sqrt(Clamp(px[i + 1], 36f)) * Sixth, 1f);
            float b = Clamp(MathF.Sqrt(Clamp(px[i + 2], 36f)) * Sixth, 1f);
            float m = r <= g ? g : r;
            float bb = b <= Sixth ? Sixth : b;
            if (m <= bb)
                m = bb;
            m = MathF.Ceiling(m * 255f) / 255f;
            (px[i], px[i + 1], px[i + 2], px[i + 3]) = (r / m, g / m, b / m, m);
        }
    }

    /// <summary>
    /// <c>GenerateMips_DepthPyramid</c> (180155950; <c>DepthPyramidMin</c> when the name ends in
    /// "Min", else Max; registers "Mip Box" 1): square power-of-two 2D only. Level 0 is the input;
    /// each later level is the 2x2 box of the one before (180c43450), then its channel 0 is the
    /// max (min) of the previous level's channel 0 over the 2x2 block, taken as RC compares:
    /// a = (x, y) vs (x+1, y), b = (x, y+1) vs (x+1, y+1), then a vs b. The level cap is
    /// 180133da0's max mip size (gameinfo "maxmipsize", default 0x100000) compared as a count,
    /// so the chain is whole unless a .vtex sets it (refused here).
    /// </summary>
    public static List<RcMipChain.Level> DepthPyramid(RcMipChain.Level input, bool min)
    {
        RequirePowerOfTwo(input, min ? "DepthPyramidMin" : "DepthPyramidMax");
        if (input.Width != input.Height)
            throw new InvalidOperationException($"RC cannot build a depth pyramid from a non-square texture ({input.Width}x{input.Height}).");
        var levels = new List<RcMipChain.Level> { input };
        while (levels[^1].Width > 1)
        {
            var prev = levels[^1];
            var next = RcMipChain.QuarterSize(prev);
            int pw = prev.Width;
            for (int y = 0; y < prev.Height; y += 2)
                for (int x = 0; x < pw; x += 2)
                {
                    float a = prev.Rgba[(y * pw + x) * 4], b = prev.Rgba[(y * pw + x + 1) * 4];
                    float c = prev.Rgba[((y + 1) * pw + x) * 4], d = prev.Rgba[((y + 1) * pw + x + 1) * 4];
                    if (min)
                    {
                        if (b <= a) a = b;
                        if (d <= c) c = d;
                        if (c <= a) a = c;
                    }
                    else
                    {
                        if (a <= b) a = b;
                        if (c <= d) c = d;
                        if (a <= c) a = c;
                    }
                    next.Rgba[((y / 2) * next.Width + x / 2) * 4] = a;
                }
            levels.Add(next);
        }
        return levels;
    }

    /// <summary>
    /// <c>GenerateMips_AutoLevels</c> (180147460, "Mip Auto Levels" 1, then the Box chain's
    /// entry): the Box chain (1801443f0), then every level remapped by level 0's extremes. The
    /// options are the algorithm name after "AutoLevels", lowercased: "perchannel" normalises R, G
    /// and B each by their own min and max; otherwise R, G and B all become the normalised
    /// luminance <c>wR R + (wB B + wG G)</c> (the disassembly's order), with weights 0.3333 each, or 0.2125, 0.7154, 0.0721 with
    /// "luminance". Alpha is always normalised alone. Mins start at 1, maxes at 0; a normalised value
    /// is <c>(v - min) / (max - min)</c> clamped to [0, 1], or, when min equals max, 0 below max and 1
    /// otherwise. "gammatolinear" (the level converted by 180c3bc80 first, other weights) is refused.
    /// </summary>
    public static List<RcMipChain.Level> AutoLevels(RcMipChain.Level input, string options)
    {
        options = options.ToLowerInvariant();
        if (options.Contains("gammatolinear", StringComparison.Ordinal))
            throw new InvalidOperationException("AutoLevels with GammaToLinear is not measured.");
        bool perChannel = options.Contains("perchannel", StringComparison.Ordinal);
        var (wr, wg, wb) = options.Contains("luminance", StringComparison.Ordinal)
            ? (0.2125f, BitConverter.UInt32BitsToSingle(0x3f372474), 0.0721f)
            : (0.3333f, BitConverter.UInt32BitsToSingle(0x3eaaa64c), 0.3333f);
        var levels = RcMipChain.Chain(input);
        float[] lo = [1f, 1f, 1f, 1f], hi = [0f, 0f, 0f, 0f];
        float lumLo = 1f, lumHi = 0f;
        var top = levels[0].Rgba;
        for (int i = 0; i < top.Length; i += 4)
        {
            for (int c = 0; c < 4; c++)
            {
                float v = top[i + c];
                if (v <= lo[c]) lo[c] = v;
                if (hi[c] <= v) hi[c] = v;
            }
            if (!perChannel)
            {
                float l = wr * top[i] + (top[i + 2] * wb + top[i + 1] * wg);
                if (l <= lumLo) lumLo = l;
                if (lumHi <= l) lumHi = l;
            }
        }
        static float Norm(float v, float min, float max)
        {
            if (min != max)
            {
                float t = (v - min) / (max - min);
                if (t <= 0f) t = 0f;
                if (1f <= t) t = 1f;
                return t + 0f;
            }
            return v - max < 0f ? 0f : 1f;
        }
        foreach (var level in levels)
        {
            var o = level.Rgba;
            for (int i = 0; i < o.Length; i += 4)
            {
                float r = o[i], g = o[i + 1], b = o[i + 2], a = o[i + 3];
                if (perChannel)
                    (o[i], o[i + 1], o[i + 2]) = (Norm(r, lo[0], hi[0]), Norm(g, lo[1], hi[1]), Norm(b, lo[2], hi[2]));
                else
                {
                    float l = Norm(wr * r + (b * wb + g * wg), lumLo, lumHi);
                    (o[i], o[i + 1], o[i + 2]) = (l, l, l);
                }
                o[i + 3] = Norm(a, lo[3], hi[3]);
            }
        }
        return levels;
    }

    /// <summary>
    /// <c>GenerateMips_HeightCombine</c> (180145330, "Mip Height Combine" 1): two inputs, the
    /// first one's power-of-two size. Level 0's channel 0 combines a = the first input's channel 0
    /// with b = the second's at (x mod its width, y mod its height): by default max(a, b) (b when
    /// a &lt;= b); "method2" in the options gives <c>((1 - a) + b) * 0.5</c>; "sumnormalized"
    /// <c>((2b - 1) + (2a - 1)) * 0.5 + 0.5</c> clamped to [0, 1]. Later levels are 2x2 boxes. The
    /// frame's other channels (180c35d20) are never written, so RC holds heap contents there; a group
    /// may read only red.
    /// </summary>
    public static List<RcMipChain.Level> HeightCombine(RcMipChain.Level a, RcMipChain.Level b, string options)
    {
        RequirePowerOfTwo(a, "HeightCombine");
        options = options.ToLowerInvariant();
        int method = options.Contains("method2", StringComparison.Ordinal) ? 1 : options.Contains("sumnormalized", StringComparison.Ordinal) ? 2 : 0;
        int w = a.Width, h = a.Height;
        var o = new float[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float va = a.Rgba[(y * w + x) * 4];
                float vb = b.Rgba[((y % b.Height) * b.Width + x % b.Width) * 4];
                float v;
                if (method == 0)
                    v = va <= vb ? vb : va;
                else if (method == 1)
                    v = ((1f - va) + vb) * 0.5f;
                else
                {
                    v = (((vb + vb) - 1f) + ((va + va) - 1f)) * 0.5f + 0.5f;
                    if (v <= 0f) v = 0f;
                    if (1f <= v) v = 1f;
                }
                o[(y * w + x) * 4] = v;
            }
        return RcMipChain.Chain(new RcMipChain.Level(w, h, o));
    }

    /// <summary>
    /// 1801521b0: <paramref name="taps"/> binomial weights, mirrored about the centre and divided
    /// by their sum: w0 = 1, w(i+1) = ((float)(taps - 1 - i) * wi) / (float)(i + 1); the sum adds
    /// each pair as <c>wi + wi</c> (the centre once) to the running total.
    /// </summary>
    internal static float[] BinomialKernel(int taps)
    {
        var k = new float[taps];
        float f = 1f, sum = 0f;
        for (int i = 0; i < taps / 2 + 1; i++)
        {
            k[i] = f;
            float add = f;
            if (i != taps / 2)
            {
                k[taps - 1 - i] = f;
                add = f + f;
            }
            sum = add + sum;
            f = ((float)(taps - 1 - i) * f) / (float)(i + 1);
        }
        for (int i = 0; i < taps; i++)
            k[i] /= sum;
        return k;
    }

    /// <summary>
    /// 180c3a150 on a 2D level: in place, a pass along x then one along y (z only for volumes),
    /// each output <c>sum over i in [-(taps - 1), taps - 1] of k[|i|] * v[(p + i) mod size]</c>,
    /// accumulated tap by tap as <c>k * v + acc</c> from 0, all four channels, from a copy of the
    /// line so the pass reads the values before it.
    /// </summary>
    internal static void SeparableWrapFilter(RcMipChain.Level level, float[] k)
    {
        int r = k.Length - 1, w = level.Width, h = level.Height;
        var px = level.Rgba;
        static int Wrap(int i, int n) => ((i % n) + n) % n;
        var line = new float[(Math.Max(w, h) + 2 * r + 1) * 4];
        for (int y = 0; y < h; y++)
        {
            for (int i = -r; i <= w + r - 1; i++)
                Array.Copy(px, (y * w + Wrap(i, w)) * 4, line, (i + r) * 4, 4);
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 4; c++)
                {
                    float acc = 0f;
                    for (int i = -r; i <= r; i++)
                        acc = k[Math.Abs(i)] * line[(x + i + r) * 4 + c] + acc;
                    px[(y * w + x) * 4 + c] = acc;
                }
        }
        for (int x = 0; x < w; x++)
        {
            for (int i = -r; i <= h + r - 1; i++)
                Array.Copy(px, (Wrap(i, h) * w + x) * 4, line, (i + r) * 4, 4);
            for (int y = 0; y < h; y++)
                for (int c = 0; c < 4; c++)
                {
                    float acc = 0f;
                    for (int i = -r; i <= r; i++)
                        acc = k[Math.Abs(i)] * line[(y + i + r) * 4 + c] + acc;
                    px[(y * w + x) * 4 + c] = acc;
                }
        }
    }

    /// <summary>
    /// <c>WrapGaussian</c> (180152360, "Mip WrapGaussian" 1): sigma from the algorithm's float
    /// argument, 3 when 0 (arguments are refused here, so always 3), giving
    /// <c>((int)(sigma * -2) / 2) * -2 + 1</c> taps (7) of <see cref="BinomialKernel"/>; then
    /// 180152520: level 0 is the input, each later level the previous one filtered
    /// (<see cref="SeparableWrapFilter"/>, which with k[|i|] over 2 taps - 1 positions gains about 4x a
    /// level) and quarter-sized (180c43450). Power-of-two only.
    /// </summary>
    public static List<RcMipChain.Level> WrapGaussian(RcMipChain.Level input)
    {
        RequirePowerOfTwo(input, "WrapGaussian");
        float sigma = 3f;
        int taps = ((int)(sigma * -2f) / 2) * -2 + 1;
        var k = BinomialKernel(taps);
        var levels = new List<RcMipChain.Level> { input };
        while (levels[^1].Width > 1 || levels[^1].Height > 1)
        {
            var copy = new RcMipChain.Level(levels[^1].Width, levels[^1].Height, (float[])levels[^1].Rgba.Clone());
            SeparableWrapFilter(copy, k);
            levels.Add(RcMipChain.QuarterSize(copy));
        }
        return levels;
    }

    // CTextureFrame's coverage samples (18014f380): 16 points per 2x2 cell, each four bilinear
    // weights for the corners (x+1,y), (x+1,y+1), (x,y), (x,y+1), carried as RC's bit patterns.
    private static readonly uint[] CoverageWeightBits =
    [
        0x3d800000, 0x3e400000, 0x3e400000, 0x3f100000,
        0x3f100000, 0x3e400000, 0x3e400000, 0x3d800000,
        0x3c800000, 0x3de00000, 0x3de00000, 0x3f440000,
        0x3ec80000, 0x3e700000, 0x3e700000, 0x3e100000,
        0x3e100000, 0x3e700000, 0x3e700000, 0x3ec80000,
        0x3f440000, 0x3de00000, 0x3de00000, 0x3c800000,
        0x3b800000, 0x3d70000d, 0x3d70000d, 0x3f60fffc,
        0x3ea1fff8, 0x3e7c0011, 0x3e7c0011, 0x3e43ffef,
        0x3dc80007, 0x3e5c0011, 0x3e5c0011, 0x3ef1fff8,
        0x3f28fffc, 0x3e1c0011, 0x3e1c0011, 0x3d10000d,
        0x3d10000d, 0x3e1c0011, 0x3e1c0011, 0x3f28fffc,
        0x3ef1fff8, 0x3e5c0011, 0x3e5c0011, 0x3dc80007,
        0x3e43ffef, 0x3e7c0011, 0x3e7c0011, 0x3ea1fff8,
        0x3f60fffc, 0x3d70000d, 0x3d70000d, 0x3b800000,
        0x3a800004, 0x3cf7ffec, 0x3cf7ffec, 0x3f704007,
        0x3e90800f, 0x3e7effe3, 0x3e7effe3, 0x3e61001d,
    ];

    private static readonly float[] CoverageWeights = CoverageWeightBits.Select(BitConverter.UInt32BitsToSingle).ToArray();

    /// <summary>
    /// <c>GenerateMips_PreserveCoverage</c> (18014fa90): each level is the 2x2 box of the one
    /// before; then, while the input's alpha-test coverage at 1 is positive and the level is not
    /// 1x1, channels 0..2 are scaled by the factor that keeps the level's coverage nearest the
    /// input's (a bisection on [0, 8] to a width of 0.01), clamped to [0, 1] (180142710), and every
    /// pixel whose 3x3 neighbourhood of channel 0 lies wholly on one side of 0.5 gets its unscaled
    /// value back (18014f840). Power-of-two sizes only.
    /// </summary>
    public static List<RcMipChain.Level> PreserveCoverage(RcMipChain.Level input)
    {
        RequirePowerOfTwo(input, "PreserveCoverage");
        float target = Coverage(input, 1f);
        var levels = new List<RcMipChain.Level> { input };
        int count = MipCount(input.Width, input.Height);
        for (int i = 1; i < count; i++)
        {
            var boxed = RcMipChain.QuarterSize(levels[^1]);
            var level = new RcMipChain.Level(boxed.Width, boxed.Height, (float[])boxed.Rgba.Clone());
            // comiss 0, target; jae skips: a target of NaN (an input a pixel wide) still searches.
            if (!(0f >= target) && !(level.Width == 1 && level.Height == 1))
            {
                float lo = 0f, hi = 8f;
                do
                {
                    float mid = (hi + lo) * 0.5f;
                    if (target > Coverage(level, mid))
                        lo = mid;
                    else
                        hi = mid;
                }
                while (hi - lo > 0.01f);
                float scale = (hi + lo) * 0.5f;
                var o = level.Rgba;
                for (int p = 0; p < level.Width * level.Height; p++)
                    for (int c = 0; c < 3; c++)
                    {
                        float v = o[p * 4 + c] * scale;
                        v = v > 0f ? v : 0f;          // maxps v, 0
                        o[p * 4 + c] = v < 1f ? v : 1f; // minps v, 1
                    }
                KeepUniform(level, boxed);
            }
            levels.Add(level);
        }
        return levels;
    }

    /// <summary>
    /// The fraction of channel 0 that passes an alpha test at 0.5 after scaling by
    /// <paramref name="scale"/> (18014f380): each 2x2 cell's corners scaled and clamped to [0, 1],
    /// sixteen bilinear samples inside it counted when at least 0.5, over <c>16 (w-1)(h-1)</c>.
    /// </summary>
    internal static float Coverage(RcMipChain.Level level, float scale)
    {
        int w = level.Width, h = level.Height, count = 0;
        var px = level.Rgba;
        float At(int x, int y)
        {
            float v = scale * px[(y * w + x) * 4];
            v = v > 0f ? v : 0f;   // maxss v, 0
            return v < 1f ? v : 1f; // minss v, 1
        }
        var t = CoverageWeights;
        for (int y = 0; y + 1 < h; y++)
        {
            float p00 = At(0, y), p01 = At(0, y + 1);
            for (int x = 0; x + 1 < w; x++)
            {
                float p10 = At(x + 1, y), p11 = At(x + 1, y + 1);
                for (int k = 0; k < 64; k += 4)
                {
                    float v = ((p11 * t[k + 1] + p10 * t[k]) + p00 * t[k + 2]) + p01 * t[k + 3];
                    if (v >= 0.5f)
                        count++;
                }
                p00 = p10;
                p01 = p11;
            }
        }
        return count / (float)((long)((h - 1) * (w - 1)) << 4);
    }

    // 18014f840: in place, row by row, a pixel whose clamped 3x3 neighbourhood of channel 0 is all
    // at least 0.5 or all under it takes channels 0..2 of the unscaled level back.
    private static void KeepUniform(RcMipChain.Level level, RcMipChain.Level unscaled)
    {
        int w = level.Width, h = level.Height;
        var o = level.Rgba;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int y0 = y >= 1 ? y - 1 : 0, y1 = Math.Min(y + 2, h), x0 = x >= 1 ? x - 1 : 0, x1 = Math.Min(x + 2, w);
                bool above = true, below = true;
                for (int yy = y0; yy < y1; yy++)
                    for (int xx = x0; xx < x1; xx++)
                    {
                        bool high = o[(yy * w + xx) * 4] >= 0.5f;
                        above = above && high;
                        below = below && !high;
                    }
                if (above || below)
                    for (int c = 0; c < 3; c++)
                        o[(y * w + x) * 4 + c] = unscaled.Rgba[(y * w + x) * 4 + c];
            }
    }

    // 180c49440: levels down to 1x1, each side halved and floored at 1.
    internal static int MipCount(int w, int h)
    {
        int n = 1;
        while (w != 1 || h != 1)
        {
            w = Math.Max(1, w >> 1);
            h = Math.Max(1, h >> 1);
            n++;
        }
        return n;
    }

    private static void RequirePowerOfTwo(RcMipChain.Level level, string algorithm)
    {
        int w = level.Width, h = level.Height;
        if (w < 1 || h < 1 || (w & (w - 1)) != 0 || (h & (h - 1)) != 0)
            throw new InvalidOperationException($"RC cannot run {algorithm} on a non-power-of-two texture ({w}x{h}).");
    }
}
