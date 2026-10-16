namespace Source2.Compiler.Lighting;

/// <summary>
/// lightmap_info.dat: each luxel's chart, from the packed geometry.
/// <list type="bullet">
/// <item><see cref="ChartIds"/>: NLightBaker::RasterizeAndSetupTexels (0923
/// 1801f32f0) walks the triangles in order with their atlas uvs times the
/// packing size (the atlas over 2^(mip - 1)). A triangle of a chart small in
/// both axes (flags bits 28 and 29) fills one packing luxel at its first
/// corner; any other is rasterised conservatively (1801f5020): a luxel is
/// in when its centre passes the three edge functions, each pushed half a
/// luxel outwards along both axes. A packing luxel covers 2^(mip - 1) square
/// atlas luxels, and each atlas luxel keeps the first chart to reach it
/// (0xFFFFFFFF for none). Every chart must reach one.</item>
/// <item><see cref="Write"/>: NLightBaker::RunVrad3 (1801f2240) writes the
/// ids block by block, row-major over the blocks and within each, a block
/// 1024 luxels (2048 with vrad3LargeBlockSize or off a customer machine)
/// but no larger than the atlas's smaller side, clipped at its edges.</item>
/// </list>
/// </summary>
internal static class LightmapTexels
{
    public static uint[] ChartIds(PackingGeometry g, int width, int height, int mip)
    {
        var ids = new uint[width * height];
        Array.Fill(ids, uint.MaxValue);
        var shift = mip - 1;
        var scale = 1 << shift;
        var sx = (float)(uint)(width >> shift);
        var sy = (float)(uint)(height >> shift);
        void Set(int x, int y, uint chart)
        {
            if ((uint)x >= (uint)width || (uint)y >= (uint)height)
                throw new InvalidDataException($"lightmap chart {chart} reaches luxel ({x}, {y}) outside the {width}x{height} atlas");
            ref var id = ref ids[y * width + x];
            if (id == uint.MaxValue)
                id = chart;
        }
        void Fill(int px, int py, uint chart)
        {
            for (var j = 0; j < scale; j++)
                for (var i = 0; i < scale; i++)
                    Set(scale * px + i, py * scale + j, chart);
        }
        for (var t = 0; t + 2 < g.Indices.Length; t += 3)
        {
            var a = (int)g.Indices[t];
            var b = (int)g.Indices[t + 1];
            var c = (int)g.Indices[t + 2];
            var record = g.Charts[g.TriangleCharts[t / 3]];
            float x0 = sx * g.AtlasUv[a * 2], y0 = sy * g.AtlasUv[a * 2 + 1];
            float x1 = sx * g.AtlasUv[b * 2], y1 = sy * g.AtlasUv[b * 2 + 1];
            float x2 = sx * g.AtlasUv[c * 2], y2 = sy * g.AtlasUv[c * 2 + 1];
            if ((record.Flags & 0x30000000u) == 0x30000000u)
                Fill((int)MathF.Floor(x0), (int)MathF.Floor(y0), record.Index);
            else
                Rasterize(x0, y0, x1, y1, x2, y2, (x, y) => Fill(x, y, record.Index));
        }
        var reached = new bool[g.Charts.Length];
        foreach (var id in ids)
            if (id != uint.MaxValue)
                reached[id] = true;
        var missing = Enumerable.Range(0, reached.Length).Where(i => !reached[i]).ToList();
        if (missing.Count > 0)
            throw new InvalidDataException($"charts have no luxels assigned: {string.Join(", ", missing.Take(10))}");
        return ids;
    }

    // 1801f5020, in its own operation order.
    private static void Rasterize(float ax, float ay, float bx, float by, float cx, float cy, Action<int, int> hit)
    {
        // Counter-clockwise: swap the second and third corners otherwise.
        if ((cx - ax) * (by - ay) - (bx - ax) * (cy - ay) < 0f)
            (bx, by, cx, cy) = (cx, cy, bx, by);
        var minX = (int)MathF.Floor(MathF.Min(MathF.Min(ax, bx), cx));
        var minY = (int)MathF.Floor(MathF.Min(MathF.Min(ay, by), cy));
        var maxX = (int)MathF.Ceiling(MathF.Max(MathF.Max(ax, bx), cx));
        var maxY = (int)MathF.Ceiling(MathF.Max(MathF.Max(ay, by), cy));
        float e1x = bx - cx, e1y = by - cy;
        float e2x = cx - ax, e2y = cy - ay;
        float e3x = ax - bx, e3y = ay - by;
        static float Half(bool negative) => negative ? -0.5f : 0.5f;
        float s1x = Half(e1x < 0f), s1y = Half(-e1y < 0f);
        float s2x = Half(e2x < 0f), s2y = Half(-e2y < 0f);
        float s3x = Half(e3x < 0f), s3y = Half(!(0f <= -e3y));
        var k1 = ((e1x * s1x - e1y * s1y) + 0f) - (e1x * cy - e1y * cx);
        var k2 = ((e2x * s2x - e2y * s2y) + 0f) - (e2x * ay - e2y * ax);
        var k3 = ((e3x * s3x - e3y * s3y) + 0f) - (e3x * by - e3y * bx);
        for (var y = minY; y < maxY; y++)
        {
            var py = (float)y + 0.5f;
            for (var x = minX; x < maxX; x++)
            {
                var px = (float)x + 0.5f;
                if (0f <= (py * e1x - px * e1y) + k1 && 0f <= (py * e2x - px * e2y) + k2 && 0f <= (py * e3x - px * e3y) + k3)
                    hit(x, y);
            }
        }
    }

    /// <summary>The ids in RunVrad3's block order.</summary>
    public static byte[] Write(uint[] ids, int width, int height, int blockSize = 0x400)
    {
        blockSize = Math.Min(blockSize, Math.Min(width, height));
        var bx = (width - 1 + blockSize) / blockSize;
        var by = (height - 1 + blockSize) / blockSize;
        var bytes = new byte[ids.Length * 4];
        var at = 0;
        for (var b = 0; b < bx * by; b++)
        {
            var x0 = b % bx * blockSize;
            var y0 = b / bx * blockSize;
            var x1 = Math.Min(x0 + blockSize, width);
            var y1 = Math.Min(y0 + blockSize, height);
            for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++, at += 4)
                    BitConverter.TryWriteBytes(bytes.AsSpan(at), ids[y * width + x]);
        }
        return bytes;
    }
}
