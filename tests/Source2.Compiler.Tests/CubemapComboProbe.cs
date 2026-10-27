using Source2.Compiler.Gpu;
using ValveResourceFormat.CompiledShader;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>Diagnosis (CUBECOMBO=&lt;material&gt;;&lt;stage&gt;;&lt;statics a=1,b=2&gt;): a material's static combo, its existence and its dynamic combos' file ids.</summary>
public sealed class CubemapComboProbe(ITestOutputHelper output)
{
    [Fact]
    public void Combo()
    {
        if (Environment.GetEnvironmentVariable("CUBECOMBO") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split(';');
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game");
        using var pak = new ValvePak.Package();
        pak.Read(Path.Combine(game, "csgo", "pak01_dir.vpk"));
        pak.ReadEntry(pak.FindEntry(parts[0] + "_c")!, out var mb);
        using var mr = new ValveResourceFormat.Resource();
        mr.Read(new MemoryStream(mb));
        var material = (Material)mr.DataBlock!;
        output.WriteLine($"shader {material.ShaderName}; F_ params: {string.Join(" ", material.IntParams.Where(p => p.Key.StartsWith("F_", StringComparison.Ordinal)).Select(p => $"{p.Key}={p.Value}"))}");
        using var shaders = new ValvePak.Package();
        shaders.Read(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"));
        var shader = Path.GetFileNameWithoutExtension(material.ShaderName);
        var statics = parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=')).ToDictionary(x => x[0], x => int.Parse(x[1], System.Globalization.CultureInfo.InvariantCulture));
        var entry = shaders.FindEntry($"shaders/vfx/{shader}_vulkan_50_{parts[1]}.vcs")!;
        shaders.ReadEntry(entry, out var bytes);
        using var program = new VfxProgramData();
        program.Read(entry.FileName + ".vcs", new MemoryStream(bytes));
        var fe = shaders.FindEntry($"shaders/vfx/{shader}_vulkan_50_features.vcs")!;
        shaders.ReadEntry(fe, out var fb);
        using var features = new VfxProgramData();
        features.Read(fe.FileName + ".vcs", new MemoryStream(fb));
        long id = 0;
        foreach (var c in program.StaticComboArray)
        {
            var value = statics.TryGetValue(c.Name, out var given) ? given
                : c.FeatureIndex >= 0 ? (int)material.IntParams.GetValueOrDefault(features.StaticComboArray[c.FeatureIndex].Name, 0) : 0;
            output.WriteLine($"  static {c.Name} range {c.RangeMin}..{c.RangeMax} feature {(c.FeatureIndex >= 0 ? features.StaticComboArray[c.FeatureIndex].Name : "-")} src {c.ComboSourceType} cmp {c.FeatureComparisonValue} -> {value}");
            id += (value - c.RangeMin) * c.ComboIndexValue;
        }
        output.WriteLine($"static id {id}: {(program.StaticComboEntries.ContainsKey(id) ? "present" : "ABSENT")}");
        foreach (var rule in program.StaticComboRules)
            output.WriteLine($"  rule {rule}");
        foreach (var c in program.DynamicComboArray)
            output.WriteLine($"  dynamic {c.Name} range {c.RangeMin}..{c.RangeMax} index {c.ComboIndexValue}");
        if (program.StaticComboEntries.ContainsKey(id))
        {
            var combo = program.GetStaticCombo(id);
            output.WriteLine($"  dynamic file ids: {string.Join(",", combo.DynamicComboRenderStates.Select(r => $"{r.DynamicComboId}:{r.ShaderFileId}"))}");
        }
    }
}
