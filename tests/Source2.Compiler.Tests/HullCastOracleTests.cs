using System.Buffers.Binary;
using System.Numerics;
using Source2.Compiler.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="HullCast"/> against vphysics2's own 180251800 on random inputs: the file
/// tools/re/hullcast_oracle.py writes (<c>HULLCAST_ORACLE</c>, default %TEMP%/hullcast_oracle.bin).
/// </summary>
public sealed class HullCastOracleTests(ITestOutputHelper output)
{
    [Fact]
    public void HullCastsMatchVphysics2()
    {
        var path = Environment.GetEnvironmentVariable("HULLCAST_ORACLE") ?? Path.Combine(Path.GetTempPath(), "hullcast_oracle.bin");
        if (!File.Exists(path))
            return;
        var b = File.ReadAllBytes(path);
        float F(int o) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o));
        Vector3 V(int o) => new(F(o), F(o + 4), F(o + 8));
        int at = 0, total = 0, same = 0, hits = 0, shown = 0;
        while (at < b.Length)
        {
            var n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at));
            var reference = V(at + 4);
            var scale = F(at + 16);
            var t = new float[12];
            for (var k = 0; k < 12; k++)
                t[k] = F(at + 20 + k * 4);
            var start = V(at + 68);
            var delta = V(at + 80);
            var half = V(at + 92);
            var pts = new Vector3[n];
            for (var k = 0; k < n; k++)
                pts[k] = V(at + 104 + k * 12);
            var r = at + 104 + n * 12;
            var returned = b[r] != 0;
            var solid = b[r + 1] != 0;
            var fraction = F(r + 4);
            at = r + 4 + 0x1c;
            total++;
            if (returned)
                hits++;
            var hit = HullCast.Cast(reference, pts, scale, t, start, delta, half);
            if (hit.Found == returned && (!returned || (BitConverter.SingleToInt32Bits(hit.Fraction) == BitConverter.SingleToInt32Bits(fraction) && hit.StartSolid == solid)))
                same++;
            else if (shown++ < 15)
                output.WriteLine($"case {total - 1}: ours {hit.Found} {hit.Fraction:R}{(hit.StartSolid ? " solid" : "")}, Valve {returned} {fraction:R}{(solid ? " solid" : "")}; " +
                                 $"{n} points, start {start} delta {delta} half {half}");
        }
        output.WriteLine($"{same} of {total} hull casts vphysics2's ({hits} hits)");
        Assert.Equal(total, same);
    }
}
