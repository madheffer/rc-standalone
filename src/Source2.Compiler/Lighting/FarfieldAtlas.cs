namespace Source2.Compiler.Lighting;

/// <summary>
/// vrad3's far field atlas (farfield_compute_lighting, 180021b60): four images, dc, sh1_r, sh1_g and
/// sh1_b, each 33 slices of 1056 (32 bricks of 33 texels) by ceil(bricks / 32) * 33 RGB float texels,
/// cleared to 0. A brick b's 32^3 luxels land at column (b &amp; 31) * 33 + x, row (b &gt;&gt; 5) * 33 + y,
/// slice z (180021fb0); the 33rd row, column and slice of each tile are the borders
/// farfield_resample_borders fills. farfield_write_atlas (1800247b0) writes each image and slice as
/// &lt;path&gt;_&lt;image&gt;_z&lt;NN&gt;&lt;suffix&gt;.exr (FloatBitMap_t, RGB halves).
/// </summary>
public sealed class FarfieldAtlas
{
    public const int Slices = 33, Tile = 33, PerRow = 32, Width = PerRow * Tile;

    public static readonly string[] Images = ["dc", "sh1_r", "sh1_g", "sh1_b"];

    public int Bricks { get; }
    public int Height { get; }

    /// <summary>[image][slice]: Width x Height RGB floats, row major.</summary>
    public float[][][] Planes { get; }

    public FarfieldAtlas(int bricks)
    {
        Bricks = bricks;
        var rows = bricks == 0 ? 32 : (bricks + 31) & ~31;
        Height = rows / 32 * Tile;
        Planes = new float[Images.Length][][];
        for (var i = 0; i < Images.Length; i++)
        {
            Planes[i] = new float[Slices][];
            for (var s = 0; s < Slices; s++)
                Planes[i][s] = new float[Width * Height * 3];
        }
    }

    /// <summary>The texel offset (of 3 floats) of brick <paramref name="b"/>'s (x, y) in a slice.</summary>
    public int At(int b, int x, int y) => (((b >> 5) * Tile + y) * Width + (b & 31) * Tile + x) * 3;

    /// <summary>
    /// 180021fb0: the bricks' luxels (12 floats each, 32^3 a brick, x fastest): dc gets floats 0, 4, 8,
    /// sh1_r 1 to 3, sh1_g 5 to 7, sh1_b 9 to 11. Also the log's numbers: luxels, black (dc r + g + b
    /// at most 1e-5), mean and max dc.
    /// </summary>
    public static FarfieldAtlas FromLuxels(int bricks, ReadOnlySpan<float> luxels, Action<string>? log = null)
    {
        var atlas = new FarfieldAtlas(bricks);
        long count = 0, black = 0;
        var sum = 0.0;
        var max = 0f;
        var l = 0;
        for (var b = 0; b < bricks; b++)
            for (var z = 0; z < 32; z++)
                for (var y = 0; y < 32; y++)
                    for (var x = 0; x < 32; x++, l += 12)
                    {
                        var o = atlas.At(b, x, y);
                        var dc = atlas.Planes[0][z];
                        (dc[o], dc[o + 1], dc[o + 2]) = (luxels[l], luxels[l + 4], luxels[l + 8]);
                        for (var k = 0; k < 3; k++)
                        {
                            var p = atlas.Planes[1 + k][z];
                            (p[o], p[o + 1], p[o + 2]) = (luxels[l + k * 4 + 1], luxels[l + k * 4 + 2], luxels[l + k * 4 + 3]);
                        }
                        var d = luxels[l] + luxels[l + 4] + luxels[l + 8];
                        sum += d;
                        max = max <= d ? d : max;
                        black += d <= 1e-05f ? 1 : 0;
                        count++;
                    }
        log?.Invoke($"  farfield lighting: {count} luxels, {black} black ({(count == 0 ? 0 : black * 100.0 / count):F1}%), mean DC {(count == 0 ? 0 : sum / count):F4}, max DC {max:F4}");
        return atlas;
    }

    /// <summary>1800210c0: a texel's 12 floats in FarfieldLuxel_t order (dc r, sh1_r xyz, dc g, sh1_g xyz, dc b, sh1_b xyz).</summary>
    public void Read(int b, int x, int y, int z, Span<float> v)
    {
        var o = At(b, x, y);
        var dc = Planes[0][z];
        v[0] = dc[o];
        v[4] = dc[o + 1];
        v[8] = dc[o + 2];
        for (var k = 0; k < 3; k++)
        {
            var p = Planes[1 + k][z];
            v[k * 4 + 1] = p[o];
            v[k * 4 + 2] = p[o + 1];
            v[k * 4 + 3] = p[o + 2];
        }
    }

    /// <summary>180021290: the inverse of <see cref="Read"/>.</summary>
    public void Write(int b, int x, int y, int z, ReadOnlySpan<float> v)
    {
        var o = At(b, x, y);
        var dc = Planes[0][z];
        (dc[o], dc[o + 1], dc[o + 2]) = (v[0], v[4], v[8]);
        for (var k = 0; k < 3; k++)
        {
            var p = Planes[1 + k][z];
            (p[o], p[o + 1], p[o + 2]) = (v[k * 4 + 1], v[k * 4 + 2], v[k * 4 + 3]);
        }
    }

    /// <summary>farfield_write_atlas: every image and slice into <paramref name="path"/>_&lt;image&gt;_zNN&lt;suffix&gt;.exr.</summary>
    public void Write(string path, string suffix = "")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        for (var s = 0; s < Slices; s++)
            for (var i = 0; i < Images.Length; i++)
            {
                var exr = new Vrad3Exr(Width, Height, ["R", "G", "B"]);
                var plane = Planes[i][s];
                foreach (var (c, k) in new[] { ("R", 0), ("G", 1), ("B", 2) })
                {
                    var h = exr.Plane(c);
                    for (var t = 0; t < Width * Height; t++)
                        h[t] = LightmapImage.ToHalfAway(plane[t * 3 + k]);
                }
                File.WriteAllBytes($"{path}_{Images[i]}_z{s:D2}{suffix}.exr", exr.Write());
            }
    }
}
