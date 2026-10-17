using Source2.Compiler.Gpu;
using Source2.Compiler.Lighting.Bake;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Checks for vrad3's lightmap bake on the GPU (<see cref="RtxBakeHost"/>, Valve's own ray tracing
/// SPIR-V). The block bake itself runs in CpuBakeTests.Bake with CPUBAKE_GPU=1.
/// </summary>
public class GpuBakeTests(ITestOutputHelper output)
{
    /// <summary>
    /// <c>RTXPROBE=&lt;shader&gt;[,&lt;shader&gt;...]</c>: each rtx program's static combos, their
    /// shader files and every file's SPIR-V entry points and resources.
    /// </summary>
    [Fact]
    public void Probe()
    {
        if (Environment.GetEnvironmentVariable("RTXPROBE") is not { Length: > 0 } names || CS2Fixtures.StockPak() is not { } pak)
            return;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        using var lib = new RtxShaderLibrary(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"), Path.Combine(game, "core", "shaders_vulkan_dir.vpk"));
        foreach (var name in names.Split(','))
        {
            var p = lib.Get(name) ?? throw new FileNotFoundException(name);
            output.WriteLine($"{name}: statics [{string.Join(", ", p.Rtx.StaticComboArray.Select(c => $"{c.Name} {c.RangeMin}..{c.RangeMax}"))}] dynamics [{string.Join(", ", p.Rtx.DynamicComboArray.Select(c => $"{c.Name} {c.RangeMin}..{c.RangeMax}"))}]");
            foreach (var id in p.Rtx.StaticComboEntries.Keys.Order().Take(4))
            {
                var combo = p.Rtx.GetStaticCombo(id);
                output.WriteLine($"  static {id}: {combo.ShaderFiles.Length} files, {combo.DynamicComboRenderStates.Length} dynamic combos");
                for (var f = 0; f < combo.ShaderFiles.Length; f++)
                {
                    var bytes = combo.ShaderFiles[f].Bytecode;
                    if (bytes.Length < 20 || BitConverter.ToUInt32(bytes, 0) != 0x07230203)
                    {
                        output.WriteLine($"    file {f}: {bytes.Length} bytes, not SPIR-V");
                        continue;
                    }
                    var r = SpirvReflection.Parse(bytes);
                    output.WriteLine($"    file {f}: {bytes.Length} bytes, entries [{string.Join(", ", r.EntryPoints.Select(e => $"{e.Model}:{e.Name}"))}]");
                    output.WriteLine($"      capabilities [{string.Join(", ", r.Capabilities)}] extensions [{string.Join(", ", r.Extensions)}]");
                    foreach (var res in r.Resources.OrderBy(x => x.Set).ThenBy(x => x.Binding))
                        output.WriteLine($"      set {res.Set} binding {res.Binding} {res.Kind} x{res.ArrayLength} {res.Name} {res.TypeName} block {res.BlockSize}");
                }
            }
        }
    }
}
