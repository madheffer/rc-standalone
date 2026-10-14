namespace Source2.Compiler.RcTexture;

/// <summary>
/// resourcecompiler's normal-map processing (docs/MATERIALS.md, "Normal maps"): the
/// <c>NormalizeNormals</c> and <c>Inverse</c> image processors and the
/// <c>HemiOctIsoRoughness_RG_B</c> mip algorithm, float for float. Normalising uses the SSE
/// reciprocal square root estimate, which differs between CPU vendors, so it is reproduced from
/// one vendor's table (<see cref="Bc7Vendor"/>), as BC7 is.
/// </summary>
public static class RcNormalMaps
{
    private static readonly Lazy<(uint[] Table, int Bits)>[] Rsqrt =
        [new(() => LoadTable("rsqrt_amd.txt")), new(() => LoadTable("rsqrt_intel.txt"))];

    private static (uint[] Table, int Bits) LoadTable(string name)
    {
        using var stream = typeof(RcNormalMaps).Assembly.GetManifestResourceStream("Source2.Compiler.RcTexture." + name)
            ?? throw new InvalidOperationException($"The {name} table is not in this build.");
        using var reader = new StreamReader(stream);
        var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var table = new uint[lines.Length];
        foreach (var line in lines)
        {
            var parts = line.Split(' ');
            table[int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture) * (lines.Length / 2) + int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)] = Convert.ToUInt32(parts[2].Trim(), 16);
        }
        return (table, System.Numerics.BitOperations.Log2((uint)(lines.Length / 2)));
    }

    /// <summary>
    /// <c>rsqrtps</c> as the vendor's hardware computes it: the estimate depends on the exponent's
    /// parity and the top bits of the mantissa (12 on AMD, 10 on Intel) and scales exactly with
    /// the exponent (tools/re/texture/rsqrtdump.c). Positive normal inputs only.
    /// </summary>
    public static float ReciprocalSqrtEstimate(float x, Bc7Vendor vendor)
    {
        var (table, bits) = Rsqrt[(int)vendor].Value;
        uint u = BitConverter.SingleToUInt32Bits(x);
        int e = (int)(u >> 23) - 127;
        if (u >> 23 == 0 || u >> 23 >= 255)
            throw new ArgumentOutOfRangeException(nameof(x), "The estimate is modelled for positive normal floats.");
        int parity = e & 1;
        uint r = table[parity * (1 << bits) + (int)((u & 0x7FFFFF) >> (23 - bits))];
        int shift = (e - parity) / 2;
        return BitConverter.UInt32BitsToSingle((r & 0x807FFFFF) | ((uint)((int)(r >> 23) - shift) << 23));
    }

    /// <summary>
    /// <c>ApplyImageProcessor_NormalizeNormals</c> (1801400c0 → 181273ec0): channels 0..2 as
    /// <c>v * 2 - 1</c>, length squared <c>(z*z + y*y) + x*x</c>, the estimate refined once as
    /// <c>(r * (3 - (r*r)*l2)) * 0.5</c>, the default (0, 0, 1) where <c>l2</c> is under 2^-23 or all
    /// three inputs are 0, then <c>n * 0.5 + 0.5</c>. Alpha is kept.
    /// </summary>
    public static RcMipChain.Level NormalizeNormals(RcMipChain.Level level, Bc7Vendor vendor)
    {
        var o = (float[])level.Rgba.Clone();
        for (int i = 0; i < o.Length; i += 4)
        {
            float vx = o[i], vy = o[i + 1], vz = o[i + 2];
            float x = vx * 2f - 1f, y = vy * 2f - 1f, z = vz * 2f - 1f;
            float l2 = (z * z + y * y) + x * x;
            float nx, ny, nz;
            if (l2 < 1.1920928955078125e-07f || (vx == 0f && vy == 0f && vz == 0f))
                (nx, ny, nz) = (0f, 0f, 1f);
            else
            {
                float r = ReciprocalSqrtEstimate(l2, vendor);
                r = (r * (3f - (r * r) * l2)) * 0.5f;
                (nx, ny, nz) = (r * x, r * y, r * z);
            }
            o[i] = nx * 0.5f + 0.5f;
            o[i + 1] = ny * 0.5f + 0.5f;
            o[i + 2] = nz * 0.5f + 0.5f;
        }
        return new RcMipChain.Level(level.Width, level.Height, o);
    }

    /// <summary><c>ApplyImageProcessor_Inverse</c> (180141590): every channel <c>1 - v</c>.</summary>
    public static RcMipChain.Level Inverse(RcMipChain.Level level) =>
        new(level.Width, level.Height, level.Rgba.Select(v => 1f - v).ToArray());

    /// <summary>
    /// <c>HemiOctIsoRoughness_RG_B</c>: <c>GenerateMips_HemiOctAnisoRoughness</c> (180148350) then the
    /// per-pixel shuffle in <c>GenerateMips</c>. Every level, down to 1x1, as RGBA floats: R and G
    /// the hemi-octahedral encoding of the level's mean normal, B the roughness (the input's plus
    /// the normals' spread), A 1. <paramref name="normal"/> is the normalised normal map
    /// (channels 0..1), <paramref name="roughness"/> the inverted roughness input or null; both the
    /// same power-of-two size.
    /// </summary>
    public static List<RcMipChain.Level> HemiOctIsoRoughness(RcMipChain.Level normal, RcMipChain.Level? roughness)
    {
        int w = normal.Width, h = normal.Height;
        if ((w & (w - 1)) != 0 || (h & (h - 1)) != 0)
            throw new InvalidOperationException($"RC cannot filter a non-power-of-two normal map ({w}x{h}).");
        if (roughness is not null && (roughness.Width != w || roughness.Height != h))
            throw new InvalidOperationException("A roughness input of another size than the normal map is not measured.");
        int n = w * h;
        // Moments, channels as RC numbers them: 10/11 mean X/Y (0..1 space), 8/9 their squares,
        // 6/7 the input roughness.
        var mean = new RcMipChain.Level(w, h, new float[n * 4]);   // 10, 11, 8, 9
        var rough = new RcMipChain.Level(w, h, new float[n * 4]);  // 6, 7
        for (int i = 0; i < n; i++)
        {
            float x = normal.Rgba[i * 4], y = normal.Rgba[i * 4 + 1];
            mean.Rgba[i * 4] = x;
            mean.Rgba[i * 4 + 1] = y;
            mean.Rgba[i * 4 + 2] = x * x;
            mean.Rgba[i * 4 + 3] = y * y;
            // A missing roughness input is a 1x1 of 1s; 1 - 1 = 0.
            rough.Rgba[i * 4] = roughness is null ? 0f : 1f - roughness.Rgba[i * 4];
            rough.Rgba[i * 4 + 1] = roughness is null ? 0f : 1f - roughness.Rgba[i * 4 + 1];
        }

        var levels = new List<RcMipChain.Level>();
        bool first = true;
        while (true)
        {
            var o = new float[mean.Width * mean.Height * 4];
            for (int i = 0; i < mean.Width * mean.Height; i++)
            {
                float m10 = mean.Rgba[i * 4], m11 = mean.Rgba[i * 4 + 1];
                // Channels 4/5: the spread of the normals, (2 * stddev)^1.5 as sqrt(v) * v (zero on mip 0).
                float s4 = 0f, s5 = 0f;
                if (!first)
                {
                    s4 = Spread(mean.Rgba[i * 4 + 2], m10);
                    s5 = Spread(mean.Rgba[i * 4 + 3], m11);
                }
                // The lambda (180149d20): the mean normal from X and Y, z rebuilt, hemi-oct encoded.
                float x = (m10 + m10) - 1f, y = (m11 + m11) - 1f;
                float zz = 1f - (y * y + x * x);
                float z = MathF.Sqrt(zz < 0f ? 0f : MathF.Min(1f, zz));
                float inv = 1f / ((MathF.Abs(y) + MathF.Abs(x)) + z);
                float px = x * inv, py = y * inv;
                float ch3 = (px + py) * 0.5f + 0.5f, ch1 = (px - py) * 0.5f + 0.5f;
                float rx = rough.Rgba[i * 4] + s4, ry = rough.Rgba[i * 4 + 1] + s5;
                // The _RG_B shuffle: R = ch3, G = ch1, B = (ch2 + ch0) * 0.5, A = 1.
                o[i * 4] = ch3;
                o[i * 4 + 1] = ch1;
                o[i * 4 + 2] = (ry + rx) * 0.5f;
                o[i * 4 + 3] = 1f;
            }
            levels.Add(new RcMipChain.Level(mean.Width, mean.Height, o));
            if (mean.Width == 1 && mean.Height == 1)
                return levels;
            mean = RcMipChain.QuarterSize(mean);
            rough = RcMipChain.QuarterSize(rough);
            first = false;
        }
    }

    // sqrt(max(0, m2 - m1*m1)) * 2 (1812775a0), then ^1.5 as sqrt(v) * v (18125d850 with 6).
    private static float Spread(float m2, float m1)
    {
        float v = MathF.Sqrt(MathF.Max(0f, m2 - m1 * m1)) * 2f;
        return MathF.Sqrt(v) * v;
    }
}
