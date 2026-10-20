namespace Source2.Compiler.Lighting;

/// <summary>
/// vrad3's far field atlas filters (vrad3_farfieldprobevolume.cpp), each run brick by brick:
/// <list type="bullet">
/// <item>farfield_median_filter [passes] (1800231e0, 180023510): per texel not solid, the luminance
/// median of itself and its six neighbours in the brick that are not solid;</item>
/// <item>farfield_dilate [passes] (180022330, 180022b90): solid texels take the mean of their valid
/// 26 neighbours, across bricks through the cell descriptors; Valve's sums are not reset between
/// texels (see <see cref="Dilate"/>);</item>
/// <item>farfield_filter_fireflies [multiplier] (180023b00, 180023dc0): a texel brighter than
/// multiplier times its neighbours' mean luminance is scaled down to it;</item>
/// <item>farfield_resample_borders (180024230, 180024400): each tile's 33rd row, column and slice,
/// trilinearly from the brick the position falls in (180021670).</item>
/// </list>
/// The solid mask is the voxelize pass's: 32^3 bits a brick, x fastest.
/// </summary>
public static class FarfieldFilters
{
    private const int Luxels = 32 * 32 * 32;

    private static bool Bit(uint[] bits, int i) => (bits[i >> 5] >> (i & 31) & 1) != 0;

    private static float Luminance(ReadOnlySpan<float> v) => v[0] * 0.2126f + v[4] * 0.7152f + v[8] * 0.0722f;

    // DAT_1802a07b0: self, -x, +x, -y, +y, -z, +z.
    private static readonly (int X, int Y, int Z)[] MedianTaps = [(0, 0, 0), (-1, 0, 0), (1, 0, 0), (0, -1, 0), (0, 1, 0), (0, 0, -1), (0, 0, 1)];

    /// <summary>
    /// 180023510 per brick: the brick's luxels and luminances (0.2126, 0.7152, 0.0722 of dc) copied
    /// first; for each texel not solid, the taps in the brick (not solid ones when the mask exists)
    /// insertion-sorted by luminance (a tie after the earlier); an odd count takes the middle one
    /// (written unless it is the texel itself), an even count (hi - lo) * 0.5 + lo of the two middle.
    /// Returns the texels visited (the log's "changed").
    /// </summary>
    public static long Median(FarfieldAtlas atlas, uint[]? solid, int passes = 1)
    {
        long visited = 0;
        var useMask = solid != null && (long)atlas.Bricks * Luxels <= (long)solid.Length * 32;
        var luxels = new float[Luxels * 12];
        var lum = new float[Luxels];
        var sorted = new (float L, int I)[7];
        var v = new float[12];
        for (var pass = 0; pass < passes; pass++)
            for (var b = 0; b < atlas.Bricks; b++)
            {
                for (var z = 0; z < 32; z++)
                    for (var y = 0; y < 32; y++)
                        for (var x = 0; x < 32; x++)
                        {
                            var i = (z * 32 + y) * 32 + x;
                            atlas.Read(b, x, y, z, luxels.AsSpan(i * 12, 12));
                            lum[i] = Luminance(luxels.AsSpan(i * 12, 12));
                        }
                for (var z = 0; z < 32; z++)
                    for (var y = 0; y < 32; y++)
                        for (var x = 0; x < 32; x++)
                        {
                            var self = (z * 32 + y) * 32 + x;
                            if (useMask && Bit(solid!, b * Luxels + self))
                                continue;
                            var n = 0;
                            foreach (var (dx, dy, dz) in MedianTaps)
                            {
                                int nx = x + dx, ny = y + dy, nz = z + dz;
                                if (nx < 0 || ny < 0 || nz < 0 || nx >= 32 || ny >= 32 || nz >= 32)
                                    continue;
                                var j = (nz * 32 + ny) * 32 + nx;
                                if (useMask && Bit(solid!, b * Luxels + j))
                                    continue;
                                var l = lum[j];
                                var at = n++;
                                while (at > 0 && !(sorted[at - 1].L <= l))
                                {
                                    sorted[at] = sorted[at - 1];
                                    at--;
                                }
                                sorted[at] = (l, j);
                            }
                            if ((n & 1) == 0)
                            {
                                int hi = sorted[n / 2].I, lo = sorted[n / 2 - 1].I;
                                for (var k = 0; k < 12; k++)
                                    v[k] = (luxels[hi * 12 + k] - luxels[lo * 12 + k]) * 0.5f + luxels[lo * 12 + k];
                                atlas.Write(b, x, y, z, v);
                            }
                            else if (sorted[n / 2].I != self)
                                atlas.Write(b, x, y, z, luxels.AsSpan(sorted[n / 2].I * 12, 12));
                            visited++;
                        }
            }
        return visited;
    }

    /// <summary>
    /// 180021480: a position in cells to the brick over it and the position in that brick's 32^3
    /// texels: the cell floor-clamped to the grid, its descriptor; false on the root (level 7).
    /// </summary>
    public static bool Locate(FarfieldBricks.Grid grid, uint[] descriptors, float px, float py, float pz, out int brick, out float lx, out float ly, out float lz)
    {
        var cz = Math.Min(Math.Max((int)MathF.Floor(pz), 0), grid.Z - 1);
        var cy = Math.Min(Math.Max((int)MathF.Floor(py), 0), grid.Y - 1);
        var cx = Math.Min(Math.Max((int)MathF.Floor(px), 0), grid.X - 1);
        var d = descriptors[grid.Index(cx, cy, cz)];
        var level = (int)(d >> 12 & 7);
        if (level == 7)
        {
            (brick, lx, ly, lz) = (0, 0f, 0f, 0f);
            return false;
        }
        brick = (int)(d & 0xfff);
        var s = 32f / (float)(1u << level);
        lx = (px - (float)cx + (float)(d >> 15 & 0x1f)) * s;
        ly = (py - (float)cy + (float)(d >> 20 & 0x1f)) * s;
        lz = (pz - (float)cz + (float)(d >> 25 & 0x1f)) * s;
        return true;
    }

    /// <summary>
    /// farfield_dilate: valid = not solid; each pass copies the valid bits, then every brick's solid
    /// texels (z, y, x order) with valid neighbours among the 27 (z, y, x order; outside the brick,
    /// unless it is the root, the texel nearest the position in the brick the descriptors give, round
    /// to nearest even and clamped to 0..31) take the sum times 1 / count, and become valid. Stops
    /// after <paramref name="passes"/> or a pass that fills nothing.
    /// <para>Valve's 12 sums are loaded at each texel from the stack slots the last written mean
    /// lives in and never cleared (180022cf6), so every fill adds the previous fill's result, and a
    /// brick's first fill starts from whatever the worker thread's stack held (Valve's own runs differ
    /// there). The thread pool hands out chunks of bricks / (2 (threads + 1)) bricks; on s2c_l5farfield128
    /// every texel Valve's two runs agree on but we do not lies in a chunk's first brick or the ones
    /// after it. Modelled as one thread over the bricks in order, the carry starting at 0 and kept
    /// across bricks and passes (closer to Valve than a reset per brick or per chunk).</para>
    /// </summary>
    public static long Dilate(FarfieldAtlas atlas, uint[] solid, FarfieldBricks.Grid grid, IReadOnlyList<FarfieldBricks.Brick> bricks, uint[] descriptors,
                              int passes = 2, Action<string>? log = null)
    {
        var total = atlas.Bricks * Luxels;
        if ((long)solid.Length * 32 < total)
            return 0;
        var valid = new uint[total >> 5];
        for (var i = 0; i < valid.Length; i++)
            valid[i] = ~solid[i];
        var carry = new float[12];
        var t = new float[12];
        long all = 0;
        for (var pass = 0; pass < passes; pass++)
        {
            var prev = (uint[])valid.Clone();
            long filled = 0;
            for (var b = 0; b < atlas.Bricks; b++)
            {
                var brick = bricks[b];
                var scale = brick.Level != 7 ? (float)(1u << brick.Level) * 0.03125f : 0f;
                for (var z = 0; z < 32; z++)
                    for (var y = 0; y < 32; y++)
                        for (var x = 0; x < 32; x++)
                        {
                            var me = b * Luxels + (z * 32 + y) * 32 + x;
                            if (Bit(prev, me))
                                continue;
                            var sum = (float[])carry.Clone();
                            var n = 0;
                            for (var dz = -1; dz <= 1; dz++)
                                for (var dy = -1; dy <= 1; dy++)
                                    for (var dx = -1; dx <= 1; dx++)
                                    {
                                        int nx = x + dx, ny = y + dy, nz = z + dz;
                                        int rb, rx, ry, rz;
                                        if (nx < 0 || ny < 0 || nz < 0 || nx > 31 || ny > 31 || nz > 31)
                                        {
                                            if (brick.Level == 7)
                                                continue;
                                            var px = (float)nx * scale + (float)brick.X;
                                            var py = (float)ny * scale + (float)brick.Y;
                                            var pz = (float)nz * scale + (float)brick.Z;
                                            if (!Locate(grid, descriptors, px, py, pz, out rb, out var lx, out var ly, out var lz))
                                                continue;
                                            rz = Math.Clamp((int)MathF.Round(lz, MidpointRounding.ToEven), 0, 31);
                                            ry = Math.Clamp((int)MathF.Round(ly, MidpointRounding.ToEven), 0, 31);
                                            rx = Math.Clamp((int)MathF.Round(lx, MidpointRounding.ToEven), 0, 31);
                                            var j = ((rb * 32 + rz) * 32 + ry) * 32 + rx;
                                            if (j >= total || !Bit(prev, j))
                                                continue;
                                        }
                                        else
                                        {
                                            (rb, rx, ry, rz) = (b, nx, ny, nz);
                                            if (!Bit(prev, b * Luxels + (nz * 32 + ny) * 32 + nx))
                                                continue;
                                        }
                                        atlas.Read(rb, rx, ry, rz, t);
                                        for (var k = 0; k < 12; k++)
                                            sum[k] += t[k];
                                        n++;
                                    }
                            if (n <= 0)
                                continue;
                            var r = 1f / (float)n;
                            for (var k = 0; k < 12; k++)
                                carry[k] = sum[k] * r;
                            atlas.Write(b, x, y, z, carry);
                            valid[me >> 5] |= 1u << (me & 31);
                            filled++;
                        }
            }
            all += filled;
            log?.Invoke($"  farfield dilate pass {pass}: filled {filled} samples");
            if (filled == 0)
                break;
        }
        var still = 0L;
        for (var i = 0; i < total; i++)
            still += Bit(valid, i) ? 0 : 1;
        if (still != 0)
            log?.Invoke($"  farfield dilate: {still} samples still solid");
        return all;
    }

    /// <summary>
    /// 180023dc0 per brick: the dc luminances (g 0.7152 + r 0.2126 + b 0.0722) copied first; a texel
    /// with luminance l &gt; 0 and its in-brick 26 neighbours' mean m (dz, dy, dx order) times
    /// <paramref name="multiplier"/> in (0, l) has all 12 values scaled by m / l.
    /// </summary>
    public static long Fireflies(FarfieldAtlas atlas, float multiplier = 4f, Action<string>? log = null)
    {
        long clamped = 0;
        var lum = new float[Luxels];
        for (var b = 0; b < atlas.Bricks; b++)
        {
            for (var z = 0; z < 32; z++)
            {
                var dc = atlas.Planes[0][z];
                for (var y = 0; y < 32; y++)
                    for (var x = 0; x < 32; x++)
                    {
                        var o = atlas.At(b, x, y);
                        lum[(z * 32 + y) * 32 + x] = dc[o + 1] * 0.7152f + dc[o] * 0.2126f + dc[o + 2] * 0.0722f;
                    }
            }
            for (var z = 0; z < 32; z++)
                for (var y = 0; y < 32; y++)
                    for (var x = 0; x < 32; x++)
                    {
                        var l = lum[(z * 32 + y) * 32 + x];
                        if (!(0f < l))
                            continue;
                        var sum = 0f;
                        var n = 0;
                        for (var dz = -1; dz <= 1; dz++)
                            for (var dy = -1; dy <= 1; dy++)
                                for (var dx = -1; dx <= 1; dx++)
                                {
                                    if (dx == 0 && dy == 0 && dz == 0)
                                        continue;
                                    int nx = x + dx, ny = y + dy, nz = z + dz;
                                    if (nx < 0 || ny < 0 || nz < 0 || nx >= 32 || ny >= 32 || nz >= 32)
                                        continue;
                                    sum += lum[(nz * 32 + ny) * 32 + nx];
                                    n++;
                                }
                        if (n <= 0)
                            continue;
                        var m = sum / (float)n * multiplier;
                        if (!(0f < m) || !(m < l))
                            continue;
                        var o = atlas.At(b, x, y);
                        for (var i = 0; i < 4; i++)
                        {
                            var p = atlas.Planes[i][z];
                            for (var c = 0; c < 3; c++)
                                p[o + c] = m / l * p[o + c];
                        }
                        clamped++;
                    }
        }
        log?.Invoke($"  farfield fireflies: clamped {clamped} of {(long)atlas.Bricks * Luxels} samples at {multiplier:F1}x");
        return clamped;
    }

    /// <summary>
    /// 180024400 per brick: every texel of the 33^3 tile with a coordinate of 32 gets the trilinear
    /// sample (180021670) of the brick its position falls in (the root, or a position over the root,
    /// samples its own brick at the texel's own coordinates).
    /// </summary>
    public static void ResampleBorders(FarfieldAtlas atlas, FarfieldBricks.Grid grid, IReadOnlyList<FarfieldBricks.Brick> bricks, uint[] descriptors)
    {
        var v = new float[12];
        for (var b = 0; b < atlas.Bricks; b++)
        {
            var brick = bricks[b];
            var scale = brick.Level != 7 ? (float)(1u << brick.Level) * 0.03125f : 0f;
            for (var z = 0; z < 33; z++)
                for (var y = 0; y < 33; y++)
                    for (var x = 0; x < 33; x++)
                    {
                        if (x < 32 && y < 32 && z < 32)
                            continue;
                        float fx = x, fy = y, fz = z;
                        var target = b;
                        if (brick.Level != 7)
                        {
                            var px = fx * scale + (float)brick.X;
                            var py = fy * scale + (float)brick.Y;
                            var pz = fz * scale + (float)brick.Z;
                            if (Locate(grid, descriptors, px, py, pz, out var rb, out var lx, out var ly, out var lz))
                                (target, fx, fy, fz) = (rb, lx, ly, lz);
                        }
                        Sample(atlas, target, fx, fy, fz, v);
                        atlas.Write(b, x, y, z, v);
                    }
        }
    }

    // 180021670: corners floor and floor + 1 clamped to 0..31, the fractions clamped to [0, 1];
    // all zero reads the texel, else (b - a) t + a along x, then y, then z.
    private static void Sample(FarfieldAtlas atlas, int b, float px, float py, float pz, float[] r)
    {
        static (int A, int B, float T) Axis(float p)
        {
            var f = (int)MathF.Floor(p);
            var a = Math.Min(Math.Max(f, 0), 31);
            var c = Math.Min(Math.Max(f + 1, 0), 31);
            var t = p - (float)f;
            t = t <= 0f ? 0f : t;
            t = 1f <= t ? 1f : t;
            return (a, c, t);
        }
        var (x0, x1, tx) = Axis(px);
        var (y0, y1, ty) = Axis(py);
        var (z0, z1, tz) = Axis(pz);
        if (tx == 0f && ty == 0f && tz == 0f)
        {
            atlas.Read(b, x0, y0, z0, r);
            return;
        }
        var c = new float[8][];
        for (var k = 0; k < 8; k++)
        {
            c[k] = new float[12];
            atlas.Read(b, (k & 1) != 0 ? x1 : x0, (k & 2) != 0 ? y1 : y0, (k & 4) != 0 ? z1 : z0, c[k]);
        }
        var xl = new float[4][];
        for (var k = 0; k < 4; k++)
        {
            xl[k] = new float[12];
            for (var i = 0; i < 12; i++)
                xl[k][i] = (c[2 * k + 1][i] - c[2 * k][i]) * tx + c[2 * k][i];
        }
        var yl = new float[2][];
        for (var k = 0; k < 2; k++)
        {
            yl[k] = new float[12];
            for (var i = 0; i < 12; i++)
                yl[k][i] = (xl[2 * k + 1][i] - xl[2 * k][i]) * ty + xl[2 * k][i];
        }
        for (var i = 0; i < 12; i++)
            r[i] = (yl[1][i] - yl[0][i]) * tz + yl[0][i];
    }
}
