using System.Runtime.InteropServices;
using ValveResourceFormat.CompiledShader;
using ValveResourceFormat.ResourceTypes;
using Value = Source2.Compiler.Gpu.VfxExpression.Value;

namespace Source2.Compiler.Gpu;

/// <summary>
/// Fills a program's <c>_Globals_</c> block from a material, as the combo's
/// write sequence lists it: artist values from the material or the shader's
/// default, expressions evaluated over them, render attributes by their
/// source name, features by index. Texture and sampler variables hold slots
/// in the bindless arrays, which the caller assigns.
/// </summary>
public static class ProgramGlobals
{
    /// <summary>Names resolved against a material, the render attributes and the program's defaults.</summary>
    public sealed class MaterialScope(Material material, ValveProgram program, IReadOnlyDictionary<string, Value> attributes) : VfxExpression.IScope
    {
        private readonly Dictionary<string, VfxVariableDescription> _byName = program.Program.VariableDescriptions
            .GroupBy(v => v.Name).ToDictionary(g => g.Key, g => g.First());

        public Value? Parameter(string name)
        {
            if (material.FloatParams.TryGetValue(name, out var f))
                return Value.Of(f);
            if (material.IntParams.TryGetValue(name, out var i))
                return Value.Of(i);
            if (material.VectorParams.TryGetValue(name, out var v))
                return Value.Of(v.X, v.Y, v.Z, v.W);
            return _byName.TryGetValue(name, out var d) ? Default(d) : null;
        }

        public Value? Attribute(string name) => attributes.TryGetValue(name, out var a) ? a : null;

        public int Feature(int index) => index < program.FeatureNames.Count
            ? (int)material.IntParams.GetValueOrDefault(program.FeatureNames[index], 0)
            : 0;
    }

    /// <summary>Components a variable type holds.</summary>
    public static int Width(VfxVariableType type) => type switch
    {
        VfxVariableType.Float2 or VfxVariableType.Int2 or VfxVariableType.Bool2 => 2,
        VfxVariableType.Float3 or VfxVariableType.Int3 or VfxVariableType.Bool3 => 3,
        VfxVariableType.Float4 or VfxVariableType.Int4 or VfxVariableType.Bool4 => 4,
        VfxVariableType.Float3x3 => 9,
        VfxVariableType.Float4x3 => 12,
        VfxVariableType.Float4x4 => 16,
        _ => 1,
    };

    private static bool IsInteger(VfxVariableType type) => type is >= VfxVariableType.Int and <= VfxVariableType.Bool4;

    /// <summary>A variable's shader default: its integer defaults for int and bool types, float defaults otherwise.</summary>
    public static Value Default(VfxVariableDescription v)
    {
        var n = Math.Min(Width(v.VfxType), 4);
        return IsInteger(v.VfxType)
            ? new Value([.. v.IntDefs.Take(n).Select(x => (float)x)])
            : new Value([.. v.FloatDefs.Take(n)]);
    }

    /// <summary>A variable's value before it is written.</summary>
    public static Value Resolve(VfxVariableDescription v, string name, MaterialScope scope)
    {
        switch (v.VariableSource)
        {
            case VfxVariableSourceType.__Expression__ or VfxVariableSourceType.__SetByArtistAndExpression__ when v.CompiledExpression.Length > 0:
                return VfxExpression.Evaluate(v.CompiledExpression, scope);
            case VfxVariableSourceType.__SetByArtist__:
                return scope.Parameter(name) ?? Default(v);
            case VfxVariableSourceType.__Attribute__:
                return scope.Attribute(v.SourceString) ?? Default(v);
            case VfxVariableSourceType.__FeatureToInt__ or VfxVariableSourceType.__FeatureToBool__ or VfxVariableSourceType.__FeatureToFloat__:
            {
                var f = scope.Feature(v.SourceIndex);
                return Value.Of(v.VariableSource == VfxVariableSourceType.__FeatureToBool__ ? (f != 0 ? 1 : 0) : f);
            }
            default:
                return Default(v);
        }
    }

    /// <summary>Writes every constant of the program's write sequence into <paramref name="block"/>.</summary>
    public static void Fill(ValveProgram program, Span<byte> block, MaterialScope scope,
                            Func<string, VfxVariableDescription, int> textureSlot, Func<string, int> samplerSlot)
    {
        foreach (var (name, offset, v) in program.Constants)
        {
            var at = block[offset..];
            switch (v.VfxType)
            {
                case VfxVariableType.Texture2DIndex or VfxVariableType.Texture3DIndex or VfxVariableType.TextureCubeIndex
                    or VfxVariableType.Texture2DArrayIndex or VfxVariableType.TextureCubeArrayIndex:
                    BitConverter.TryWriteBytes(at, textureSlot(name, v));
                    continue;
                case VfxVariableType.SamplerStateIndex:
                    BitConverter.TryWriteBytes(at, samplerSlot(name));
                    continue;
            }
            var value = Resolve(v, name, scope);
            var n = Width(v.VfxType);
            if (n > 4)
            {
                // Matrices: row after row as the expression built them.
                var m = new float[n];
                for (var i = 0; i < n; i++)
                    m[i] = i < value.Width ? value.C[i] : 0f;
                MemoryMarshal.AsBytes(m.AsSpan()).CopyTo(at);
                continue;
            }
            for (var i = 0; i < n; i++)
            {
                var x = value[i];
                if (v.VfxType is >= VfxVariableType.Bool and <= VfxVariableType.Bool4)
                    BitConverter.TryWriteBytes(at[(i * 4)..], x != 0f ? 1 : 0);
                else if (IsInteger(v.VfxType))
                    BitConverter.TryWriteBytes(at[(i * 4)..], (int)x);
                else
                    BitConverter.TryWriteBytes(at[(i * 4)..], x);
            }
        }
    }
}
