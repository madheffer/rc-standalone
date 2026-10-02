using System.Numerics;
using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (ledger 38; <c>MESHTURN=&lt;capture_meshturn.py jsonl&gt;</c>,
/// <c>NODEENTRIES_VMAP</c>, <c>MESHTURN_NODES=1370,6516</c>): each listed
/// node's corners beside the Hammer mesh's corners as HammerMesh_TransformToWorld
/// received them (after the subdivision bake, before the world turn), paired
/// by their normal's bits: how many of our stored tangents equal Valve's.
/// </summary>
public class SubdividedTangentsProbe(ITestOutputHelper output)
{
    [Fact]
    public void AgainstHammerMesh()
    {
        if (Environment.GetEnvironmentVariable("MESHTURN") is not { } path || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap)
            return;
        var nodes = (Environment.GetEnvironmentVariable("MESHTURN_NODES") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
        static float F(string hex) => BitConverter.Int32BitsToSingle(Convert.ToInt32(hex, 16));
        var calls = new Dictionary<int, (string[] Matrix, string[]? Normals, string[]? Tangents)>();
        foreach (var line in File.ReadLines(path))
        {
            var d = JsonDocument.Parse(line).RootElement;
            var call = d.GetProperty("call").GetInt32();
            if (d.TryGetProperty("matrix", out var m))
            {
                var before = d.GetProperty("before");
                string[]? A(string k) => before.GetProperty(k).ValueKind == JsonValueKind.Null ? null : [.. before.GetProperty(k).EnumerateArray().Select(x => x.GetString()!)];
                calls[call] = ([.. m.EnumerateArray().Select(x => x.GetString()!)], A("normal"), A("tangent"));
            }
        }
        var ours = NodeMeshEntries.FromWorld(DmxBinary.ReadFile(vmap));
        foreach (var node in nodes)
        {
            var entries = ours.Where(e => e.NodeId == node).ToList();
            if (entries.Count == 0)
                continue;
            var turnBits = entries[0].Turn.Select(x => BitConverter.SingleToInt32Bits(x).ToString("x")).ToArray();
            var hit = calls.Values.FirstOrDefault(c => c.Matrix.Select(x => Convert.ToInt32(x, 16).ToString("x")).SequenceEqual(turnBits));
            if (hit.Normals == null || hit.Tangents == null)
            {
                output.WriteLine($"node {node}: no captured call with its matrix");
                continue;
            }
            // Valve's corners by normal bits.
            var byNormal = new Dictionary<(int, int, int), List<Vector4>>();
            for (var c = 0; c < hit.Normals.Length / 3; c++)
            {
                var key = (Convert.ToInt32(hit.Normals[c * 3], 16), Convert.ToInt32(hit.Normals[c * 3 + 1], 16), Convert.ToInt32(hit.Normals[c * 3 + 2], 16));
                if (!byNormal.TryGetValue(key, out var l))
                    byNormal[key] = l = [];
                l.Add(new Vector4(F(hit.Tangents[c * 4]), F(hit.Tangents[c * 4 + 1]), F(hit.Tangents[c * 4 + 2]), F(hit.Tangents[c * 4 + 3])));
            }
            int corners = 0, paired = 0, same = 0, shown = 0;
            foreach (var e in entries)
            {
                var nf = e.Streams.First(x => x.Name == "normal").First;
                var tf = e.Streams.First(x => x.Name == "tangent").First;
                for (var c = 0; c < e.Stored.Length / e.Stride; c++)
                {
                    corners++;
                    var at = c * e.Stride;
                    var key = (BitConverter.SingleToInt32Bits(e.Stored[at + nf]), BitConverter.SingleToInt32Bits(e.Stored[at + nf + 1]), BitConverter.SingleToInt32Bits(e.Stored[at + nf + 2]));
                    if (!byNormal.TryGetValue(key, out var cands))
                        continue;
                    paired++;
                    var t = new Vector4(e.Stored[at + tf], e.Stored[at + tf + 1], e.Stored[at + tf + 2], e.Stored[at + tf + 3]);
                    if (cands.Any(x => x == t))
                        same++;
                    else if (shown++ < 4)
                        output.WriteLine($"  node {node} corner {c}: normal {e.Stored[at + nf]:R},{e.Stored[at + nf + 1]:R},{e.Stored[at + nf + 2]:R} ours {t} valve {string.Join(" | ", cands.Distinct().Take(3))}");
                }
            }
            output.WriteLine($"node {node}: {corners} corners, {paired} paired by normal, {same} with Valve's tangent");
        }
    }
}
