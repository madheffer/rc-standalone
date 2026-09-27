using ValvePak;
using ValveResourceFormat.CompiledShader;

namespace Source2.Compiler.Gpu;

/// <summary>
/// One of Valve's compiled programs, taken from the shader vpk
/// (shaders_&lt;platform&gt;_dir.vpk): the SPIR-V for a static and dynamic
/// combo, its reflection, and where each shader variable goes in its
/// <c>_Globals_</c> block (the combo's write sequence, register offsets in
/// dwords) with the variable's description (type, defaults, expression).
/// </summary>
public sealed class ValveProgram
{
    public required string Name { get; init; }
    public required byte[] Spirv { get; init; }
    public required SpirvReflection Reflection { get; init; }

    /// <summary>Each constant the combo writes: variable name, byte offset in <c>_Globals_</c>, description.</summary>
    public required IReadOnlyList<(string Name, int Offset, VfxVariableDescription Variable)> Constants { get; init; }

    /// <summary>The static combo's attributes (such as BindlessResources).</summary>
    public required IReadOnlyList<VfxShaderAttribute> Attributes { get; init; }

    /// <summary>The whole program file, and the picked combo's write sequence (evaluated, render state, constants).</summary>
    public required VfxProgramData Program { get; init; }
    public required VfxVariableIndexArray Sequence { get; init; }

    /// <summary>The shader's feature names (F_...), in the order expressions index them.</summary>
    public required IReadOnlyList<string> FeatureNames { get; init; }

    private static VfxProgramData Read(Package package, string name)
    {
        var entry = package.FindEntry($"shaders/vfx/{name}.vcs") ?? throw new FileNotFoundException(name);
        package.ReadEntry(entry, out var bytes);
        var program = new VfxProgramData();
        program.Read($"{name}.vcs", new MemoryStream(bytes));
        return program;
    }

    /// <summary>
    /// Loads <c>shaders/vfx/&lt;shader&gt;_&lt;platform&gt;_&lt;stage&gt;.vcs</c> and picks the
    /// combo. A static combo tied to a feature takes the material's feature
    /// value (== or != a value where the combo says so); <paramref name="statics"/>
    /// overrides; the rest are 0. Needs a VRF that reads VCS 72.
    /// </summary>
    public static ValveProgram Load(Package package, string shader, string platform, string stage,
                                    IReadOnlyDictionary<string, int> statics, IReadOnlyDictionary<string, int> dynamics,
                                    IReadOnlyDictionary<string, int>? features = null)
    {
        var name = $"{shader}_{platform}_{stage}";
        var program = Read(package, name);
        using var featureFile = Read(package, $"{shader}_{platform}_features");
        string[] featureNames = [.. featureFile.StaticComboArray.Select(f => f.Name)];
        long staticId = 0;
        foreach (var c in program.StaticComboArray)
        {
            var value = 0;
            if (statics.TryGetValue(c.Name, out var given))
                value = given;
            else if (c.FeatureIndex >= 0 && features != null)
            {
                var f = features.GetValueOrDefault(featureNames[c.FeatureIndex], 0);
                value = (VfxStaticComboSourceType)c.ComboSourceType switch
                {
                    VfxStaticComboSourceType.__SET_BY_FEATURE_EQ__ => f == c.FeatureComparisonValue ? 1 : 0,
                    VfxStaticComboSourceType.__SET_BY_FEATURE_NE__ => f != c.FeatureComparisonValue ? 1 : 0,
                    _ => f,
                };
            }
            staticId += (value - c.RangeMin) * c.ComboIndexValue;
        }
        if (!program.StaticComboEntries.ContainsKey(staticId))
            throw new InvalidDataException($"{name}: no static combo {staticId}");
        var combo = program.GetStaticCombo(staticId);
        long dynamicId = 0;
        foreach (var c in program.DynamicComboArray)
            dynamicId += (dynamics.GetValueOrDefault(c.Name, 0) - c.RangeMin) * c.ComboIndexValue;
        var index = combo.GetDynamicComboIndex(dynamicId);
        if (index < 0)
            throw new InvalidDataException($"{name}: static {staticId} has no dynamic combo {dynamicId}");
        var file = combo.DynamicComboRenderStates[index].ShaderFileId is var fileId && fileId >= 0 && fileId < combo.ShaderFiles.Length
            ? combo.ShaderFiles[fileId]
            : combo.ShaderFiles[index];
        var spirv = file.Bytecode;
        var sequence = combo.DynamicComboVariables[index];
        var constants = sequence.Constants
            .Select(f => (program.VariableDescriptions[f.VariableIndex].Name, f.RegisterOffset * 4, program.VariableDescriptions[f.VariableIndex]))
            .ToList();
        return new ValveProgram
        {
            Name = $"{name} static {staticId} dynamic {dynamicId}",
            Spirv = spirv,
            Reflection = SpirvReflection.Parse(spirv),
            Constants = constants,
            Attributes = combo.Attributes,
            Program = program,
            Sequence = sequence,
            FeatureNames = featureNames,
        };
    }
}
