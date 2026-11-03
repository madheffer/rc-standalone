using System.Numerics;
using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>BAKEBEFORE=&lt;vmap&gt;|&lt;capture_bake.py jsonl&gt;</c>): each subdivided mesh's
/// faces as our bake leaves them before the vertex merge, against Valve's "baked" snapshot with
/// the same face count: the first faces whose corner positions differ, and the coincident vertex
/// pairs on each side.
/// </summary>
public class BakeBeforeMergeProbe(ITestOutputHelper output)
{
    [Fact]
    public void AgainstBakedSnapshot()
    {
        if (Environment.GetEnvironmentVariable("BAKEBEFORE") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var valve = new List<List<(int V, Vector3 P)>>();
        foreach (var line in File.ReadLines(parts[1]))
        {
            var j = JsonDocument.Parse(line).RootElement;
            if (!j.TryGetProperty("tag", out var tag) || tag.GetString() != "baked")
                continue;
            foreach (var f in j.GetProperty("faces").EnumerateArray())
                valve.Add([.. f.GetProperty("loop").EnumerateArray().Select(c => (c[0].GetInt32(), new Vector3(c[1][0].GetSingle(), c[1][1].GetSingle(), c[1][2].GetSingle())))]);
            break;
        }
        var doc = DmxBinary.ReadFile(parts[0]);
        foreach (var mesh in MapMeshes.Read(doc).DistinctBy(m => m.NodeId))
        {
            var data = mesh.Element?.Get<DmxBinary.Element>("meshData");
            var levels = data?.Get<DmxBinary.Element>("subdivisionData")?.Get<object?[]>("subdivisionLevels");
            if (data == null || levels == null || !levels.Any(x => x is int i && i > 0))
                continue;
            List<List<(int V, Vector3 P)>>? ours = null;
            var ourOps = new List<string>();
            // The face list before each op, so ourStates[i + 1] is the state after op i, as Valve's capture logs it.
            var ourStates = new List<string>();
            static string State(HalfEdgeMesh m) => string.Join(" ", m.Faces.Handles.Select(f => $"{f}:[{string.Join(",", m.Loop(f).Select(h => $"{h}/{m.He(h).Vertex}"))}]"));
            HalfEdgeMesh.OpLog = (m, name, args) =>
            {
                ourOps.Add($"{name} {string.Join(",", args)}");
                ourStates.Add(State(m));
            };
            SubdivisionBake.BeforeMerge = m => ours = [.. m.Faces.Handles.Select(f => m.Loop(f).Select(h => (m.He(h).Vertex, m.Vertices[m.He(h).Vertex].Position)).ToList())];
            try
            {
                SubdivisionBake.Bake(data);
            }
            catch (NotSupportedException e)
            {
                output.WriteLine($"node {mesh.NodeId}: {e.Message}");
            }
            finally
            {
                SubdivisionBake.BeforeMerge = null;
                HalfEdgeMesh.OpLog = null;
            }
            var valveOps = File.ReadLines(parts[1]).Select(l => JsonDocument.Parse(l).RootElement)
                .Where(j => j.TryGetProperty("op", out var op) && op.GetString() is "AddVertexToEdge" or "AddEdgeToFace" or "CollapseFace")
                .Select(j => $"{j.GetProperty("op").GetString()} {string.Join(",", j.GetProperty("args").EnumerateArray().Select(x => x.GetInt32()))}").ToList();
            foreach (var m in ourOps.Where(o => o.StartsWith("MergeVertexPair", StringComparison.Ordinal)))
                output.WriteLine($"  ours: {m}");
            // Each merge's face list (ours is logged after the merge, as the capture's hook sends it).
            var valveMerges = File.ReadLines(parts[1]).Select(l => JsonDocument.Parse(l).RootElement)
                .Where(j => j.TryGetProperty("op", out var op) && op.GetString() == "MergeVertexPair" && j.TryGetProperty("faces", out var fs) && fs.ValueKind == JsonValueKind.Array)
                .Select(j => string.Join(" ", j.GetProperty("faces").EnumerateArray().Select(f => $"{f[0].GetInt32() & 0x3fffff}:[{string.Join(",", f[1].EnumerateArray().Select(c => $"{c[0].GetInt32()}/{c[1].GetInt32()}"))}]"))).ToList();
            var ourMerges = ourOps.Select((o, i) => (o, i)).Where(x => x.o.StartsWith("MergeVertexPair", StringComparison.Ordinal)).Select(x => ourStates[x.i]).ToList();
            for (var i = 0; i < Math.Min(valveMerges.Count, ourMerges.Count); i++)
            {
                var ow = ourMerges[i].Split(' ');
                var vw = valveMerges[i].Split(' ');
                output.WriteLine($"  merge {i}: {(ow.SequenceEqual(vw) ? "faces equal" : "faces differ")} ({ow.Length} / {vw.Length})");
                if (!ow.SequenceEqual(vw))
                {
                    output.WriteLine($"    ours  {string.Join(" ", ow.Where(x => !vw.Contains(x)).Take(12))}");
                    output.WriteLine($"    valve {string.Join(" ", vw.Where(x => !ow.Contains(x)).Take(12))}");
                    var firstDiff = Enumerable.Range(0, Math.Min(ow.Length, vw.Length)).FirstOrDefault(k => ow[k] != vw[k], -1);
                    output.WriteLine($"    first dense difference at {firstDiff}: ours {(firstDiff >= 0 ? ow[firstDiff] : "")} valve {(firstDiff >= 0 ? vw[firstDiff] : "")}");
                }
            }
            ourOps.RemoveAll(o => o.StartsWith("MergeVertexPair", StringComparison.Ordinal));
            var at0 = Enumerable.Range(0, Math.Min(ourOps.Count, valveOps.Count)).FirstOrDefault(i => ourOps[i] != valveOps[i], -1);
            output.WriteLine($"  ops: ours {ourOps.Count}, Valve's {valveOps.Count}, first difference at {at0}");
            if (at0 >= 0)
                for (var i = Math.Max(0, at0 - 3); i < Math.Min(at0 + 8, Math.Min(ourOps.Count, valveOps.Count)); i++)
                    output.WriteLine($"    {i}: {ourOps[i],-32} | {valveOps[i]}");
            // The first op after which the face lists (handles, loops of half-edge/vertex) differ.
            var valveStates = File.ReadLines(parts[1]).Select(l => JsonDocument.Parse(l).RootElement)
                .Where(j => j.TryGetProperty("op", out var op) && op.GetString() is "AddVertexToEdge" or "AddEdgeToFace" or "CollapseFace")
                .Select(j => string.Join(" ", j.GetProperty("faces").EnumerateArray().Select(f => $"{f[0].GetInt32() & 0x3fffff}:[{string.Join(",", f[1].EnumerateArray().Select(c => $"{c[0].GetInt32()}/{c[1].GetInt32()}"))}]"))).ToList();
            for (var i = 0; i + 1 < ourStates.Count && i < valveStates.Count; i++)
                if (ourStates[i + 1] != valveStates[i])
                {
                    output.WriteLine($"  state differs after op {i} ({valveOps[i]})");
                    var ow = ourStates[i + 1].Split(' ');
                    var vw = valveStates[i].Split(' ');
                    output.WriteLine($"    ours  {string.Join(" ", ow.Where(x => !vw.Contains(x)).Take(12))}");
                    output.WriteLine($"    valve {string.Join(" ", vw.Where(x => !ow.Contains(x)).Take(12))}");
                    output.WriteLine($"    counts {ow.Length} / {vw.Length}; dense order equal: {ow.SequenceEqual(vw)}");
                    break;
                }
            output.WriteLine($"node {mesh.NodeId}: ours {ours?.Count} faces, Valve's {valve.Count}");
            if (ours == null)
                continue;
            var shown = 0;
            for (var f = 0; f < Math.Min(ours.Count, valve.Count) && shown < 10; f++)
                if (!ours[f].Select(c => c.P).SequenceEqual(valve[f].Select(c => c.P)))
                {
                    shown++;
                    output.WriteLine($"  face {f}: ours {string.Join(" ", ours[f].Select(c => $"{c.V}({c.P.X},{c.P.Y},{c.P.Z})"))}");
                    output.WriteLine($"          valve {string.Join(" ", valve[f].Select(c => $"{c.V}({c.P.X},{c.P.Y},{c.P.Z})"))}");
                }
            foreach (var (name, side) in new[] { ("ours", ours), ("valve", valve) })
            {
                var at = side.SelectMany(x => x).DistinctBy(c => c.V).GroupBy(c => c.P).Where(g => g.Count() > 1);
                output.WriteLine($"  {name} coincident: {string.Join("; ", at.Select(g => $"({g.Key.X},{g.Key.Y},{g.Key.Z}) {string.Join(",", g.Select(c => c.V))}"))}");
            }
        }
    }
}
