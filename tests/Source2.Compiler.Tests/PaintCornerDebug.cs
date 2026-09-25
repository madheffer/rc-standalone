using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>Exploration: every corner of a mesh node whose paint channel 0 is one of the given values: face, loop index, level, vertex position. <c>PAINTCORNERS=&lt;vmap&gt;|&lt;node&gt;|v,v,...</c>.</summary>
public class PaintCornerDebug(ITestOutputHelper output)
{
    [Fact]
    public void Corners()
    {
        if (Environment.GetEnvironmentVariable("PAINTCORNERS") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var doc = DmxBinary.ReadFile(parts[0]);
        var id = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        var mesh = doc.OfType("CMapMesh").First(m => m.GetValue<int>("nodeID") == id);
        var data = mesh.Get<DmxBinary.Element>("meshData")!;
        int[] Ints(string name) => (data.Get<object?[]>(name) ?? []).Select(x => x is int i ? i : -1).ToArray();
        var next = Ints("edgeNextIndices"); var to = Ints("edgeVertexIndices"); var cd = Ints("edgeVertexDataIndices"); var first = Ints("faceEdgeIndices"); var vd = Ints("vertexDataIndices");
        var pos = data.Get<DmxBinary.Element>("vertexData")!.GetElements("streams").First(s => s.Name.StartsWith("position")).Get<object?[]>("data")!;
        var paint = data.Get<DmxBinary.Element>("faceVertexData")!.GetElements("streams").First(s => s.Name.StartsWith("VertexPaintBlendParams")).Get<object?[]>("data")!;
        var levels = data.Get<DmxBinary.Element>("subdivisionData")?.Get<object?[]>("subdivisionLevels") ?? [];
        var want = parts[2].Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
        for (var f = 0; f < first.Length; f++)
        {
            var h = first[f]; var j = 0;
            do
            {
                var p = (Vector4)paint[cd[h]]!;
                if (want.Contains(p.X))
                    output.WriteLine($"face {f} corner {j} h{h} v{to[h]} at {pos[vd[to[h]]]}: paint {p.X} level {(cd[h] < levels.Length ? levels[cd[h]] : "-")}; face corners {string.Join(" ", Loop(f).Select(k => $"v{to[k]}:{((Vector4)paint[cd[k]]!).X}/L{(cd[k] < levels.Length ? levels[cd[k]] : "-")}"))}");
                h = next[h]; j++;
            } while (h != first[f]);
        }
        IEnumerable<int> Loop(int f) { var h = first[f]; do { yield return h; h = next[h]; } while (h != first[f]); }
    }
}
