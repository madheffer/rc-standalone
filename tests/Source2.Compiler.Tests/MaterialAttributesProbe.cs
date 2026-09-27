using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: a material's string and int attributes as the map builders
/// read them (csgo and core paks, loose addon files).
/// <c>MATATTRS=&lt;addon&gt;|&lt;material&gt;[;&lt;material&gt;...]</c>.
/// </summary>
public class MaterialAttributesProbe(ITestOutputHelper output)
{
    [Fact]
    public void Attributes()
    {
        if (Environment.GetEnvironmentVariable("MATATTRS") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        var game = Path.Combine(cs2, "game");
        using var models = new SettleBuildTests.PakModels(Path.Combine(game, "csgo", "pak01_dir.vpk"), Path.Combine(game, "csgo_addons", p[0]));
        foreach (var name in p[1].Split(';'))
        {
            var info = models.Material(name);
            output.WriteLine($"{name}: {(info == null ? "not found" : $"shader {info.Shader}")}");
            if (info == null)
                continue;
            foreach (var (k, v) in info.Strings)
                output.WriteLine($"  str {k} = {v}");
            foreach (var (k, v) in info.Ints)
                output.WriteLine($"  int {k} = {v}");
            foreach (var (k, v) in info.Params)
                output.WriteLine($"  param {k} = {v}");
        }
    }
}
