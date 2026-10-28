using System.Numerics;
using Source2.Compiler.Maps.NavGen;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>The mesh build's ladder maths against resourcecompiler.dll's own functions, on random inputs.</summary>
public sealed unsafe class NavMeshLaddersOracleTests(ITestOutputHelper output)
{
    private static float F(Random r, double lo, double hi) => (float)(lo + r.NextDouble() * (hi - lo));

    /// <summary>18129b060 (the closest point on a triangle, which 1818e4490 runs over an area's fan) to the bit.</summary>
    [Fact]
    public void TheClosestPointOnATriangleMatches()
    {
        if (ResourceCompilerOracle.Load() is not { } module)
            return;
        var closest = (delegate* unmanaged<float*, float*, float*, float*, float*, float*>)ResourceCompilerOracle.At(module, 0x18129b060);
        var random = new Random(28);
        var buffer = stackalloc float[15];
        const int trials = 200_000;
        var same = 0;
        var shown = 0;
        for (var t = 0; t < trials; t++)
        {
            // Mostly nav-sized triangles around the point, some degenerate (a repeated or collinear corner).
            var v = new Vector3[4];
            for (var k = 0; k < 4; k++)
                v[k] = new Vector3(F(random, -2000, 2000), F(random, -2000, 2000), F(random, -300, 300));
            if (random.Next(20) == 0)
                v[3] = v[1 + random.Next(2)];
            if (random.Next(20) == 0)
                v[3] = (v[1] + v[2]) * 0.5f;
            for (var k = 0; k < 4; k++)
            {
                buffer[3 + 3 * k] = v[k].X;
                buffer[4 + 3 * k] = v[k].Y;
                buffer[5 + 3 * k] = v[k].Z;
            }
            closest(buffer, buffer + 3, buffer + 6, buffer + 9, buffer + 12);
            var want = new Vector3(buffer[0], buffer[1], buffer[2]);
            var got = NavMeshLadders.ClosestPointOnTriangle(v[0], v[1], v[2], v[3]);
            if (BitConverter.SingleToInt32Bits(got.X) == BitConverter.SingleToInt32Bits(want.X)
                && BitConverter.SingleToInt32Bits(got.Y) == BitConverter.SingleToInt32Bits(want.Y)
                && BitConverter.SingleToInt32Bits(got.Z) == BitConverter.SingleToInt32Bits(want.Z))
                same++;
            else if (shown++ < 10)
                output.WriteLine($"trial {t}: ours {got:R}, Valve {want:R}");
        }
        output.WriteLine($"{same} of {trials} closest points Valve's");
        Assert.Equal(trials, same);
    }
}
