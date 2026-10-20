namespace Source2.Compiler.Lighting;

/// <summary>
/// vrad3's OIDN lightmap filters (quality 1 and 2): lightmap_image_filter_oidn &lt;mips&gt; (18004c9b0: ambient
/// occlusion, 180054f50, then irradiance, 180056470) and lightmap_image_filter_sh_oidn &lt;mips&gt; (18004cb00:
/// SH radiance L0, 180057d80, then c1..c8, 1800597d0), each on one OIDN device (<see cref="Oidn"/>).
/// <para>A pass ("lg") takes the texels of large charts (valid, a chart, neither small flag:
/// 18004c970) into a width x height float3 image, zeros elsewhere; twice, each empty texel with more than
/// one valid texel in its 3 x 3 window takes their mean (the window as the round starts, in place);
/// RTLightmap; the output written back to the large texels. Then ("sm") the line charts (small in y only)
/// are laid in bands of five rows across the image: a chart's first row three times (rows 1..3) with its
/// end texels repeated left and right, 8 texels apart; a batch is filtered (width x band rows) when the
/// next band would leave fewer than 10 rows, and its first 2^(mips - 1) chart rows written back. After
/// the last chart a batch is filtered only when more than one band was used.</para>
/// </summary>
public static class LightmapOidn
{
    private static bool Large(LightmapImage image, int i)
    {
        var flags = image.LuxelRecords[i * 12 + 8];
        var chart = BitConverter.ToUInt32(image.LuxelRecords, i * 12);
        return (flags & 1) != 0 && chart != 0xffffffff && (flags & 4) == 0 && (flags & 2) == 0;
    }

    private static bool Line(PackingGeometry.Chart c) => c.SmallY && !c.SmallX;

    /// <summary>lightmap_image_filter_oidn &lt;mips&gt;: ambient occlusion, then irradiance.</summary>
    public static void Filter(LightmapImage image, PackingGeometry packing, int mips, Oidn oidn)
    {
        var ao = image.Channels[LightmapImage.Ch.AmbientOcclusion]!.Data;
        Pass(image, packing, mips, oidn, false, false,
            i => (ao[i * 4], ao[i * 4], ao[i * 4]),
            (i, v) => ao[i * 4] = v.X);
        var irradiance = image.Channels[LightmapImage.Ch.Irradiance]!;
        Pass(image, packing, mips, oidn, false, true, Rgb(irradiance), SetRgb(irradiance));
    }

    // 180043bf0: a channel's R, G and B, 0 where the mask lacks one.
    private static Func<int, (float X, float Y, float Z)> Rgb(LightmapImage.Channel ch)
        => i => ((ch.Mask & 1) != 0 ? ch.Data[i * 4] : 0f, (ch.Mask & 2) != 0 ? ch.Data[i * 4 + 1] : 0f, (ch.Mask & 4) != 0 ? ch.Data[i * 4 + 2] : 0f);

    // CVrad3LightmapImage::SetPixelRGB.
    private static Action<int, (float X, float Y, float Z)> SetRgb(LightmapImage.Channel ch)
        => (i, v) => (ch.Data[i * 4], ch.Data[i * 4 + 1], ch.Data[i * 4 + 2]) = (v.X, v.Y, v.Z);

    /// <summary>lightmap_image_filter_sh_oidn &lt;mips&gt;: SH radiance L0, then c1..c8.</summary>
    /// <remarks>
    /// L0 (180057d80) is the SH red, green and blue channels' first component, normalised per chart as the
    /// irradiance. Each of c1..c8 (1800597d0, directional) is divided per colour by max(L0, 1e-5) on the
    /// way in and multiplied by L0 on the way out (the L0 the first pass left).
    /// </remarks>
    public static void FilterSh(LightmapImage image, PackingGeometry packing, int mips, Oidn oidn)
    {
        var r = image.Channels[LightmapImage.Ch.ShR]!.Data;
        var g = image.Channels[LightmapImage.Ch.ShG]!.Data;
        var b = image.Channels[LightmapImage.Ch.ShB]!.Data;
        Pass(image, packing, mips, oidn, false, true,
            i => (r[i * 4], g[i * 4], b[i * 4]),
            (i, v) => (r[i * 4], g[i * 4], b[i * 4]) = (v.X, v.Y, v.Z));
        for (var k = 1; k < 9; k++)
        {
            var (cr, cg, cb, at) = Coefficient(image, k);
            Pass(image, packing, mips, oidn, true, false,
                i =>
                {
                    float lr = r[i * 4], lg = g[i * 4], lb = b[i * 4];
                    return (cr[i * 4 + at.R] / (lr >= 1e-5f ? lr : 1e-5f), cg[i * 4 + at.G] / (lg >= 1e-5f ? lg : 1e-5f),
                            cb[i * 4 + at.B] / (lb >= 1e-5f ? lb : 1e-5f));
                },
                (i, v) => (cr[i * 4 + at.R], cg[i * 4 + at.G], cb[i * 4 + at.B]) = (r[i * 4] * v.X, g[i * 4] * v.Y, b[i * 4] * v.Z));
        }
    }

    // 180044810: coefficient k's red, green and blue: 0..3 the SH channels' component k, 4..7 the
    // quadratic channels' component k - 4, 8 the final quadratic channel's R, G and B.
    private static (float[] R, float[] G, float[] B, (int R, int G, int B) At) Coefficient(LightmapImage image, int k)
    {
        float[] C(int c) => image.Channels[c]!.Data;
        return k switch
        {
            < 4 => (C(LightmapImage.Ch.ShR), C(LightmapImage.Ch.ShG), C(LightmapImage.Ch.ShB), (k, k, k)),
            < 8 => (C(LightmapImage.Ch.QuadraticR), C(LightmapImage.Ch.QuadraticG), C(LightmapImage.Ch.QuadraticB), (k - 4, k - 4, k - 4)),
            _ => (C(LightmapImage.Ch.QuadraticFinal), C(LightmapImage.Ch.QuadraticFinal), C(LightmapImage.Ch.QuadraticFinal), (0, 1, 2)),
        };
    }

    /// <summary>
    /// One pass (180054f50's shape): <paramref name="get"/> a texel's three values, <paramref name="set"/>
    /// the denoised ones.
    /// </summary>
    private static void Pass(LightmapImage image, PackingGeometry packing, int mips, Oidn oidn, bool directional, bool normalize,
                             Func<int, (float X, float Y, float Z)> get, Action<int, (float X, float Y, float Z)> set)
    {
        int w = image.Width, h = image.Height, n = w * h;
        var color = new float[n * 3];
        var output = new float[n * 3];
        var invalid = new bool[n];
        Array.Fill(invalid, true);
        // 180056470: with normalize, each chart's large texels scaled by count 0.25 / their summed luminance
        // ((g 0.7152 + r 0.2126) + b 0.0722), unscaled after the filter (a chart summing to 0 comes back 0).
        var scale = normalize ? new float[packing.Charts.Length] : null;
        var count = normalize ? new int[packing.Charts.Length] : null;
        int Chart(int i) => BitConverter.ToInt32(image.LuxelRecords, i * 12);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (!Large(image, i))
                    continue;
                var (a, b, c) = get(i);
                (color[i * 3], color[i * 3 + 1], color[i * 3 + 2]) = (a, b, c);
                invalid[i] = false;
                if (scale != null && Chart(i) is var k and not -1)
                {
                    scale[k] = color[i * 3 + 1] * 0.7152f + color[i * 3] * 0.2126f + color[i * 3 + 2] * 0.0722f + scale[k];
                    count![k]++;
                }
            }
        if (scale != null)
        {
            for (var k = 0; k < scale.Length; k++)
                if (scale[k] != 0f)
                    scale[k] = count![k] * 0.25f / scale[k];
            for (var i = 0; i < n; i++)
                if (Large(image, i) && Chart(i) is var k and not -1)
                {
                    var f = scale[k];
                    (color[i * 3], color[i * 3 + 1], color[i * 3 + 2]) = (f * color[i * 3], f * color[i * 3 + 1], f * color[i * 3 + 2]);
                }
        }
        Dilate(color, invalid, w, h);
        oidn.RtLightmap(color, output, w, h, directional);
        if (scale != null)
        {
            for (var k = 0; k < scale.Length; k++)
                if (scale[k] != 0f)
                    scale[k] = 1f / scale[k];
            for (var i = 0; i < n; i++)
                if (Large(image, i) && Chart(i) is var k and not -1)
                {
                    var f = scale[k];
                    (output[i * 3], output[i * 3 + 1], output[i * 3 + 2]) = (f * output[i * 3], f * output[i * 3 + 1], f * output[i * 3 + 2]);
                }
        }
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (Large(image, i))
                    set(i, (output[i * 3], output[i * 3 + 1], output[i * 3 + 2]));
            }
        Lines(image, packing, mips, oidn, directional, get, set, color, output);
    }

    // Two rounds: each invalid texel (in index order) with more than one valid texel in its 3 x 3 window
    // (rows, then columns; the window clamped to the image) takes their mean; it becomes valid after the round.
    private static void Dilate(float[] color, bool[] invalid, int w, int h)
    {
        var filled = new List<int>();
        for (var round = 0; round < 2; round++)
        {
            filled.Clear();
            for (var i = 0; i < invalid.Length; i++)
            {
                if (!invalid[i])
                    continue;
                int y = i / w, x = i - y * w;
                int x0 = Math.Max(x - 1, 0), x1 = Math.Min(x + 1, w - 1), y0 = Math.Max(y - 1, 0), y1 = Math.Min(y + 1, h - 1);
                float sx = 0f, sy = 0f, sz = 0f;
                var count = 0;
                for (var yy = y0; yy <= y1; yy++)
                    for (var xx = x0; xx <= x1; xx++)
                    {
                        var j = yy * w + xx;
                        if (invalid[j])
                            continue;
                        count++;
                        sx += color[j * 3];
                        sy += color[j * 3 + 1];
                        sz += color[j * 3 + 2];
                    }
                if (count > 1)
                {
                    var inv = 1f / count;
                    (color[i * 3], color[i * 3 + 1], color[i * 3 + 2]) = (sx * inv, sy * inv, sz * inv);
                    filled.Add(i);
                }
            }
            if (filled.Count == 0)
                break;
            foreach (var i in filled)
                invalid[i] = false;
        }
    }

    // The "sm" batches over the line charts (180054f50's tail, 180056030).
    private static void Lines(LightmapImage image, PackingGeometry packing, int mips, Oidn oidn, bool directional,
                              Func<int, (float X, float Y, float Z)> get, Action<int, (float X, float Y, float Z)> set,
                              float[] color, float[] output)
    {
        int w = image.Width, h = image.Height;
        Array.Clear(color);
        var places = new (int X, int Y)[packing.Charts.Length];
        int first = 0, rows = 0, cursor = 0, band = 0;
        void Put(int x, int y, (float X, float Y, float Z) v)
        {
            var i = (y * w + x) * 3;
            (color[i], color[i + 1], color[i + 2]) = (v.X, v.Y, v.Z);
        }
        (float, float, float) At(int x, int y)
        {
            var i = (y * w + x) * 3;
            return (color[i], color[i + 1], color[i + 2]);
        }
        void Flush(int from, int to, int height)
        {
            oidn.RtLightmap(color, output, w, height, directional);
            for (var c = from; c < to; c++)
            {
                var chart = packing.Charts[c];
                if (!Line(chart))
                    continue;
                var (px, py) = places[c];
                for (var r = 0; r < 1 << (mips - 1); r++)
                    for (var k = 0; k < chart.Width; k++)
                    {
                        var o = (py * w + px + k) * 3;
                        set((chart.Y + r) * w + chart.X + k, (output[o], output[o + 1], output[o + 2]));
                    }
            }
        }
        for (var c = 0; c < packing.Charts.Length; c++)
        {
            var chart = packing.Charts[c];
            if (!Line(chart))
                continue;
            var x0 = cursor;
            if (w <= chart.Width + 8 + cursor)
            {
                band = rows + 5;
                x0 = 0;
                if (h <= rows + 10)
                {
                    Flush(first, c, band);
                    band = 0;
                    Array.Clear(color);
                    first = c;
                }
            }
            places[c] = (x0 + 1, band + 2);
            for (var k = 0; k < chart.Width; k++)
            {
                var v = get(chart.Y * w + chart.X + k);
                Put(x0 + k + 1, band + 1, v);
                Put(x0 + k + 1, band + 2, v);
                Put(x0 + k + 1, band + 3, v);
            }
            var left = At(x0 + 1, band + 2);
            Put(x0, band + 3, left);
            Put(x0, band + 2, left);
            Put(x0, band + 1, left);
            var right = At(x0 + chart.Width, band + 2);
            Put(x0 + chart.Width + 1, band + 3, right);
            Put(x0 + chart.Width + 1, band + 2, right);
            Put(x0 + chart.Width + 1, band + 1, right);
            cursor = x0 + 8 + chart.Width;
            rows = band;
        }
        if (rows != 0)
            Flush(first, packing.Charts.Length, rows + 5);
    }
}
