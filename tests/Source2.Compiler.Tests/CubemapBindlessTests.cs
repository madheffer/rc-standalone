using System.Text.Json.Nodes;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The bindless set's inputs against a capture (<c>CUBEBINDLESS=&lt;a face's dump&gt;</c>): the 3D texture a foliage
/// draw (<c>CUBEBINDLESS_EID</c>, 585 by default: atixref face 0) reads at set 4 binding 46 must be the material's
/// <c>g_tNoiseMap</c> (<c>CUBEBINDLESS_MATERIAL</c>, materials/foliage/trees/bark/foliage_bark_pine_01.vmat by
/// default) mip for mip, as CubemapFaceRenderer uploads it (ReadTextureMipLevel per level).
/// </summary>
public sealed class CubemapBindlessTests(ITestOutputHelper output)
{
    [Fact]
    public void NoiseMapIsTheCapturesTexture()
    {
        if (Environment.GetEnvironmentVariable("CUBEBINDLESS") is not { Length: > 0 } dir)
            return;
        var eid = int.Parse(Environment.GetEnvironmentVariable("CUBEBINDLESS_EID") ?? "585", System.Globalization.CultureInfo.InvariantCulture);
        var materialPath = Environment.GetEnvironmentVariable("CUBEBINDLESS_MATERIAL") ?? "materials/foliage/trees/bark/foliage_bark_pine_01.vmat";
        var action = File.ReadLines(Path.Combine(dir, "actions.jsonl")).Select(l => JsonNode.Parse(l)!).First(a => (int?)a["eid"] == eid);
        var bound = action["stages"]!["vs"]!["readOnly"]!.AsArray().First(x => (int)x!["bind"]!["set"]! == 4 && (int)x["bind"]!["binding"]! == 46)!;
        var image = (string)bound["descriptor"]!["resource"]!;
        var captured = action["contents"]!["before"]![image]!.AsArray().ToDictionary(m => (int)m!["mip"]!, m => (string)m!["sha1"]!);

        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game");
        byte[] Read(string path)
        {
            foreach (var pak in new[] { Path.Combine(game, "csgo", "pak01_dir.vpk"), Path.Combine(game, "core", "pak01_dir.vpk") })
            {
                using var p = new ValvePak.Package();
                p.Read(pak);
                if (p.FindEntry(path + "_c") is { } e)
                {
                    p.ReadEntry(e, out var bytes);
                    return bytes;
                }
            }
            throw new FileNotFoundException(path);
        }
        using var material = new Resource();
        material.Read(new MemoryStream(Read(materialPath)));
        var noise = ((Material)material.DataBlock!).TextureParams["g_tNoiseMap"];
        using var texture = new Resource();
        texture.Read(new MemoryStream(Read(noise)));
        var tex = (Texture)texture.DataBlock!;
        output.WriteLine($"{image} ({bound["descriptor"]!["format"]}, {captured.Count} mips) against {noise}: {tex.Width} x {tex.Height} x {tex.Depth} {tex.Format}");
        var failures = new List<string>();
        foreach (var (level, _, _, _, size) in tex.GetEveryMipLevelMetrics())
        {
            var data = new byte[size];
            tex.ReadTextureMipLevel(data, level);
            var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(data));
            var equal = captured.GetValueOrDefault((int)level) == sha;
            output.WriteLine($"  mip {level}: {data.Length} bytes, {(equal ? "the capture's" : "DIFFERS")}");
            if (!equal)
                failures.Add($"mip {level}");
        }
        Assert.Equal(captured.Count, tex.GetEveryMipLevelMetrics().Count());
        Assert.Empty(failures);
    }
}
