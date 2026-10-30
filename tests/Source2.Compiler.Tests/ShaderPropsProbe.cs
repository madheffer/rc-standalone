using System.Reflection;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>SHADERPROPS=&lt;shader&gt;</c>): every non-empty list property on the shader's Vulkan programs,
/// with its count and the type of its items, to find where VRF keeps a layout.
/// </summary>
public class ShaderPropsProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("SHADERPROPS") is not { Length: > 0 } shader)
            return;
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game");
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"), Path.Combine(game, "core", "shaders_vulkan_dir.vpk")) { Platform = "vulkan_50" };
        var set = shaders.Collection(shader) ?? throw new FileNotFoundException(shader);
        foreach (var program in set.Programs.Prepend(set.Features))
            foreach (var p in program.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (p.GetValue(program) is System.Collections.ICollection list && list.Count > 0)
                    output.WriteLine($"{program.VcsProgramType} {p.Name}: {list.Count} x {list.Cast<object?>().First()?.GetType().Name}");
    }
}
