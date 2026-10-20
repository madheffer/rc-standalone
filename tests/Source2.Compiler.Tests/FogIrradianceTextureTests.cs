using Source2.Compiler.Lighting;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="LightmapTextures.FogIrradianceVtex"/> on vrad3's own fiv.exr against the
/// fog_irradiance_volume.vtex_c resourcecompiler made from it: <c>FOGVTEX=&lt;fiv.exr&gt;|&lt;vtex_c&gt;|x|y|z|&lt;addon&gt;</c>.
/// </summary>
public sealed class FogIrradianceTextureTests(ITestOutputHelper output)
{
    [Fact]
    public void FogTextureIsValves()
    {
        if (Environment.GetEnvironmentVariable("FOGVTEX") is not { Length: > 0 } spec || CS2Fixtures.StockPak() is not { } pak)
            return;
        var p = spec.Split('|');
        var exr = Vrad3Exr.Read(File.ReadAllBytes(p[0]));
        int x = int.Parse(p[2]), y = int.Parse(p[3]), z = int.Parse(p[4]);
        var image = new RcTexture.RcMipChain.Level(x * z, y, LightProbeAtlasImages.Rgba(exr));
        var native = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, "..", "bin", "win64"));
        var ours = LightmapTextures.FogIrradianceVtex(image, x, y, z, $"csgo_addons/{p[5]}", native);
        var valve = File.ReadAllBytes(p[1]);
        var at = 0;
        while (at < Math.Min(ours.Length, valve.Length) && ours[at] == valve[at])
            at++;
        output.WriteLine($"ours {ours.Length} bytes, valve {valve.Length}, first difference at {at}");
        Assert.True(ours.AsSpan().SequenceEqual(valve));
    }
}
