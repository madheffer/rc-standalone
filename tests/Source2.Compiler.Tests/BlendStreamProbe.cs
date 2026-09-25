using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: the data streams of one .vmap mesh node (vertex, face-vertex,
/// edge and face), with each stream's value count and first values, for
/// finding where a mesh keeps its vertex paint.
/// <c>BLENDSTREAM=&lt;vmap&gt;|&lt;node id&gt;</c>.
/// </summary>
public class BlendStreamProbe(ITestOutputHelper output)
{
    [Fact]
    public void StreamsOfNode()
    {
        if (Environment.GetEnvironmentVariable("BLENDSTREAM") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var doc = DmxBinary.ReadFile(parts[0]);
        var id = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        var mesh = doc.OfType("CMapMesh").First(m => m.GetValue<int>("nodeID") == id);
        var data = mesh.Get<DmxBinary.Element>("meshData")!;
        var levels = data.Get<DmxBinary.Element>("subdivisionData")?.Get<object?[]>("subdivisionLevels") ?? [];
        output.WriteLine($"subdivision levels: {string.Join(" ", levels.GroupBy(x => x).Select(g => $"{g.Key}x{g.Count()}"))}; first 40: {string.Join(",", levels.Take(40))}");
        var sd = data.Get<DmxBinary.Element>("subdivisionData");
        if (sd != null)
        {
            foreach (var (k, val) in sd.Attributes)
                output.WriteLine($"SUBDIV ATTR {k}: {(val is object?[] arr ? $"array {arr.Length}" : val?.ToString())}");
            foreach (var st in sd.GetElements("streams"))
            {
                var d = st.Get<object?[]>("data") ?? [];
                output.WriteLine($"SUBDIV STREAM {st.Name}: {d.Length} values: {string.Join(" ", d.Take(4))}");
                if (st.Name.StartsWith("VertexPaintBlendParams") && Environment.GetEnvironmentVariable("BLENDSTREAM_DUMP") is { } dump)
                    File.WriteAllLines(dump, d.Select(x => ((System.Numerics.Vector4)x!).X.ToString("R")));
            }
        }
        foreach (var block in new[] { "vertexData", "faceVertexData", "edgeData", "faceData" })
        {
            var element = data.Get<DmxBinary.Element>(block);
            output.WriteLine($"{block}: size {element?.GetValue<int>("size")}");
            foreach (var stream in element?.GetElements("streams") ?? [])
            {
                var values = stream.Get<object?[]>("data") ?? [];
                output.WriteLine($"  {stream.Name} ({stream.Get<string>("semanticName")} {stream.GetValue<int>("semanticIndex")}): {values.Length} values: {string.Join(" ", values.Take(6))}");
            }
        }
    }
}
