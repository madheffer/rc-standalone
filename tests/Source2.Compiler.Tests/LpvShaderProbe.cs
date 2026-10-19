using Source2.Compiler.Gpu;
using Source2.Compiler.Lighting.Bake;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary><c>LPVSHADER=&lt;program&gt;</c>: a ray tracing program's combos, the file each uses, and its reflected resources.</summary>
public class LpvShaderProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("LPVSHADER") is not { Length: > 0 } name || CS2Fixtures.StockPak() is not { } pak)
            return;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var shaders = new RtxShaderLibrary(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"), Path.Combine(game, "core", "shaders_vulkan_dir.vpk"));
        var program = shaders.Get(name) ?? throw new FileNotFoundException(name);
        var rtx = program.Rtx;
        output.WriteLine("static: " + string.Join(", ", rtx.StaticComboArray.Select(c => $"{c.Name} x{c.ComboIndexValue} range {c.RangeMin}..{c.RangeMax}")));
        output.WriteLine("dynamic: " + string.Join(", ", rtx.DynamicComboArray.Select(c => $"{c.Name} x{c.ComboIndexValue} range {c.RangeMin}..{c.RangeMax}")));
        var seen = new HashSet<string>();
        for (var st = 0; st < 64; st++)
        {
            ValveResourceFormat.CompiledShader.VfxStaticComboData combo;
            try { combo = rtx.GetStaticCombo(st); } catch (Exception) { continue; }
            for (var dy = 0; dy < 16; dy++)
            {
                var index = combo.GetDynamicComboIndex(dy);
                if (index < 0)
                    continue;
                var fileId = combo.DynamicComboRenderStates[index].ShaderFileId;
                var bytes = (fileId >= 0 && fileId < combo.ShaderFiles.Length ? combo.ShaderFiles[fileId] : combo.ShaderFiles[index]).Bytecode;
                var md5 = Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(bytes));
                output.WriteLine($"static {st} dynamic {dy}: file {fileId} md5 {md5}");
                if (!seen.Add(md5))
                    continue;
                var r = SpirvReflection.Parse(bytes);
                if (r.PushConstants is { } pc)
                    output.WriteLine($"    push constants {pc.TypeName} {pc.BlockSize} bytes: " + string.Join(" ", pc.Members.Select(m => $"{m.Name}@{m.Offset}:{m.Size}({m.Type})")));
                foreach (var res in r.Resources.OrderBy(x => x.Set).ThenBy(x => x.Binding))
                    output.WriteLine($"    set {res.Set} binding {res.Binding} {res.Kind} x{res.ArrayLength} {res.Name} ({res.TypeName} {res.Dim}) block {res.BlockSize}"
                                     + (res.Members.Count > 0 ? ": " + string.Join(" ", res.Members.Select(m => $"{m.Name}@{m.Offset}")) : ""));
            }
        }
    }
}
