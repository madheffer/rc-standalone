using System.Text.RegularExpressions;
using Source2.Compiler.Lighting;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The five env_farfield_0 textures from vrad3's own farfield_0_bricks.kv3 and ff/ atlas EXRs against
/// the ones resourcecompiler packed: <c>FFVTEX=&lt;vrad3 folder&gt;|&lt;vpk&gt;|&lt;addon&gt;|&lt;map&gt;</c>.
/// </summary>
public sealed partial class FarfieldTextureTests(ITestOutputHelper output)
{
    [Fact]
    public void TexturesAreValves()
    {
        if (Environment.GetEnvironmentVariable("FFVTEX") is not { Length: > 0 } spec || CS2Fixtures.StockPak() is not { } pak)
            return;
        var p = spec.Split('|');
        var native = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, "..", "bin", "win64"));
        var searchPath = $"csgo_addons/{p[2]}";
        using var vpk = new ValvePak.Package();
        vpk.Read(p[1]);
        byte[] Valve(string name)
        {
            var e = vpk.FindEntry($"maps/{p[3]}/lightmaps/{name}.vtex_c") ?? throw new FileNotFoundException(name);
            vpk.ReadEntry(e, out var b);
            return b;
        }
        var text = File.ReadAllText(Path.Combine(p[0], "farfield_0_bricks.kv3"));
        int Int(string key) => int.Parse(Regex.Match(text, $@"\b{key} = (\d+)").Groups[1].Value);
        var descriptors = Numbers().Matches(text[text.IndexOf("cell_descriptors", StringComparison.Ordinal)..]).Select(m => uint.Parse(m.Value)).ToArray();
        var all = Compare("env_farfield_0_indirection", LightmapTextures.FarfieldIndirectionVtex(descriptors, Int("cells_x"), Int("cells_y"), Int("cells_z"), searchPath), Valve("env_farfield_0_indirection"));
        foreach (var (image, name, format) in new[]
                 {
                     ("dc", "sh2_dc", LightmapTextures.ImageFormat.Bc6h), ("sh1_r", "sh2_l1r", LightmapTextures.ImageFormat.Bc7),
                     ("sh1_g", "sh2_l1g", LightmapTextures.ImageFormat.Bc7), ("sh1_b", "sh2_l1b", LightmapTextures.ImageFormat.Bc7),
                 })
        {
            var slices = Enumerable.Range(0, 33).Select(z => LightmapTextures.Bitmap(Vrad3Exr.Read(File.ReadAllBytes(Path.Combine(p[0], "ff", $"farfield_0_{image}_z{z:D2}.exr"))))).ToArray();
            var stacked = new RcTexture.RcMipChain.Level(slices[0].Width, slices[0].Height * 33, [.. slices.SelectMany(s => s.Rgba)]);
            all &= Compare($"env_farfield_0_{name}", LightmapTextures.FarfieldAtlasVtex(stacked, slices[0].Height, format, searchPath, native), Valve($"env_farfield_0_{name}"));
        }
        Assert.True(all);
    }

    private bool Compare(string name, byte[] ours, byte[] valve)
    {
        var at = 0;
        while (at < Math.Min(ours.Length, valve.Length) && ours[at] == valve[at])
            at++;
        output.WriteLine($"{name}: ours {ours.Length} bytes, valve {valve.Length}, first difference at {at}");
        var dump = Path.Combine(Path.GetTempPath(), "s2c_ffvtex");
        Directory.CreateDirectory(dump);
        File.WriteAllBytes(Path.Combine(dump, name + ".ours"), ours);
        File.WriteAllBytes(Path.Combine(dump, name + ".valve"), valve);
        return ours.AsSpan().SequenceEqual(valve);
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Numbers();
}
