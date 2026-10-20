using System.Runtime.InteropServices;
using Source2.Compiler.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// CrtMath.Atan and CrtMath.Acos against the installed tier0.dll's V_atanf and V_acosf, called
/// in-process: every float with a sweep of exponents and mantissas, the range edges, and random bits.
/// </summary>
public sealed class Tier0TrigOracleTests(ITestOutputHelper output)
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate float FloatFn(float x);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate float FloatFn2(float x, float y);

    private static FloatFn2? Export2(string name)
    {
        var dir = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game", "bin", "win64");
        if (!NativeLibrary.TryLoad(Path.Combine(dir, "tier0.dll"), out var handle))
            return null;
        return Marshal.GetDelegateForFunctionPointer<FloatFn2>(NativeLibrary.GetExport(handle, name));
    }

    private static FloatFn? Export(string name)
    {
        var dir = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game", "bin", "win64");
        var path = Path.Combine(dir, "tier0.dll");
        if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out var handle))
            return null;
        return Marshal.GetDelegateForFunctionPointer<FloatFn>(NativeLibrary.GetExport(handle, name));
    }

    private static IEnumerable<float> Inputs(bool unitRange)
    {
        var rng = new Random(1234);
        // Every 9973rd float bit pattern of the positive range, both signs.
        for (uint bits = 0; bits < 0x7f800000u; bits += 9973)
        {
            var f = BitConverter.UInt32BitsToSingle(bits);
            if (!unitRange || f <= 1f)
            {
                yield return f;
                yield return -f;
            }
        }
        foreach (var edge in new[] { 0.4375f, 0.6875f, 1.1875f, 2.4375f, 0.5f, 1f, 5.5e18f, float.PositiveInfinity, 1e-30f })
            for (var k = -4; k <= 4; k++)
            {
                var f = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(edge) + k);
                if (float.IsFinite(f) || !unitRange)
                {
                    yield return f;
                    yield return -f;
                }
            }
        for (var i = 0; i < 200000; i++)
        {
            var f = unitRange ? (float)(rng.NextDouble() * 2 - 1) : (float)(rng.NextDouble() * 400 - 200);
            yield return f;
        }
    }

    [Fact]
    public void AtanIsTier0s()
    {
        if (Export("V_atanf") is not { } atanf)
            return;
        int total = 0, differ = 0;
        foreach (var x in Inputs(unitRange: false))
        {
            if (float.IsNaN(x))
                continue;
            var want = atanf(x);
            var got = CrtMath.Atan(x);
            total++;
            if (BitConverter.SingleToInt32Bits(got) != BitConverter.SingleToInt32Bits(want) && differ++ < 10)
                output.WriteLine($"atanf({x:R}): ours {got:R}, tier0 {want:R}");
        }
        output.WriteLine($"{total - differ} of {total} atanf results tier0's");
        Assert.Equal(0, differ);
    }

    [Fact]
    public void AcosIsTier0s()
    {
        if (Export("V_acosf") is not { } acosf)
            return;
        int total = 0, differ = 0;
        foreach (var x in Inputs(unitRange: true))
        {
            var want = acosf(x);
            var got = CrtMath.Acos(x);
            total++;
            if (BitConverter.SingleToInt32Bits(got) != BitConverter.SingleToInt32Bits(want) && differ++ < 10)
                output.WriteLine($"acosf({x:R}): ours {got:R}, tier0 {want:R}");
        }
        output.WriteLine($"{total - differ} of {total} acosf results tier0's");
        Assert.Equal(0, differ);
    }

    /// <summary>
    /// CrtMath.Pow against V_powf: every 9973rd positive float to the 1/3 (the Lab conversion's exponent), then
    /// random x in (0, 4) and y in (-8, 8). Arguments the port refuses (overflow, underflow) are skipped.
    /// </summary>
    [Fact]
    public void PowIsTier0s()
    {
        if (Export2("V_powf") is not { } powf)
            return;
        int total = 0, differ = 0, skipped = 0;
        void Check(float x, float y)
        {
            float got;
            try
            {
                got = CrtMath.Pow(x, y);
            }
            catch (NotSupportedException)
            {
                skipped++;
                return;
            }
            var want = powf(x, y);
            total++;
            if (BitConverter.SingleToInt32Bits(got) != BitConverter.SingleToInt32Bits(want) && differ++ < 10)
                output.WriteLine($"powf({x:R}, {y:R}): ours {got:R}, tier0 {want:R}");
        }
        var third = BitConverter.Int32BitsToSingle(0x3eaaaaab);
        for (uint bits = 1; bits < 0x7f800000u; bits += 9973)
            Check(BitConverter.UInt32BitsToSingle(bits), third);
        for (var k = -20000; k <= 20000; k++)
            Check(1f + k * 1e-5f, third);
        var rng = new Random(77);
        for (var i = 0; i < 300000; i++)
            Check((float)(rng.NextDouble() * 4), (float)(rng.NextDouble() * 16 - 8));
        output.WriteLine($"{total - differ} of {total} powf results tier0's ({skipped} refused)");
        Assert.Equal(0, differ);
    }
}
