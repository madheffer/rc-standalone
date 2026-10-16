using System.Numerics;
using Source2.Compiler.Maps;

namespace Source2.Compiler.Lighting;

/// <summary>
/// resourcecompiler's NUVChartPacker (0923 uvchartpacker.cpp; docs/BAKED.md
/// "Lightmap charts"): the charts of a packing geometry laid out in the
/// lightmap atlas. NUVChartPacker::PackCharts (1802272d0) gives each chart
/// its triangles, 3D bounds, a debug colour from SFMT-19937 (seed 0x777f17e)
/// and its best rotation (FindBestRotation, 180223460 / 180222aa0), then
/// searches the uv scale over its passes, each one InitializeChartUVs
/// (1802235d0) and TryPack (180226370).
/// </summary>
public static class LightmapPacker
{
    /// <summary>A chart as the packer keeps it (the 0x70-byte record).</summary>
    public sealed class Chart
    {
        public int Index;                       // +0x20
        public readonly List<int> Triangles = [];  // +0x00
        public readonly List<Vector2> Uvs = [];    // +0x10, three per triangle
        public uint Color;                      // +0x24
        public Vector3 Min = new(float.MaxValue), Max = new(-float.MaxValue);  // +0x28, +0x34
        public int X0, Y0;                      // +0x40, +0x44
        public float Width, Height;             // +0x48, +0x4c: the extents
        public int GutterX, GutterY;            // +0x50, +0x54
        public int Luxels;                      // +0x58
        public int PaddedWidth, PaddedHeight;   // +0x5c, +0x60
        public float Angle;                     // +0x64
        public float CurrentAngle;              // +0x68
        public uint Flags;                      // +0x6c
    }

    /// <summary>The charts of <paramref name="geometry"/> before any scale pass: triangles, bounds, flags, colours, rotations.</summary>
    public static List<Chart> Prepare(PackingGeometry geometry, float[] luxelUvs)
    {
        var charts = new List<Chart>(geometry.Charts.Length);
        for (var i = 0; i < geometry.Charts.Length; i++)
            charts.Add(new Chart());
        for (var t = 0; t < geometry.TriangleCharts.Length; t++)
            if (geometry.TriangleCharts[t] != uint.MaxValue)
                charts[(int)geometry.TriangleCharts[t]].Triangles.Add(t);
        for (var i = 0; i < charts.Count; i++)
        {
            var c = charts[i];
            c.Index = i;
            var record = geometry.Charts[i].Flags;
            c.Flags ^= ((record >> 28) ^ c.Flags) & 4;
            c.Flags = (record << 4) | (c.Flags & 0xf);
        }
        // The 3D bounds of each chart's corners.
        var stride = geometry.VertexStride;
        foreach (var c in charts)
            foreach (var t in c.Triangles)
                for (var k = 0; k < 3; k++)
                {
                    var v = (int)geometry.Indices[t * 3 + k];
                    var p = new Vector3(BitConverter.ToSingle(geometry.Vertices, v * stride + (int)geometry.Offsets[0]),
                                        BitConverter.ToSingle(geometry.Vertices, v * stride + (int)geometry.Offsets[0] + 4),
                                        BitConverter.ToSingle(geometry.Vertices, v * stride + (int)geometry.Offsets[0] + 8));
                    if (p.Z <= c.Min.Z) c.Min.Z = p.Z;
                    if (p.X <= c.Min.X) c.Min.X = p.X;
                    if (p.Y <= c.Min.Y) c.Min.Y = p.Y;
                    if (c.Max.Z <= p.Z) c.Max.Z = p.Z;
                    if (c.Max.X <= p.X) c.Max.X = p.X;
                    if (c.Max.Y <= p.Y) c.Max.Y = p.Y;
                }
        // A random hue per chart, as an sRGB ARGB colour.
        var random = new Sfmt19937(0x777f17e);
        foreach (var c in charts)
        {
            var h = ((float)random.Next() * 2.328306e-10f + 0f) * 6f;
            var r = MathF.Abs(h - 3f) - 1f;
            var g = 2f - MathF.Abs(h - 2f);
            var b = 2f - MathF.Abs(h - 4f);
            if (r <= 0f) r = 0f;
            if (g <= 0f) g = 0f;
            if (b <= 0f) b = 0f;
            if (1f <= r) r = 1f;
            if (1f <= g) g = 1f;
            if (1f <= b) b = 1f;
            c.Color = Srgb(r) | (uint)Srgb(g) << 8 | (uint)Srgb(b) << 16 | 0xff000000u;
        }
        foreach (var c in charts)
            c.Angle = BestRotation(luxelUvs, c, geometry.Indices);
        return charts;
    }

    /// <summary>
    /// NUVChartPacker::InitializeChartUVs (1802235d0, its job 180223970): every
    /// chart's corners rotated by its angle and scaled, shrunk to fit the atlas,
    /// given its rect, gutter and padding; then the charts in packing order
    /// (18022a700: unflagged first, big ones first, by luxels, then index).
    /// </summary>
    public static List<Chart> InitializeChartUvs(List<Chart> charts, float[] uvs, uint[] indices, float scale, int width, int height, int gutter = 1)
    {
        foreach (var c in charts)
            InitializeChart(c, uvs, indices, scale, width, height, gutter);
        var order = new List<Chart>(charts);
        order.Sort((a, b) => Before(a, b, (uint)width, (uint)height) ? -1 : Before(b, a, (uint)width, (uint)height) ? 1 : 0);
        return order;
    }

    private static bool Big(Chart c, uint width, uint height)
        => !((uint)c.PaddedWidth < width >> 1 && (uint)c.PaddedHeight < height >> 1 && (uint)c.Luxels < width * height >> 4);

    private static bool Before(Chart a, Chart b, uint width, uint height)
    {
        var a0 = a.Flags & 1;
        var b0 = b.Flags & 1;
        if (a0 == 0 || b0 == 0)
        {
            if (a0 != b0)
                return b0 != 0;
            var (bigA, bigB) = (Big(a, width, height), Big(b, width, height));
            if (bigA != bigB)
                return bigA;
            if ((uint)a.Luxels != (uint)b.Luxels)
                return (uint)b.Luxels < (uint)a.Luxels;
        }
        else if (a.PaddedWidth != b.PaddedWidth)
            return b.PaddedWidth < a.PaddedWidth;
        return (uint)a.Index < (uint)b.Index;
    }

    private static void InitializeChart(Chart c, float[] uvs, uint[] indices, float scale, int width, int height, int gutter)
    {
        var corners = c.Triangles.Count * 3;
        var rotated = new Vector2[corners];
        var scaled = new Vector2[corners];
        var angle = c.Angle;
        int luxelsX, luxelsY;
        uint thinY, thinX;
        while (true)
        {
            c.CurrentAngle = angle;
            var cos = LightCrt.Cos(angle);
            var sin = LightCrt.Sin(angle);
            float minX = float.MaxValue, minY = float.MaxValue;
            for (var i = 0; i < corners; i++)
            {
                var v = indices[c.Triangles[i / 3] * 3 + i % 3];
                float u = uvs[v * 2], w = uvs[v * 2 + 1];
                var x = (cos * u - sin * w) * scale;
                var y = (sin * u + cos * w) * scale;
                rotated[i] = new Vector2(x, y);
                if (x <= minX) minX = x;
                if (y <= minY) minY = y;
            }
            if (minX < 0f || minY < 0f)
                for (var i = 0; i < corners; i++)
                    rotated[i] = new Vector2(rotated[i].X - minX, rotated[i].Y - minY);
            c.Flags &= 0xfffffffc;
            c.Luxels = int.MaxValue;
            var factor = 1f;
            var shrunk = 0u;
            float loX, loY, hiX, hiY, extentX, extentY;
            while (true)
            {
                (loX, loY, hiX, hiY) = (float.MaxValue, float.MaxValue, -float.MaxValue, -float.MaxValue);
                for (var i = 0; i < corners; i++)
                {
                    var p = new Vector2(rotated[i].X * factor, rotated[i].Y * factor);
                    scaled[i] = p;
                    if (hiX <= p.X) hiX = p.X;
                    if (hiY <= p.Y) hiY = p.Y;
                    if (p.X <= loX) loX = p.X;
                    if (p.Y <= loY) loY = p.Y;
                }
                extentX = hiX - loX;
                extentY = hiY - loY;
                if (extentX <= (float)(width - 0x20) && extentY <= (float)(height - 0x20))
                    break;
                shrunk = 1;
                var fy = (float)(height - 0x30) / extentY;
                factor = (float)(width - 0x30) / extentX;
                if (fy <= factor)
                    factor = fy;
            }
            luxelsX = (int)MathF.Ceiling(hiX) - (int)MathF.Floor(loX);
            if (luxelsX < 1)
                luxelsX = 1;
            luxelsY = (int)MathF.Ceiling(hiY) - (int)MathF.Floor(loY);
            if (luxelsY < 1)
                luxelsY = 1;
            c.X0 = (int)loX;
            c.Y0 = (int)loY;
            c.Width = extentX;
            c.Height = extentY;
            c.Luxels = luxelsX * luxelsY;
            c.Uvs.Clear();
            c.Uvs.AddRange(scaled);
            thinY = luxelsY == 1 ? 1u : 0u;
            thinX = luxelsX == 1 ? 2u : 0u;
            c.Flags = thinY | (c.Flags & 0xfffffff4u) | thinX | shrunk * 8;
            (c.GutterX, c.GutterY) = luxelsY == 1 ? (1, 0) : (gutter, gutter);
            if ((thinY | thinX) == 3)
                c.Luxels = 1;
            if (thinX == 0 || luxelsY == 1)
                break;
            angle = c.CurrentAngle + 90f;
        }
        c.PaddedWidth = (int)MathF.Ceiling(c.Width + 0.0001f + (float)(c.GutterX * 2));
        c.PaddedHeight = (int)MathF.Ceiling(c.Height + 0.0001f + (float)(c.GutterY * 2));
        if ((thinY | thinX) == 3)
        {
            (c.PaddedWidth, c.PaddedHeight, c.GutterX, c.GutterY, c.X0, c.Y0) = (1, 1, 0, 0, 0, 0);
            for (var i = 0; i < c.Uvs.Count; i++)
                c.Uvs[i] = new Vector2(0.5f, 0.5f);
            return;
        }
        if (thinY != 0)
        {
            (c.PaddedHeight, c.GutterY, c.Y0) = (1, 0, 0);
            for (var i = 0; i < c.Uvs.Count; i++)
                c.Uvs[i] = new Vector2(c.Uvs[i].X, 0.5f);
        }
        float sx = 1f, sy = 1f;
        if (c.PaddedWidth % 4 != 0 && c.PaddedHeight % 4 != 0)
        {
            sx = ((float)(int)(((c.PaddedWidth + 3) & ~3) - c.GutterX * 2) - 0.0001f) * 0.9999f / c.Width;
            if ((c.Flags & 1) == 0)
            {
                sy = ((float)(int)(((c.PaddedHeight + 3) & ~3) - c.GutterY * 2) - 0.0001f) * 0.9999f / c.Height;
                var m = sy <= sx ? sy : sx;
                (sx, sy) = (1f, 1f);
                if (1f <= m)
                    (sx, sy) = (m, m);
            }
            var w = sx * c.Width;
            c.Height = sy * c.Height;
            c.Width = w;
            var padY = (int)MathF.Ceiling((float)(c.GutterY * 2) + c.Height + 0.0001f);
            var padX = (int)MathF.Ceiling((float)(c.GutterX * 2) + w + 0.0001f);
            c.PaddedHeight = (c.Flags & 1) != 0 ? 1 : padY;
            c.PaddedWidth = padX;
        }
        for (var i = 0; i < c.Uvs.Count; i++)
        {
            var p = c.Uvs[i];
            var x = (p.X - (float)c.X0) * sx + (float)c.GutterX;
            var y = (p.Y - (float)c.Y0) * sy + (float)c.GutterY;
            c.Uvs[i] = new Vector2(x, y);
        }
    }

    /// <summary>One TryPack attempt's outcome: whether every chart found a place, and each chart's (x, y) by index.</summary>
    public sealed record Attempt(float Scale, bool Packed, int[] Placements);

    /// <summary>
    /// NUVChartPacker::PackCharts (1802272d0) from the charts' luxel uvs: the
    /// scale search over <paramref name="passes"/> (halving steps from
    /// sqrt(atlas luxels / chart luxels)), the last packed attempt's charts
    /// written into <paramref name="geometry"/>'s records (colour, index, x, y,
    /// padded size, flags) and its uvs normalised into <paramref name="uvs"/>.
    /// </summary>
    public static bool Pack(PackingGeometry geometry, float[] uvs, int width, int height, int mip, int passes, List<Attempt>? attempts = null)
    {
        var shift = mip - 1;
        width >>= shift;
        height >>= shift;
        if (width * height <= (int)MathF.Ceiling(geometry.Charts.Length * 2.5f))
            return false;
        var luxelUvs = (float[])uvs.Clone();
        var charts = Prepare(geometry, luxelUvs);
        InitializeChartUvs(charts, luxelUvs, geometry.Indices, 1f, width, height);
        var total = 0u;
        foreach (var c in charts)
        {
            total = (uint)((int)total + c.Luxels);
            if (1f < c.Width)
                total = (uint)((int)total + (int)MathF.Ceiling(c.Height) * c.GutterX * 2);
            if (1f < c.Height)
                total = (uint)((int)total + (int)MathF.Ceiling(c.Width) * c.GutterY * 2);
        }
        var ratio = (float)(uint)(width * height) / (float)total;
        var step = ratio < 0f ? throw new NotSupportedException("a negative luxel ratio") : MathF.Sqrt(ratio);
        var scale = step;
        if (passes == 0)
            passes = 1;
        float best = 0f;
        int[]? placed = null;
        for (var p = 0; p < passes; p++)
        {
            step *= 0.5f;
            var order = InitializeChartUvs(charts, luxelUvs, geometry.Indices, scale, width, height);
            var positions = new int[charts.Count * 2];
            var ok = TryPack(order, width, height, positions);
            attempts?.Add(new Attempt(scale, ok, positions));
            if (ok)
            {
                best = scale;
                placed = positions;
                scale += step;
            }
            else
                scale -= step;
        }
        if (best == 0f || placed is null)
            return false;
        var final = InitializeChartUvs(charts, luxelUvs, geometry.Indices, best, width, height);
        foreach (var c in final)
        {
            var record = geometry.Charts[c.Index];
            var flags = record.Flags;
            flags ^= ((c.Flags << 27) ^ flags) & 0x10000000u;
            flags ^= ((c.Flags << 29) ^ flags) & 0x20000000u;
            flags ^= ((c.Flags << 28) ^ flags) & 0x40000000u;
            var (x, y) = (placed[c.Index * 2], placed[c.Index * 2 + 1]);
            flags = (flags & 0xf0000000u) | (c.Flags >> 4);
            geometry.Charts[c.Index] = new PackingGeometry.Chart(c.Color, (uint)c.Index, x << shift, y << shift,
                                                                 c.PaddedWidth << shift, c.PaddedHeight << shift, flags);
            for (var i = 0; i < c.Triangles.Count * 3; i++)
            {
                var v = geometry.Indices[c.Triangles[i / 3] * 3 + i % 3];
                uvs[v * 2] = (c.Uvs[i].X + (float)x) / (float)width;
                uvs[v * 2 + 1] = (c.Uvs[i].Y + (float)y) / (float)height;
            }
        }
        return true;
    }

    /// <summary>
    /// NUVChartPacker::TryPack (180226370): the charts in packing order into
    /// an empty atlas, as Valve's phases run them with DeterministicBuild: the
    /// big ones, then the other unflagged ones (180226c30, a chart's dilated
    /// coverage masks through 180225a60), the single luxels (180226100), and
    /// the lines (180225350).
    /// </summary>
    public static bool TryPack(List<Chart> order, int width, int height, int[] positions, int gutter = 1)
    {
        var atlas = new ulong[(width >> 3) * (height >> 3)];
        var big = 0;
        while (big < order.Count && (order[big].Flags & 1) == 0 && Big(order[big], (uint)width, (uint)height))
            big++;
        var lines = order.FindIndex(c => (c.Flags & 1) != 0);
        if (lines < 0)
            lines = order.Count;
        var pixels = order.FindIndex(lines, c => (c.Flags & 2) != 0);
        if (pixels < 0)
            pixels = order.Count;
        var cache = new Dictionary<(int, int), (int Col, int Row)>();
        for (var i = 0; i < big; i++)
            if (!PlaceChart(order[i], width, height, atlas, positions, gutter, cache))
                return false;
        cache = [];
        for (var i = big; i < lines; i++)
            if (!PlaceChart(order[i], width, height, atlas, positions, gutter, cache))
                return false;
        return PlacePixels(order, pixels, order.Count, width, height, atlas, positions)
               && PlaceLines(order, lines, pixels, width, height, atlas, positions);
    }

    // 180225020: a group no larger than its limit keeps the packing order; a
    // larger one takes the primitive order of a TinyBVH built over the charts'
    // 3D bounds (callback 180225320), c_trav the limit and c_int 1.
    private static List<Chart> Group(List<Chart> order, int from, int to, int limit)
    {
        var group = order.GetRange(from, to - from);
        if (group.Count <= limit)
            return group;
        var bvh = TinyBvh.Order(group.ConvertAll(c => (c.Min, c.Max)), limit, 1f);
        return Array.ConvertAll(bvh, i => group[i]).ToList();
    }

    // 180226c30 for one chart: the coverage masks, one per sub-block offset,
    // dilated by the gutter, placed by 180225a60.
    private static bool PlaceChart(Chart c, int width, int height, ulong[] atlas, int[] positions, int gutter, Dictionary<(int, int), (int Col, int Row)> cache)
    {
        var gy = (c.Flags & 1) != 0 ? 1 : 4;
        var gx = 4;
        if (0xff < c.PaddedWidth)
            gx = 8;
        if (0xff < c.PaddedHeight)
            gy = 8;
        if (gx - 8 + width < c.PaddedWidth || gy - 8 + height < c.PaddedHeight)
            return false;
        var bh = (c.PaddedHeight - gy + 0xf) >> 3;
        var bw = (c.PaddedWidth - gx + 0xf) >> 3;
        var n = (8 / gx) * (8 / gy);
        var raster = new ulong[n][];
        var masks = new ulong[n][];
        for (var k = 0; k < n; k++)
        {
            raster[k] = new ulong[bw * bh];
            masks[k] = new ulong[bw * bh];
        }
        for (var i = 0; i + 2 < c.Uvs.Count; i += 3)
            Rasterize(c.Uvs[i], c.Uvs[i + 1], c.Uvs[i + 2], gy, gx, bw, raster);
        var (dx, dy) = (c.Flags & 1) != 0 ? (1, 0) : (gutter, gutter);
        Dilate(dx, dy, raster, masks, gx, gy, bw, bh);
        var rect = false;
        var area = (uint)(c.PaddedHeight * c.PaddedWidth);
        if (area < 0x801)
        {
            long bits = 0;
            for (var i = 0; i < bh * bw; i++)
                bits += BitOperations.PopCount(masks[0][i]);
            rect = bits == (int)area;
        }
        return Place(c, width, height, atlas, bw, bh, masks, gx, gy, positions, rect ? cache : null);
    }

    // 180228ea0: the luxels a triangle covers, conservatively (each edge pushed
    // out half a luxel by its normal's signs), set in every offset mask.
    private static void Rasterize(Vector2 p0, Vector2 p1, Vector2 p2, int gy, int gx, int bw, ulong[][] masks)
    {
        float ax = p0.X, ay = p0.Y, bx = p1.X, by = p1.Y, cx = p2.X, cy = p2.Y;
        if ((cx - ax) * (by - ay) - (bx - ax) * (cy - ay) < 0f)
            (bx, by, cx, cy) = (cx, cy, bx, by);
        var m = ax;
        if (bx <= m) m = bx;
        if (cx <= m) m = cx;
        var x0 = MathF.Floor(m);
        m = ay;
        if (by <= m) m = by;
        if (cy <= m) m = cy;
        var y0 = (int)MathF.Floor(m);
        m = ax;
        if (ax <= bx) m = bx;
        if (m <= cx) m = cx;
        var x1 = MathF.Ceiling(m);
        m = ay;
        if (ay <= by) m = by;
        if (m <= cy) m = cy;
        var y1 = MathF.Ceiling(m);
        var f18 = by - cy;
        var f28 = cx - ax;
        var f12 = cy - ay;
        var f21 = bx - cx;
        var f26 = ax - bx;
        var f27 = ay - by;
        var o9 = 0f <= f21 ? 0.5f : -0.5f;
        var o13 = -f18 < 0f ? -0.5f : 0.5f;
        var o25 = 0f <= f28 ? 0.5f : -0.5f;
        var o14 = -f12 < 0f ? -0.5f : 0.5f;
        var o15 = f26 < 0f ? -0.5f : 0.5f;
        var o17 = 0f <= -f27 ? 0.5f : -0.5f;
        for (var y = y0; y < (int)y1; y++)
        {
            if (!((int)x0 < (int)x1))
                continue;
            var fy = (float)y + 0.5f;
            for (var x = (int)x0; x < (int)x1; x++)
            {
                var fx = (float)x + 0.5f;
                if (0f <= (fy * f21 - fx * f18) + (((o9 * f21 - o13 * f18) + 0f) - (cy * f21 - cx * f18))
                    && 0f <= (fy * f28 - fx * f12) + (((o25 * f28 - o14 * f12) + 0f) - (ay * f28 - ax * f12))
                    && 0f <= (fy * f26 - fx * f27) + (((f26 * o15 - f27 * o17) + 0f) - (by * f26 - bx * f27)))
                {
                    var k = 0;
                    for (var oy = 0; oy < 8; oy += gy)
                        for (var ox = 0; ox < 8; ox += gx)
                        {
                            var at = ((oy + y) >> 3) * bw + ((ox + x) >> 3);
                            masks[k++][at] |= 1ul << (((oy + y) & 7) * 8 + ((ox + x) & 7));
                        }
                }
            }
        }
    }

    // 180224770: each offset mask grown by the gutter (x by gx, y by gy luxels,
    // clamped at the mask's edges), from the mask's own offset on.
    private static void Dilate(int gutterX, int gutterY, ulong[][] src, ulong[][] dst, int stepX, int stepY, int bw, int bh)
    {
        var k = 0;
        for (var oy = 0; oy < 8; oy += stepY)
            for (var ox = 0; ox < 8; ox += stepX, k++)
            {
                var s = src[k];
                var d = dst[k];
                for (var y = oy; y < bh * 8; y++)
                {
                    var below = bh * 8 - 1 - y;
                    var dyLo = -(y < gutterY ? y : gutterY);
                    var dyHi = below < gutterY ? below : gutterY;
                    for (var x = ox; x < bw * 8; x++)
                    {
                        var right = bw * 8 - 1 - x;
                        var dxLo = x < gutterX ? x : gutterX;
                        var dxHi = right < gutterX ? right : gutterX;
                        var word = (x >> 3) + (y >> 3) * bw;
                        var bit = (x & 7) + (y & 7) * 8;
                        if ((d[word] >> bit & 1) != 0 || !(dyLo <= dyHi))
                            continue;
                        for (var dy = dyLo; dy <= dyHi; dy++)
                        {
                            var yy = y + dy;
                            var found = false;
                            if (-dxLo <= dxHi)
                                for (var xx = x - dxLo; xx - x <= dxHi; xx++)
                                    if ((s[(xx >> 3) + (yy >> 3) * bw] >> ((xx & 7) + (yy & 7) * 8) & 1) != 0)
                                    {
                                        found = true;
                                        break;
                                    }
                            if (found)
                            {
                                d[word] |= 1ul << bit;
                                break;
                            }
                        }
                    }
                }
            }
    }

    // 180225a60: the first row, then column, then offset mask where the chart's
    // mask meets nothing in the atlas; a rectangle resumes from where the last
    // chart of its padded size went.
    private static bool Place(Chart c, int width, int height, ulong[] atlas, int bw, int bh, ulong[][] masks, int gx, int gy,
                              int[] positions, Dictionary<(int, int), (int Col, int Row)>? cache)
    {
        var w8 = width >> 3;
        var rows = (height >> 3) - bh;
        var cols = w8 - bw;
        var (col0, row0) = (0, 0);
        if (cache is not null && cache.TryGetValue((c.PaddedWidth, c.PaddedHeight), out var at))
            (col0, row0) = at;
        for (var r = row0; r < rows; r++)
        {
            for (var col = r == row0 ? col0 : 0; col < cols; col++)
                for (var k = 0; k < masks.Length; k++)
                {
                    var mask = masks[k];
                    var fits = true;
                    for (var i = 0; fits && i < bh; i++)
                        for (var j = 0; j < bw; j++)
                            if ((atlas[(r + i) * w8 + col + j] & mask[i * bw + j]) != 0)
                            {
                                fits = false;
                                break;
                            }
                    if (!fits)
                        continue;
                    for (var i = 0; i < bh; i++)
                        for (var j = 0; j < bw; j++)
                            atlas[(r + i) * w8 + col + j] |= mask[i * bw + j];
                    positions[c.Index * 2] = ((k * gx) & 7) + col * 8;
                    positions[c.Index * 2 + 1] = ((k * gx) >> 3) * gy + r * 8;
                    if (cache is not null)
                        cache[(c.PaddedWidth, c.PaddedHeight)] = (col, r);
                    return true;
                }
        }
        return false;
    }

    // 180226100: single luxels from the bottom block row up, each in the first
    // block with a free bit: its quadrant rows 4-7 x cols 4-7, then rows 4-7
    // x cols 0-3, rows 0-3 x cols 4-7, rows 0-3 x cols 0-3, lowest bit first.
    private static bool PlacePixels(List<Chart> order, int from, int to, int width, int height, ulong[] atlas, int[] positions)
    {
        var group = Group(order, from, to, 0x10);
        if (group.Count == 0)
            return true;
        var w8 = width >> 3;
        int row = -1, col = -1;
        foreach (var c in group)
        {
            if (row < 0)
                row = (height >> 3) - 1;
            if (col < 0)
                col = 0;
            var placed = false;
            while (!placed)
            {
                if (row < 0)
                    return false;
                for (; col < w8; col++)
                {
                    var block = atlas[row * w8 + col];
                    if (block == ulong.MaxValue)
                        continue;
                    var free = ~block;
                    var m1 = (free & 0xf0f0f0f000000000ul) != 0 ? 0xf0f0f0f000000000ul : 0x0f0f0f0f00000000ul;
                    var m2 = (free & m1) != 0 ? m1 : 0xf0f0f0f0ul;
                    var sel = m2 & free;
                    if (sel == 0)
                        sel = free & 0x0f0f0f0ful;
                    var bit = sel == 0 ? -1 : BitOperations.TrailingZeroCount(sel);
                    atlas[row * w8 + col] = block | 1ul << (bit & 0x3f);
                    positions[c.Index * 2] = (bit & 7) + col * 8;
                    positions[c.Index * 2 + 1] = ((bit >> 3) & 7) + row * 8;
                    placed = true;
                    break;
                }
                if (!placed)
                {
                    row--;
                    col = 0;
                }
            }
        }
        return true;
    }

    // 180225350: runs of lines of one 4-aligned width, each run from the bottom
    // block row up through 180224a50.
    private static bool PlaceLines(List<Chart> order, int from, int to, int width, int height, ulong[] atlas, int[] positions)
    {
        var i = from;
        while (i != to)
        {
            var j = i + 1;
            while (j < to && ((order[j].PaddedWidth + 3) & ~3) == ((order[i].PaddedWidth + 3) & ~3))
                j++;
            var cursor = (Col: -1, Row: -1);
            foreach (var c in Group(order, i, j, 4))
                if (!PlaceLine(c, width, height, atlas, positions, ref cursor))
                    return false;
            i = j;
        }
        return true;
    }

    // 180224a50: a one-luxel-high chart into the first block column with a free
    // byte row for its width, at offset 0, else at offset 4.
    private static bool PlaceLine(Chart c, int width, int height, ulong[] atlas, int[] positions, ref (int Col, int Row) cursor)
    {
        var pw = c.PaddedWidth;
        var w8 = width >> 3;
        var nb = (pw + 7) >> 3;
        int wide4;
        int wide0;
        if (pw == -4)
        {
            wide4 = 8;
            wide0 = (pw + 7) & ~7;
        }
        else
        {
            wide4 = (pw + 0xb) & ~7;
            wide0 = pw == 0 ? 8 : (pw + 7) & ~7;
        }
        var lastCol = w8 - nb - 1;
        var nb4 = nb;
        if (wide4 <= wide0)
        {
            lastCol = w8 - nb;
            nb4 = nb - 1;
        }
        if (cursor.Row < 0)
            cursor.Row = (height >> 3) - 1;
        if (cursor.Col < 0)
            cursor.Col = 0;
        var mask0 = new ulong[nb + 1];
        var mask4 = new ulong[nb + 1];
        for (var i = 0; i < pw; i++)
        {
            mask0[i >> 3] |= 0x0101010101010101ul << (i & 7);
            mask4[(i + 4) >> 3] |= 0x0101010101010101ul << ((i + 4) & 7);
        }
        for (var row = cursor.Row; row >= 0; row--)
        {
            var b = row * w8;
            for (var col = cursor.Col; col < lastCol; col++)
            {
                ulong occ0 = 0, occ4 = 0;
                var full = false;
                for (var j = 0; j < nb; j++)
                {
                    occ0 |= mask0[j] & atlas[b + col + j];
                    occ4 |= mask4[j] & atlas[b + col + j];
                    if ((occ4 | occ0) == ulong.MaxValue)
                    {
                        full = true;
                        break;
                    }
                }
                if (full)
                    continue;
                for (var k = 0; k < 8; k++)
                {
                    var m = 0xfful << (k * 8);
                    if ((occ0 & m) != 0)
                        continue;
                    positions[c.Index * 2] = col * 8;
                    positions[c.Index * 2 + 1] = k + row * 8;
                    for (var j = 0; j < nb; j++)
                        atlas[b + col + j] |= mask0[j] & m;
                    cursor = (col, row);
                    return true;
                }
                for (var k = 0; k < 8; k++)
                {
                    var m = 0xfful << (k * 8);
                    if (((occ4 | (mask4[nb4] & atlas[b + col + nb4])) & m) != 0)
                        continue;
                    positions[c.Index * 2] = col * 8 + 4;
                    positions[c.Index * 2 + 1] = k + row * 8;
                    for (var j = 0; j <= nb4; j++)
                        atlas[b + col + j] |= mask4[j] & m;
                    cursor = (col, row);
                    return true;
                }
            }
            cursor.Col = 0;
        }
        return false;
    }

    // 18126a270's channel: clamped, linear to sRGB (tier0's powf), times 255, rounded to even.
    private static uint Srgb(float f)
    {
        var x = 0f <= f ? (f <= 1f ? f : 1f) : 0f;
        x = 0.0031308f < x ? LightPow.Pow(x, BitConverter.Int32BitsToSingle(0x3ed55555)) * 1.055f - 0.055f : x * 12.92f;
        x *= 255f;
        if (x <= 0f) x = 0f;
        if (255f <= x) x = 255f;
        return (uint)(byte)(int)MathF.Round(x);
    }

    private readonly record struct Edge(uint A, uint B) : IEquatable<Edge>;

    // 180223460 / 180222aa0: the longest run of near collinear boundary edges
    // (|dot| at least 0.99) sets the angle; of -angle and pi - angle, the one
    // that puts the run's midpoint below the chart's centroid.
    private static float BestRotation(float[] uvs, Chart chart, uint[] indices)
    {
        var corners = new List<uint>(chart.Triangles.Count * 3);
        foreach (var t in chart.Triangles)
            for (var k = 0; k < 3; k++)
                corners.Add(indices[t * 3 + k]);
        var edges = BoundaryEdges(corners);
        if (edges.Count == 0)
            return 0f;
        var byVertex = new UtlHashtable<uint, List<int>>();
        for (var i = 0; i < edges.Count; i++)
            foreach (var v in new[] { edges[i].A, edges[i].B })
            {
                var slot = byVertex.FindOrInsert(v, Fmix(v), out var inserted);
                if (inserted)
                    byVertex.Value(slot) = [];
                byVertex.Value(slot).Add(i);
            }
        Vector2 Uv(uint v) => new(uvs[v * 2], uvs[v * 2 + 1]);
        var visited = new bool[edges.Count];
        var best = -1;
        float bestLength = 0f, bestX = 0f, bestY = 0f;
        for (var i = 0; i < edges.Count; i++)
        {
            if (visited[i])
                continue;
            visited[i] = true;
            var dx = Uv(edges[i].B).X - Uv(edges[i].A).X;
            var dy = Uv(edges[i].B).Y - Uv(edges[i].A).Y;
            var length = MathF.Sqrt(dy * dy + dx * dx);
            if (length != 0f)
            {
                dx *= 1f / length;
                dy *= 1f / length;
            }
            else
                (dx, dy) = (0f, 0f);
            if (!(0.0001f <= length))
                continue;
            var total = length;
            for (var end = 0; end < 2; end++)
            {
                var v = end == 0 ? edges[i].A : edges[i].B;
                while (true)
                {
                    var slot = byVertex.Find(v, Fmix(v));
                    var next = -1;
                    var nextLength = 0f;
                    if (slot >= 0)
                        foreach (var j in byVertex.Value(slot))
                        {
                            if (visited[j])
                                continue;
                            var ex = Uv(edges[j].B).X - Uv(edges[j].A).X;
                            var ey = Uv(edges[j].B).Y - Uv(edges[j].A).Y;
                            var l = MathF.Sqrt(ey * ey + ex * ex);
                            if (l != 0f)
                            {
                                ex *= 1f / l;
                                ey *= 1f / l;
                            }
                            else
                                (ex, ey) = (0f, 0f);
                            if (l != 0f && 0.99f <= MathF.Abs(dx * ex + dy * ey))
                            {
                                next = j;
                                nextLength = l;
                                break;
                            }
                        }
                    if (next < 0)
                        break;
                    visited[next] = true;
                    total += nextLength;
                    v = edges[next].A == v ? edges[next].B : edges[next].A;
                }
            }
            if (bestLength < total)
            {
                best = i;
                bestLength = total;
                bestX = dx;
                bestY = dy;
            }
        }
        if (best < 0)
            return 0f;
        float cx = 0f, cy = 0f;
        foreach (var v in corners)
        {
            cx += Uv(v).X;
            cy += Uv(v).Y;
        }
        cx /= corners.Count;
        cy /= corners.Count;
        var mx = (Uv(edges[best].B).X + Uv(edges[best].A).X) * 0.5f;
        var my = (Uv(edges[best].B).Y + Uv(edges[best].A).Y) * 0.5f;
        var theta = LightCrt.Atan2(bestY, bestX);
        var a = -theta;
        var b = 3.1415927f - theta;
        float sa = LightCrt.Sin(a), ca = LightCrt.Cos(a), sb = LightCrt.Sin(b), cb = LightCrt.Cos(b);
        return (ca * my + mx * sa) - (ca * cy + cx * sa) < (cb * my + mx * sb) - (cb * cy + cx * sb) ? a : b;
    }

    // 180222600: each triangle's edges counted under their sorted vertex pair;
    // the ones seen once, in slot order, as first seen.
    private static List<Edge> BoundaryEdges(List<uint> corners)
    {
        var table = new UtlHashtable<Edge, (Edge First, int Count)>();
        table.Reserve(corners.Count * 4 / 3);
        for (var t = 0; t + 2 < corners.Count; t += 3)
            for (var k = 0; k < 3; k++)
            {
                var (a, b) = (corners[t + k], corners[t + (k + 1) % 3]);
                var key = new Edge(Math.Min(a, b), Math.Max(a, b));
                var h = (key.A + 0xebe18056u) ^ 0x3501a674u;
                var hash = (key.B + h * 0x40u + (h >> 2) + 0x9e3779b9u) ^ h;
                var slot = table.FindOrInsert(key, hash, out var inserted);
                ref var value = ref table.Value(slot);
                value = inserted ? (new Edge(a, b), 1) : (value.First, value.Count + 1);
            }
        var edges = new List<Edge>();
        foreach (var slot in table.Slots())
            if (table.Value(slot).Count == 1)
                edges.Add(table.Value(slot).First);
        return edges;
    }

    private static uint Fmix(uint x)
    {
        var h = (x >> 16 ^ x) * 0x85ebca6bu;
        h = (h >> 13 ^ h) * 0xc2b2ae35u;
        return h >> 16 ^ h;
    }
}
