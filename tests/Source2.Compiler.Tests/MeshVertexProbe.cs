using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: which .vmap vertices of one mesh sit at a world position, and
/// the faces (with their materials) that use each. For telling apart
/// vertices a compile keeps separate from those it joins.
/// <c>MESHVERT=&lt;vmap&gt;|&lt;node id&gt;|x,y,z[;x,y,z...]</c>.
/// </summary>
public class MeshVertexProbe(ITestOutputHelper output)
{
    [Fact]
    public void VerticesAtPositions()
    {
        if (Environment.GetEnvironmentVariable("MESHVERT") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var node = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        var targets = parts[2].Split(';').Select(t => t.Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray())
                              .Select(a => new Vector3(a[0], a[1], a[2])).ToList();
        var doc = DmxBinary.ReadFile(parts[0]);
        foreach (var mesh in MapMeshes.Read(doc).Where(m => m.NodeId == node))
        {
            var data = mesh.Element!.Get<DmxBinary.Element>("meshData")!;
            var next = Ints(data, "edgeNextIndices");
            var to = Ints(data, "edgeVertexIndices");
            var first = Ints(data, "faceEdgeIndices");
            var materials = data.Get<DmxBinary.Element>("faceData");
            output.WriteLine($"node {node}: {first.Length} faces, instances [{string.Join(",", mesh.Instances)}]");
            var matNames = data.Get<object?[]>("materials") ?? [];
            output.WriteLine("  materials: " + string.Join(" ", matNames.Select((m, i) => $"{i}={Path.GetFileNameWithoutExtension(m as string)}")));
            foreach (var st in data.Get<DmxBinary.Element>("faceData")?.GetElements("streams") ?? [])
            {
                var vals = st.Get<object?[]>("data") ?? [];
                output.WriteLine($"  faceData stream {st.Name}: {vals.Length} values, distinct {string.Join(",", vals.Distinct().Take(8))}");
            }
            // Connected parts: faces sharing a .vmap vertex, and each part's
            // vertex count per material.
            var loops = new List<int[]>();
            for (var f = 0; f < first.Length; f++)
            {
                var e = first[f];
                var vs = new List<int>();
                do
                {
                    vs.Add(to[e]);
                    e = next[e];
                }
                while (e != first[f] && vs.Count <= next.Length);
                loops.Add([.. vs]);
            }
            var parent = Enumerable.Range(0, first.Length).ToArray();
            int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
            var owner = new Dictionary<int, int>();
            for (var f = 0; f < loops.Count; f++)
            {
                foreach (var v in loops[f])
                {
                    if (owner.TryGetValue(v, out var g))
                        parent[Find(f)] = Find(g);
                    else
                        owner[v] = f;
                }
            }
            foreach (var part in Enumerable.Range(0, loops.Count).GroupBy(Find).OrderBy(g => g.Min()))
            {
                var perMaterial = part.GroupBy(f => Path.GetFileNameWithoutExtension(mesh.Faces[f].Material))
                                      .Select(g => $"{g.Key}:{g.SelectMany(f => loops[f]).Distinct().Count()}");
                output.WriteLine($"  part from face {part.Min()} ({part.Count()} faces): {string.Join(" ", perMaterial)}");
            }
            for (var f = 0; f < first.Length; f++)
            {
                var e = first[f];
                var corners = new List<int>();
                do
                {
                    corners.Add(to[e]);
                    e = next[e];
                }
                while (e != first[f] && corners.Count <= next.Length);
                var face = mesh.Faces[f];
                for (var c = 0; c < face.Corners.Length; c++)
                {
                    foreach (var t in targets)
                    {
                        if (Vector3.DistanceSquared(face.Corners[c], t) < 1e-4f)
                            output.WriteLine($"  {t}: vertex {corners[c]} in face {f} ({Path.GetFileNameWithoutExtension(face.Material)}), corners [{string.Join(",", corners)}]");
                    }
                }
            }
        }
    }

    private static int[] Ints(DmxBinary.Element data, string name) => (data.Get<object?[]>(name) ?? []).Select(x => x is int i ? i : 0).ToArray();
}
