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
