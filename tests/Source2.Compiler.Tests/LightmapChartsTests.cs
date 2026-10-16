using System.Buffers.Binary;
using System.Text.Json;
using Source2.Compiler.Lighting;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="LightmapCharts"/> against Valve's charting, captured by
/// tools/vis/capture_charts.py (<c>CHARTCAP=&lt;jsonl&gt;</c>): each chart mesh as
/// StaticLighting_ChartMesh received it goes through the port, and every
/// array it leaves with must be Valve's bit for bit.
/// </summary>
public class LightmapChartsTests(ITestOutputHelper output)
{
    [Fact]
    public void EveryCapturedMeshChartsAsValves()
    {
        if (Environment.GetEnvironmentVariable("CHARTCAP") is not { Length: > 0 } path)
            return;
        var calls = new Dictionary<string, Queue<JsonElement>>();
        var enters = new Dictionary<string, Queue<JsonElement>>();
        var pairs = new List<(JsonElement Call, JsonElement Enter, JsonElement Leave)>();
        foreach (var line in File.ReadLines(path))
        {
            var e = JsonDocument.Parse(line).RootElement.Clone();
            var mesh = e.GetProperty("mesh").GetString()!;
            var stage = e.GetProperty("stage").GetString();
            var into = stage == "call" ? calls : stage == "enter" ? enters : null;
            if (into is not null)
            {
                if (!into.TryGetValue(mesh, out var q))
                    into[mesh] = q = new Queue<JsonElement>();
                q.Enqueue(e);
            }
            else
                pairs.Add((calls[mesh].Dequeue(), enters[mesh].Dequeue(), e));
        }
        int exact = 0;
        var misses = new SortedDictionary<string, int>();
        foreach (var (call, enter, leave) in pairs)
        {
            var head = Hex(enter, "head");
            int I(byte[] h, int o) => BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(o));
            var scales = Hex(call, "scales");
            var input = new LightmapCharts.Input
            {
                Vertices = Hex(enter, "vertices"), Stride = I(head, 0x20), PositionOffset = I(head, 0x58), TexcoordOffset = I(head, 0x5c),
                Indices = Ints(Hex(enter, "indices")), CornerNeighbours = Ints(Hex(enter, "corners")), SourceVertex = Ints(Hex(enter, "list")),
                TriangleScales = Floats(scales), Aspect = Bits(call, "xmm1"), Scale = Bits(call, "xmm2"), Ratio = Bits(call, "xmm3"),
                MeshUvs = call.GetProperty("flag").GetInt32() != 0,
            };
            var r = LightmapCharts.Run(input);
            var out_ = Hex(leave, "head");
            var diffs = new List<string>();
            void Check(string what, bool same) { if (!same) diffs.Add(what); }
            Check("vertex count", r.VertexCount == I(out_, 0x24));
            Check("chart count", r.ChartCount == I(out_, 0x48));
            Check("vertices", r.Vertices.AsSpan().SequenceEqual(Hex(leave, "vertices")));
            Check("indices", r.Indices.AsSpan().SequenceEqual(Ints(Hex(leave, "indices"))));
            Check("triangle charts", r.TriangleCharts.AsSpan().SequenceEqual(Ints(Hex(leave, "triangle_charts"))));
            Check("chart uvs", Bytes(r.ChartUvs).AsSpan().SequenceEqual(Hex(leave, "chart_uvs")));
            Check("source vertex", r.SourceVertex.AsSpan().SequenceEqual(Ints(Hex(leave, "list"))));
            Check("uv area", BitConverter.SingleToInt32Bits(r.UvArea) == I(out_, 0x68));
            Check("world area", BitConverter.SingleToInt32Bits(r.WorldArea) == I(out_, 0x6c));
            if (diffs.Count == 0)
                exact++;
            else
            {
                foreach (var d in diffs)
                    misses[d] = misses.GetValueOrDefault(d) + 1;
                if (misses.Values.Sum() <= 12)
                    output.WriteLine($"{enter.GetProperty("mesh").GetString()}: {input.Indices.Length / 3} triangles, {string.Join(", ", diffs)}"
                                     + $" (charts {r.ChartCount} vs {I(out_, 0x48)}, vertices {r.VertexCount} vs {I(out_, 0x24)})");
            }
        }
        output.WriteLine($"{exact} of {pairs.Count} chart meshes exact");
        foreach (var (k, v) in misses)
            output.WriteLine($"  {v,5} {k}");
        Assert.Equal(pairs.Count, exact);
    }

    private static byte[] Hex(JsonElement e, string name) => Convert.FromHexString(e.GetProperty(name).GetString() ?? "");

    private static float Bits(JsonElement e, string name) => BitConverter.Int32BitsToSingle(unchecked((int)e.GetProperty(name).GetUInt32()));

    private static int[] Ints(byte[] b)
    {
        var r = new int[b.Length / 4];
        for (var i = 0; i < r.Length; i++)
            r[i] = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(i * 4));
        return r;
    }

    private static float[] Floats(byte[] b)
    {
        var r = new float[b.Length / 4];
        for (var i = 0; i < r.Length; i++)
            r[i] = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(i * 4));
        return r;
    }

    private static byte[] Bytes(float[] f)
    {
        var b = new byte[f.Length * 4];
        for (var i = 0; i < f.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i * 4), f[i]);
        return b;
    }
}
