using Silk.NET.Vulkan;
using ValveResourceFormat.CompiledShader;

namespace Source2.Compiler.Gpu;

/// <summary>
/// A cubemap face draw's samplers from its program (ledger 153): each sampler slot's state variables in the combo's
/// write sequence (Filter, AddressU/V/W, ComparisonFunc, BorderColor, MaxAniso; literals, or expressions on the
/// material), bound at set 1, binding 14 + the slot. Checked field by field against the world colour and sky draws
/// of three captures.
/// </summary>
public static class CubemapFaceSamplers
{
    /// <summary>
    /// The anisotropy of the material filter (Filter 255) and of a slot with no filter of its own: 4 in the captures'
    /// video config (the level the video settings pick, as the shadow quality's cascades are; not read).
    /// </summary>
    public const float Anisotropy = 4f;

    /// <summary>The samplers' LOD range: 0 to 15 on every captured sampler.</summary>
    public const float MaxLod = 15f;

    /// <summary>The program's sampler slots (set 1 slot numbers) and each one's state.</summary>
    public static Dictionary<int, SamplerCreateInfo> Samplers(ValveProgram program, VfxExpression.IScope? material = null)
    {
        var states = program.Sequence.RenderState.Select(f => (f.LayoutSet, f.BindingSlot, Variable: program.Program.VariableDescriptions[f.VariableIndex]))
            .Where(f => f.Variable.RegisterType == VfxRegisterType.SamplerState && f.LayoutSet == 1).ToList();
        var result = new Dictionary<int, SamplerCreateInfo>();
        foreach (var slot in states.Select(s => s.BindingSlot).Distinct())
        {
            int? Value(string name)
            {
                var v = states.FirstOrDefault(s => s.BindingSlot == slot && s.Variable.Name == name).Variable;
                if (v == null)
                    return null;
                return v.CompiledExpression.Length > 0
                    ? (int)VfxExpression.Evaluate(v.CompiledExpression, material ?? throw new ArgumentException($"{name} needs the material", nameof(material)))[0]
                    : v.IntDefs[0];
            }
            result[slot] = State(Value("Filter"), Value("AddressU"), Value("AddressV"), Value("AddressW"), Value("ComparisonFunc"), Value("BorderColor"), Value("MaxAniso"));
        }
        return result;
    }

    /// <summary>
    /// One sampler from the state values. Filter is D3D11's code (bit 0 linear mip, bit 2 linear magnification, bit 4
    /// linear minification, 0x55 anisotropic, 0x80 comparison); 255, or no Filter at all (a bare DefaultSamplerState),
    /// is the material filter: anisotropic. Address modes are Source 2's (0 wrap, 1 mirror, 2 clamp, 3 border, 4
    /// mirror once), wrap where absent; the comparison is RsComparison; border colour 0 is transparent black.
    /// </summary>
    public static SamplerCreateInfo State(int? filter, int? u, int? v, int? w, int? compare, int? border, int? maxAniso)
    {
        static SamplerAddressMode Address(int? mode) => mode switch
        {
            null or 0 => SamplerAddressMode.Repeat,
            1 => SamplerAddressMode.MirroredRepeat,
            2 => SamplerAddressMode.ClampToEdge,
            3 => SamplerAddressMode.ClampToBorder,
            4 => SamplerAddressMode.MirrorClampToEdge,
            var m => throw new NotSupportedException($"address mode {m}"),
        };
        var f = filter ?? 255;
        var anisotropic = f == 255 || (f & 0x7f) == 0x55;
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = anisotropic || (f & 4) != 0 ? Filter.Linear : Filter.Nearest,
            MinFilter = anisotropic || (f & 0x10) != 0 ? Filter.Linear : Filter.Nearest,
            MipmapMode = anisotropic || (f & 1) != 0 ? SamplerMipmapMode.Linear : SamplerMipmapMode.Nearest,
            AddressModeU = Address(u),
            AddressModeV = Address(v),
            AddressModeW = Address(w),
            AnisotropyEnable = anisotropic,
            MaxAnisotropy = anisotropic ? (maxAniso is > 0 ? maxAniso.Value : Anisotropy) : 1f,
            CompareEnable = f != 255 && (f & 0x80) != 0,
            CompareOp = (CompareOp)(compare ?? 0),
            MinLod = 0f,
            MaxLod = MaxLod,
            BorderColor = (border ?? 0) switch
            {
                0 => BorderColor.FloatTransparentBlack,
                var b => throw new NotSupportedException($"border colour {b}"),
            },
        };
        return info;
    }
}
