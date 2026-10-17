using Source2.Compiler.Maps;
using ValveResourceFormat.CompiledShader;
using ValveResourceFormat.IO;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Diagnostics for scenesystem's default hit group block (180098cd0): the shader attributes a
/// material's static combo declares (name, murmur, type, constant or expression, binding), for
/// the materials of a vrad3 job's scene_meshes.kv3 (<c>DEFAULTHIT=&lt;folder&gt;</c>).
/// </summary>
public class DefaultHitGroupTests(ITestOutputHelper output)
{
    [Fact]
    public void Attributes()
    {
        if (Environment.GetEnvironmentVariable("DEFAULTHIT") is not { } folder || CS2Fixtures.StockPak() is not { } pak)
            return;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        using var content = new GameContent(pak);
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_pc_dir.vpk"), Path.Combine(game, "core", "shaders_pc_dir.vpk"));
        var materials = SceneInfoTests.ReadBinary(Path.Combine(folder, "scene_meshes.kv3"))["meshes"].Values
            .Select(m => m["material_name"].ToString(null) ?? "").Distinct().ToList();
        foreach (var dflt in new[] { "materials/dev/rtx/hitgroup_default_opaque.vmat", "materials/dev/rtx/hitgroup_default_transparent.vmat" })
        {
            var d = content.Material(dflt);
            output.WriteLine($"{dflt}: shader {d?.Shader} params {string.Join(" ", d?.Params.Select(p => $"{p.Key}={p.Value}") ?? [])}");
            using var rtx = new Lighting.Bake.RtxShaderLibrary(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"), Path.Combine(game, "core", "shaders_vulkan_dir.vpk"));
            if (d != null && rtx.Combo(d) is var (program, combo))
                output.WriteLine($"  rtx {program.Shader} combo {combo}");
        }
        foreach (var name in materials)
        {
            var info = content.Material(name);
            output.WriteLine($"{name}: shader {info?.Shader}");
            if (info == null || shaders.Collection(info.Shader) is not { } set)
                continue;
            var features = (info.Params ?? new Dictionary<string, long>()).Where(p => p.Key.StartsWith("F_", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => (byte)p.Value);
            foreach (var program in set.Programs)
            {
                var (_, id) = ShaderDataProvider.GetStaticConfiguration_ForFeatureState(set.Features, program, features);
                if (!program.StaticComboEntries.ContainsKey(id))
                    continue;
                foreach (var a in program.GetStaticCombo(id).Attributes)
                {
                    var binding = a.VariableBinding >= 0 && a.VariableBinding < program.VariableDescriptions.Length ? program.VariableDescriptions[a.VariableBinding].Name : "";
                    output.WriteLine($"  {program.VcsProgramType} {a.Name} {a.Murmur32:x8} {a.VfxType} const {a.ConstValue} expr {(a.DynExpression is { } e ? ShaderUtilHelpers.ParseDynamicExpression(e) : "")} binding {binding}");
                }
            }
            output.WriteLine($"  textures {string.Join(" ", info.Textures.Select(t => $"{t.Key}={t.Value}"))}");
            output.WriteLine($"  vectors {string.Join(" ", info.Vectors.Select(t => $"{t.Key}=({string.Join(",", t.Value)})"))}");
            output.WriteLine($"  floats {string.Join(" ", info.Floats.Select(t => $"{t.Key}={t.Value}"))}");
        }
    }
}
