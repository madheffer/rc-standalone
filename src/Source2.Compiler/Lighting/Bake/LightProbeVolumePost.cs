namespace Source2.Compiler.Lighting.Bake;

/// <summary>
/// The CPU half of vrad3's compute_lpv routine for the ambient cube type,
/// on the image <see cref="LightProbeVolumeBaker"/> reads back: (X Z) by
/// (Y 7) RGBA texels, cell (x, y, z) face f at column z X + x, row f Y + y.
/// "Solid" is the bordered grid's mask after the flood fill, looked up a cell
/// in from the border. Faces are +X, +Y, +Z, -X, -Y, -Z. Each command keeps
/// vrad3_lightprobevolume.cpp's float order:
/// <list type="bullet">
/// <item><see cref="AmbientOcclusion"/>: lpv_image_ambient_occlusion
/// (180082fc0, per face 180083580).</item>
/// <item><see cref="RemoveFireflies"/>: lpv_image_remove_fireflies
/// (180084c50, 1800851b0).</item>
/// <item><see cref="Dilate"/> and <see cref="WriteOpenBand"/>: the cube
/// type's part of lpv_image_resolve (18007f720 / 18007fa30, 1800812a0).</item>
/// <item><see cref="MedianFilter"/>: lpv_image_median_filter (180085b60,
/// 1800860b0).</item>
/// </list>
/// </summary>
public static class LightProbeVolumePost
{
    private static readonly (int X, int Y, int Z)[] Faces = [(1, 0, 0), (0, 1, 0), (0, 0, 1), (-1, 0, 0), (0, -1, 0), (0, 0, -1)];

    /// <summary>The six neighbours in vrad3's table order (18028d610).</summary>
    private static readonly (int X, int Y, int Z)[] Neighbours = Faces;

    /// <summary>The grid an image belongs to.</summary>
    public sealed class Grid(int x, int y, int z, uint[] borderedSolidMask)
    {
        public int X { get; } = x;
        public int Y { get; } = y;
        public int Z { get; } = z;
        public int Width => X * Z;

        public bool Inside(int x, int y, int z) => x >= 0 && y >= 0 && z >= 0 && x < X && y < Y && z < Z;

        public bool Solid(int x, int y, int z) => borderedSolidMask[((z + 1) * (Y + 2) + y + 1) * (X + 2) + x + 1] != 0;

        public int Texel(int face, int x, int y, int z) => ((face * Y + y) * Width + z * X + x) * 4;
    }

    /// <summary>
    /// Per open cell and face, a factor from the 26 neighbour directions
    /// d = normalize(dx, dy, dz): w = ((n.d 0.5 + 0.5) - max(n.d, 0)) 0.33 +
    /// max(n.d, 0); two steps along d from the cell, w halving each step, add w
    /// to the total and, from the first solid cell on, to the occluded sum. The
    /// factor 1 - weight (occluded / total)^2 (weights 0.7, 0.7, 0.85, 0.7,
    /// 0.7, 0.9 by face) scales the face's four channels when it is under 1.
    /// </summary>
    public static void AmbientOcclusion(float[] image, Grid g)
    {
        var dirs = Directions();
        for (var z = 0; z < g.Z; z++)
            for (var y = 0; y < g.Y; y++)
                for (var x = 0; x < g.X; x++)
                {
                    if (g.Solid(x, y, z))
                        continue;
                    for (var face = 0; face < 6; face++)
                    {
                        var f = OcclusionFactor(g, dirs, face, x, y, z);
                        if (!(f < 1f))
                            continue;
                        var at = g.Texel(face, x, y, z);
                        image[at] = f * image[at];
                        image[at + 1] = image[at + 1] * f;
                        image[at + 2] = image[at + 2] * f;
                        image[at + 3] = image[at + 3] * f;
                    }
                }
    }

    private static readonly float[] FaceWeights = [0.7f, 0.7f, 0.85f, 0.7f, 0.7f, 0.9f];

    // 180082fc0's table: normalize((dx, dy, dz)), dx innermost, as its loop computes it.
    private static (float X, float Y, float Z)[] Directions()
    {
        var d = new (float, float, float)[27];
        for (var a = -1; a <= 1; a++)
            for (var b = -1; b <= 1; b++)
            {
                float fa = a, fb = b;
                var s = fb * fb + fa * fa;
                var len1 = MathF.Sqrt(s + 1f);
                var len0 = MathF.Sqrt(s + 0f);
                var i1 = 1f / len1;
                d[(a + 1) * 9 + (b + 1) * 3] = (i1 * -1f, fb * i1, fa * i1);
                if (len0 < 1e-17f)
                    d[(a + 1) * 9 + (b + 1) * 3 + 1] = (0f, 0f, 0f);
                else
                {
                    var i0 = 1f / len0;
                    d[(a + 1) * 9 + (b + 1) * 3 + 1] = (i0 * 0f, fb * i0, fa * i0);
                }
                d[(a + 1) * 9 + (b + 1) * 3 + 2] = (i1, fb * i1, fa * i1);
            }
        return d;
    }

    private static float OcclusionFactor(Grid g, (float X, float Y, float Z)[] dirs, int face, int x, int y, int z)
    {
        var n = Faces[face];
        float occluded = 0f, total = 0f;
        for (var dz = -1; dz <= 1; dz++)
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dz == 0 && dy == 0 && dx == 0)
                        continue;
                    var dir = dirs[(dz + 1) * 9 + (dy + 1) * 3 + dx + 1];
                    var d = n.Z * dir.Z + n.Y * dir.Y + n.X * dir.X;
                    var m = 0f > d ? 0f : d;
                    var w = ((d * 0.5f + 0.5f) - m) * 0.33f + m;
                    if (w == 0f)
                        continue;
                    var hit = false;
                    int px = x + dx, py = y + dy, pz = z + dz;
                    for (var step = 0; step < 2; step++)
                    {
                        if (!hit && g.Inside(px, py, pz))
                            hit = g.Solid(px, py, pz);
                        if (hit)
                            occluded += w;
                        total += w;
                        px += dx;
                        py += dy;
                        pz += dz;
                        w *= 0.5f;
                    }
                }
        var r = occluded / total;
        var f = MathF.Pow(r, 2f);
        return ((1f - f) - 1f) * FaceWeights[face] + 1f;
    }

    /// <summary>
    /// Per face: a detect pass flags an open cell whose luminance (0.2126,
    /// 0.7152, 0.0722) is at least 5e-5 and whose R, G or B exceeds ratio times
    /// the mean of its open neighbours (26, in the grid); a fix pass gives each
    /// flagged cell the mean of its open, unflagged neighbours. Returns the
    /// fireflies flagged.
    /// </summary>
    public static int RemoveFireflies(float[] image, Grid g, float ratio)
    {
        var removed = 0;
        var flagged = new bool[g.X * g.Y * g.Z];
        for (var face = 0; face < 6; face++)
        {
            Array.Clear(flagged);
            for (var pass = 0; pass < 2; pass++)
            {
                var fix = pass == 1;
                for (var z = 0; z < g.Z; z++)
                    for (var y = 0; y < g.Y; y++)
                        for (var x = 0; x < g.X; x++)
                        {
                            var cell = (z * g.Y + y) * g.X + x;
                            if (fix && !flagged[cell])
                                continue;
                            if (g.Solid(x, y, z))
                                continue;
                            var at = g.Texel(face, x, y, z);
                            float r = image[at], gr = image[at + 1], b = image[at + 2];
                            if (!(5e-05f <= gr * 0.7152f + r * 0.2126f + b * 0.0722f))
                                continue;
                            float sr = 0f, sg = 0f, sb = 0f;
                            var count = 0;
                            for (var dz = -1; dz <= 1; dz++)
                                for (var dy = -1; dy <= 1; dy++)
                                    for (var dx = -1; dx <= 1; dx++)
                                    {
                                        if (dz == 0 && dy == 0 && dx == 0)
                                            continue;
                                        int nx = x + dx, ny = y + dy, nz = z + dz;
                                        if (!g.Inside(nx, ny, nz) || g.Solid(nx, ny, nz))
                                            continue;
                                        if (fix && flagged[(nz * g.Y + ny) * g.X + nx])
                                            continue;
                                        var n = g.Texel(face, nx, ny, nz);
                                        sg += image[n + 1];
                                        sr += image[n];
                                        count++;
                                        sb += image[n + 2];
                                    }
                            if (count > 0)
                            {
                                var inv = 1f / count;
                                sr *= inv;
                                sg *= inv;
                                sb *= inv;
                            }
                            if (fix)
                            {
                                image[at] = sr;
                                image[at + 1] = sg;
                                image[at + 2] = sb;
                            }
                            else if (sr * ratio < r || sg * ratio < gr || sb * ratio < b)
                            {
                                removed++;
                                flagged[cell] = true;
                            }
                        }
            }
        }
        return removed;
    }

    private static readonly float[] DistanceWeights = [0f, 0.23076923f, 0.46153846f, 0.30769232f];

    /// <summary>
    /// Per face, the solid cells filled from the open ones: sweeps over the
    /// cells not yet valid, each summing its valid 3 x 3 x 3 neighbours with
    /// a weight by Manhattan distance (0.2308, 0.4615, 0.3077), halved when
    /// the offset does not point along the face; a cell whose weight passes the
    /// threshold takes the weighted mean (alpha 1) and turns valid after the
    /// sweep. Sweeps repeat until none fills, then the threshold drops by 0.1
    /// from 0.5 while it is above 0.
    /// </summary>
    public static void Dilate(float[] image, Grid g)
    {
        var count = g.X * g.Y * g.Z;
        for (var face = 0; face < 6; face++)
        {
            var valid = new bool[count];
            for (var z = 0; z < g.Z; z++)
                for (var y = 0; y < g.Y; y++)
                    for (var x = 0; x < g.X; x++)
                        valid[(z * g.Y + y) * g.X + x] = !g.Solid(x, y, z);
            var fresh = new bool[count];
            var dir = Faces[face];
            var threshold = 0.5f;
            while (true)
            {
                while (true)
                {
                    Array.Clear(fresh);
                    var filled = 0;
                    for (var z = 0; z < g.Z; z++)
                        for (var y = 0; y < g.Y; y++)
                            for (var x = 0; x < g.X; x++)
                            {
                                var cell = (z * g.Y + y) * g.X + x;
                                if (valid[cell])
                                    continue;
                                float wsum = 0f, r = 0f, gr = 0f, b = 0f;
                                int x0 = Math.Max(0, x - 1), x1 = Math.Min(g.X, x + 2);
                                int y0 = Math.Max(0, y - 1), y1 = Math.Min(g.Y, y + 2);
                                int z0 = Math.Max(0, z - 1), z1 = Math.Min(g.Z, z + 2);
                                for (var nz = z0; nz < z1; nz++)
                                    for (var ny = y0; ny < y1; ny++)
                                        for (var nx = x0; nx < x1; nx++)
                                        {
                                            var dist = Math.Abs(nz - z) + Math.Abs(ny - y) + Math.Abs(nx - x);
                                            if (dist == 0 || !valid[(nz * g.Y + ny) * g.X + nx])
                                                continue;
                                            var w = DistanceWeights[dist];
                                            if ((nx - x) * dir.X + (ny - y) * dir.Y + (nz - z) * dir.Z < 1)
                                                w *= 0.5f;
                                            wsum += w;
                                            var at = g.Texel(face, nx, ny, nz);
                                            r += image[at] * w;
                                            gr += image[at + 1] * w;
                                            b += image[at + 2] * w;
                                        }
                                if (!(threshold < wsum))
                                    continue;
                                filled++;
                                var inv = 1f / wsum;
                                fresh[cell] = true;
                                var t = g.Texel(face, x, y, z);
                                image[t] = r * inv;
                                image[t + 1] = gr * inv;
                                image[t + 2] = b * inv;
                                image[t + 3] = 1f;
                            }
                    if (filled == 0)
                        break;
                    for (var i = 0; i < count; i++)
                        valid[i] |= fresh[i];
                }
                if (!(0f < threshold))
                    break;
                threshold += -0.1f;
            }
        }
    }

    /// <summary>
    /// 180081560, the first step of lpv_image_resolve: two rounds of filling
    /// empty shadow slots (light index 255) from the 3 x 3 x 3 neighbourhood.
    /// Round one starts from every cell with an empty slot, in z, y, x order.
    /// A candidate that still has an empty slot is ready when some neighbour
    /// (dz, dy, dx order) holds that slot. Each ready cell then takes, for each
    /// empty slot, the mean of that slot over its neighbours that are not ready
    /// and hold it, marked 254; when it filled any, its neighbours that are not
    /// ready, still have an empty slot and are not yet queued become the next
    /// round's candidates.
    /// </summary>
    public static void DilateShadows(byte[] ids, float[] values, int nx, int ny, int nz)
    {
        var count = nx * ny * nz;
        bool Empty(int c) => ids[c * 4] == 255 || ids[c * 4 + 1] == 255 || ids[c * 4 + 2] == 255 || ids[c * 4 + 3] == 255;
        var candidates = new List<int>();
        for (var c = 0; c < count; c++)
            if (Empty(c))
                candidates.Add(c);
        var ready = new bool[count];
        var queued = new bool[count];
        var readyList = new List<int>();
        Span<float> sum = stackalloc float[4];
        Span<float> num = stackalloc float[4];
        for (var round = 0; round < 2; round++)
        {
            Array.Clear(ready);
            readyList.Clear();
            foreach (var c in candidates)
            {
                if (!Empty(c))
                    continue;
                int x = c % nx, y = c / nx % ny, z = c / nx / ny;
                var found = false;
                for (var zz = Math.Max(0, z - 1); zz < Math.Min(nz, z + 2) && !found; zz++)
                    for (var yy = Math.Max(0, y - 1); yy < Math.Min(ny, y + 2) && !found; yy++)
                        for (var xx = Math.Max(0, x - 1); xx < Math.Min(nx, x + 2) && !found; xx++)
                        {
                            var n = (zz * ny + yy) * nx + xx;
                            for (var k = 0; k < 4 && !found; k++)
                                found = ids[c * 4 + k] == 255 && ids[n * 4 + k] != 255;
                        }
                if (found)
                {
                    ready[c] = true;
                    readyList.Add(c);
                }
            }
            Array.Clear(queued);
            candidates = [];
            foreach (var c in readyList)
            {
                queued[c] = true;
                int x = c % nx, y = c / nx % ny, z = c / nx / ny;
                int x0 = Math.Max(0, x - 1), x1 = Math.Min(nx, x + 2), y0 = Math.Max(0, y - 1), y1 = Math.Min(ny, y + 2), z0 = Math.Max(0, z - 1), z1 = Math.Min(nz, z + 2);
                sum.Clear();
                num.Clear();
                for (var zz = z0; zz < z1; zz++)
                    for (var yy = y0; yy < y1; yy++)
                        for (var xx = x0; xx < x1; xx++)
                        {
                            var n = (zz * ny + yy) * nx + xx;
                            if (ready[n])
                                continue;
                            for (var k = 0; k < 4; k++)
                                if (ids[c * 4 + k] == 255 && ids[n * 4 + k] != 255)
                                {
                                    sum[k] += values[n * 4 + k];
                                    num[k] += 1f;
                                }
                        }
                var filled = false;
                for (var k = 0; k < 4; k++)
                    if (ids[c * 4 + k] == 255 && num[k] != 0f)
                    {
                        filled = true;
                        values[c * 4 + k] = sum[k] / num[k];
                        ids[c * 4 + k] = 254;
                    }
                if (!filled)
                    continue;
                for (var zz = z0; zz < z1; zz++)
                    for (var yy = y0; yy < y1; yy++)
                        for (var xx = x0; xx < x1; xx++)
                        {
                            var n = (zz * ny + yy) * nx + xx;
                            if (ready[n] || !Empty(n) || queued[n])
                                continue;
                            queued[n] = true;
                            candidates.Add(n);
                        }
            }
        }
    }

    /// <summary>
    /// lpv_image_resolve's shadow image, (X Z) by Y, the four slots as R, G, B
    /// and A: an empty slot is 1, else 1 - clamp(v, 0, 1), or 1 - sqrt(v) for a
    /// positive v with -gamma20.
    /// </summary>
    public static float[] DirectLightShadows(byte[] ids, float[] values, int nx, int ny, int nz, bool gamma20 = false)
    {
        var w = nx * nz;
        var image = new float[w * ny * 4];
        for (var c = 0; c < nx * ny * nz; c++)
        {
            int x = c % nx, y = c / nx % ny, z = c / nx / ny;
            var at = (y * w + z * nx + x) * 4;
            for (var k = 0; k < 4; k++)
            {
                float v;
                if (ids[c * 4 + k] == 255)
                    v = 1f;
                else
                {
                    v = values[c * 4 + k];
                    if (v <= 0f)
                        v = 0f;
                    if (1f <= v)
                        v = 1f;
                    v = gamma20 && 0f < v ? 1f - MathF.Sqrt(v) : 1f - v;
                }
                image[at + k] = v;
            }
        }
        return image;
    }

    /// <summary>1800812a0 on band 6: RGB 0.3 for an open cell and 0 for a solid one, alpha 1.</summary>
    public static void WriteOpenBand(float[] image, Grid g, int band = 6)
    {
        for (var z = 0; z < g.Z; z++)
            for (var y = 0; y < g.Y; y++)
                for (var x = 0; x < g.X; x++)
                {
                    var v = g.Solid(x, y, z) ? 0f : 0.3f;
                    var at = g.Texel(band, x, y, z);
                    image[at] = v;
                    image[at + 1] = v;
                    image[at + 2] = v;
                    image[at + 3] = 1f;
                }
    }

    /// <summary>
    /// Per face and open cell, each of R, G and B becomes the median of the
    /// open cells reached from it through open face neighbours within radius
    /// max(1, (size - 1) / 2) (squared distance), the cell itself not counted;
    /// the mean of the middle two for an even count; unchanged with one or
    /// none. The results replace the face's values after the whole face.
    /// </summary>
    public static void MedianFilter(float[] image, Grid g, int size)
    {
        if (size == 0)
            return;
        var radius = Math.Max(1, (size - 1) / 2);
        var r2 = radius * radius;
        var list = new List<(int X, int Y, int Z)>();
        var seen = new HashSet<int>();
        var values = new[] { new List<float>(), new List<float>(), new List<float>() };
        for (var face = 0; face < 6; face++)
        {
            var result = new float[g.X * g.Y * g.Z * 3];
            for (var z = 0; z < g.Z; z++)
                for (var y = 0; y < g.Y; y++)
                    for (var x = 0; x < g.X; x++)
                    {
                        var cell = (z * g.Y + y) * g.X + x;
                        var at = g.Texel(face, x, y, z);
                        for (var c = 0; c < 3; c++)
                            result[cell * 3 + c] = image[at + c];
                    }
            for (var z = 0; z < g.Z; z++)
                for (var y = 0; y < g.Y; y++)
                    for (var x = 0; x < g.X; x++)
                    {
                        if (g.Solid(x, y, z))
                            continue;
                        list.Clear();
                        seen.Clear();
                        foreach (var v in values)
                            v.Clear();
                        list.Add((x, y, z));
                        seen.Add((z * g.Y + y) * g.X + x);
                        for (var i = 0; i < list.Count; i++)
                            foreach (var d in Neighbours)
                            {
                                int nx = list[i].X + d.X, ny = list[i].Y + d.Y, nz = list[i].Z + d.Z;
                                if (!g.Inside(nx, ny, nz) || g.Solid(nx, ny, nz))
                                    continue;
                                int ex = x - nx, ey = y - ny, ez = z - nz;
                                if (ex * ex + ey * ey + ez * ez > r2 || !seen.Add((nz * g.Y + ny) * g.X + nx))
                                    continue;
                                list.Add((nx, ny, nz));
                                var at = g.Texel(face, nx, ny, nz);
                                for (var c = 0; c < 3; c++)
                                    values[c].Add(image[at + c]);
                            }
                        if (values[0].Count < 2)
                            continue;
                        var cell = (z * g.Y + y) * g.X + x;
                        for (var c = 0; c < 3; c++)
                        {
                            var v = values[c];
                            v.Sort();
                            var mid = v.Count / 2;
                            result[cell * 3 + c] = (v.Count & 1) == 0 ? (v[mid] + v[mid - 1]) * 0.5f : v[mid];
                        }
                    }
            for (var z = 0; z < g.Z; z++)
                for (var y = 0; y < g.Y; y++)
                    for (var x = 0; x < g.X; x++)
                    {
                        var cell = (z * g.Y + y) * g.X + x;
                        var at = g.Texel(face, x, y, z);
                        for (var c = 0; c < 3; c++)
                            image[at + c] = result[cell * 3 + c];
                    }
        }
    }
}
