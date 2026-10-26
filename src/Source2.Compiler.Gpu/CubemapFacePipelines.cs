using Silk.NET.Vulkan;
using ValveResourceFormat.CompiledShader;

namespace Source2.Compiler.Gpu;

/// <summary>
/// The fixed-function state of a cubemap face's draws (docs/CUBEMAPS.md, "The pipelines"; ledger 150), from Valve's
/// program and the pass that draws it: a pixel program's .vcs render state (its rasterizer, depth-stencil and blend
/// descriptors for the picked dynamic combo) and what the engine sets on top for the pass. Checked field by field
/// against every world, sky and downsample draw of three captures (CubemapFaceReplayTests.ChainedCubeMakesTheGamesExr
/// draws with it).
/// </summary>
public static class CubemapFacePipelines
{
    /// <summary>The face's passes, in their order within a face.</summary>
    public enum Pass
    {
        /// <summary>A sun cascade's depth into the shadow atlas (csgo_depth_only, no pixel program).</summary>
        SunShadow,

        /// <summary>The depth prepass (csgo_depth_only, no pixel program).</summary>
        DepthPrepass,

        /// <summary>The half resolution depth (downsample_depth, D_MINMAX_CHECKER_2X2), a 4-vertex strip.</summary>
        DownsampleHalf,

        /// <summary>The world's colour (generic in ToolsVis).</summary>
        Colour,

        /// <summary>The sky cube.</summary>
        Sky,

        /// <summary>The quarter resolution depth (downsample_depth, D_MAX), a 4-vertex strip.</summary>
        DownsampleQuarter,
    }

    /// <summary>
    /// sc_shadow_slopescale_depth_bias's default (scenesystem 180006d50, the float stored as the registration's
    /// default): the sun cascades' slope-scaled bias. Their constant bias and clamp are 0 (sc_shadow_depth_bias_clamp
    /// defaults to 0, 180006e00).
    /// </summary>
    public const float ShadowSlopeBias = 2.13f;

    /// <summary>
    /// <paramref name="template"/> (programs, vertex layout, viewport and scissor rectangle, target formats) with the
    /// pass's fixed-function state. <paramref name="pixel"/> is the pixel program's render state; null for the
    /// depth-only passes, which have none.
    /// </summary>
    public static SceneDrawState Apply(SceneDrawState template, Pass pass, VfxRenderStateInfoPixelShader? pixel)
    {
        var state = template with
        {
            Topology = pass is Pass.DownsampleHalf or Pass.DownsampleQuarter ? PrimitiveTopology.TriangleStrip : PrimitiveTopology.TriangleList,
            // Every pipeline has depth bias on (a dynamic state the game always sets) and counter-clockwise front faces.
            Front = FrontFace.CounterClockwise,
            DepthBias = true,
            DepthBiasConstant = 0f,
            DepthBiasSlope = pass == Pass.SunShadow ? ShadowSlopeBias : 0f,
            DepthBiasClamp = 0f,
            DepthClamp = false,
            StencilTest = false,
            StencilFront = Keep(CompareOp.Always, 255, 255, 0),
            StencilBack = Keep(CompareOp.Always, 255, 255, 0),
            Blend = null,
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            Viewport = template.Viewport with
            {
                MinDepth = pass == Pass.Sky ? 1f : 0f,
                MaxDepth = pass is Pass.Colour or Pass.DepthPrepass ? 0.95f : 1f,
            },
        };
        if (pixel == null)
        {
            // Depth only: back faces culled, depth tested less-or-equal and written.
            if (pass is not (Pass.SunShadow or Pass.DepthPrepass))
                throw new ArgumentException($"{pass} needs its pixel program's render state", nameof(pixel));
            return state with { Cull = CullModeFlags.BackBit, DepthTest = true, DepthWrite = true, DepthCompare = CompareOp.LessOrEqual };
        }
        var r = pixel.RasterizerStateDesc ?? throw new InvalidDataException("no rasterizer state");
        var d = pixel.DepthStencilStateDesc ?? throw new InvalidDataException("no depth-stencil state");
        var b = pixel.BlendStateDesc ?? throw new InvalidDataException("no blend state");
        state = state with
        {
            Cull = r.CullMode switch { RsCullMode.None => CullModeFlags.None, RsCullMode.Back => CullModeFlags.BackBit, RsCullMode.Front => CullModeFlags.FrontBit, var c => throw new NotSupportedException($"cull {c}") },
            DepthBiasConstant = state.DepthBiasConstant + r.DepthBias,
            DepthBiasSlope = state.DepthBiasSlope + r.SlopeScaledDepthBias,
            DepthBiasClamp = r.DepthBiasClamp,
            DepthTest = d.DepthTestEnable,
            DepthWrite = d.DepthWriteEnable,
            // RsComparison and RsStencilOp enumerate as Vulkan's CompareOp and StencilOp do.
            DepthCompare = (CompareOp)(int)d.DepthFunc,
            StencilTest = d.StencilEnable,
            StencilFront = new StencilOpState((StencilOp)(int)d.FrontStencilFailOp, (StencilOp)(int)d.FrontStencilPassOp, (StencilOp)(int)d.FrontStencilDepthFailOp,
                (CompareOp)(int)d.FrontStencilFunc, d.StencilReadMask, d.StencilWriteMask, 0),
            StencilBack = new StencilOpState((StencilOp)(int)d.BackStencilFailOp, (StencilOp)(int)d.BackStencilPassOp, (StencilOp)(int)d.BackStencilDepthFailOp,
                (CompareOp)(int)d.BackStencilFunc, d.StencilReadMask, d.StencilWriteMask, 0),
            ColorWriteMask = (ColorComponentFlags)(int)b.RenderTargetWriteMask[0],
            Blend = b.BlendEnable[0]
                ? (Factor(b.SrcBlend[0]), Factor(b.DestBlend[0]), (BlendOp)(int)b.BlendOp[0], Factor(b.SrcBlendAlpha[0]), Factor(b.DestBlendAlpha[0]), (BlendOp)(int)b.BlendOpAlpha[0])
                : null,
        };
        return pass switch
        {
            // The colour pass tests the stencil the prepass leaves: front faces where (stencil & 1) != 3 & 1, back
            // faces always; nothing written (the write mask 254 with keep everywhere).
            Pass.Colour => state with { StencilTest = true, StencilFront = Keep(CompareOp.NotEqual, 1, 254, 3), StencilBack = Keep(CompareOp.Always, 1, 254, 3) },
            // The quarter resolution depth clears its stencil as it writes: always replace with 0.
            Pass.DownsampleQuarter => state with
            {
                StencilTest = true,
                StencilFront = new StencilOpState(StencilOp.Replace, StencilOp.Replace, StencilOp.Replace, CompareOp.Always, 0, 255, 0),
                StencilBack = new StencilOpState(StencilOp.Replace, StencilOp.Replace, StencilOp.Replace, CompareOp.Always, 0, 255, 0),
            },
            _ => state,
        };
    }

    private static StencilOpState Keep(CompareOp compare, uint compareMask, uint writeMask, uint reference)
        => new(StencilOp.Keep, StencilOp.Keep, StencilOp.Keep, compare, compareMask, writeMask, reference);

    /// <summary>The .vcs blend modes (D3D's order) as Vulkan's blend factors.</summary>
    private static BlendFactor Factor(RsBlendMode m) => m switch
    {
        RsBlendMode.Zero => BlendFactor.Zero,
        RsBlendMode.One => BlendFactor.One,
        RsBlendMode.SrcColor => BlendFactor.SrcColor,
        RsBlendMode.InvSrcColor => BlendFactor.OneMinusSrcColor,
        RsBlendMode.SrcAlpha => BlendFactor.SrcAlpha,
        RsBlendMode.InvSrcAlpha => BlendFactor.OneMinusSrcAlpha,
        RsBlendMode.DestAlpha => BlendFactor.DstAlpha,
        RsBlendMode.InvDestAlpha => BlendFactor.OneMinusDstAlpha,
        RsBlendMode.DestColor => BlendFactor.DstColor,
        RsBlendMode.InvDestColor => BlendFactor.OneMinusDstColor,
        RsBlendMode.SrcAlphaSat => BlendFactor.SrcAlphaSaturate,
        _ => throw new NotSupportedException($"blend {m}"),
    };
}
