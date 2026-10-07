using System.Buffers.Binary;
using Source2.Compiler.Lighting;
using Source2.Compiler.Simulation;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// The lightmap post-process port's self-consistency: MSVC nth_element, the SH
/// rotation against direct evaluation, the AHD solver against its closed form,
/// gutter filling and the shadow writer on small hand-made images. Byte parity
/// with vrad3 itself is LightmapPostOracleTests.
/// </summary>
public class LightmapPostTests
{
    [Fact]
    public void NthElementPlacesTheNthValue()
    {
        var random = new Random(7);
        for (var n = 1; n <= 120; n++)
            for (var trial = 0; trial < 4; trial++)
            {
                var a = Enumerable.Range(0, n).Select(_ => (float)random.Next(0, 40)).ToArray();
                var sorted = a.OrderBy(v => v).ToArray();
                var nth = random.Next(0, n);
                MsvcSort.NthElement(a.AsSpan(), nth, default(MsvcSort.FloatLess));
                Assert.Equal(sorted[nth], a[nth]);
                Assert.All(a[..nth], v => Assert.True(v <= a[nth]));
                Assert.All(a[(nth + 1)..], v => Assert.True(v >= a[nth]));
                if (n <= 32)
                    Assert.Equal(sorted, a);
            }
    }

    [Fact]
    public void EvenMedianAveragesTheTwoMiddleValues()
    {
        Assert.Equal(2.5f, LightmapPost.MedianOf([4f, 1f, 3f, 2f]));
        Assert.Equal(3f, LightmapPost.MedianOf([5f, 1f, 3f, 2f, 4f]));
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(-1f)]
    public void RotatedShEvaluatesLikeTheWorldSh(float sign)
    {
        var random = new Random(11);
        for (var trial = 0; trial < 200; trial++)
        {
            var n = RandomUnit(random);
            var t = Normalize(Cross(RandomUnit(random), n));
            var b = Cross(n, t);
            Span<float> m = [t[0], t[1], t[2], 0, b[0] * sign, b[1] * sign, b[2] * sign, 0, n[0], n[1], n[2], 0];
            var sh = Enumerable.Range(0, 9).Select(_ => (float)random.NextDouble() * 2 - 1).ToArray();
            var rotated = (float[])sh.Clone();
            LightmapAhd.RotateSh(rotated, m);
            for (var k = 0; k < 8; k++)
            {
                var v = RandomUnit(random);
                float dx = m[0] * v[0] + m[1] * v[1] + m[2] * v[2];
                float dy = m[4] * v[0] + m[5] * v[1] + m[6] * v[2];
                float dz = m[8] * v[0] + m[9] * v[1] + m[10] * v[2];
                Assert.True(MathF.Abs(Evaluate(rotated, dx, dy, dz) - Evaluate(sh, v[0], v[1], v[2])) < 1e-4f,
                    $"trial {trial}: tangent-space SH differs from the world SH by {Evaluate(rotated, dx, dy, dz) - Evaluate(sh, v[0], v[1], v[2])}, m10 {m[10]}");
            }
        }
    }

    [Fact]
    public void AhdSolveMatchesTheClosedForm()
    {
        var random = new Random(3);
        for (var trial = 0; trial < 100; trial++)
        {
            var a = Enumerable.Range(0, 256).Select(_ => (float)random.NextDouble()).ToArray();
            var b = Enumerable.Range(0, 256).Select(_ => (float)random.NextDouble() - 0.3f).ToArray();
            double atb = 0, ata = 0;
            for (var i = 0; i < 256; i++)
            {
                atb += (double)a[i] * b[i];
                ata += (double)a[i] * a[i];
            }
            Assert.True(Math.Abs(LightmapAhd.Solve(a, b) - atb / ata) < 1e-4 * Math.Max(1, Math.Abs(atb / ata)));
        }
    }

    [Fact]
    public void GutterLuxelsTakeTheMeanOfTheirCoveredRing()
    {
        // A 4 x 4 chart rect at (1, 1) whose border ring is uncovered.
        var image = new LightmapImage(6, 6);
        var irradiance = image.AddChannel(LightmapImage.Ch.Irradiance, 7);
        for (var p = 0; p < 36; p++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(image.LuxelRecords.AsSpan(p * 12), 0xffffffff);
            image.ShadowRecords.AsSpan(p * 20, 4).Fill(0xff);
        }
        float[] values = [1, 2, 3, 4];
        int[] inner = [2 * 6 + 2, 2 * 6 + 3, 3 * 6 + 2, 3 * 6 + 3];
        for (var i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(image.LuxelRecords.AsSpan(inner[i] * 12), 0);
            image.SetValid(inner[i]);
            irradiance.Data[inner[i] * 4] = values[i];
        }
        LightmapPost.FillGutters(image, [new PackingGeometry.Chart(0, 0, 1, 1, 4, 4, 0)], 1);
        // Corner (1, 1) sees only (2, 2); edge (2, 1) sees (2, 2) and (3, 2).
        Assert.Equal(1f, irradiance.Data[(1 * 6 + 1) * 4]);
        Assert.Equal(1.5f, irradiance.Data[(1 * 6 + 2) * 4]);
        Assert.Equal(0u, image.ChartId(1 * 6 + 1));
        Assert.True(image.Valid(1 * 6 + 1));
        // Outside the rect nothing changes.
        Assert.Equal(0xffffffffu, image.ChartId(0));
    }

    [Fact]
    public void ShadowWriterInvertsClampsAndKeepsEmptySlots()
    {
        var image = new LightmapImage(1, 1);
        byte[] record = [0, 0xff, 2, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(4), 0.25f);
        BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(12), 1.5f);
        BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(16), -1f);
        record.CopyTo(image.ShadowRecords, 0);
        var exr = LightmapPost.DirectLightShadowsExr(image);
        Assert.Equal((Half)0.75f, exr.Plane("R")[0]);
        Assert.Equal((Half)1f, exr.Plane("G")[0]);
        Assert.Equal((Half)0f, exr.Plane("B")[0]);
        Assert.Equal((Half)1f, exr.Plane("A")[0]);
        var gamma = LightmapPost.DirectLightShadowsExr(image, 4, gamma20: true, invert: false);
        Assert.Equal((Half)0.5f, gamma.Plane("R")[0]);
        Assert.Equal((Half)0f, gamma.Plane("G")[0]);
    }

    private static float Evaluate(float[] sh, float x, float y, float z)
    {
        Span<float> three = stackalloc float[27];
        sh.CopyTo(three);
        Span<float> result = stackalloc float[3];
        LightmapPost.EvaluateSh(three, x, y, z, result);
        return result[0];
    }

    private static float[] RandomUnit(Random random)
    {
        while (true)
        {
            float[] v = [(float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1];
            var l = MathF.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            if (l > 0.1f && l <= 1f)
                return [v[0] / l, v[1] / l, v[2] / l];
        }
    }

    private static float[] Cross(float[] a, float[] b) =>
        [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];

    private static float[] Normalize(float[] v)
    {
        var l = MathF.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
        return [v[0] / l, v[1] / l, v[2] / l];
    }
}
