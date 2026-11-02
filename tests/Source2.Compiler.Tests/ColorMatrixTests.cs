using Source2.Compiler.Gpu;
using Source2.Compiler.Lighting;
using Source2.Compiler.Lighting.Bake;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// materialsystem2's colour matrix expressions (<see cref="ColorMatrices"/>, ledgers 196 and 197) and
/// TextureAverageColor, the texture's .vtex header reflectivity, in both evaluators.
/// </summary>
public sealed class ColorMatrixTests(ITestOutputHelper output)
{
    private sealed class Scope(string name, string texture, float[]? reflectivity) : VfxExpression.IScope
    {
        public VfxExpression.Value? Parameter(string n) => n == name ? VfxExpression.Value.Of(0f) with { Texture = texture } : null;
        public VfxExpression.Value? Attribute(string n) => null;
        public int Feature(int index) => 0;
        public float[]? TextureReflectivity(string path) => path == texture ? reflectivity : null;
    }

    [Fact]
    public void TextureAverageColorIsTheHeaderReflectivity()
    {
        // MATERIAL_PARAM <token>, FUNC TextureAverageColor (0x28, one argument), RETURN.
        byte[] code = [0x1D, 0x78, 0x56, 0x34, 0x12, 0x06, 0x28, 0x01, 0x00];
        var value = VfxExpression.Evaluate(code, new Scope("12345678", "materials/t.vtex", [0.25f, 0.5f, 0.75f, 0.125f]));
        Assert.Equal([0.25f, 0.5f, 0.75f, 1f], value.C);
        // A texture we cannot read, or an argument that is not a texture: zero, w included (18009bb70's other branch).
        Assert.Equal([0f, 0f, 0f, 0f], VfxExpression.Evaluate(code, new Scope("12345678", "materials/t.vtex", null)).C);
        Assert.Equal([0f, 0f, 0f, 0f], VfxExpression.Evaluate(code, new Scope("other", "materials/t.vtex", [1f, 1f, 1f, 1f])).C);
    }

    [Fact]
    public void BakeEvaluatorReadsTheTextureByName()
    {
        float[]? Reflectivity(string name) => name == "g_tColor1" ? [0.25f, 0.5f, 0.75f, 0.125f] : null;
        Assert.Equal([0.25f, 0.5f, 0.75f, 1f], VfxEval.Evaluate("return TextureAverageColor( g_tColor1 );", _ => null, [], Reflectivity));
        Assert.Equal([0f, 0f, 0f, 1f], VfxEval.Evaluate("return TextureAverageColor(g_tColor2);", _ => null, [], Reflectivity));
    }

    /// <summary>At contrast 1 the average cancels word for word: -a + a is +0 in every dpps (ledger 196's capture holds).</summary>
    [Fact]
    public void ContrastOneCancelsTheAverage()
    {
        var zero = ColorMatrices.Correct2(1f, 1f, 1f, [0f, 0f, 0f]);
        var average = ColorMatrices.Correct2(1f, 1f, 1f, [0.3141f, 0.2718f, 0.1618f]);
        Assert.Equal(zero.Select(BitConverter.SingleToUInt32Bits), average.Select(BitConverter.SingleToUInt32Bits));
        Assert.NotEqual(zero, ColorMatrices.Correct2(1.5f, 1f, 1f, [0.3141f, 0.2718f, 0.1618f]));
    }

    /// <summary>
    /// Survey (<c>COLORMATRIX_SCAN=&lt;vpk&gt;[;...]</c>): materials whose colour adjustment is not the default
    /// (a contrast, saturation or brightness other than 1, or a tint other than white), where TextureAverageColor
    /// and the HSV path of MatrixColorTint2 reach a block.
    /// </summary>
    [Fact]
    public void NonDefaultColorAdjust()
    {
        if (Environment.GetEnvironmentVariable("COLORMATRIX_SCAN") is not { Length: > 0 } scan)
            return;
        foreach (var vpk in scan.Split(';'))
        {
            using var pak = new ValvePak.Package();
            pak.Read(vpk);
            var count = 0;
            foreach (var entry in pak.Entries?.GetValueOrDefault("vmat_c") ?? [])
            {
                pak.ReadEntry(entry, out var bytes);
                using var resource = new Resource();
                resource.Read(new MemoryStream(bytes));
                if (resource.DataBlock is not Material mat)
                    continue;
                var odd = mat.FloatParams.Where(p => (p.Key.Contains("Contrast", StringComparison.Ordinal) || p.Key.Contains("Saturation", StringComparison.Ordinal)
                                                      || p.Key.Contains("Brightness", StringComparison.Ordinal)) && p.Key.Contains("TextureColor", StringComparison.Ordinal) && p.Value != 1f)
                    .Select(p => $"{p.Key}={p.Value}")
                    .Concat(mat.VectorParams.Where(p => p.Key.Contains("TextureColorTint", StringComparison.Ordinal) && (p.Value.X != 1f || p.Value.Y != 1f || p.Value.Z != 1f))
                        .Select(p => $"{p.Key}=({p.Value.X} {p.Value.Y} {p.Value.Z})"))
                    .ToList();
                if (odd.Count == 0)
                    continue;
                count++;
                output.WriteLine($"{entry.GetFullPath()} [{mat.ShaderName}]: {string.Join(", ", odd)}");
            }
            output.WriteLine($"{vpk}: {count} materials");
        }
    }
}
