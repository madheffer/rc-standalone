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
    /// <summary>
    /// Names resolved against a material, the render attributes and the program's defaults;
    /// <paramref name="shaderIdColor"/> is the shader's <see cref="ShaderIdColor"/> (variables whose
    /// source is ShaderIDColor), when known.
    /// </summary>
    public sealed class MaterialScope(Material material, ValveProgram program, IReadOnlyDictionary<string, Value> attributes, Value? shaderIdColor = null) : VfxExpression.IScope
    {
        public Value? ShaderIdColor { get; } = shaderIdColor;

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
            case VfxVariableSourceType.ShaderIDColor:
                return scope.ShaderIdColor ?? Default(v);
            case VfxVariableSourceType.__FeatureToInt__ or VfxVariableSourceType.__FeatureToBool__ or VfxVariableSourceType.__FeatureToFloat__:
            {
                var f = scope.Feature(v.SourceIndex);
                return Value.Of(v.VariableSource == VfxVariableSourceType.__FeatureToBool__ ? (f != 0 ? 1 : 0) : f);
            }
            default:
                return Default(v);
        }
    }

    /// <summary>
    /// A shader's ID colour as materialsystem2 loads gameinfo's MaterialSystem2/ShaderIDColors (180035b2a):
    /// the "r g b" bytes through 180082cf0 (each over 255, clamped to [0, 1], sRGB to linear with tier0's
    /// V_powf at 2.4), alpha 1. Read back from the cubemap capture: generic's (255 255 255) is (1, 1, 1, 1).
    /// </summary>
    public static Value ShaderIdColor(int r, int g, int b)
    {
        static float Channel(int byteValue)
        {
            var c = byteValue * 0.003921569f;
            c = 0f <= c ? (c <= 1f ? c : 1f) : 0f;
            return 0.04045f < c ? Maps.LightPow.Pow((c + 0.055f) / 1.055f, 2.4f) : c / 12.92f;
        }
        return Value.Of(Channel(r), Channel(g), Channel(b), 1f);
    }

    /// <summary>gameinfo.gi's MaterialSystem2/ShaderIDColors: shader file ("generic.vfx") to its "r g b" bytes.</summary>
    public static Dictionary<string, (int R, int G, int B)> ShaderIdColors(string gameinfo)
    {
        var colors = new Dictionary<string, (int, int, int)>(StringComparer.OrdinalIgnoreCase);
        var at = gameinfo.IndexOf("ShaderIDColors", StringComparison.Ordinal);
        if (at < 0)
            return colors;
        var open = gameinfo.IndexOf('{', at);
        var close = gameinfo.IndexOf('}', open);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(gameinfo[open..close], @"""([^""]+)""\s+""(\d+) (\d+) (\d+)"""))
            colors[m.Groups[1].Value] = (int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value));
        return colors;
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
                // Matrices: row after row as the expression built them, a 4 x 4 transposed (the block holds its columns:
                // ze_hold_em_p's g_mTextureColorAdjust1, every word the game's, ledger 196). 3 x 3 and 4 x 3 as built (not
                // measured).
                var m = new float[n];
                for (var i = 0; i < n; i++)
                    m[i] = i < value.Width ? value.C[i] : 0f;
                if (v.VfxType == VfxVariableType.Float4x4 && value.Width == 16)
                    m = [.. Enumerable.Range(0, 16).Select(k => value.C[(k % 4) * 4 + k / 4])];
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
