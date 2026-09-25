using System.Numerics;
using System.Runtime.InteropServices;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The light precompute's maths against resourcecompiler.dll's own
/// functions, called in this process on random inputs.
/// </summary>
public sealed unsafe class LightPrecomputeOracleTests(ITestOutputHelper output)
{
    private static float F(Random r, double lo, double hi) => (float)(lo + r.NextDouble() * (hi - lo));

    private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);

    private static string Hex(ReadOnlySpan<float> v) => string.Join(' ', v.ToArray().Select(f => Bits(f).ToString("x8")));

    /// <summary>FUN_180f1aa70 on random point clouds: rotation, centre and half extents to the bit.</summary>
    [Fact]
    public void TheBoxFitMatches()
    {
        if (ResourceCompilerOracle.Load() is not { } module)
            return;
        var fit = (delegate* unmanaged<float*, long*, float*>)ResourceCompilerOracle.At(module, 0x180f1aa70);
        var random = new Random(91);
        var outBox = (float*)NativeMemory.AlignedAlloc(64, 16);
        var vec = (long*)NativeMemory.AllocZeroed(32);
        const int trials = 300;
        for (var t = 0; t < trials; t++)
        {
            var n = random.Next(3) == 0 ? 2 + random.Next(10) : 20 + random.Next(400);
            var points = new List<Vector3>();
            var data = (float*)NativeMemory.AlignedAlloc((nuint)(16 * n), 16);
            var centre = new Vector3(F(random, -3000, 3000), F(random, -3000, 3000), F(random, -3000, 3000));
            var q = Quaternion.Normalize(new Quaternion(F(random, -1, 1), F(random, -1, 1), F(random, -1, 1), F(random, -1, 1)));
            var size = new Vector3(F(random, 1, 400), F(random, 1, 400), F(random, 1, 400));
            for (var i = 0; i < n; i++)
            {
                var p = new Vector3(F(random, -1, 1) * size.X, F(random, -1, 1) * size.Y, F(random, -1, 1) * size.Z);
                if (t % 2 == 0)
                    p = Vector3.Transform(p, q);
                p += centre;
                points.Add(p);
                data[4 * i] = p.X;
                data[4 * i + 1] = p.Y;
                data[4 * i + 2] = p.Z;
                data[4 * i + 3] = 0f;
            }
            vec[0] = n;
            vec[1] = (long)data;
            vec[2] = n;
            fit(outBox, vec);
            var ours = LightObb.Fit(points);
            var valve = new ReadOnlySpan<float>(outBox, 10);
            float[] mine = [ours.Rotation.X, ours.Rotation.Y, ours.Rotation.Z, ours.Rotation.W,
                            ours.Origin.X, ours.Origin.Y, ours.Origin.Z, ours.Extent.X, ours.Extent.Y, ours.Extent.Z];
            Assert.True(Hex(valve) == Hex(mine), $"trial {t} ({n} points): valve {Hex(valve)} ours {Hex(mine)}");
            NativeMemory.AlignedFree(data);
        }
        output.WriteLine($"{trials} point clouds fitted exactly");
    }
}
