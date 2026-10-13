using Source2.Compiler.RcTexture;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// The normal-map and alpha-weighting mip algorithms against resourcecompiler's RGBA8888
/// compiles of the same inputs (RcReference/textures/algorithms): every level's bytes and the
/// reflectivity floats. RGBA8888 keeps every level exact, so this is the algorithm alone.
/// </summary>
public class RcMipAlgorithmTests
{
    private static string Fixture(string name)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null && !Directory.Exists(Path.Combine(dir, "RcReference")); i++)
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? AppContext.BaseDirectory, "RcReference", "textures", "algorithms", name);
    }

    private static RcMipChain.Level Load(string file, bool srgb)
    {
        var (rgba, w, h) = VtexCompiler.DecodeRgba(File.ReadAllBytes(Fixture(file)), file);
        return RcMipChain.Load(rgba, w, h, srgb);
    }

    private static void AssertMatches(string name, IReadOnlyList<RcMipChain.Level> levels, bool srgb)
    {
        var rc = File.ReadAllBytes(Fixture(name + ".vtex_c"));
        var data = CompiledResource.Read(rc).File["DATA"]!.Data;
        for (int c = 0; c < 4; c++)
            Assert.Equal(BitConverter.ToSingle(data, 4 + 4 * c), levels[^1].Rgba[c]);
        int pos = rc.Length;
        for (int m = 0; m < levels.Count; m++)
        {
            var bytes = RcMipChain.WriteRgba8(levels[m], srgb);
            pos -= bytes.Length;
            Assert.True(rc.AsSpan(pos, bytes.Length).SequenceEqual(bytes), $"{name} mip {m}");
        }
    }

    [Theory]
    [InlineData("hemi_bumps64", "bumps64.png", "rough64.png", true)]
    [InlineData("hemi_noisy64", "noisy64.png", "rough64.png", true)]
    [InlineData("hemi_bumps16", "bumps16.png", "rough16.png", true)]
    [InlineData("hemi_noproc64", "bumps64.png", "rough64.png", false)]
    public void HemiOctIsoRoughness_MatchesResourceCompiler(string name, string normalFile, string roughFile, bool processors)
    {
        var normal = Load(normalFile, false);
        var rough = Load(roughFile, false);
        if (processors)
        {
            normal = RcNormalMaps.NormalizeNormals(normal, Bc7Vendor.Amd);
            rough = RcNormalMaps.Inverse(rough);
        }
        AssertMatches(name, RcNormalMaps.HemiOctIsoRoughness(normal, rough), srgb: false);
    }

    [Fact]
    public void AlphaWeighted_MatchesResourceCompiler()
    {
        var color = Load("color64.png", true);
        var trans = Load("trans64.png", false);
        var work = (float[])color.Rgba.Clone();
        for (int p = 0; p < color.Width * color.Height; p++)
            work[p * 4 + 3] = trans.Rgba[p * 4];
        var weighted = RcAlphaWeighted.Mips(new RcMipChain.Level(color.Width, color.Height, work));
        // The texture's alpha is a separate Box channel of the same translucency image.
        var box = RcMipChain.Chain(trans);
        var levels = weighted.Select((l, m) =>
        {
            var o = (float[])l.Rgba.Clone();
            for (int p = 0; p < l.Width * l.Height; p++)
                o[p * 4 + 3] = box[m].Rgba[p * 4];
            return new RcMipChain.Level(l.Width, l.Height, o);
        }).ToList();
        AssertMatches("aw_color64", levels, srgb: true);
    }

    [Fact]
    public void ReciprocalSqrtEstimate_ScalesWithTheExponent()
    {
        // AMD's table at 1.0 and 4.0 differ by exactly a factor of 2 (the estimate depends on
        // the exponent's parity and the top mantissa bits only).
        float one = RcNormalMaps.ReciprocalSqrtEstimate(1f, Bc7Vendor.Amd);
        Assert.Equal(one * 0.5f, RcNormalMaps.ReciprocalSqrtEstimate(4f, Bc7Vendor.Amd));
        Assert.InRange(one, 0.999f, 1.001f);
        Assert.InRange(RcNormalMaps.ReciprocalSqrtEstimate(2f, Bc7Vendor.Intel), 0.7065f, 0.708f);
    }
}
