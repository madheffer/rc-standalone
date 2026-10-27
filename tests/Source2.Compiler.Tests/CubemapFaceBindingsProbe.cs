using Source2.Compiler.Gpu;
using ValveResourceFormat.CompiledShader;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Diagnosis (CUBEBINDINGS=1): each program of a cubemap face, its SPIR-V's descriptor bindings beside the write
/// sequence's render-state entries (variable, register type, source, layout set, binding slot) and the constant
/// buffer descriptions, to read which variable a binding is from Valve's own tables.
/// </summary>
public sealed class CubemapFaceBindingsProbe(ITestOutputHelper output)
{
    [Fact]
    public void ListBindings()
    {
        if (Environment.GetEnvironmentVariable("CUBEBINDINGS") != "1")
            return;
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game");
        using var csgo = new ValvePak.Package();
        csgo.Read(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"));
        using var core = new ValvePak.Package();
        core.Read(Path.Combine(game, "core", "shaders_vulkan_dir.vpk"));
        void Dump(ValvePak.Package pak, string shader, string stage, Dictionary<string, int> statics, Dictionary<string, int> dynamics)
        {
            ValveProgram p;
            try
            {
                p = ValveProgram.Load(pak, shader, "vulkan_50", stage, statics, dynamics);
            }
            catch (Exception e)
            {
                output.WriteLine($"== {shader} {stage}: {e.Message}");
                return;
            }
            output.WriteLine($"== {p.Name}");
            foreach (var r in p.Reflection.Resources.OrderBy(r => r.Set).ThenBy(r => r.Binding))
                output.WriteLine($"  spirv set {r.Set} binding {r.Binding} {r.Kind} {r.Dim} size {r.BlockSize} x{r.ArrayLength}");
            if (p.Reflection.PushConstants is { } pc)
                output.WriteLine($"  spirv push {pc.BlockSize}");
            foreach (var f in p.Sequence.RenderState)
            {
                var v = p.Program.VariableDescriptions[f.VariableIndex];
                output.WriteLine($"  seq {v.Name} {v.RegisterType} src {v.VariableSource}/{v.SourceString} set {f.LayoutSet} slot {f.BindingSlot} ctl {f.Control} desc {v.DescriptorSet} reg {v.RegisterOffset}");
            }
            foreach (var c in p.Program.ExtConstantBufferDescriptions)
                output.WriteLine($"  cbuffer {c.Index} {c.Name} size {c.BufferSize} push {c.IsPushConstantBuffer} type {c.Type}");
        }
        foreach (var compressed in new[] { 0, 1 })
            Dump(csgo, "csgo_depth_only", "vs", [], new() { ["D_COMPRESSED_NORMALS_AND_TANGENTS"] = compressed });
        Dump(csgo, "generic", "vs", new() { ["S_MODE_TOOLS_VIS"] = 1 }, new() { ["D_BAKED_LIGHTING_FROM_LIGHTMAP"] = 1, ["D_COMPRESSED_NORMALS_AND_TANGENTS"] = 1 });
        Dump(csgo, "generic", "ps", new() { ["S_MODE_TOOLS_VIS"] = 1 }, new() { ["D_BAKED_LIGHTING_FROM_LIGHTMAP"] = 1 });
        Dump(csgo, "sky", "vs", [], []);
        Dump(csgo, "sky", "ps", [], new() { ["D_FOG_TYPE"] = 0 });
        Dump(core, "downsample_depth", "vs", [], []);
        Dump(core, "downsample_depth", "ps", [], new() { ["D_MINMAX_CHECKER_2X2"] = 1, ["D_NON_MSAA_DEPTH"] = 1 });
        Dump(core, "downsample_depth", "ps", [], new() { ["D_MAX"] = 1, ["D_NON_MSAA_DEPTH"] = 1 });
        foreach (var pak in new[] { core, csgo })
        {
            Dump(pak, "compute_depthbin_cullbits", "cs", [], []);
            Dump(pak, "compute_tile_cullbits", "cs", [], new() { ["D_SUBGROUP_SIZE_32"] = 1 });
        }
    }
}
