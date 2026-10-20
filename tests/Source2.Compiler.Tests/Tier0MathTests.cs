using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// tier0's V_powf / V_logf against MathF.Pow / MathF.Log on the records tools/re/tier0_powlog_oracle.py
/// wrote (%TEMP%/tier0_powlog.bin: op, a, b, result as float32; op 0 powf, 1 logf).
/// </summary>
public sealed class Tier0MathTests(ITestOutputHelper output)
{
    [Fact]
    public void PowAndLogAreTier0s()
    {
        var path = Path.Combine(Path.GetTempPath(), "tier0_powlog.bin");
        if (!File.Exists(path))
            return;
        var b = File.ReadAllBytes(path);
        int total = 0, differ = 0;
        for (var at = 0; at + 16 <= b.Length; at += 16)
        {
            var op = BitConverter.ToInt32(b, at);
            var x = BitConverter.ToSingle(b, at + 4);
            var y = BitConverter.ToSingle(b, at + 8);
            var want = BitConverter.ToSingle(b, at + 12);
            var got = op == 0 ? MathF.Pow(x, y) : MathF.Log(x);
            total++;
            if (BitConverter.SingleToInt32Bits(got) != BitConverter.SingleToInt32Bits(want) && differ++ < 10)
                output.WriteLine($"{(op == 0 ? "powf" : "logf")}({x:R}, {y:R}): ours {got:R}, tier0 {want:R}");
        }
        output.WriteLine($"{total - differ} of {total} equal");
        Assert.Equal(0, differ);
    }
}
