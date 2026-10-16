namespace Source2.Compiler.Lighting;

/// <summary>
/// The lightmap switches CStaticLightingProcessor::BakeLighting (0923
/// 180217740) reads before charting, from the map's lighting settings (the
/// processor's settings vf 0x50 / 0x58: bool and int by name, with defaults)
/// and gameinfo:
/// <list type="bullet">
/// <item>the atlas side: lightmapMaxResolution (4096), lowered to the
/// builder's requested size (+0x2f8) when that is smaller, raised to 512 when
/// under it; it must be a power of two;</item>
/// <item>the mips: lightmapMipCount (1), fewer when a mip would drop under
/// 512, more until the largest fits ResourceCompiler/BakedLighting/
/// MinSpecLightmapSize (4096 in CS2);</item>
/// <item>the packer's passes: lightmapChartPackIterations, by default 4, 7
/// or 9 for lightmapVRadQuality 0, 1 or 2 (182a8cc68);</item>
/// <item>the weld: unless lightmapNoWeld;</item>
/// <item>GenerateCharts' second argument: 1 when directional_irradiance_sh2_dc
/// is among the lightmap outputs (gameinfo LightmapChannels; not in CS2's),
/// which keeps vertices of one position but different normals together in
/// the corner adjacency; 0 splits them.</item>
/// </list>
/// </summary>
internal sealed record LightmapSettings(int Width, int Height, int Mip, int Passes, bool Weld, bool Sh2Dc)
{
    private static readonly int[] PassesByQuality = [4, 7, 9];

    /// <param name="settings">The map's lighting settings by name (absent ones take their defaults).</param>
    /// <param name="requested">The builder's requested size (+0x2f8).</param>
    /// <param name="minSpec">gameinfo MinSpecLightmapSize (0: none).</param>
    /// <param name="sh2Dc">directional_irradiance_sh2_dc is a lightmap output.</param>
    public static LightmapSettings From(IReadOnlyDictionary<string, string> settings, int requested, int minSpec, bool sh2Dc)
    {
        int Int(string name, int fallback) => settings.TryGetValue(name, out var v) && int.TryParse(v, out var x) ? x : fallback;
        bool Bool(string name) => settings.TryGetValue(name, out var v) && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
        var size = Int("lightmapMaxResolution", 0x1000);
        if (requested < size)
            size = requested;
        else if (size < 0x200)
            size = requested = 0x200;
        else
            requested = size;
        if (requested < 1 || (requested & (requested - 1)) != 0)
            throw new NotSupportedException($"lightmap resolution {requested} is not a power of two");
        int w = requested, h = requested;
        var mips = 1;
        var count = Int("lightmapMipCount", 1);
        if (count >= 2)
        {
            mips = count;
            for (var m = 1; m < count; m++)
            {
                if (Math.Min(w, h) < 0x201)
                {
                    mips = m;
                    break;
                }
                w /= 2;
                h /= 2;
            }
        }
        if (minSpec > 0x1ff)
            while (Math.Min(w, h) > minSpec)
            {
                w /= 2;
                h /= 2;
                mips++;
            }
        var quality = Int("lightmapVRadQuality", 0);
        var passes = Int("lightmapChartPackIterations", quality is >= 0 and < 3 ? PassesByQuality[quality]
            : throw new NotSupportedException($"lightmapVRadQuality {quality}"));
        return new LightmapSettings(requested, requested, mips, passes, !Bool("lightmapNoWeld"), sh2Dc);
    }
}
