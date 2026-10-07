using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>VMAPVLIGHT=&lt;vmap&gt;</c>): the elements carrying
/// vertexLightingData / Positions / Normals, the attributes beside them, and
/// the first values of each.
/// </summary>
public class VmapVertexLightingProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("VMAPVLIGHT") is not { Length: > 0 } vmap)
            return;
        var doc = DmxBinary.ReadFile(vmap);
        var shown = 0;
        foreach (var e in doc.Elements)
        {
            if (!e.Attributes.Any(a => a.Key.StartsWith("vertexLighting", StringComparison.Ordinal)) || shown++ > 2)
                continue;
            output.WriteLine($"element {e.Name} ({e.Type}) nodeID {(e.Attributes.Any(a => a.Key == "nodeID") ? e.GetValue<int>("nodeID") : -1)}");
            foreach (var a in e.Attributes)
            {
                var text = a.Value switch
                {
                    object?[] arr => $"[{arr.Length}] " + string.Join(" ", arr.Take(4)),
                    byte[] bytes => $"bytes[{bytes.Length}] " + Convert.ToHexString(bytes.AsSpan(0, Math.Min(32, bytes.Length))),
                    System.Collections.ICollection c => $"({c.Count})",
                    _ => a.Value?.ToString() ?? "null",
                };
                output.WriteLine($"  {a.Key}: {a.Value?.GetType().Name} {text}");
                if (a.Value is object?[] items && items.FirstOrDefault() is byte[])
                    foreach (var item in items.Take(3).OfType<byte[]>())
                        output.WriteLine($"    bytes[{item.Length}] {Convert.ToHexString(item)}");
            }
        }
        output.WriteLine($"{shown} elements");
    }
}
