using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="Simulation.CrtMath.Sin"/> and <see cref="Simulation.CrtMath.Cos"/> (ports of tier0's
/// FMA3 sinf and cosf) bit for bit against the Windows CRT's, which .NET's
/// MathF calls on Windows: every degree angle in thousandths up to two turns
/// as the map code forms it (degrees times pi/180 in float), and a sweep of
/// float bit patterns up to 1e5. Skipped on other systems, whose libm differs.
/// </summary>
public class LightCrtSinCosTests(ITestOutputHelper output)
{
    [Fact]
    public void MatchesTheWindowsCrt()
    {
        if (!OperatingSystem.IsWindows())
            return;
        int checkedCount = 0, sinOff = 0, cosOff = 0;
        void Check(float x)
        {
            checkedCount++;
            if (BitConverter.SingleToInt32Bits(Simulation.CrtMath.Sin(x)) != BitConverter.SingleToInt32Bits(MathF.Sin(x)))
            {
                if (sinOff++ < 5)
                    output.WriteLine($"sin {x:R}: ours {Simulation.CrtMath.Sin(x):R} crt {MathF.Sin(x):R}");
            }
            if (BitConverter.SingleToInt32Bits(Simulation.CrtMath.Cos(x)) != BitConverter.SingleToInt32Bits(MathF.Cos(x)))
            {
                if (cosOff++ < 5)
                    output.WriteLine($"cos {x:R}: ours {Simulation.CrtMath.Cos(x):R} crt {MathF.Cos(x):R}");
            }
        }
        const float radians = MathF.PI / 180f;
        for (var d = -720000; d <= 720000; d++)
        {
            Check(d / 1000f * radians);
            Check(d / 1000f * (radians * 0.5f));
        }
        for (var bits = 0x30000000; bits < 0x47c35000; bits += 997)
        {
            var x = BitConverter.Int32BitsToSingle(bits);
            Check(x);
            Check(-x);
        }
        output.WriteLine($"{checkedCount} arguments, sin differs on {sinOff}, cos on {cosOff}");
        Assert.Equal(0, sinOff);
        Assert.Equal(0, cosOff);
    }
}
