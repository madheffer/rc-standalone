using Source2.Compiler.Gpu;
using ValvePak;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>Exploration: every constant a program's _Globals_ write sequence holds, with its source. <c>PROGCONST=&lt;shader&gt;|&lt;stage&gt;|&lt;S_X=v,...&gt;|&lt;D_X=v,...&gt;</c>.</summary>
public class ProgramConstantsProbe(ITestOutputHelper output)
{
    [Fact]
    public void Constants()
    {
        if (Environment.GetEnvironmentVariable("PROGCONST") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        static Dictionary<string, int> Parse(string s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(kv => kv.Split('=')).ToDictionary(kv => kv[0], kv => int.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture));
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game", "csgo");
        using var package = new Package();
        package.Read(Path.Combine(game, "shaders_vulkan_dir.vpk"));
        var prog = ValveProgram.Load(package, p[0], "vulkan_50", p[1], Parse(p[2]), Parse(p.Length > 3 ? p[3] : ""));
        output.WriteLine(prog.Name);
        foreach (var (name, offset, v) in prog.Constants.OrderBy(c => c.Offset))
        {
            string expr = "";
            try { expr = v.CompiledExpression.Length > 0 ? new ValveResourceFormat.Serialization.VfxEval.VfxEval(v.CompiledExpression).DynamicExpressionResult : ""; } catch (Exception e) { expr = "?" + e.GetType().Name; }
            output.WriteLine($"CONST +{offset} {name} {v.VfxType} src {v.VariableSource} '{v.SourceString}' i[{string.Join(",", v.IntDefs)}] f[{string.Join(",", v.FloatDefs)}] {expr}");
        }
        foreach (var (kind, fields) in new[] { ("EVAL", prog.Sequence.Evaluated), ("STATE", prog.Sequence.RenderState) })
            foreach (var f in fields)
            {
                var v = prog.Program.VariableDescriptions[f.VariableIndex];
                string expr = "";
                try { expr = v.CompiledExpression.Length > 0 ? new ValveResourceFormat.Serialization.VfxEval.VfxEval(v.CompiledExpression, features: prog.FeatureNames).DynamicExpressionResult : ""; } catch (Exception e) { expr = "?" + e.GetType().Name; }
                output.WriteLine($"{kind} set {f.LayoutSet} slot {f.BindingSlot} ctl {f.Control} {v.Name} {v.VfxType} {v.RegisterType} src {v.VariableSource} srcidx {v.SourceIndex} '{v.SourceString}' i[{string.Join(",", v.IntDefs)}] f[{string.Join(",", v.FloatDefs)}] {expr}");
            }
    }
}
