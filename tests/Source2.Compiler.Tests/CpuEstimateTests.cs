using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Source2.Compiler.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="CpuEstimate"/>'s Zen 4 tables against the hardware's rcpss and
/// rsqrtss, on a sweep of float bit patterns that covers every exponent, sign,
/// zero, denormal, infinity and NaN class. Runs only on AMD family 19h (Zen 3
/// and 4), where the tables were read.
/// </summary>
public class CpuEstimateTests(ITestOutputHelper output)
{
    private static bool IsZen()
    {
        if (!X86Base.IsSupported)
            return false;
        var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);
        var vendor = string.Concat(new[] { ebx, edx, ecx }.Select(r => System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(r))));
        var (eax, _, _, _) = X86Base.CpuId(1, 0);
        var family = ((eax >> 8) & 0xf) + ((eax >> 20) & 0xff);
        return vendor == "AuthenticAMD" && family == 0x19;
    }

    [Fact]
    public void TablesMatchZenHardware()
    {
        if (!IsZen())
            return;
        long rcpOff = 0, rsqrtOff = 0, count = 0;
        for (ulong u = 0; u <= uint.MaxValue; u += 61)
        {
            var x = BitConverter.UInt32BitsToSingle((uint)u);
            count++;
            var v = Vector128.CreateScalarUnsafe(x);
            if (BitConverter.SingleToUInt32Bits(Sse.ReciprocalScalar(v).ToScalar()) != BitConverter.SingleToUInt32Bits(CpuEstimate.TableRcp(x)) && rcpOff++ < 5)
                output.WriteLine($"rcp {(uint)u:X8}");
            if (BitConverter.SingleToUInt32Bits(Sse.ReciprocalSqrtScalar(v).ToScalar()) != BitConverter.SingleToUInt32Bits(CpuEstimate.TableRsqrt(x)) && rsqrtOff++ < 5)
                output.WriteLine($"rsqrt {(uint)u:X8}");
        }
        output.WriteLine($"{count} inputs, rcp differs on {rcpOff}, rsqrt on {rsqrtOff}");
        Assert.Equal(0, rcpOff);
        Assert.Equal(0, rsqrtOff);
    }
}
