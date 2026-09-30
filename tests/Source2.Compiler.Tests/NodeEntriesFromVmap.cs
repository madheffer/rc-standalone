using System.Numerics;
using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>NODEENTRIES=&lt;capture&gt;</c>, <c>NODEENTRIES_VMAP=&lt;vmap&gt;</c>):
/// the node mesh entries BuildNode starts from, as
/// <c>tools/vis/capture_nodeentries.py</c> recorded them, against the pieces
/// we build from the .vmap: which match, and for the rest the first stream
/// that differs.
/// </summary>
public class NodeEntriesFromVmap(ITestOutputHelper output)
{
    internal sealed record Captured(string Stage, int Index, string Material, int Stride, float[] Vertices, int[] Indices, string[] Streams, byte[] Raw);

    internal static List<Captured> Read(string path)
    {
        var data = File.ReadAllBytes(path);
        var list = new List<Captured>();
        for (var at = 0; at < data.Length;)
        {
            var n = BitConverter.ToInt32(data, at);
            var head = JsonDocument.Parse(data.AsMemory(at + 4, n)).RootElement;
            at += 4 + n;
            var m = BitConverter.ToInt32(data, at);
            var blob = data.AsSpan(at + 4, m).ToArray();
            at += 4 + m;
            if (head.GetProperty("ev").GetString() != "entry")
                continue;
            int nv = head.GetProperty("nv").GetInt32(), stride = head.GetProperty("stride").GetInt32(), ni = head.GetProperty("ni").GetInt32();
            var v = new float[nv * stride];
            Buffer.BlockCopy(blob, 0x238, v, 0, v.Length * 4);
            var idx = new int[ni];
            Buffer.BlockCopy(blob, 0x238 + v.Length * 4, idx, 0, ni * 4);
            list.Add(new Captured(head.GetProperty("stage").GetString()!, head.GetProperty("i").GetInt32(), head.GetProperty("material").GetString()!,
                                  stride, v, idx, [.. head.GetProperty("streams").EnumerateArray().Select(s => s.GetProperty("name").GetString()!)],
                                  blob.AsSpan(0, 0x238).ToArray()));
        }
        return list;
    }

    [Fact]
    public void BuildNodeInput()
    {
        if (Environment.GetEnvironmentVariable("NODEENTRIES") is not { } path || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap)
            return;
        var valve = Read(path).Where(c => c.Stage == "BuildNode:in").ToList();
        var ours = new List<(string Material, float[] V, int Stride, int NodeId)>();
        foreach (var mesh in MapMeshes.Read(DmxBinary.ReadFile(vmap)))
        {
            if (mesh.Element is null || mesh.Hidden || mesh.ParentType != "CMapWorld")
                continue;
            var names = mesh.Element.Get<DmxBinary.Element>("meshData")?.Get<object?[]>("materials")?.OfType<string>().ToArray() ?? [];
            var world = mesh.World;
            foreach (var piece in MapMeshCorners.Build(mesh.Element, true, p => MapMeshes.Transform(world, p), withTangent: true))
            {
                var v = (float[])piece.Vertices.Clone();
                var s = piece.Stride;
                for (var c = 0; c < v.Length / s; c++)
                {
                    var p = MapMeshes.Transform(world, new Vector3(v[c * s], v[c * s + 1], v[c * s + 2]));
                    (v[c * s], v[c * s + 1], v[c * s + 2]) = (p.X, p.Y, p.Z);
                    foreach (var st in piece.Streams.Where(x => x.Name is "normal" or "tangent"))
                    {
                        var d = Rotate(world, new Vector3(v[c * s + st.First], v[c * s + st.First + 1], v[c * s + st.First + 2]));
                        (v[c * s + st.First], v[c * s + st.First + 1], v[c * s + st.First + 2]) = (d.X, d.Y, d.Z);
                    }
                }
                var name = piece.Material < names.Length ? names[piece.Material] : "?";
                ours.Add((name, v, s, mesh.NodeId));
            }
        }
        output.WriteLine($"valve {valve.Count} entries, ours {ours.Count} pieces");
        if (Environment.GetEnvironmentVariable("NODEENTRIES_DETAIL") == "1")
        {
            var first = MapMeshes.Read(DmxBinary.ReadFile(vmap)).First(m => m.NodeId == 57).Element!;
            foreach (var st in first.Get<DmxBinary.Element>("meshData")!.Get<DmxBinary.Element>("faceVertexData")!.GetElements("streams"))
                output.WriteLine($"  node 57 faceVertexData stream {st.Name}: {st.Get<object?[]>("data")?.Length} values, first {string.Join(" ", (st.Get<object?[]>("data") ?? []).Take(3).Select(x => x?.ToString()))}");
        }
        var used = new HashSet<int>();
        foreach (var e in valve)
        {
            var key = Positions(e.Vertices, e.Stride);
            var j = ours.FindIndex(o => !used.Contains(ours.IndexOf(o)) && string.Equals(o.Material, e.Material, StringComparison.OrdinalIgnoreCase)
                                        && Positions(o.V, o.Stride).SequenceEqual(key));
            if (j < 0)
            {
                output.WriteLine($"entry {e.Index} {e.Material} {e.Vertices.Length / e.Stride}v: no piece with the same corners");
                continue;
            }
            used.Add(j);
            var o = ours[j];
            var diff = "same";
            if (o.Stride != e.Stride)
                diff = $"stride {o.Stride} vs {e.Stride} [{string.Join(",", e.Streams)}]";
            else if (o.V.Length != e.Vertices.Length)
                diff = $"{o.V.Length / o.Stride} vs {e.Vertices.Length / e.Stride} corners";
            else
                for (var k = 0; k < o.V.Length; k++)
                    if (BitConverter.SingleToInt32Bits(o.V[k]) != BitConverter.SingleToInt32Bits(e.Vertices[k]))
                    {
                        diff = $"float {k % e.Stride} of corner {k / e.Stride}: ours {o.V[k]:R} valve {e.Vertices[k]:R}";
                        break;
                    }
            output.WriteLine($"entry {e.Index} {Path.GetFileName(e.Material)} node {o.NodeId} (piece {j}): {diff}");
            if (o.Stride == e.Stride && o.V.Length == e.Vertices.Length && Environment.GetEnvironmentVariable("NODEENTRIES_DETAIL") == "1")
            {
                var byFloat = Enumerable.Range(0, e.Stride).Select(f => Enumerable.Range(0, e.Vertices.Length / e.Stride)
                    .Count(c => BitConverter.SingleToInt32Bits(o.V[c * e.Stride + f]) != BitConverter.SingleToInt32Bits(e.Vertices[c * e.Stride + f]))).ToArray();
                output.WriteLine($"    differing corners per float: {string.Join(" ", byFloat)}");
                var firstPos = Enumerable.Range(0, e.Vertices.Length / e.Stride).FirstOrDefault(c => Enumerable.Range(0, 3).Any(k =>
                    BitConverter.SingleToInt32Bits(o.V[c * e.Stride + k]) != BitConverter.SingleToInt32Bits(e.Vertices[c * e.Stride + k])), -1);
                if (firstPos >= 0)
                    output.WriteLine($"    positions differ in order from corner {firstPos}: ours {string.Join(" ", Enumerable.Range(firstPos - firstPos % 3, 6).Select(c => $"({o.V[c * e.Stride]},{o.V[c * e.Stride + 1]},{o.V[c * e.Stride + 2]})"))}"
                                     + $" valve {string.Join(" ", Enumerable.Range(firstPos - firstPos % 3, 6).Select(c => $"({e.Vertices[c * e.Stride]},{e.Vertices[c * e.Stride + 1]},{e.Vertices[c * e.Stride + 2]})"))}");
                if (e.Stride > 12)
                    output.WriteLine($"    valve PerVertexLighting of corners 0..3: {string.Join(" | ", Enumerable.Range(0, 4).Select(c => string.Join(",", e.Vertices.Skip(c * e.Stride + 12).Take(4).Select(x => $"{x * 255:0.##}"))))}");
            }
        }
    }

    static IEnumerable<(float, float, float)> Positions(float[] v, int stride)
        => Enumerable.Range(0, v.Length / stride).Select(c => (v[c * stride], v[c * stride + 1], v[c * stride + 2])).Order();

    static Vector3 Rotate(float[] m, Vector3 d)
        => new(m[0] * d.X + m[1] * d.Y + m[2] * d.Z, m[4] * d.X + m[5] * d.Y + m[6] * d.Z, m[8] * d.X + m[9] * d.Y + m[10] * d.Z);
}
