using Source2.Compiler.Gpu;
using ValveResourceFormat.CompiledShader;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Diagnosis (<c>CUBEPIPE=1</c>): the render state Valve's .vcs carries for each pixel program the cubemap face
/// draws with (the combos of docs/CUBEMAPS.md's module table), to set beside the captured pipelines.
/// </summary>
public sealed class CubemapPipelineProbe(ITestOutputHelper output)
{
    [Fact]
    public void RenderStates()
    {
        if (Environment.GetEnvironmentVariable("CUBEPIPE") != "1")
            return;
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game");
        using var csgo = new ValvePak.Package();
        csgo.Read(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"));
        using var core = new ValvePak.Package();
        core.Read(Path.Combine(game, "core", "shaders_vulkan_dir.vpk"));
        (string Name, ValvePak.Package Pak, string Shader, Dictionary<string, int> Statics, Dictionary<string, int> Dynamics)[] programs =
        [
            ("generic colour", csgo, "generic", new() { ["S_MODE_TOOLS_VIS"] = 1 }, new() { ["D_BAKED_LIGHTING_FROM_LIGHTMAP"] = 1 }),
            ("sky", csgo, "sky", [], new() { ["D_FOG_TYPE"] = 0 }),
            ("downsample half", core, "downsample_depth", [], new() { ["D_MINMAX_CHECKER_2X2"] = 1, ["D_NON_MSAA_DEPTH"] = 1 }),
            ("downsample quarter", core, "downsample_depth", [], new() { ["D_MAX"] = 1, ["D_NON_MSAA_DEPTH"] = 1 }),
        ];
        foreach (var (name, pak, shader, statics, dynamics) in programs)
        {
            var p = ValveProgram.Load(pak, shader, "vulkan_50", "ps", statics, dynamics);
            output.WriteLine($"{name}: {p.Name}");
            if (p.RenderState is not VfxRenderStateInfoPixelShader ps)
            {
                output.WriteLine("  no pixel render state");
                continue;
            }
            if (ps.RasterizerStateDesc is { } r)
                output.WriteLine($"  raster fill {r.FillMode} cull {r.CullMode} clip {r.DepthClipEnable} ms {r.MultisampleEnable} bias {r.DepthBias} clamp {r.DepthBiasClamp} slope {r.SlopeScaledDepthBias}");
            if (ps.DepthStencilStateDesc is { } d)
                output.WriteLine($"  depth test {d.DepthTestEnable} write {d.DepthWriteEnable} func {d.DepthFunc} stencil {d.StencilEnable} read {d.StencilReadMask} write {d.StencilWriteMask} front {d.FrontStencilFunc} {d.FrontStencilFailOp}/{d.FrontStencilDepthFailOp}/{d.FrontStencilPassOp} back {d.BackStencilFunc} {d.BackStencilFailOp}/{d.BackStencilDepthFailOp}/{d.BackStencilPassOp}");
            // CUBEPIPE_SLOTS=1: every texture and sampler variable the combo binds, by set and slot.
            if (Environment.GetEnvironmentVariable("CUBEPIPE_SLOTS") == "1")
                foreach (var f in p.Sequence.RenderState)
                {
                    var v = p.Program.VariableDescriptions[f.VariableIndex];
                    output.WriteLine($"  slot set {f.LayoutSet} binding {f.BindingSlot}: {v.Name} {v.RegisterType} {v.VariableSource} '{v.SourceString}' default '{v.DefaultInputTexture}'");
                }
            if (ps.BlendStateDesc is { } b)
                output.WriteLine($"  blend {b.BlendEnable[0]} {b.SrcBlend[0]}/{b.DestBlend[0]}/{b.BlendOp[0]} alpha {b.SrcBlendAlpha[0]}/{b.DestBlendAlpha[0]}/{b.BlendOpAlpha[0]} mask {b.RenderTargetWriteMask[0]} srgb {b.SrgbWriteEnable[0]} a2c {b.AlphaToCoverageEnable}");
        }
    }
}
