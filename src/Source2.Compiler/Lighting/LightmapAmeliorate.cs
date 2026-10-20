using System.Buffers.Binary;

namespace Source2.Compiler.Lighting;

/// <summary>
/// vrad3's lightmap_ameliorate_block_compression_artefacts -mips &lt;n&gt; (quality 1 and 2; Run 1800672e0,
/// vrad3_lightmap_ameliorate_bc_artefacts.cpp): pixel and line charts move out of the 4x4 blocks they share
/// with other charts into free lines of whole blocks, so block compression does not bleed between charts,
/// and the empty texels of every part-filled block take the block's most common colour. It writes
/// chart_repacking.dat (<see cref="LightmapChartRepacking"/>), which BakeLighting applies to the lightmap
/// uvs. The phases, in Valve's order:
/// <list type="number">
/// <item>Sanitize line charts (180066c60): in each line chart (flags bit 29 without bit 28), an empty texel
/// (chart -1) copies the last filled one to its left, then (right to left) to its right.</item>
/// <item>Zero-variation charts (lambda_6 180074670, 18006caa0, compare 180064f30): a chart whose sampled
/// texels all match its first texel (row major) is a pixel chart.</item>
/// <item>Luxel hashes (1800648f0) and buckets of texels sharing one; a pixel chart counts only its first
/// texel.</item>
/// <item>Single-pixel aliases (18006cc70): in a bucket, a pixel chart's texel matching a texel of another
/// chart, or of another pixel chart, reads that texel instead of moving.</item>
/// <item>Line charts by length: the pixel charts (length 1) and line charts left, grouped by length.</item>
/// <item>chart_repacking.dat's header and each texel's chart; the charts copied into temporary rows
/// (18006d430); pixel charts, line charts and empty texels cleared (18006dbc0); the 4x4 blocks left empty
/// (1800743a0).</item>
/// <item>The longest lines of empty blocks (FindLongestLinesCoveringCells 180065690), longest chart lengths
/// first into the longest lines, then the copies back (18006def0).</item>
/// <item>FillBlocks (180066230), the aliases (lambda_40 1800741b0), the per-chart records.</item>
/// </list>
/// Not ported: the repack iterations Valve runs when charts are left after the longest lines (its "Repack
/// n/m remaining" stages), and -mips above 1 (the quality scripts pass 1). Both throw.
/// </summary>
public static class LightmapAmeliorate
{
    private const uint Magic = 0x0531771e;
    private const uint M = 0x5bd1e995;
    private const int Pixel = 0x30000000;

    private readonly record struct Line(int Length, int X, int Y, bool Vertical);

    // The record each chart gets: its length in texels, new corner, along y.
    private struct Placement
    {
        public int Size, X, Y;
        public bool Vertical;
    }

    // lambda_40's alias: the chart a pixel chart reads and the texel's offset in it.
    private readonly record struct Alias(int Chart, int Dx, int Dy);

    /// <summary>
    /// The command on <paramref name="image"/> with the packing's <paramref name="charts"/>; returns
    /// chart_repacking.dat, or null when the lightmap has no pixel or line charts left (Valve writes none).
    /// </summary>
    public static byte[]? Run(LightmapImage image, IReadOnlyList<PackingGeometry.Chart> charts, int mips = 1, Action<string>? log = null)
    {
        if (mips != 1)
            throw new NotSupportedException($"lightmap_ameliorate_block_compression_artefacts -mips {mips} is not ported (the quality scripts pass 1)");
        var w = image.Width;
        var h = image.Height;
        var n = charts.Count;

        SanitizeLineCharts(image, charts);

        var first = FirstTexels(image, charts);
        var pixel = ZeroVariation(image, n, first);
        bool IsPixel(uint c) => pixel[c] || (charts[(int)c].Flags & Pixel) == Pixel;

        // Hashes, then counts and buckets over the texels that count.
        var hashes = new uint[w * h];
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
                if (image.ChartId(y * w + x) != 0xffffffff)
                    hashes[y * w + x] = Hash(image, x, y);
        });
        bool Counts(int x, int y)
        {
            var c = image.ChartId(y * w + x);
            return c != 0xffffffff && !(IsPixel(c) && first[c] != (x, y));
        }
        var counts = new Dictionary<uint, int>();
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (Counts(x, y))
                    counts[hashes[y * w + x]] = counts.GetValueOrDefault(hashes[y * w + x]) + 1;
        var buckets = new Dictionary<uint, List<(int X, int Y)>>();
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (Counts(x, y) && counts[hashes[y * w + x]] > 1)
                {
                    if (!buckets.TryGetValue(hashes[y * w + x], out var list))
                        buckets[hashes[y * w + x]] = list = [];
                    list.Add((x, y));
                }
        hashes = null;

        var aliases = new Alias[n];
        Array.Fill(aliases, new Alias(-1, 0, 0));
        Parallel.ForEach(buckets.Values, bucket => AliasBucket(image, charts, bucket, IsPixel, aliases));
        int pixelCharts = 0, aliased = 0;
        for (var c = 0; c < n; c++)
            if (IsPixel((uint)c))
            {
                pixelCharts++;
                aliased += aliases[c].Chart != -1 ? 1 : 0;
            }
        log?.Invoke(pixelCharts == 0 ? "Lightmap does not have pixel charts." : $"{aliased}/{pixelCharts} ({aliased * 100f / pixelCharts:F2}%) Pixel charts aliased to other luxels.");

        // Arranging line charts by length: the charts to move and, per length, their places in that list.
        var moving = new List<int>();
        var byLength = new Dictionary<int, List<int>>();
        for (var c = 0; c < n; c++)
        {
            var chart = charts[c];
            if (!pixel[c])
            {
                if ((chart.Flags & Pixel) == 0)
                    continue;
                if (chart.Height != 1)
                    throw new InvalidDataException($"Expecting pixel/line chart to be of height 1, found {chart.Width}x{chart.Height} for chart {c}");
            }
            if (aliases[c].Chart != -1)
                continue;
            var at = moving.Count;
            moving.Add(c);
            var length = pixel[c] ? 1 : chart.Width;
            if (!byLength.TryGetValue(length, out var list))
                byLength[length] = list = [];
            list.Add(at);
        }
        var lengths = byLength.Keys.Order().ToList();
        if (moving.Count == 0)
        {
            log?.Invoke("Lightmap does not have line charts.");
            return null;
        }

        var file = new MemoryStream();
        var bw = new BinaryWriter(file);
        bw.Write(Magic);
        bw.Write((uint)n);
        bw.Write(w);
        bw.Write(h);
        for (var i = 0; i < w * h; i++)
            bw.Write(image.ChartId(i));

        // Packing line charts into temporary storage: longest first, first row with room.
        var temp = new (int X, int Y)[n];
        Array.Fill(temp, (-1, -1));
        var rows = new List<int>();
        for (var k = lengths.Count - 1; k >= 0; k--)
        {
            var length = lengths[k];
            var r = 0;
            foreach (var at in byLength[length])
            {
                while (r < rows.Count && rows[r] < length)
                    r++;
                if (r == rows.Count)
                    rows.Add(w);
                var c = moving[at];
                if (aliases[c].Chart == -1)
                {
                    temp[c] = (w - rows[r], r);
                    rows[r] -= length;
                }
            }
        }
        var store = new LightmapImage(w, Math.Max(rows.Count, 1));
        for (var ch = 0; ch < LightmapImage.ChannelCount; ch++)
            if (image.Channels[ch] is { } channel)
                store.AddChannel(ch, channel.Mask);
        Parallel.For(0, n, c =>
        {
            if (temp[c].X == -1)
                return;
            var chart = charts[c];
            if (!pixel[c])
                for (var i = 0; i < chart.Width; i++)
                    CopyTexel(store, temp[c].X + i, temp[c].Y, image, chart.X + i, chart.Y);
            else if (first[c].X >= 0)
                CopyTexel(store, temp[c].X, temp[c].Y, image, first[c].X, first[c].Y);
            else
                throw new InvalidDataException($"pixel chart {c} has no texel (Valve reads before the image)");
        });

        // Clear what moves and what is empty; the free texels, then the free blocks.
        var free = new bool[w * h];
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                var c = image.ChartId(i);
                if (c == 0xffffffff || (image.LuxelRecords[i * 12 + 8] & 2) != 0 || pixel[c])
                {
                    Clear(image, i);
                    free[i] = true;
                }
            }
        });
        int bw4 = w / 4, bh4 = h / 4;
        var blocks = new bool[bw4 * bh4];
        Parallel.For(0, bh4, by =>
        {
            for (var bx = 0; bx < bw4; bx++)
            {
                var all = true;
                for (var y = by * 4; y < by * 4 + 4 && all; y++)
                    for (var x = bx * 4; x < bx * 4 + 4; x++)
                        if (!free[y * w + x])
                        {
                            all = false;
                            break;
                        }
                if (all)
                    blocks[by * bw4 + bx] = true;
            }
        });

        // Packing longest line charts into their own lines of 4x4 blocks.
        var lines = FindLongestLines(blocks, bw4, bh4);
        var placed = new Placement[n];
        while (true)
        {
            var any = false;
            var left = new List<Line>();
            for (var li = lines.Count - 1; li >= 0; li--)
            {
                var line = lines[li];
                int room = line.Length, x = line.X, y = line.Y;
                for (var k = lengths.Count - 1; k >= 0 && room != 0; k--)
                {
                    var length = lengths[k];
                    var need = (length == 0 ? 4 : (length + 3) & ~3) / 4;
                    if (need > room)
                        continue;
                    var list = byLength[length];
                    if (list.Count == 0)
                        continue;
                    any = true;
                    var j = list.Count - 1;
                    while (true)
                    {
                        room -= need;
                        var c = moving[list[j]];
                        placed[c] = new Placement { Size = length, X = x * 4, Y = y * 4, Vertical = line.Vertical };
                        if (line.Vertical)
                            y += need;
                        else
                            x += need;
                        list.RemoveAt(list.Count - 1);
                        if (list.Count == 0)
                        {
                            lengths.RemoveAt(k);
                            byLength.Remove(length);
                            break;
                        }
                        if (room < need || --j < 0)
                            break;
                    }
                }
                if (room > 0)
                    left.Add(new Line(room, x, y, line.Vertical));
                lines.RemoveAt(li);
            }
            if (!any)
                break;
            lines = left;
            lines.Sort(CompareLines);
        }

        // The copies back from temporary storage.
        Parallel.For(0, n, c =>
        {
            var p = placed[c];
            for (var i = 0; i < p.Size; i++)
            {
                int x = p.X + (p.Vertical ? 0 : i), y = p.Y + (p.Vertical ? i : 0);
                CopyTexel(image, x, y, store, temp[c].X + i, temp[c].Y);
                free[y * w + x] = false;
            }
        });

        var remaining = moving.Count(c => placed[c].Size <= 0);
        if (remaining > 0)
            throw new NotSupportedException($"lightmap_ameliorate_block_compression_artefacts: {remaining} charts left after the longest free lines; Valve's repack iterations are not ported");

        FillBlocks(image);

        // lambda_40: an aliased chart's record is its target's corner (or the target's new place) plus the offset.
        for (var c = 0; c < n; c++)
        {
            var a = aliases[c];
            if (a.Chart == -1)
                continue;
            var target = placed[a.Chart];
            int x = charts[a.Chart].X, y = charts[a.Chart].Y;
            if (target.Size > 0)
                (x, y) = (target.X, target.Y);
            placed[c] = new Placement
            {
                Size = 1,
                X = (target.Vertical ? a.Dy : a.Dx) + x,
                Y = (target.Vertical ? a.Dx : a.Dy) + y,
            };
        }

        for (var c = 0; c < n; c++)
        {
            var p = placed[c];
            bw.Write(p.X);
            bw.Write(p.Y);
            bw.Write((p.Vertical ? 1 : 0) | (IsPixel((uint)c) ? 2 : 0));
            bw.Write(p.Size);
        }
        return file.ToArray();
    }

    // 1800653b0: a texel's luxel record, shadow record and channels from another texel (each channel's
    // components as the source's mask has them, written where the target's mask has them).
    private static void CopyTexel(LightmapImage to, int x, int y, LightmapImage from, int sx, int sy)
    {
        int d = y * to.Width + x, s = sy * from.Width + sx;
        from.LuxelRecords.AsSpan(s * 12, 12).CopyTo(to.LuxelRecords.AsSpan(d * 12));
        from.ShadowRecords.AsSpan(s * 20, 20).CopyTo(to.ShadowRecords.AsSpan(d * 20));
        for (var c = 0; c < LightmapImage.ChannelCount; c++)
        {
            if (to.Channels[c] is not { } dst || from.Channels[c] is not { } src)
                continue;
            for (var k = 0; k < 4; k++)
                if ((dst.Mask >> k & 1) != 0)
                    dst.Data[d * 4 + k] = (src.Mask >> k & 1) != 0 ? src.Data[s * 4 + k] : 0f;
        }
    }

    // 18006dbc0's cleared texel. The luxel record's bitfields around the chart id and flags are a stack
    // value Valve never sets; measured on probe01 (every cleared texel): sample word 0, flags word 0xfffffff8.
    private static void Clear(LightmapImage image, int i)
    {
        var rec = image.LuxelRecords.AsSpan(i * 12);
        BinaryPrimitives.WriteUInt32LittleEndian(rec, 0xffffffff);
        BinaryPrimitives.WriteUInt32LittleEndian(rec[4..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(rec[8..], 0xfffffff8);
        var shadow = image.ShadowRecords.AsSpan(i * 20);
        BinaryPrimitives.WriteUInt32LittleEndian(shadow, 0xffffffff);
        for (var k = 0; k < 4; k++)
            BinaryPrimitives.WriteSingleLittleEndian(shadow[(4 + k * 4)..], 1f);
        foreach (var ch in image.Channels)
            if (ch is not null)
                for (var k = 0; k < 4; k++)
                    if ((ch.Mask >> k & 1) != 0)
                        ch.Data[i * 4 + k] = 0f;
    }

    private static float Component(LightmapImage.Channel ch, int i, int k) => (ch.Mask >> k & 1) != 0 ? ch.Data[i * 4 + k] : 0f;

    // 180066c60.
    private static void SanitizeLineCharts(LightmapImage image, IReadOnlyList<PackingGeometry.Chart> charts)
    {
        var w = image.Width;
        Parallel.For(0, charts.Count, c =>
        {
            var chart = charts[c];
            if ((chart.Flags >> 29 & 1) == 0 || (chart.Flags >> 28 & 1) != 0)
                return;
            int x0 = chart.X, x1 = chart.X + chart.Width, y0 = chart.Y, y1 = chart.Y + chart.Height;
            if (y0 >= y1)
                return;
            // The last filled texel carries over from one row to the next, as Valve's loop does.
            var last = -1;
            for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++)
                {
                    if (image.ChartId(y * w + x) != 0xffffffff)
                        last = x;
                    else if (last != -1)
                        CopyTexel(image, x, y, image, last, y);
                }
            last = -1;
            for (var y = y0; y < y1; y++)
                for (var x = x1 - 1; x >= x0; x--)
                {
                    if (image.ChartId(y * w + x) != 0xffffffff)
                        last = x;
                    else if (last != -1)
                        CopyTexel(image, x, y, image, last, y);
                }
        });
    }

    // lambda_6: each chart's first texel of its own id, row major in its rect ((-1, -1) for none).
    private static (int X, int Y)[] FirstTexels(LightmapImage image, IReadOnlyList<PackingGeometry.Chart> charts)
    {
        var w = image.Width;
        var first = new (int X, int Y)[charts.Count];
        Parallel.For(0, charts.Count, c =>
        {
            first[c] = (-1, -1);
            var chart = charts[c];
            for (var y = chart.Y; y < chart.Y + chart.Height; y++)
                for (var x = chart.X; x < chart.X + chart.Width; x++)
                    if (image.ChartId(y * w + x) == (uint)c)
                    {
                        first[c] = (x, y);
                        return;
                    }
        });
        return first;
    }

    // 18006caa0 then the inversion (180062930): a chart is a pixel chart unless a sampled texel of it
    // differs from its first texel.
    private static bool[] ZeroVariation(LightmapImage image, int n, (int X, int Y)[] first)
    {
        var w = image.Width;
        var varying = new bool[n];
        Parallel.For(0, image.Height, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                var c = image.ChartId(i);
                if (c == 0xffffffff || image.SampleCount(i) == 0 || Volatile.Read(ref varying[c]))
                    continue;
                if (first[c] != (x, y) && !Similar(image, first[c].X, first[c].Y, x, y))
                    Volatile.Write(ref varying[c], true);
            }
        });
        var pixel = new bool[n];
        for (var c = 0; c < n; c++)
            pixel[c] = !varying[c];
        return pixel;
    }

    // 180064f30: two texels match when every live channel does (its R only, within 0.04, for 8-13, 15
    // and 17-19; else each component within 1.5% of the larger magnitude, at least 1) and the shadow
    // record's four floats agree on zero and are within 0.04. A NaN difference does not match.
    private static bool Similar(LightmapImage image, int x1, int y1, int x2, int y2)
    {
        int a = y1 * image.Width + x1, b = y2 * image.Width + x2;
        for (var c = 0; c < LightmapImage.ChannelCount; c++)
        {
            if (image.Channels[c] is not { } ch)
                continue;
            if (Rounded(c))
            {
                if (!(0.04f >= MathF.Abs(Component(ch, a, 0) - Component(ch, b, 0))))
                    return false;
                continue;
            }
            for (var k = 0; k < 4; k++)
            {
                float u = Component(ch, a, k), v = Component(ch, b, k);
                var diff = MathF.Abs(u - v);
                float au = MathF.Abs(u), av = MathF.Abs(v);
                var larger = au > av ? au : av;
                var threshold = (1f > larger ? 1f : larger) * 0.015f;
                if (!(threshold >= diff))
                    return false;
            }
        }
        var sa = image.ShadowRecords.AsSpan(a * 20);
        var sb = image.ShadowRecords.AsSpan(b * 20);
        for (var k = 1; k <= 4; k++)
        {
            float u = BinaryPrimitives.ReadSingleLittleEndian(sa[(k * 4)..]), v = BinaryPrimitives.ReadSingleLittleEndian(sb[(k * 4)..]);
            if ((u == 0f) != (v == 0f) || !(0.04f >= MathF.Abs(u - v)))
                return false;
        }
        return true;
    }

    // The channels 180064f30 and 1800648f0 treat as scalars rounded to 0.1.
    private static bool Rounded(int c) => c is 8 or 9 or 10 or 11 or 12 or 13 or 15 or 17 or 18 or 19;

    private static uint Mix(uint v)
    {
        v *= M;
        return (v >> 24 ^ v) * M;
    }

    private static uint Final(uint v)
    {
        v = (v >> 13 ^ v) * M;
        return v >> 15 ^ v;
    }

    private static float Tenth(float v) => MathF.Floor(v / 0.1f + 0.5f) * 0.1f;

    private static uint Bits(float v) => BitConverter.SingleToUInt32Bits(v);

    // 1800648f0: per live channel, its four components (rounded to 0.1 for the scalar channels, else 0
    // below 0.01 and the float's sign, exponent and top mantissa bit) hashed and combined; then the shadow
    // record, its floats rounded to 0.1 and a light index byte read as 0xff where its float rounds to 1.
    private static uint Hash(LightmapImage image, int x, int y)
    {
        var i = y * image.Width + x;
        var acc = 0x7a17e123u;
        Span<uint> v = stackalloc uint[4];
        for (var c = 0; c < LightmapImage.ChannelCount; c++)
        {
            if (image.Channels[c] is not { } ch)
                continue;
            for (var k = 0; k < 4; k++)
            {
                if ((ch.Mask >> k & 1) == 0)
                {
                    v[k] = 0;
                    continue;
                }
                var f = ch.Data[i * 4 + k];
                v[k] = Rounded(c) ? Bits(Tenth(f)) : 0.01f > f ? 0u : Bits(f) & 0xffc00000;
            }
            var hc = Final(Mix(v[3]) ^ (Mix(v[2]) ^ (Mix(v[1]) ^ (Mix(v[0]) ^ 0xbe0adc34) * M) * M) * M);
            acc ^= (acc >> 2) + hc + (acc << 6) + 0x9e3779b9;
        }
        var s = image.ShadowRecords.AsSpan(i * 20);
        Span<float> r = stackalloc float[4];
        for (var k = 0; k < 4; k++)
            r[k] = Tenth(BinaryPrimitives.ReadSingleLittleEndian(s[(4 + k * 4)..]));
        var word = 0u;
        for (var k = 0; k < 4; k++)
            word |= (uint)(r[k] == 1f ? 0xff : s[k]) << (k * 8);
        var hs = Final(Mix(Bits(r[3])) ^ (Mix(Bits(r[2])) ^ (Mix(Bits(r[1])) ^ (Mix(Bits(r[0])) ^ (Mix(word) ^ 0x4ec335e0) * M) * M) * M) * M);
        return (acc >> 2) + hs + (acc << 6) + 0x9e3779b9 ^ acc;
    }

    // 18006cc70, one bucket: each pixel-chart texel (last first) takes the first other-chart texel it
    // matches; then each pixel texel left, in order, takes the later ones (last first) that match it.
    private static void AliasBucket(LightmapImage image, IReadOnlyList<PackingGeometry.Chart> charts, List<(int X, int Y)> bucket,
        Func<uint, bool> isPixel, Alias[] aliases)
    {
        var w = image.Width;
        var others = new List<(int X, int Y)>();
        var pixels = new List<(int X, int Y)>();
        foreach (var t in bucket)
            (isPixel(image.ChartId(t.Y * w + t.X)) ? pixels : others).Add(t);
        for (var j = pixels.Count - 1; j >= 0; j--)
        {
            var p = pixels[j];
            foreach (var o in others)
            {
                if (!Similar(image, p.X, p.Y, o.X, o.Y))
                    continue;
                var target = (int)image.ChartId(o.Y * w + o.X);
                aliases[image.ChartId(p.Y * w + p.X)] = new Alias(target, o.X - charts[target].X, o.Y - charts[target].Y);
                RemoveSwap(pixels, j);
                break;
            }
        }
        for (var i = 0; i < pixels.Count; i++)
        {
            var target = (int)image.ChartId(pixels[i].Y * w + pixels[i].X);
            for (var j = pixels.Count - 1; j > i; j--)
                if (Similar(image, pixels[i].X, pixels[i].Y, pixels[j].X, pixels[j].Y))
                {
                    aliases[image.ChartId(pixels[j].Y * w + pixels[j].X)] = new Alias(target, 0, 0);
                    RemoveSwap(pixels, j);
                }
        }
    }

    // CUtlVector::FastRemove: the last element moves into the hole.
    private static void RemoveSwap<T>(List<T> list, int i)
    {
        list[i] = list[^1];
        list.RemoveAt(list.Count - 1);
    }

    // The comparator 18006ca60 sorts lines with (MSVC std::sort; a total order): length, then horizontal
    // before vertical, then y, then x.
    private static int CompareLines(Line a, Line b)
    {
        if (a.Length != b.Length)
            return a.Length.CompareTo(b.Length);
        if (a.Vertical != b.Vertical)
            return a.Vertical ? 1 : -1;
        return a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X);
    }

    // FindLongestLinesCoveringCells (180065690): rounds until no cell is left. Each round (180065bb0,
    // 180065d20) gives every cell the length of the horizontal and the vertical run through it; then
    // (180065e50) a row run whose cells are all at least as long across as down becomes a line, and a
    // column run whose cells are all longer down than across; their cells are taken. Sorted at the end.
    private static List<Line> FindLongestLines(bool[] cells, int w, int h)
    {
        var lines = new List<Line>();
        var across = new int[w * h];
        var down = new int[w * h];
        while (Array.IndexOf(cells, true) >= 0)
        {
            Array.Clear(across);
            Array.Clear(down);
            Parallel.For(0, h, y =>
            {
                var run = 0;
                for (var x = w - 1; x >= 0; x--)
                    across[y * w + x] = cells[y * w + x] ? ++run : run = 0;
                var prev = 0;
                for (var x = 0; x < w; x++)
                {
                    var v = across[y * w + x];
                    if (v != 0 && prev != 0)
                        across[y * w + x] = v = prev;
                    prev = v;
                }
            });
            Parallel.For(0, w, x =>
            {
                var run = 0;
                for (var y = h - 1; y >= 0; y--)
                    down[y * w + x] = cells[y * w + x] ? ++run : run = 0;
                var prev = 0;
                for (var y = 0; y < h; y++)
                {
                    var v = down[y * w + x];
                    if (v != 0 && prev != 0)
                        down[y * w + x] = v = prev;
                    prev = v;
                }
            });
            var found = new List<Line>();
            for (var y = 0; y < h; y++)
            {
                int start = 0;
                var blocked = false;
                for (var x = 0; x < w; x++)
                {
                    var v = across[y * w + x];
                    if (v == 0)
                    {
                        if (!blocked && x - start > 0)
                            found.Add(new Line(x - start, start, y, false));
                        start = x + 1;
                        blocked = false;
                    }
                    else if (!blocked)
                        blocked = v < down[y * w + x];
                }
                if (!blocked && w - start > 0)
                    found.Add(new Line(w - start, start, y, false));
            }
            for (var x = 0; x < w; x++)
            {
                int start = 0;
                var blocked = false;
                for (var y = 0; y < h; y++)
                {
                    var v = down[y * w + x];
                    if (v == 0)
                    {
                        if (!blocked && y - start > 0)
                            found.Add(new Line(y - start, x, start, true));
                        start = y + 1;
                        blocked = false;
                    }
                    else if (!blocked)
                        blocked = v <= across[y * w + x];
                }
                if (!blocked && h - start > 0)
                    found.Add(new Line(h - start, x, start, true));
            }
            foreach (var line in found)
                for (var k = 0; k < line.Length; k++)
                    cells[(line.Y + (line.Vertical ? k : 0)) * w + line.X + (line.Vertical ? 0 : k)] = false;
            lines.AddRange(found);
        }
        lines.Sort(CompareLines);
        return lines;
    }

    // AmeliorateBlockCompressionArtefacts_FillBlocks (180066230): in each 4x4 block with some but not
    // all texels filled, per live channel, the empty texels take the filled texels' most common colour,
    // counted in a CUtlHashtable (32 slots, emptied per channel) keyed by R, G and A rounded to 1/255
    // (Valve's key drops B, a typo, and its fourth float is a stack value the function never writes:
    // <see cref="KeyW"/>), the first texel's exact value of that key; of equally common keys the one of
    // largest squared length, the first in slot order on a tie.
    private static void FillBlocks(LightmapImage image)
    {
        var w = image.Width;
        int bw = w / 4, bh = image.Height / 4;
        Parallel.For(0, bw * bh, () => new UtlHashtable<ColorKey, (int Count, float V0, float V1, float V2, float V3)>(32), (b, _, table) =>
        {
            int x0 = b % bw * 4, y0 = b / bw * 4;
            var filled = 0;
            for (var y = y0; y < y0 + 4; y++)
                for (var x = x0; x < x0 + 4; x++)
                    filled += image.ChartId(y * w + x) != 0xffffffff ? 1 : 0;
            if (filled == 0 || filled == 16)
                return table;
            foreach (var ch in image.Channels)
            {
                if (ch is null)
                    continue;
                table.Clear();
                var most = 0;
                for (var y = y0; y < y0 + 4; y++)
                    for (var x = x0; x < x0 + 4; x++)
                    {
                        var i = y * w + x;
                        if (image.ChartId(i) == 0xffffffff)
                            continue;
                        float v0 = Component(ch, i, 0), v1 = Component(ch, i, 1), v2 = Component(ch, i, 2), v3 = Component(ch, i, 3);
                        var key = new ColorKey(Round255(v0), Round255(v1), Round255(v3), KeyW);
                        var hash = Final(Mix(Bits(key.W)) ^ (Mix(Bits(key.A)) ^ (Mix(Bits(key.G)) ^ (Mix(Bits(key.R)) ^ 0xbe0adc34) * M) * M) * M);
                        var slot = table.FindOrInsert(key, hash, out var inserted);
                        ref var e = ref table.Value(slot);
                        if (inserted)
                            e = (1, v0, v1, v2, v3);
                        else
                            e.Count++;
                        if (e.Count > most)
                            most = e.Count;
                    }
                float b0 = 0, b1 = 0, b2 = 0, b3 = 0;
                var equal = 0;
                foreach (var slot in table.Slots())
                {
                    var e = table.Value(slot);
                    if (e.Count != most)
                        continue;
                    var len = e.V0 * e.V0 + e.V1 * e.V1 + e.V2 * e.V2 + e.V3 * e.V3;
                    var best = b1 * b1 + b0 * b0 + b2 * b2 + b3 * b3;
                    if (len > best)
                    {
                        (b0, b1, b2, b3) = (e.V0, e.V1, e.V2, e.V3);
                        equal = 1;
                    }
                    else if (len == best)
                        equal++;
                }
                if (equal > 1)
                    TieObserver?.Invoke(x0, y0, Array.IndexOf(image.Channels, ch));
                for (var y = y0; y < y0 + 4; y++)
                    for (var x = x0; x < x0 + 4; x++)
                    {
                        var i = y * w + x;
                        if (image.ChartId(i) != 0xffffffff)
                            continue;
                        if ((ch.Mask & 1) != 0) ch.Data[i * 4] = b0;
                        if ((ch.Mask & 2) != 0) ch.Data[i * 4 + 1] = b1;
                        if ((ch.Mask & 4) != 0) ch.Data[i * 4 + 2] = b2;
                        if ((ch.Mask & 8) != 0) ch.Data[i * 4 + 3] = b3;
                    }
            }
            return table;
        }, _ => { });
    }

    /// <summary>
    /// The fourth float of FillBlocks' colour key: a stack slot the function never writes. Measured under Frida
    /// (tools/vrad3/capture_amel_keyw.py): 0 on vrad3's 15 worker threads, a small value that changes from run to
    /// run (0x495, 0x34b, 0x50a, 0x50a as bits) on the one thread that also runs jobs. It only moves which of two
    /// equally common keys of equal squared length wins, so Valve's own runs can differ there.
    /// </summary>
    private const float KeyW = 0f;

    /// <summary>Told of each block (corner) and channel whose fill was such a tie (tests).</summary>
    internal static Action<int, int, int>? TieObserver;

    private static float Round255(float v) => MathF.Round(v * 255f, MidpointRounding.AwayFromZero) / 255f;

    // FillBlocks' key, compared as floats (1800770e0).
    private readonly record struct ColorKey(float R, float G, float A, float W) : IEquatable<ColorKey>
    {
        public bool Equals(ColorKey o) => R == o.R && G == o.G && A == o.A && W == o.W;
        public override int GetHashCode() => 0;
    }
}
