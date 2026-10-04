using System.Numerics;
using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The bake after its vertex merge against Valve's (tools/physics/capture_bake.py
/// "after" snapshots): BAKEMERGE=&lt;vmap&gt;|&lt;capture jsonl&gt;. Each subdivided
/// mesh of the map is matched to the snapshot with its face count and first
/// face; then every face's loop (corner positions bit for bit), the vertex
/// identities (one consistent mapping between Valve's handles and ours) and
/// the half-edge count must agree.
/// </summary>
public class BakeMergeCaptureTests(ITestOutputHelper output)
{
    private sealed record Snapshot(int HalfEdges, List<List<(int Vertex, Vector3 At)>> Faces);

    [Fact]
    public void AfterTheMerge()
    {
        if (Environment.GetEnvironmentVariable("BAKEMERGE") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var snapshots = new List<Snapshot>();
        foreach (var line in File.ReadLines(parts[1]))
        {
            var j = JsonDocument.Parse(line).RootElement;
            if (!j.TryGetProperty("tag", out var tag) || tag.GetString() != "after")
                continue;
            var faces = new List<List<(int, Vector3)>>();
            foreach (var f in j.GetProperty("faces").EnumerateArray())
                faces.Add([.. f.GetProperty("loop").EnumerateArray().Select(c =>
                {
                    var p = c[1];
                    return (c[0].GetInt32(), p.ValueKind == JsonValueKind.Null ? new Vector3(float.NaN) : new Vector3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle()));
                })]);
            snapshots.Add(new Snapshot(j.GetProperty("halfedges").GetInt32(), faces));
        }

        int compared = 0, exact = 0;
        var doc = DmxBinary.ReadFile(parts[0]);
        foreach (var mesh in MapMeshes.Read(doc).DistinctBy(m => m.NodeId))
        {
            var data = mesh.Element?.Get<DmxBinary.Element>("meshData");
            var levels = data?.Get<DmxBinary.Element>("subdivisionData")?.Get<object?[]>("subdivisionLevels");
            if (data == null || levels == null || !levels.Any(x => x is int i && i > 0))
                continue;
            HalfEdgeMesh? after = null;
            SubdivisionBake.AfterMerge = m => after = m;
            try
            {
                SubdivisionBake.Bake(data);
            }
            finally
            {
                SubdivisionBake.AfterMerge = null;
            }
            var ours = after!.Faces.Handles.Select(f => after.Loop(f).Select(h => (Vertex: after.He(h).Vertex, At: after.Vertices[after.He(h).Vertex].Position)).ToList()).ToList();
            var match = snapshots.FirstOrDefault(s => s.Faces.Count == ours.Count && s.Faces[0].Select(c => c.At).SequenceEqual(ours[0].Select(c => c.At)));
            if (match == null)
                continue;
            compared++;
            var why = Compare(ours, after.HalfEdges.Count, match);
            if (why == null)
                exact++;
            output.WriteLine($"node {mesh.NodeId}: {ours.Count} faces, {after.HalfEdges.Count} half-edges (Valve's {match.HalfEdges}): {why ?? "exact"}");
        }
        output.WriteLine($"{compared} meshes compared, {exact} exact");
        Assert.Equal(compared, exact);
    }

    private static string? Compare(List<List<(int Vertex, Vector3 At)>> ours, int halfEdges, Snapshot valve)
    {
        var toValve = new Dictionary<int, int>();
        var toOurs = new Dictionary<int, int>();
        for (var f = 0; f < ours.Count; f++)
        {
            if (ours[f].Count != valve.Faces[f].Count)
                return $"face {f}: {ours[f].Count} corners against {valve.Faces[f].Count}";
            for (var k = 0; k < ours[f].Count; k++)
            {
                var (v, at) = ours[f][k];
                var (w, theirs) = valve.Faces[f][k];
                if (!Bits(at, theirs))
                    return $"face {f} corner {k}: {at} against {theirs}";
                if (toValve.TryGetValue(v, out var mapped) ? mapped != w : toOurs.ContainsKey(w))
                    return $"face {f} corner {k}: our vertex {v} against Valve's {w} (identity differs)";
                toValve[v] = w;
                toOurs[w] = v;
            }
        }
        return halfEdges == valve.HalfEdges ? null : $"{halfEdges} half-edges against {valve.HalfEdges}";
    }

    private static bool Bits(Vector3 a, Vector3 b)
        => BitConverter.SingleToUInt32Bits(a.X) == BitConverter.SingleToUInt32Bits(b.X)
           && BitConverter.SingleToUInt32Bits(a.Y) == BitConverter.SingleToUInt32Bits(b.Y)
           && BitConverter.SingleToUInt32Bits(a.Z) == BitConverter.SingleToUInt32Bits(b.Z);
}
