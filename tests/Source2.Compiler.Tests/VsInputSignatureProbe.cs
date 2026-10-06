using Source2.Compiler.Maps;
using ValveResourceFormat.CompiledShader;
using ValveResourceFormat.IO;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>VSINPUT=&lt;material&gt;,...</c>): each material's vertex shader
/// static combo and the input signatures its dynamic combos use, by semantic.
/// </summary>
public class VsInputSignatureProbe(ITestOutputHelper output)
{
    [Fact]
    public void Signatures()
    {
        if (Environment.GetEnvironmentVariable("VSINPUT") is not { Length: > 0 } list || CS2Fixtures.StockPak() is not { } pak)
            return;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", "s2c_rc_probe"));
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_pc_dir.vpk"), Path.Combine(game, "core", "shaders_pc_dir.vpk"));
        foreach (var path in list.Split(','))
        {
            if (content.Material(path) is not { } info || shaders.Collection(info.Shader) is not { } set)
            {
                output.WriteLine($"{path}: no material or shader");
                continue;
            }
            var features = (info.Params ?? new Dictionary<string, long>()).Where(p => p.Key.StartsWith("F_", StringComparison.Ordinal))
                .ToDictionary(p => p.Key, p => (byte)p.Value);
            foreach (var program in set.Programs.Where(p => p.VcsProgramType == VcsProgramType.VertexShader))
            {
                var (config, id) = ShaderDataProvider.GetStaticConfiguration_ForFeatureState(set.Features, program, features);
                if (!program.StaticComboEntries.ContainsKey(id))
                {
                    output.WriteLine($"{path}: vs static combo {id} absent");
                    continue;
                }
                var combo = program.GetStaticCombo(id);
                var signatures = program.VsInputSignatures.Length > 0 ? program.VsInputSignatures : set.Features.VsInputSignatures;
                var used = combo.VsInputSignatureIndices.Distinct().ToList();
                output.WriteLine($"{path}: static {id}, {combo.VsInputSignatureIndices.Length} dynamic combos, signatures {string.Join(",", used)}");
                foreach (var i in used)
                    if (i >= 0 && i < signatures.Length)
                        output.WriteLine($"  [{i}] {string.Join(",", signatures[i].Elements.Select(e => e.Semantic))}");
            }
        }
    }
}
