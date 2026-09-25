using ValvePak;
using ValveResourceFormat.CompiledShader;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: the attributes a compiled shader declares (shaders_pc_dir.vpk),
/// read from each static combo of its vs and ps programs and filtered to names
/// holding a word, for finding a shader's vertex paint layer setup.
/// <c>SHADERATTR=&lt;shader name&gt;[,...]|&lt;word&gt;[,...]</c>; "*" scans every
/// shader's vs and ps, and a word may be a murmur ("0x...").
/// CS2's shaders are VCS version 72, which the pinned VRF cannot read: apply
/// upstream commit cbca49a (its ValveResourceFormat/ part) to the vendored copy
/// first, or re-vendor past it.
/// </summary>
public class ShaderAttributeProbe(ITestOutputHelper output)
{
    [Fact]
    public void AttributesOfShaders()
    {
        if (Environment.GetEnvironmentVariable("SHADERATTR") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game", "csgo");
        using var package = new Package();
        package.Read(Path.Combine(game, "shaders_pc_dir.vpk"));
        // "*" scans every shader's vs program; the word may then be a murmur ("0x...").
        var names = parts[0] == "*"
            ? package.Entries!["vcs"].Select(e => e.FileName).Where(n => n.EndsWith("_pc_50_vs")).Select(n => n[..^"_pc_50_vs".Length])
            : parts[0].Split(',');
        foreach (var shader in names.SelectMany(x => parts[0] == "*" ? new[] { x + "_pc_50_vs", x + "_pc_50_ps" } : new[] { x + "_pc_50_features", x + "_pc_50_vs", x + "_pc_50_ps" }))
        {
            var entry = package.FindEntry($"shaders/vfx/{shader}.vcs");
            if (entry == null)
            {
                output.WriteLine($"{shader}: no file");
                continue;
            }
            package.ReadEntry(entry, out var bytes);
            using var program = new VfxProgramData();
            try
            {
                program.Read($"{shader}.vcs", new MemoryStream(bytes));
            }
            catch (Exception e)
            {
                output.WriteLine($"{shader}: {e.GetType().Name} {e.Message}");
                continue;
            }
            var seen = new Dictionary<string, HashSet<string>>();
            output.WriteLine($"== {shader} static combos: {string.Join(" ", program.StaticComboArray.Select(c => $"{c.Name}[{c.RangeMin}..{c.RangeMax}]x{c.ComboIndexValue}"))}");
            foreach (var id in program.StaticComboEntries.Keys)
            {
                var combo = program.GetStaticCombo(id);
                var config = string.Join(" ", program.StaticComboArray.Select(c => $"{c.Name}={id / Math.Max(1, c.ComboIndexValue) % (c.RangeMax - c.RangeMin + 1) + c.RangeMin}"));
                foreach (var attribute in combo.Attributes)
                {
                    if (!parts[1].Split(',').Any(w => attribute.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || w == $"0x{attribute.Murmur32:x8}"))
                        continue;
                    var key = $"{attribute.Name} 0x{attribute.Murmur32:x8} {attribute.VfxType}";
                    if (!seen.TryGetValue(key, out var values))
                        seen[key] = values = [];
                    values.Add(attribute.DynExpression != null ? attribute.ToString().Trim() : $"{attribute.ConstValue} @ {config}");
                }
            }
            output.WriteLine($"== {shader}: {program.StaticComboEntries.Count} static combos");
            foreach (var (key, values) in seen.OrderBy(x => x.Key))
                foreach (var value in values.Order())
                    output.WriteLine($"{key}: {value}");
        }
    }
}
