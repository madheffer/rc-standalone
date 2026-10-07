using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>MESHSTREAMS=&lt;addon&gt;|&lt;map&gt;|&lt;material substring&gt;</c>): each
/// CMapMesh using a matching material, with every faceVertexData stream's
/// attributes other than its data.
/// </summary>
public class MeshStreamProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("MESHSTREAMS") is not { Length: > 0 } spec || spec.Split('|') is not [var addon, var map, var part])
            return;
        var doc = DmxBinary.ReadFile(MapFixtures.VmapSource(addon, map)!);
        var shown = 0;
        foreach (var e in doc.Elements)
        {
            if (e.Get<DmxBinary.Element>("meshData") is not { } data)
                continue;
            var names = string.Join(",", (data.Get<object?[]>("materials") ?? []).Select(x => x as string ?? ""));
            if (!names.Contains(part, StringComparison.OrdinalIgnoreCase) || shown++ > 3)
                continue;
            output.WriteLine($"node {e.GetValue<int>("nodeID")}: {names}");
            foreach (var (group, label) in new[] { ("vertexData", "v"), ("faceVertexData", "fv") })
                foreach (var s in data.Get<DmxBinary.Element>(group)?.GetElements("streams") ?? [])
                {
                    output.WriteLine($"  {label} {s.Name}: " + string.Join(" ", s.Attributes.Where(a => a.Key != "data").Select(a => $"{a.Key}={a.Value}")));
                    if (s.Name.StartsWith("PerVertexLighting", StringComparison.Ordinal) && s.Get<object?[]>("data") is { } d)
                        output.WriteLine($"    {d.Length} values: " + string.Join(" ", d.Take(6)) + $"; distinct {d.Select(x => x?.ToString()).Distinct().Count()}");
                }
        }
    }
}
