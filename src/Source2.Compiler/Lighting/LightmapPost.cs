using System.Buffers.Binary;

namespace Source2.Compiler.Lighting;

/// <summary>
/// vrad3's lightmap post-process commands (vrad3_lightmap.cpp), each a port of
/// its Run on a <see cref="LightmapImage"/> and the packing geometry's charts.
/// Scored against vrad3 itself run on the same saved image
/// (lightmap_image_load, the command, lightmap_image_save).
/// </summary>
public static class LightmapPost
{
    /// <summary>
    /// lightmap_image_consolidate_small_charts &lt;mip&gt; (180047f90). For a chart
    /// small in x (flags bit 28) and/or y (bit 29), the rect is cut into cells of
    /// 2^(mip - 1) luxels on each small axis. Each cell takes two passes over the
    /// luxels of its own chart: a running sample-count-weighted mean of every
    /// live channel component (t = n / total; mean = value t + mean (1 - t)) and
    /// of the shadow record (a slot's first light fixes it; only that light
    /// blends), then the result written to all of them with the summed count, and
    /// the luxel marked valid. The cell is not clipped to the chart rect, as in
    /// vrad3 (a luxel past it is of another chart and skipped).
    /// </summary>
    public static void ConsolidateSmallCharts(LightmapImage image, IReadOnlyList<PackingGeometry.Chart> charts, int mip)
    {
        if (mip <= 1)
            return;
        var cell = 1 << (mip - 1);
        var width = image.Width;
        var count = image.Width * image.Height;
        var means = new float[LightmapImage.ChannelCount * 4];
        for (var chartIndex = 0; chartIndex < charts.Count; chartIndex++)
        {
            var chart = charts[chartIndex];
            if ((chart.Flags & 0x30000000) == 0)
                continue;
            var sx = chart.SmallX ? cell : 1;
            var sy = chart.SmallY ? cell : 1;
            for (var cy = 0; cy < chart.Height; cy += sy)
                for (var cx = 0; cx < chart.Width; cx += sx)
                {
                    Array.Clear(means);
                    uint total = 0;
                    Span<byte> ids = [0xff, 0xff, 0xff, 0xff];
                    Span<float> slot = [1f, 1f, 1f, 1f];
                    for (var pass = 0; pass < 2; pass++)
                        for (var dy = 0; dy < sy; dy++)
                            for (var dx = 0; dx < sx; dx++)
                            {
                                var y = chart.Y + dy + cy;
                                var x = cx + dx + chart.X;
                                var luxel = y * width + x;
                                if ((uint)luxel >= (uint)count || image.ChartId(luxel) != (uint)chartIndex)
                                    continue;
                                if (pass == 0)
                                    Accumulate(image, luxel, means, ref total, ids, slot);
                                else if (total != 0)
                                    WriteBack(image, luxel, means, total, ids, slot);
                            }
                }
        }
    }

    /// <summary>
    /// lightmap_image_dilate_over_invalid_jfa (1800488e0): a jump flood with
    /// two index buffers, both seeded (lambda_1) with each luxel's own index
    /// where it is valid or no chart covers it, -1 elsewhere. Passes run with
    /// steps next_pow2(max(w, h)) / 2 halving to 1, the current buffer flipping
    /// before each pass after the first (lambda_3, 180048d90). A luxel whose
    /// current entry is not itself looks at its 3x3 neighbours at -s, 0, s of
    /// the same chart and takes a neighbour's entry from the other buffer when
    /// it has none or that seed is nearer (integer squared distance). The
    /// current buffer is written in place and is not first copied from the
    /// other. The fill (lambda_4, 180048fe0) copies every live channel and the
    /// first 16 bytes of the shadow record from the seed, then marks it valid.
    /// </summary>
    public static void DilateOverInvalidJfa(LightmapImage image)
    {
        var w = image.Width;
        var h = image.Height;
        var n = w * h;
        int[][] buffers = [new int[n], new int[n]];
        for (var p = 0; p < n; p++)
        {
            var seed = image.ChartId(p) == 0xffffffff || image.Valid(p) ? p : -1;
            buffers[0][p] = seed;
            buffers[1][p] = seed;
        }
        var m = (uint)Math.Max(w, h) - 1;
        m |= m >> 1;
        m |= m >> 2;
        m |= m >> 4;
        m |= m >> 8;
        var step = (int)((m >> 16 | m) + 1) / 2;
        var current = 0;
        JfaPass(image, buffers, current, step);
        while (step != 1)
        {
            step /= 2;
            current = 1 - current;
            JfaPass(image, buffers, current, step);
        }
        var final = buffers[current];
        for (var p = 0; p < n; p++)
        {
            var seed = final[p];
            if (seed == -1 || seed == p)
                continue;
            for (var c = 0; c < LightmapImage.ChannelCount; c++)
            {
                if (image.Channels[c] is not { } channel)
                    continue;
                for (var k = 0; k < 4; k++)
                    if ((channel.Mask >> k & 1) != 0)
                        channel.Data[p * 4 + k] = channel.Data[seed * 4 + k];
            }
            image.ShadowRecords.AsSpan(seed * 20, 16).CopyTo(image.ShadowRecords.AsSpan(p * 20, 16));
            image.SetValid(p);
        }
    }

    /// <summary>
    /// lightmap_image_fill_gutters &lt;mip&gt; (180049370), with gutterSize from
    /// gameinfo ResourceCompiler/BakedLighting/LightmapGutterSize (default 1).
    /// lambda_1 (18005cc10) marks every luxel a chart covers; the mask is never
    /// updated, so a filled luxel is not a source and the repeats change nothing.
    /// Pass 0 walks charts with neither small flag, gutterSize times; pass 1
    /// charts small on exactly one axis, once (both: skipped). Each chart's rect
    /// is walked in cells of 2^(mip - 1) (not clipped) and each unmarked luxel
    /// is filled (180049830) from the first square of radius 1 to the cell
    /// size holding marked luxels of the chart: see <see cref="FillGutterLuxel"/>.
    /// </summary>
    public static void FillGutters(LightmapImage image, IReadOnlyList<PackingGeometry.Chart> charts, int mip, int gutterSize = 1)
    {
        var cell = 1 << ((mip - 1) & 31);
        var w = image.Width;
        var n = w * image.Height;
        var covered = new bool[n];
        for (var p = 0; p < n; p++)
            covered[p] = image.ChartId(p) != 0xffffffff;
        var sums = new float[LightmapImage.ChannelCount * 4];
        for (var pass = 0; pass < 2; pass++)
            for (var chartIndex = 0; chartIndex < charts.Count; chartIndex++)
            {
                var chart = charts[chartIndex];
                var small = chart.Flags & 0x30000000;
                if (small == 0x30000000 || (pass != 0) != (small != 0))
                    continue;
                var repeats = pass != 0 ? 1 : gutterSize;
                for (var r = 0; r < repeats; r++)
                    for (var cy = chart.Y; cy < chart.Y + chart.Height; cy += cell)
                        for (var cx = chart.X; cx < chart.X + chart.Width; cx += cell)
                            for (var y = cy; y < cy + cell; y++)
                                for (var dx = 0; dx < cell; dx++)
                                {
                                    var p = y * w + dx + cx;
                                    if ((uint)p < (uint)n && !covered[p])
                                        FillGutterLuxel(image, covered, sums, dx + cx, y, (uint)chartIndex, chart.Flags, cell);
                                }
            }
    }

    /// <summary>
    /// 180049830: for radius 1 to maxRadius, the full square around (x, y) in
    /// the image is scanned rows then columns for marked luxels of the chart;
    /// the first radius with any stops the search. Their live channels are
    /// summed per masked component and averaged; the shadow record keeps per
    /// slot the first light index met (0xFF until one is) and sums the floats
    /// of luxels whose index equals the slot's, unset ones included (an unset
    /// slot sums the floats of luxels with no light there), divided by the
    /// count. The luxel takes the chart id, valid when any source was, and
    /// flag bits 1 and 2 from the chart's bits 29 and 28; the sample count is
    /// left as it was.
    /// </summary>
    private static void FillGutterLuxel(LightmapImage image, bool[] covered, float[] sums, int x, int y, uint chart, uint chartFlags, int maxRadius)
    {
        var w = image.Width;
        var h = image.Height;
        Array.Clear(sums);
        Span<byte> ids = [0xff, 0xff, 0xff, 0xff];
        Span<float> shadow = [0f, 0f, 0f, 0f];
        var count = 0;
        var valid = false;
        for (var radius = 1; radius <= maxRadius; radius++)
        {
            for (var row = y - radius; row <= y + radius; row++)
            {
                if (row < 0 || row >= h)
                    continue;
                for (var col = x - radius; col <= x + radius; col++)
                {
                    if (col < 0 || col >= w)
                        continue;
                    var q = row * w + col;
                    if (!covered[q] || image.ChartId(q) != chart)
                        continue;
                    var record = image.ShadowRecords.AsSpan(q * 20, 20);
                    for (var s = 0; s < 4; s++)
                    {
                        if (ids[s] == 0xff)
                            ids[s] = record[s];
                        if (ids[s] == record[s])
                            shadow[s] += BinaryPrimitives.ReadSingleLittleEndian(record[(4 + s * 4)..]);
                    }
                    for (var c = 0; c < LightmapImage.ChannelCount; c++)
                    {
                        if (image.Channels[c] is not { } channel)
                            continue;
                        for (var k = 0; k < 4; k++)
                            sums[c * 4 + k] += (channel.Mask >> k & 1) != 0 ? channel.Data[q * 4 + k] : 0f;
                    }
                    valid |= image.Valid(q);
                    count++;
                }
            }
            if (count > 0)
                break;
        }
        if (count == 0)
            return;
        var p = y * w + x;
        var target = image.ShadowRecords.AsSpan(p * 20, 20);
        ids.CopyTo(target);
        for (var s = 0; s < 4; s++)
            BinaryPrimitives.WriteSingleLittleEndian(target[(4 + s * 4)..], shadow[s] / count);
        for (var c = 0; c < LightmapImage.ChannelCount; c++)
        {
            if (image.Channels[c] is not { } channel)
                continue;
            for (var k = 0; k < 4; k++)
                if ((channel.Mask >> k & 1) != 0)
                    channel.Data[p * 4 + k] = sums[c * 4 + k] / count;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(image.LuxelRecords.AsSpan(p * 12), chart);
        ref var flags = ref image.LuxelRecords[p * 12 + 8];
        flags = (byte)(flags & ~7 | (valid ? 1 : 0) | (int)(chartFlags >> 29 & 1) << 1 | (int)(chartFlags >> 28 & 1) << 2);
    }

    /// <summary>
    /// lightmap_image_filter_median &lt;size&gt; (18004a030; per luxel 18004a3b0).
    /// r = max((size - 1) / 2, 1). For each valid luxel of a chart that is not
    /// small on both axes, the window is clamped to the chart rect and walked
    /// rows then columns. A neighbour counts when it is within the disc,
    /// of the same chart, valid, and its normal dot the luxel's is at least
    /// cos 15 degrees. From each, the irradiance (masked components, else 0),
    /// AO (channel 8 x) and SH coefficients 0 to 3 of each colour are gathered.
    /// With more than 2, each list's median is taken through MSVC's
    /// nth_element (see <see cref="MedianOf"/>). The results go to a zeroed
    /// 128-byte record per luxel; Run then writes the irradiance (masked), AO
    /// and all 27 SH floats back (1800443d0), so SH coefficients 4 to 8
    /// (channels 4 to 7) of a filtered luxel become 0.
    /// </summary>
    public static void FilterMedian(LightmapImage image, IReadOnlyList<PackingGeometry.Chart> charts, int size = 3)
    {
        var r = Math.Max((size - 1) / 2, 1);
        var rr = r * r;
        var cos = MathF.Cos(BitConverter.Int32BitsToSingle(0x3e860a92));
        var w = image.Width;
        var h = image.Height;
        var n = w * h;
        var irradiance = image.Channels[LightmapImage.Ch.Irradiance]!;
        var ao = image.Channels[LightmapImage.Ch.AmbientOcclusion]!;
        var normal = image.Channels[LightmapImage.Ch.Normal]!;
        var done = new bool[n];
        var records = new float[n * 32];
        var lists = new List<float>[16];
        for (var i = 0; i < lists.Length; i++)
            lists[i] = new List<float>(49);
        Span<float> sh = stackalloc float[27];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var p = y * w + x;
                if (!image.Valid(p))
                    continue;
                var id = image.ChartId(p);
                if (id == 0xffffffff || (charts[(int)id].Flags & 0x30000000) == 0x30000000)
                    continue;
                var chart = charts[(int)id];
                float mx = normal.Data[p * 4], my = normal.Data[p * 4 + 1], mz = normal.Data[p * 4 + 2];
                int y0 = Math.Max(y - r, chart.Y), y1 = Math.Min(y + r + 1, chart.Y + chart.Height);
                int x0 = Math.Max(x - r, chart.X), x1 = Math.Min(x + r + 1, chart.X + chart.Width);
                foreach (var list in lists)
                    list.Clear();
                for (var row = y0; row < y1; row++)
                    for (var col = x0; col < x1; col++)
                    {
                        if ((x - col) * (x - col) + (y - row) * (y - row) > rr)
                            continue;
                        var q = row * w + col;
                        if (image.ChartId(q) != id || !image.Valid(q))
                            continue;
                        var dot = mx * normal.Data[q * 4] + normal.Data[q * 4 + 2] * mz + normal.Data[q * 4 + 1] * my;
                        if (!(cos <= dot))
                            continue;
                        lists[15].Add(ao.Data[q * 4]);
                        ReadSh(image, q, sh);
                        for (var c = 0; c < 3; c++)
                        {
                            lists[c].Add((irradiance.Mask >> c & 1) != 0 ? irradiance.Data[q * 4 + c] : 0f);
                            for (var k = 0; k < 4; k++)
                                lists[3 + c * 4 + k].Add(sh[c * 9 + k]);
                        }
                    }
                if (lists[15].Count <= 2)
                    continue;
                done[p] = true;
                var record = records.AsSpan(p * 32, 32);
                record[4] = MedianOf(lists[15]);
                for (var c = 0; c < 3; c++)
                {
                    record[1 + c] = MedianOf(lists[c]);
                    for (var k = 0; k < 4; k++)
                        record[5 + c * 9 + k] = MedianOf(lists[3 + c * 4 + k]);
                }
            }
        for (var p = 0; p < n; p++)
        {
            if (!done[p])
                continue;
            var record = records.AsSpan(p * 32, 32);
            image.SetValid(p);
            ao.Data[p * 4] = record[4];
            for (var c = 0; c < 3; c++)
                if ((irradiance.Mask >> c & 1) != 0)
                    irradiance.Data[p * 4 + c] = record[1 + c];
            WriteSh(image, p, record[5..]);
        }
    }

    /// <summary>
    /// lightmap_image_remove_fireflies &lt;threshold&gt; (Run 18004b530, rows
    /// 18004ba20); the threshold is (float)V_atoi, default 4. Luxels taken:
    /// valid, of a chart not small on both axes. Neighbours: the 8 around it,
    /// same chart, valid, Rec.709 luminance not under 0.0005, and in pass 1 not
    /// flagged. Their irradiance (masked) and their SH evaluated at their own
    /// normal (180054c70, each colour clamped at 0) are averaged by 1/n.
    /// Pass 0 flags the luxel unless per colour: rgb &lt;= T mean rgb,
    /// |SH(own normal)| &lt;= T mean SH, and |SH| &lt;= T rgb. With no neighbours
    /// the means are 0. Pass 1 sets each flagged luxel's irradiance (masked) to
    /// the mean of its unflagged neighbours when it has any. Returns the count.
    /// </summary>
    public static int RemoveFireflies(LightmapImage image, IReadOnlyList<PackingGeometry.Chart> charts, float threshold = 4f)
    {
        var w = image.Width;
        var h = image.Height;
        var irradiance = image.Channels[LightmapImage.Ch.Irradiance]!;
        var normal = image.Channels[LightmapImage.Ch.Normal]!;
        var mask = irradiance.Mask;
        var flagged = new bool[w * h];
        var found = 0;
        Span<float> sh = stackalloc float[27];
        Span<float> eval = stackalloc float[3];
        for (var pass = 0; pass < 2; pass++)
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var p = y * w + x;
                    if (pass == 1 && !flagged[p])
                        continue;
                    var id = image.ChartId(p);
                    if (!image.Valid(p) || id == 0xffffffff || (charts[(int)id].Flags & 0x30000000) == 0x30000000)
                        continue;
                    float sr = 0, sg = 0, sb = 0, er = 0, eg = 0, eb = 0;
                    var count = 0;
                    for (var row = y - 1; row <= y + 1; row++)
                        for (var col = x - 1; col <= x + 1; col++)
                        {
                            if ((col == x && row == y) || col < 0 || col >= w || row < 0 || row >= h)
                                continue;
                            var q = row * w + col;
                            if (pass == 1 && flagged[q])
                                continue;
                            if (image.ChartId(q) != id || !image.Valid(q))
                                continue;
                            var r = (mask & 1) != 0 ? irradiance.Data[q * 4] : 0f;
                            var g = (mask & 2) != 0 ? irradiance.Data[q * 4 + 1] : 0f;
                            var b = (mask & 4) != 0 ? irradiance.Data[q * 4 + 2] : 0f;
                            if (0.0005f > g * 0.7152f + r * 0.2126f + b * 0.0722f)
                                continue;
                            count++;
                            ReadSh(image, q, sh);
                            sr += r;
                            sg += g;
                            sb += b;
                            EvaluateSh(sh, normal.Data[q * 4], normal.Data[q * 4 + 1], normal.Data[q * 4 + 2], eval);
                            er += eval[0] <= 0f ? 0f : eval[0];
                            eg += eval[1] <= 0f ? 0f : eval[1];
                            eb += eval[2] <= 0f ? 0f : eval[2];
                        }
                    if (count > 0)
                    {
                        var inv = 1f / count;
                        sr *= inv;
                        sb *= inv;
                        er *= inv;
                        eg *= inv;
                        sg *= inv;
                        eb *= inv;
                    }
                    if (pass == 1)
                    {
                        if (count <= 0)
                            continue;
                        if ((mask & 1) != 0)
                            irradiance.Data[p * 4] = sr;
                        if ((mask & 2) != 0)
                            irradiance.Data[p * 4 + 1] = sg;
                        if ((mask & 4) != 0)
                            irradiance.Data[p * 4 + 2] = sb;
                        continue;
                    }
                    var or = (mask & 1) != 0 ? irradiance.Data[p * 4] : 0f;
                    var og = (mask & 2) != 0 ? irradiance.Data[p * 4 + 1] : 0f;
                    var ob = (mask & 4) != 0 ? irradiance.Data[p * 4 + 2] : 0f;
                    ReadSh(image, p, sh);
                    EvaluateSh(sh, normal.Data[p * 4], normal.Data[p * 4 + 1], normal.Data[p * 4 + 2], eval);
                    float hr = MathF.Abs(eval[0]), hg = MathF.Abs(eval[1]), hb = MathF.Abs(eval[2]);
                    var t = threshold;
                    var keep = or <= sr * t && og <= sg * t && ob <= sb * t
                        && hr <= t * er && hg <= t * eg && hb <= t * eb
                        && hr <= t * or && hg <= t * og && hb <= t * ob;
                    if (keep)
                        continue;
                    flagged[p] = true;
                    found++;
                }
        return found;
    }

    /// <summary>
    /// 180054c70: the 9 SH coefficients of each colour evaluated at a direction,
    /// with the constants and operation order of vrad3 (single precision).
    /// </summary>
    public static void EvaluateSh(ReadOnlySpan<float> sh, float x, float y, float z, Span<float> result)
    {
        var xy2 = x * x - y * y;
        var z2 = z * 3f * z - 1f;
        for (var c = 0; c < 3; c++)
        {
            var s = sh.Slice(c * 9, 9);
            result[c] = s[0] * 0.282095f - s[1] * 0.325735f * y + s[2] * 0.325735f * z - s[3] * 0.325735f * x
                + s[4] * 0.273137f * x * y - s[5] * 0.273137f * y * z + s[6] * 0.078848f * z2
                - s[7] * 0.273137f * x * z + s[8] * 0.136569f * xy2;
        }
    }

    /// <summary>
    /// lightmap_image_write_ahd's direction file (Run 180053560, pixels
    /// 18005b9f0): R, G = channel 13 x, y (masked, else 0) times 0.5 plus 0.5,
    /// B = channel 15 x, A = channel 8 x when channel 8 exists (the bitmap is
    /// then RGBA, else RGB). The colour file is channel 14 as it stands.
    /// </summary>
    public static Vrad3Exr AhdDirectionExr(LightmapImage image)
    {
        var vector = image.Channels[13]!;
        var ambient = image.Channels[15]!;
        var ao = image.Channels[LightmapImage.Ch.AmbientOcclusion];
        var exr = new Vrad3Exr(image.Width, image.Height, ao is null ? ["R", "G", "B"] : ["R", "G", "B", "A"]);
        Half[] r = exr.Plane("R"), g = exr.Plane("G"), b = exr.Plane("B");
        var a = ao is null ? null : exr.Plane("A");
        for (var p = 0; p < r.Length; p++)
        {
            r[p] = (Half)(((vector.Mask & 1) != 0 ? vector.Data[p * 4] : 0f) * 0.5f + 0.5f);
            g[p] = (Half)(((vector.Mask & 2) != 0 ? vector.Data[p * 4 + 1] : 0f) * 0.5f + 0.5f);
            b[p] = (Half)ambient.Data[p * 4];
            if (a is not null)
                a[p] = (Half)ao!.Data[p * 4];
        }
        return exr;
    }

    /// <summary>
    /// lightmap_image_write_direct_light_shadows (Run 180053a10, pixels
    /// 18005c100): an RGBA file of the first four shadow slots. Each starts at
    /// 1 when inverting (the default; -noinvert turns it off), else 0. Of the
    /// first <paramref name="indexLights"/> slots (-indexlights, default 4),
    /// one with a light takes clamp(value, 0, 1), its square root under
    /// -gamma20 when above 0, then 1 - v when inverting.
    /// </summary>
    public static Vrad3Exr DirectLightShadowsExr(LightmapImage image, int indexLights = 4, bool gamma20 = false, bool invert = true)
    {
        var exr = new Vrad3Exr(image.Width, image.Height, ["R", "G", "B", "A"]);
        Half[][] planes = [exr.Plane("R"), exr.Plane("G"), exr.Plane("B"), exr.Plane("A")];
        Span<float> slots = stackalloc float[4];
        for (var p = 0; p < image.Width * image.Height; p++)
        {
            slots.Fill(invert ? 1f : 0f);
            var record = image.ShadowRecords.AsSpan(p * 20, 20);
            for (var k = 0; k < Math.Min(indexLights, 4); k++)
            {
                if (record[k] == 0xff)
                    continue;
                var v = BinaryPrimitives.ReadSingleLittleEndian(record[(4 + k * 4)..]);
                if (v <= 0f)
                    v = 0f;
                if (1f <= v)
                    v = 1f;
                if (gamma20 && 0f < v)
                    v = MathF.Sqrt(v);
                if (invert)
                    v = 1f - v;
                slots[k] = v;
            }
            for (var k = 0; k < 4; k++)
                planes[k][p] = (Half)slots[k];
        }
        return exr;
    }

    // 180043e30 / 1800443d0: per colour c the 9 SH coefficients are channel 1 + c
    // (0 to 3), channel 4 + c (4 to 7) and channel 7's component c (8).
    internal static void ReadSh(LightmapImage image, int p, Span<float> sh)
    {
        for (var c = 0; c < 3; c++)
        {
            var low = image.Channels[LightmapImage.Ch.ShR + c]!.Data;
            var high = image.Channels[LightmapImage.Ch.QuadraticR + c]!.Data;
            for (var k = 0; k < 4; k++)
            {
                sh[c * 9 + k] = low[p * 4 + k];
                sh[c * 9 + 4 + k] = high[p * 4 + k];
            }
            sh[c * 9 + 8] = image.Channels[LightmapImage.Ch.QuadraticFinal]!.Data[p * 4 + c];
        }
    }

    private static void WriteSh(LightmapImage image, int p, ReadOnlySpan<float> sh)
    {
        for (var c = 0; c < 3; c++)
        {
            var low = image.Channels[LightmapImage.Ch.ShR + c]!.Data;
            var high = image.Channels[LightmapImage.Ch.QuadraticR + c]!.Data;
            for (var k = 0; k < 4; k++)
            {
                low[p * 4 + k] = sh[c * 9 + k];
                high[p * 4 + k] = sh[c * 9 + 4 + k];
            }
            image.Channels[LightmapImage.Ch.QuadraticFinal]!.Data[p * 4 + c] = sh[c * 9 + 8];
        }
    }

    /// <summary>
    /// vrad3's median of a list (18004a3b0): nth_element at n / 2 and, for even
    /// n, again at n / 2 + 1, then (a[n/2] + a[n/2 - 1]) * 0.5 with whatever the
    /// second call left at n/2 - 1. Up to 32 values nth_element is an insertion
    /// sort, so this is the textbook median for the default sizes.
    /// </summary>
    public static float MedianOf(List<float> values)
    {
        var a = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(values);
        var half = a.Length / 2;
        Simulation.MsvcSort.NthElement(a, half, default(Simulation.MsvcSort.FloatLess));
        var v = a[half];
        if ((a.Length & 1) == 0)
        {
            Simulation.MsvcSort.NthElement(a, half + 1, default(Simulation.MsvcSort.FloatLess));
            v = (v + a[half - 1]) * 0.5f;
        }
        return v;
    }

    private static void JfaPass(LightmapImage image, int[][] buffers, int current, int step)
    {
        var w = image.Width;
        var h = image.Height;
        var write = buffers[current];
        var read = buffers[1 - current];
        for (var row = 0; row < h; row++)
            for (var col = 0; col < w; col++)
            {
                var p = row * w + col;
                var chart = image.ChartId(p);
                for (var ny = row - step; ny <= row + step; ny += step)
                    for (var nx = col - step; nx <= col + step; nx += step)
                    {
                        if (nx < 0 || nx >= w || ny < 0 || ny >= h)
                            continue;
                        var mine = write[p];
                        if (mine == p || chart != image.ChartId(ny * w + nx))
                            continue;
                        var candidate = read[ny * w + nx];
                        if (candidate == -1)
                            continue;
                        if (mine != -1)
                        {
                            int my = mine / w - row, mx = mine - w * (mine / w) - col;
                            int cy = candidate / w - row, cx = candidate - w * (candidate / w) - col;
                            if (cy * cy + cx * cx >= my * my + mx * mx)
                                continue;
                        }
                        write[p] = candidate;
                    }
            }
    }

    private static void Accumulate(LightmapImage image, int luxel, float[] means, ref uint total, Span<byte> ids, Span<float> slot)
    {
        var n = image.SampleCount(luxel);
        if (n == 0)
            return;
        total += n;
        var t = (float)n / (float)total;
        var keep = 1f - t;
        for (var c = 0; c < LightmapImage.ChannelCount; c++)
        {
            if (image.Channels[c] is not { } channel)
                continue;
            for (var k = 0; k < 4; k++)
            {
                var v = (channel.Mask >> k & 1) != 0 ? channel.Data[luxel * 4 + k] : 0f;
                means[c * 4 + k] = v * t + keep * means[c * 4 + k];
            }
        }
        var record = image.ShadowRecords.AsSpan(luxel * 20, 20);
        for (var s = 0; s < 4; s++)
        {
            var id = record[s];
            if (id == 0xff)
                continue;
            var value = BinaryPrimitives.ReadSingleLittleEndian(record[(4 + s * 4)..]);
            if (ids[s] == 0xff)
            {
                ids[s] = id;
                slot[s] = value;
            }
            else if (ids[s] == id)
                slot[s] = slot[s] + (value - slot[s]) * t;
        }
    }

    private static void WriteBack(LightmapImage image, int luxel, float[] means, uint total, ReadOnlySpan<byte> ids, ReadOnlySpan<float> slot)
    {
        for (var c = 0; c < LightmapImage.ChannelCount; c++)
        {
            if (image.Channels[c] is not { } channel)
                continue;
            for (var k = 0; k < 4; k++)
                if ((channel.Mask >> k & 1) != 0)
                    channel.Data[luxel * 4 + k] = means[c * 4 + k];
        }
        var record = image.ShadowRecords.AsSpan(luxel * 20, 20);
        ids.CopyTo(record);
        for (var s = 0; s < 4; s++)
            BinaryPrimitives.WriteSingleLittleEndian(record[(4 + s * 4)..], slot[s]);
        image.SetSampleCount(luxel, total);
        image.SetValid(luxel);
    }
}
