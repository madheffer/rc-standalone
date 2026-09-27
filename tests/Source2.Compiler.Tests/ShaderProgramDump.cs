using ValvePak;
using ValveResourceFormat.CompiledShader;
using Vortice.SpirvCross;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: one static combo of a shader's vs and ps (shaders_&lt;platform&gt;_dir.vpk),
/// its dynamic combos, and every program file in it saved to a folder: the
/// bytecode, and for Vulkan the SPIR-V decompiled to GLSL with Valve's names.
/// Static combos not listed are taken as 0. Needs VCS 72 support in the
/// vendored VRF (see <see cref="ShaderAttributeProbe"/>).
/// <c>SHADERDUMP=&lt;shader&gt;|&lt;platform: pc_50, vulkan_50&gt;|&lt;S_X=v,...&gt;|&lt;out folder&gt;[|&lt;dynamic D_X=v,...&gt;]</c>.
/// </summary>
public class ShaderProgramDump(ITestOutputHelper output)
{
    [Fact]
    public void DumpCombo()
    {
        if (Environment.GetEnvironmentVariable("SHADERDUMP") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var shader = parts[0];
        var platform = parts[1];
        var settings = Parse(parts[2]);
        var dynamic = parts.Length > 4 ? Parse(parts[4]) : null;
        var outDir = parts[3];
        Directory.CreateDirectory(outDir);
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game", "csgo");
        using var package = new Package();
        package.Read(Path.Combine(game, $"shaders_{platform.Split('_')[0]}_dir.vpk"));
        foreach (var stage in new[] { "vs", "ps" })
        {
            var name = $"{shader}_{platform}_{stage}";
            var entry = package.FindEntry($"shaders/vfx/{name}.vcs");
            if (entry == null)
            {
                output.WriteLine($"{name}: no file");
                continue;
            }
            package.ReadEntry(entry, out var bytes);
            using var program = new VfxProgramData();
            program.Read($"{name}.vcs", new MemoryStream(bytes));
            // "*=1" in the settings: every static combo with the listed switches set, first dynamic file only.
            var wanted = new Dictionary<string, int>(settings);
            if (wanted.Remove("*", out _))
            {
                foreach (var cid in program.StaticComboEntries.Keys.Order())
                {
                    var state = program.StaticComboArray.ToDictionary(c => c.Name, c => (int)(cid / Math.Max(1, c.ComboIndexValue) % (c.RangeMax - c.RangeMin + 1)) + c.RangeMin);
                    if (wanted.Any(kv => state.GetValueOrDefault(kv.Key, -1) != kv.Value))
                        continue;
                    var sc = program.GetStaticCombo(cid);
                    var first = sc.ShaderFiles.FirstOrDefault(x => !x.IsEmpty());
                    if (first is VfxShaderFileVulkan fvk && ShaderSpirvReflection.ReflectSpirv(fvk, Backend.GLSL, out var code))
                        File.WriteAllText(Path.Combine(outDir, $"{name}_{cid}.glsl"), code);
                    output.WriteLine($"COMBO {name} {cid}: {string.Join(" ", state.Where(kv => kv.Value != 0).Select(kv => $"{kv.Key}={kv.Value}"))}");
                }
                continue;
            }
            long id = 0;
            foreach (var c in program.StaticComboArray)
                id += (settings.GetValueOrDefault(c.Name, 0) - c.RangeMin) * c.ComboIndexValue;
            output.WriteLine($"== {name}: static {string.Join(" ", program.StaticComboArray.Select(c => $"{c.Name}={settings.GetValueOrDefault(c.Name, 0)}"))} -> id {id}; exists {program.StaticComboEntries.ContainsKey(id)}");
            output.WriteLine($"   dynamic combos: {string.Join(" ", program.DynamicComboArray.Select(c => $"{c.Name}[{c.RangeMin}..{c.RangeMax}]x{c.ComboIndexValue}"))}");
            if (!program.StaticComboEntries.ContainsKey(id))
                continue;
            var combo = program.GetStaticCombo(id);
            output.WriteLine($"   {combo.ShaderFiles.Length} program files, {combo.DynamicComboVariables.Length} dynamic variable sets");
            long? wantDynamic = null;
            if (dynamic != null)
            {
                wantDynamic = 0;
                foreach (var c in program.DynamicComboArray)
                    wantDynamic += (dynamic.GetValueOrDefault(c.Name, 0) - c.RangeMin) * c.ComboIndexValue;
                output.WriteLine($"   dynamic id {wantDynamic} -> file index {combo.GetDynamicComboIndex(wantDynamic.Value)}");
            }
            // The variables this combo writes, by name.
            var vars = combo.AllVariables.Fields.Select(f => program.VariableDescriptions[f.VariableIndex].Name).ToList();
            File.WriteAllLines(Path.Combine(outDir, $"{name}_{id}_variables.txt"), vars);
            for (var i = 0; i < combo.ShaderFiles.Length; i++)
            {
                if (wantDynamic != null && i != combo.GetDynamicComboIndex(wantDynamic.Value))
                    continue;
                var file = combo.ShaderFiles[i];
                if (file.IsEmpty())
                    continue;
                var stem = Path.Combine(outDir, $"{name}_{id}_{i}");
                File.WriteAllBytes(stem + "." + file.SourceType.ToLowerInvariant(), file.Bytecode);
                if (file is VfxShaderFileVulkan vk && ShaderSpirvReflection.ReflectSpirv(vk, Backend.GLSL, out var glsl))
                    File.WriteAllText(stem + ".glsl", glsl);
                output.WriteLine($"   file {i}: {file.SourceType} {file.Bytecode.Length} bytes");
            }
        }
    }

    private static Dictionary<string, int> Parse(string s)
        => s.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(kv => kv.Split('=')).ToDictionary(kv => kv[0], kv => int.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture));
}
