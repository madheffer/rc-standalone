using System.Reflection;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>SHADERVARS=&lt;shader&gt;|&lt;name regex&gt;</c>, e.g. <c>generic|Lightmap</c>): every variable
/// description of the shader's Vulkan programs whose name matches, with every field VRF parses (defaults included).
/// </summary>
public class ShaderVariableProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("SHADERVARS") is not { Length: > 0 } spec)
            return;
        var (shader, pattern) = spec.Split('|') is [var s, var p] ? (s, p) : (spec, ".");
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game");
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"), Path.Combine(game, "core", "shaders_vulkan_dir.vpk")) { Platform = "vulkan_50" };
        var set = shaders.Collection(shader) ?? throw new FileNotFoundException(shader);
        foreach (var program in set.Programs.Prepend(set.Features))
            foreach (var v in program.VariableDescriptions.Where(v => System.Text.RegularExpressions.Regex.IsMatch(v.Name, pattern)))
            {
                var fields = v.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(f => (f.Name, Value: f.GetValue(v)))
                    .Select(f => $"{f.Name}={(f.Value is System.Collections.IEnumerable e and not string ? "[" + string.Join(",", e.Cast<object>()) + "]" : f.Value)}");
                output.WriteLine($"{program.VcsProgramType}: {string.Join(" ", fields)}");
            }
    }
}
