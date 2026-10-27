using Source2.Compiler.Gpu;
using ValveResourceFormat.CompiledShader;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>Diagnosis (CUBEMODES=1): each shader's render modes (features file) and which depth combos exist.</summary>
public sealed class CubemapModesProbe(ITestOutputHelper output)
{
    [Fact]
    public void ListModes()
    {
        if (Environment.GetEnvironmentVariable("CUBEMODES") != "1")
            return;
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game");
        using var csgo = new ValvePak.Package();
        csgo.Read(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"));
        foreach (var shader in new[] { "generic", "csgo_foliage", "csgo_environment_blend", "csgo_environment", "csgo_complex", "csgo_vertexlitgeneric", "csgo_lightmappedgeneric", "csgo_depth_only", "csgo_simple_2way_blend", "sky" })
        {
            var entry = csgo.FindEntry($"shaders/vfx/{shader}_vulkan_50_features.vcs");
            if (entry == null) { output.WriteLine($"{shader}: no features"); continue; }
            csgo.ReadEntry(entry, out var bytes);
            using var f = new VfxProgramData();
            f.Read($"{shader}_vulkan_50_features.vcs", new MemoryStream(bytes));
            output.WriteLine($"{shader}: " + string.Join("; ", f.FeaturesHeader!.Modes.Select(m => $"{m.Name}=>{m.ShaderFallback}/{m.StaticComboName}={m.StaticComboValue}")));
            foreach (var stage in new[] { "vs", "ps" })
                foreach (var (statics, label) in new[] { (new Dictionary<string, int> { ["S_MODE_DEPTH"] = 1 }, "S_MODE_DEPTH=1"), (new Dictionary<string, int>(), "none"), (new Dictionary<string, int> { ["S_ALPHA_TEST"] = 1 }, "S_ALPHA_TEST=1") })
                {
                    try
                    {
                        var p = ValveProgram.Load(csgo, shader, "vulkan_50", stage, statics, new Dictionary<string, int>());
                        if (stage == "vs" || stage == "ps")
                            output.WriteLine($"   attrs {stage} {label}: {string.Join(" ", p.Attributes.Select(at => $"{at.Name}={(at.ConstValue ?? at.DynExpression?.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?")}"))}");
                        output.WriteLine($"   {stage} {label}: {p.Name} {p.Spirv.Length} bytes; file ids {string.Join(",", p.Program.GetStaticCombo(long.Parse(p.Name.Split(" static ")[1].Split(" ")[0], System.Globalization.CultureInfo.InvariantCulture)).DynamicComboRenderStates.Take(8).Select(r => r.ShaderFileId))}; files {p.Program.GetStaticCombo(long.Parse(p.Name.Split(" static ")[1].Split(" ")[0], System.Globalization.CultureInfo.InvariantCulture)).ShaderFiles.Length}");
                    }
                    catch (Exception e) { output.WriteLine($"   {stage} {label}: {e.GetType().Name} {e.Message}"); }
                }
        }
    }
}
