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
            if (name.StartsWith("modes:", StringComparison.Ordinal))
            {
                // modes:<shader>: the features file's modes (name, fallback shader, static combo and value).
                var f = lib.FeaturesOf(name[6..]) ?? throw new FileNotFoundException(name);
                foreach (var m in f.FeaturesHeader!.Modes)
                    output.WriteLine($"{name}: mode {m.Name} shader '{m.ShaderFallback}' static '{m.StaticComboName}' = {m.StaticComboValue}");
                continue;
            }
            if (name.StartsWith("writes:", StringComparison.Ordinal) && name[7..].Split('@') is [var sh, var cb])
            {
                // writes:<shader>@<static combo>: the rtx program's write sequence for that combo.
                var prog = lib.Get(sh) ?? throw new FileNotFoundException(sh);
                var vars = prog.Rtx.VariableDescriptions;
                foreach (var f in prog.Rtx.GetStaticCombo(long.Parse(cb, System.Globalization.CultureInfo.InvariantCulture)).AllVariables.Fields)
                {
                    var d = vars[f.VariableIndex];
                    output.WriteLine($"{name}: {d.Name} reg {f.RegisterOffset} type {d.VfxType} source {d.VariableSource} regtype {d.RegisterType} ctx {d.ContextStateAffectedByVariable} srcidx {d.SourceIndex} regoff {d.RegisterOffset} bits {d.TypeSpecificBits} ints [{string.Join(",", d.IntDefs)}]");
                }
                continue;
            }
            if (name.StartsWith("file:", StringComparison.Ordinal) && name[5..].Split('@') is [var fsh, var fsuffix])
            {
                // file:<shader>@<level_suffix> (e.g. lightmap_rasterize@50_ps): combos, files, entries, bindings.
                var level = fsuffix[..2];
                var prog = lib.ProgramFile50(fsh, fsuffix) ?? throw new FileNotFoundException(name);
                output.WriteLine($"{name}: statics [{string.Join(", ", prog.StaticComboArray.Select(c => $"{c.Name} {c.RangeMin}..{c.RangeMax}"))}] dynamics [{string.Join(", ", prog.DynamicComboArray.Select(c => $"{c.Name} {c.RangeMin}..{c.RangeMax}"))}]");
                foreach (var id in prog.StaticComboEntries.Keys.Order())
                {
                    var combo = prog.GetStaticCombo(id);
                    foreach (var rs in combo.DynamicComboRenderStates)
                        if (rs is ValveResourceFormat.CompiledShader.VfxRenderStateInfoPixelShader ps)
                            output.WriteLine($"  static {id} dynamic {rs.DynamicComboId} file {rs.ShaderFileId}: raster {ps.RasterizerStateDesc} depth {ps.DepthStencilStateDesc?.DepthTestEnable} write {ps.DepthStencilStateDesc?.DepthWriteEnable} func {ps.DepthStencilStateDesc?.DepthFunc} blend {ps.BlendStateDesc?.BlendEnable[0]} mask {ps.BlendStateDesc?.RenderTargetWriteMask[0]}");
                        else
                            output.WriteLine($"  static {id} dynamic {rs.DynamicComboId} file {rs.ShaderFileId}");
                    for (var f = 0; f < combo.ShaderFiles.Length; f++)
                    {
                        var r = SpirvReflection.Parse(combo.ShaderFiles[f].Bytecode);
                        foreach (var inp in r.Inputs)
                            output.WriteLine($"    input location {inp.Location} {inp.Name} {inp.Type} x{inp.Components}");
                        output.WriteLine($"  static {id} file {f}: entries [{string.Join(", ", r.EntryPoints.Select(e => $"{e.Model}:{e.Name}"))}] {string.Join("; ", r.Resources.Select(x => $"set {x.Set} b {x.Binding} {x.Kind} {x.Name} block {x.BlockSize}"))}");
                    }
                }
                continue;
            }
            if (name.StartsWith("expr:", StringComparison.Ordinal) && name[5..].Split('@') is [var esh, var evar])
            {
                // expr:<shader>@<variable>: its compiled expressions as VRF prints them.
                var prog = lib.Get(esh) ?? throw new FileNotFoundException(esh);
                foreach (var d in prog.Rtx.VariableDescriptions.Where(v => v.Name == evar))
                    output.WriteLine($"{name}: {d.VariableSource} {d.VfxType}: {(d.CompiledExpression.Length > 0 ? ValveResourceFormat.CompiledShader.ShaderUtilHelpers.ParseDynamicExpression(d.CompiledExpression) : "(none)")}");
                continue;
            }
            if (name.StartsWith("vars:", StringComparison.Ordinal))
            {
                // vars:<shader>: a compute program's variables with their declared int ranges and defaults.
                var cs = lib.ProgramFile(name[5..], "cs") ?? throw new FileNotFoundException(name);
                foreach (var d in cs.VariableDescriptions)
                    output.WriteLine($"{name}: {d.Name} {d.VfxType} source {d.VariableSource} ints def [{string.Join(",", d.IntDefs)}] min [{string.Join(",", d.IntMins)}] max [{string.Join(",", d.IntMaxs)}]");
                continue;
            }
            if (name.StartsWith("cs:", StringComparison.Ordinal))
            {
                // cs:<shader>: a compute program's static combo 0 bindings.
                var cs = lib.ProgramFile(name[3..], "cs") ?? throw new FileNotFoundException(name);
                var spirv = cs.GetStaticCombo(cs.StaticComboEntries.Keys.Min()).ShaderFiles[0].Bytecode;
                var r = SpirvReflection.Parse(spirv);
                output.WriteLine($"{name}: entries [{string.Join(", ", r.EntryPoints.Select(e => $"{e.Model}:{e.Name}"))}] caps [{string.Join(", ", r.Capabilities)}]");
                foreach (var res in r.Resources)
                    output.WriteLine($"  set {res.Set} binding {res.Binding} {res.Kind} x{res.ArrayLength} {res.Name} block {res.BlockSize} [{string.Join(", ", res.Members.Select(m => $"{m.Name}@{m.Offset}"))}]");
                continue;
            }
            if (name.StartsWith("attrs:", StringComparison.Ordinal))
            {
                // attrs:<shader>: the attributes its vulkan vs / ps programs declare for static combo 0.
                var info = new Source2.Compiler.Maps.SettleWorld.MaterialInfo(new Dictionary<string, long>(), new Dictionary<string, string>()) { Shader = name[6..] + ".vfx" };
                foreach (var at in lib.Attributes(info))
                    output.WriteLine($"{name}: {at.Name} 0x{at.Murmur:x8} = {at.Constant ?? at.Expression} binding {at.Binding}");
                continue;
            }
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
