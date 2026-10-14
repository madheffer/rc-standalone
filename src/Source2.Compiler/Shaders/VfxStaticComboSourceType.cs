// From ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// CompiledShader/Enums/VfxStaticComboSourceType.cs.

namespace Source2.Compiler.Shaders;

/// <summary>Where a static combo's value comes from: a feature, or a system value.</summary>
public enum VfxStaticComboSourceType
{
#pragma warning disable CS1591
    __SET_BY_FEATURE__,
    S_TOOLS_ENABLED,
    S_SHADER_QUALITY,
    S_VR,
    S_MOLTEN_VK,
    S_BINDLESS_RUNTIME, // Removed in version 71, following values are shifted down by two in newer files
    S_MOBILE, // Removed in version 71
    S_REVERSE_DEPTH_BUFFER,
    S_SUPPORTS_CLIP_DISTANCE,
    S_SUBGROUP_WAVE_OPS,
    __SET_BY_FEATURE_EQ__,
    __SET_BY_FEATURE_NE__,
    S_EXECUTION_REORDERING,
    S_SUPPORTS_DX_11_1,
    S_GPU_VENDOR_ID,
    S_GPU_DEVICE_ID,
#pragma warning restore CS1591
}
