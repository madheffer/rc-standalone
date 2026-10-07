using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>VMAPFINDUV=&lt;addon&gt;|&lt;map&gt;|&lt;value&gt;</c>): every stored texcoord
/// component of the .vmap's meshes within 1e-5 of the value, with its node,
/// stream and float bits.
/// </summary>
public class VmapValueProbe(ITestOutputHelper output)
{
    [Fact]
    public void FindTexcoord()
    {
        if (Environment.GetEnvironmentVariable("VMAPFINDUV") is not { Length: > 0 } spec || spec.Split('|') is not [var addon, var map, var text])
            return;
        var target = float.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        var doc = DmxBinary.ReadFile(MapFixtures.VmapSource(addon, map)!);
        var shown = 0;
        foreach (var e in doc.Elements)
        {
            if (e.Get<DmxBinary.Element>("meshData") is not { } data)
                continue;
            foreach (var s in data.Get<DmxBinary.Element>("faceVertexData")?.GetElements("streams") ?? [])
            {
                if (!s.Name.StartsWith("texcoord", StringComparison.Ordinal) || s.Get<object?[]>("data") is not { } values)
                    continue;
                for (var i = 0; i < values.Length && shown < 40; i++)
                    if (values[i] is Vector2 uv)
                        foreach (var (c, v) in new[] { ('u', uv.X), ('v', uv.Y) })
                            if (MathF.Abs(v - target) < 1e-5f)
                            {
                                shown++;
                                output.WriteLine($"node {e.GetValue<int>("nodeID")} {s.Name} corner {i} {c} = {v:R} ({BitConverter.SingleToInt32Bits(v):X8})");
                            }
            }
        }
        output.WriteLine($"{shown} found");
    }
}
