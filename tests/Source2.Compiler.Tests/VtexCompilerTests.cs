using Source2.Compiler.Compiling;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary><c>.vtex</c> sources through <see cref="ResourceCompiler"/>, whole file against
/// resourcecompiler's output for the same source and image (RcReference/textures).</summary>
public class VtexCompilerTests
{
    private static string Fixture(string name)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null && !Directory.Exists(Path.Combine(dir, "RcReference")); i++)
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? AppContext.BaseDirectory, "RcReference", "textures", name);
    }

    private static MemoryContentSource Images()
    {
        var content = new MemoryContentSource();
        foreach (var png in Directory.GetFiles(Path.GetDirectoryName(Fixture("probe.png"))!, "*.png"))
            content.Add("materials/s2ctex/" + Path.GetFileName(png), File.ReadAllBytes(png));
        return content;
    }

    private static CompileResult Compile(string name, IContentSource? content)
        => ResourceCompiler.CreateDefault().Compile(new CompileRequest($"materials/s2ctex/{name}.vtex", File.ReadAllBytes(Fixture(name + ".vtex")))
        {
            SearchPath = "csgo_addons/vpkstuff_rc_probe",
            Content = content,
        });

    [Theory]
    [InlineData("rgba")]
    [InlineData("rgba_npot")]
    [InlineData("rgba_7x5")]
    [InlineData("dxt1")]
    [InlineData("dxt5")]
    [InlineData("dxt1_npot")]
    [InlineData("dxt1_opaque")]
    [InlineData("dxt5_7x5")]
    [InlineData("ati1n")]
    [InlineData("ati2n")]
    [InlineData("bc7")]
    [InlineData("bc7_4")]
    [InlineData("bc7_7x5")]
    [InlineData("bc7_nolod")]
    [InlineData("bc7_npot")]
    [InlineData("bc7_opaque")]
    [InlineData("rgba_flat512")]
    [InlineData("dxt1_flat512")]
    public void VtexSource_CompilesToResourceCompilersBytes(string name)
    {
        var result = Compile(name, Images());
        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        Assert.Equal($"materials/s2ctex/{name}.vtex_c", result.OutputPath);
        Assert.True(File.ReadAllBytes(Fixture(name + ".vtex_c")).AsSpan().SequenceEqual(result.Output),
            $"{name}.vtex_c differs from resourcecompiler's");
        Assert.Contains(result.InputDependencies, d => d.Path == $"materials/s2ctex/{name}.vtex" && d.Exists);
        Assert.Contains(result.InputDependencies, d => d.Path.EndsWith(".png") && d.Exists);
        Assert.Contains(result.InputDependencies, d => d.Path.EndsWith(".txt") && d.Optional && !d.Exists);
    }

    // RcReference/textures/algorithms: several inputs, image processors, channel groups and the
    // mip algorithms, compiled by RC from materials/s2chemi (10-13) and materials/s2calg (11-03).
    private static MemoryContentSource AlgorithmImages()
    {
        var content = new MemoryContentSource();
        foreach (var png in Directory.GetFiles(Path.Combine(Path.GetDirectoryName(Fixture("probe.png"))!, "algorithms"), "*.png"))
            foreach (var folder in new[] { "materials/s2chemi", "materials/s2calg" })
                content.Add($"{folder}/" + Path.GetFileName(png), File.ReadAllBytes(png));
        return content;
    }

    [Theory]
    [InlineData("s2chemi", "hemi_bumps64")]
    [InlineData("s2chemi", "hemi_noisy64")]
    [InlineData("s2chemi", "hemi_bumps16")]
    [InlineData("s2chemi", "hemi_noproc64")]
    [InlineData("s2chemi", "aw_color64")]
    [InlineData("s2calg", "none_rgba")]
    [InlineData("s2calg", "none_bc7")]
    [InlineData("s2calg", "boxinv_rgba")]
    [InlineData("s2calg", "metal_rgba")]
    [InlineData("s2calg", "metal_ati2n")]
    [InlineData("s2calg", "mipnormal_rgba")]
    [InlineData("s2calg", "pc_rgba")]
    [InlineData("s2calg", "pc_alpha_rgba")]
    [InlineData("s2calg", "pc_alpha_bc7")]
    [InlineData("s2calg", "hemianiso_rgba")]
    [InlineData("s2calg", "hemianiso_bc7")]
    [InlineData("s2calg", "hemianiso_norough_rgba")]
    [InlineData("s2calg", "anisorough_rgba")]
    [InlineData("s2calg", "anisorough_ati2n")]
    [InlineData("s2calg", "mod2x_rgba")]
    [InlineData("s2calg", "mod2x_srgb_rgba")]
    [InlineData("s2calg", "detail_bc7")]
    [InlineData("s2calg", "box_none_rgba")]
    [InlineData("s2calg", "none_box_rgba")]
    [InlineData("s2calg", "sticker_normal_rgba")]
    [InlineData("s2calg", "sticker_normal_bc7")]
    public void VtexSource_WithChannelGroupsAndAlgorithms_CompilesToResourceCompilersBytes(string folder, string name)
    {
        var result = ResourceCompiler.CreateDefault().Compile(new CompileRequest($"materials/{folder}/{name}.vtex",
            File.ReadAllBytes(Fixture($"algorithms/{name}.vtex")))
        {
            SearchPath = "csgo_addons/vpkstuff_rc_probe",
            Content = AlgorithmImages(),
        });
        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        Assert.True(File.ReadAllBytes(Fixture($"algorithms/{name}.vtex_c")).AsSpan().SequenceEqual(result.Output),
            $"{name}.vtex_c differs from resourcecompiler's");
    }

    // RcReference/textures/algorithms2: the rest of GenerateMips' algorithms, the image processors
    // and the output formats, compiled by RC (1.41.9.5) from materials/s2calg2 on 11-04
    // (make_probes.py's set; the formats RC refuses, "Unsupported image format", have no row).
    [Theory]
    [InlineData("box1_rgba")]
    [InlineData("box2_rgba")]
    [InlineData("box5_rgba")]
    [InlineData("box9_rgba")]
    [InlineData("multiplybox_rgba")]
    [InlineData("multiplynomip_rgba")]
    [InlineData("rgbm_rgba")]
    [InlineData("rgbm_srgb_rgba")]
    [InlineData("encodergbm_rgba")]
    [InlineData("ycocg_rgba")]
    [InlineData("ycocg_srgb_rgba")]
    [InlineData("ycocg_dxt5")]
    [InlineData("hemianisoba_rgba")]
    [InlineData("alphaweighted_rgba")]
    [InlineData("artist_box_rgba")]
    [InlineData("hemioctnormal_rgba")]
    [InlineData("scalepow2_rgba")]
    [InlineData("fillpow2_rgba")]
    [InlineData("fillmult4_rgba")]
    [InlineData("heighttonormal_rgb")]
    [InlineData("mipwidth_r")]
    [InlineData("mipheight_r")]
    [InlineData("mipdepth_r")]
    [InlineData("fmt_i8")]
    [InlineData("fmt_ia88")]
    [InlineData("fmt_a8")]
    [InlineData("fmt_r8_unorm")]
    [InlineData("fmt_bgra8888")]
    [InlineData("fmt_r16")]
    [InlineData("fmt_rg1616")]
    [InlineData("fmt_rgba16161616")]
    [InlineData("fmt_r16f")]
    [InlineData("fmt_rg1616f")]
    [InlineData("fmt_rgba16161616f")]
    [InlineData("fmt_r32f")]
    [InlineData("fmt_rg3232f")]
    [InlineData("fmt_rgb323232f")]
    [InlineData("fmt_rgba32323232f")]
    public void VtexSource_RemainingAlgorithms_CompileToResourceCompilersBytes(string name)
    {
        var content = new MemoryContentSource();
        foreach (var png in Directory.GetFiles(Path.Combine(Path.GetDirectoryName(Fixture("probe.png"))!, "algorithms2"), "*.png"))
            content.Add("materials/s2calg2/" + Path.GetFileName(png), File.ReadAllBytes(png));
        var result = ResourceCompiler.CreateDefault().Compile(new CompileRequest($"materials/s2calg2/{name}.vtex",
            File.ReadAllBytes(Fixture($"algorithms2/{name}.vtex"))) { SearchPath = "csgo_addons/vpkstuff_rc_probe", Content = content });
        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        if (Environment.GetEnvironmentVariable("VTEX_DUMP") is { Length: > 0 } dump)
            File.WriteAllBytes(Path.Combine(dump, name + ".vtex_c"), result.Output!);
        Assert.True(File.ReadAllBytes(Fixture($"algorithms2/{name}.vtex_c")).AsSpan().SequenceEqual(result.Output), $"{name}.vtex_c differs from resourcecompiler's");
    }

    // RcReference/textures/tga: 32-bit TGAs RC read on 11-03, one right-to-left (RC ignores that
    // bit) and one whose descriptor declares no alpha bits (RC takes the fourth byte anyway).
    [Theory]
    [InlineData("tga_rtl")]
    [InlineData("tga_noalphabits")]
    public void VtexSource_FromTga_CompilesToResourceCompilersBytes(string name)
    {
        var content = new MemoryContentSource();
        foreach (var tga in Directory.GetFiles(Path.Combine(Path.GetDirectoryName(Fixture("probe.png"))!, "tga"), "*.tga"))
            content.Add("materials/s2ctga/" + Path.GetFileName(tga), File.ReadAllBytes(tga));
        var result = ResourceCompiler.CreateDefault().Compile(new CompileRequest($"materials/s2ctga/{name}.vtex",
            File.ReadAllBytes(Fixture($"tga/{name}.vtex"))) { SearchPath = "csgo_addons/vpkstuff_rc_probe", Content = content });
        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        Assert.True(File.ReadAllBytes(Fixture($"tga/{name}.vtex_c")).AsSpan().SequenceEqual(result.Output), $"{name}.vtex_c differs from resourcecompiler's");
    }

    [Fact]
    public void Bc7_IntelTable_CompilesTheSameLayout()
    {
        var amd = Compile("bc7", Images());
        var intel = ResourceCompiler.CreateDefault().Compile(new CompileRequest("materials/s2ctex/bc7.vtex", File.ReadAllBytes(Fixture("bc7.vtex")))
        {
            SearchPath = "csgo_addons/vpkstuff_rc_probe",
            Content = Images(),
            Settings = new Source2.Compiler.RcTexture.VtexCompileSettings(Source2.Compiler.RcTexture.Bc7Vendor.Intel),
        });
        Assert.True(intel.Succeeded, string.Join("\n", intel.Diagnostics));
        Assert.Equal(amd.Output!.Length, intel.Output!.Length);
    }

    [Fact]
    public void MissingImage_IsAnError()
    {
        var result = Compile("dxt1", new MemoryContentSource());
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("probe.png"));
    }

    [Fact]
    public void UnmeasuredSetting_IsRefusedNotGuessed()
    {
        var text = File.ReadAllText(Fixture("dxt1.vtex")).Replace("\"m_outputFormat\"", "\"m_flBumpStrength\" \"float\" \"2\"\n\t\"m_outputFormat\"");
        var result = ResourceCompiler.CreateDefault().Compile(new CompileRequest("materials/s2ctex/x.vtex", System.Text.Encoding.UTF8.GetBytes(text)) { Content = Images() });
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("m_flBumpStrength"));
    }
}
