using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="TJunctionFix"/> against a node entries capture
/// (<c>tools/vis/capture_nodeentries.py</c>, <c>NODEENTRIES=&lt;capture&gt;</c>):
/// the entries FixTJunctionEdgeCracks received, through the port, against
/// what it left, every vertex float and index.
/// </summary>
public class TJunctionFixReplay(ITestOutputHelper output)
{
    [Fact]
    public void MatchesValve()
    {
        if (Environment.GetEnvironmentVariable("NODEENTRIES") is not { } path)
            return;
        var all = NodeEntriesFromVmap.Read(path);
        var before = all.Where(c => c.Stage == "FixTJunctions:in").ToList();
        var after = all.Where(c => c.Stage == "FixTJunctions:out").ToList();
        var meshes = before.Select(e => new TJunctionFix.Mesh
        {
            Vertices = [.. e.Vertices],
            Stride = e.Stride,
            Indices = [.. e.Indices],
            Group = BitConverter.ToInt32(e.Raw, 0x40),
            Excluded = (BitConverter.ToUInt64(e.Raw, 0x1b0) & 1) != 0 || e.Raw[0x1a4] != 0,
            SkipOwnNode = e.Raw[0x1a0] != 0,
        }).ToList();
        TJunctionFix.Run(meshes);
        int exact = 0, changed = 0, shown = 0;
        for (var i = 0; i < meshes.Count; i++)
        {
            var (o, e) = (meshes[i], after[i]);
            if (!before[i].Indices.SequenceEqual(e.Indices) || before[i].Vertices.Length != e.Vertices.Length)
                changed++;
            var same = o.Indices.SequenceEqual(e.Indices) && o.Vertices.Count == e.Vertices.Length
                       && o.Vertices.Select(BitConverter.SingleToInt32Bits).SequenceEqual(e.Vertices.Select(BitConverter.SingleToInt32Bits));
            if (same)
                exact++;
            else if (shown++ < 8)
            {
                var firstIndex = Enumerable.Range(0, Math.Min(o.Indices.Count, e.Indices.Length)).FirstOrDefault(k => o.Indices[k] != e.Indices[k], -1);
                var firstFloat = Enumerable.Range(0, Math.Min(o.Vertices.Count, e.Vertices.Length)).FirstOrDefault(k => BitConverter.SingleToInt32Bits(o.Vertices[k]) != BitConverter.SingleToInt32Bits(e.Vertices[k]), -1);
                output.WriteLine($"entry {i} {Path.GetFileName(e.Material)}: ours {o.Vertices.Count / o.Stride}v {o.Indices.Count / 3}t, valve {e.Vertices.Length / e.Stride}v {e.Indices.Length / 3}t (before {before[i].Vertices.Length / e.Stride}v {before[i].Indices.Length / 3}t); first index diff {firstIndex}, first float diff {firstFloat}"
                                 + (firstFloat >= 0 ? $" ({o.Vertices[firstFloat]:R} vs {e.Vertices[firstFloat]:R})" : ""));
            }
        }
        output.WriteLine($"{meshes.Count} entries ({changed} changed by Valve), {exact} exact");
        Assert.Equal(meshes.Count, exact);
    }
}

/// <summary>
/// BuildNode's closing weld (CMesh_Weld at 1/32) against a node entries
/// capture: the entries Step25d500 left, welded by <c>MeshWeld</c>, against
/// BuildNode's output. <c>NODEWELD_MODE</c> picks the renumbering.
/// </summary>
public class NodeWeldReplay(ITestOutputHelper output)
{
    [Fact]
    public void MatchesValve()
    {
        if (Environment.GetEnvironmentVariable("NODEENTRIES") is not { } path)
            return;
        var all = NodeEntriesFromVmap.Read(path);
        var before = all.Where(c => c.Stage == "Step25d500:out").ToList();
        var after = all.Where(c => c.Stage == "BuildNode:out").ToList();
        var renumber = Environment.GetEnvironmentVariable("NODEWELD_MODE") == "renumber";
        int exact = 0, shown = 0;
        for (var i = 0; i < before.Count; i++)
        {
            var (b, e) = (before[i], after[i]);
            var welds = !(b.Raw[0x1a0] != 0 && b.Raw[0x98] != 0 && (b.Raw[0xc0] & 0x10) != 0);
            var streams = b.Layout.Select(x => new Physics.MeshWeld.Stream(x.Name, x.First, x.Count, false, x.Type)).ToList();
            var (v, idx) = welds ? Physics.MeshWeld.Weld(b.Vertices, b.Stride, b.Indices, streams, 1f / 32f, renumber) : (b.Vertices, b.Indices);
            var same = idx.SequenceEqual(e.Indices) && v.Select(BitConverter.SingleToInt32Bits).SequenceEqual(e.Vertices.Select(BitConverter.SingleToInt32Bits));
            if (same)
                exact++;
            else if (shown++ < 6)
                output.WriteLine($"entry {i} {Path.GetFileName(b.Material)}: ours {v.Length / b.Stride}v {idx.Length / 3}t, valve {e.Vertices.Length / e.Stride}v {e.Indices.Length / 3}t (welds {welds})");
        }
        output.WriteLine($"{before.Count} entries, {exact} exact");
        Assert.Equal(before.Count, exact);
    }
}

/// <summary>
/// BuildNode end to end for a map without static props or later-added
/// entries: our entries from the .vmap (<see cref="NodeMeshEntries"/>), the
/// drops (+0x1a5 or attribute bit 34; then Step257b50's bits 10 and 36), the
/// T-junction fix and the closing weld, against BuildNode's captured output.
/// The entry flags and the baked PerVertexLighting come from the capture's
/// BuildNode input (the material-side flags are not ported yet).
/// <c>NODEENTRIES</c> and <c>NODEENTRIES_VMAP</c> as for NodeEntriesFromVmap.
/// </summary>
public class BuildNodeEndToEnd(ITestOutputHelper output)
{
    [Fact]
    public void MatchesValve()
    {
        if (Environment.GetEnvironmentVariable("NODEENTRIES") is not { } path || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap
            || CS2Fixtures.StockPak() is not { } pak)
            return;
        var all = NodeEntriesFromVmap.Read(path);
        var valveIn = all.Where(c => c.Stage == "BuildNode:in").ToList();
        var valveOut = all.Where(c => c.Stage == "BuildNode:out").ToList();
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vmap)))!;
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        IReadOnlyCollection<string> Signature(string material) => content.Read(material + "_c") is { } b
            ? [.. MaterialAuthor.ExtractInputSignature(b).Select(x => x.Semantic)] : [];
        var ours = NodeMeshEntries.FromWorld(DmxBinary.ReadFile(vmap), signature: Signature);
        Assert.Equal(valveIn.Count, ours.Count);

        var meshes = new List<(TJunctionFix.Mesh Mesh, NodeEntriesFromVmap.Captured Valve, NodeMeshEntries.Entry Ours)>();
        for (var i = 0; i < ours.Count; i++)
        {
            var (o, e) = (ours[i], valveIn[i]);
            var v = (float[])o.Vertices.Clone();
            foreach (var st in o.Streams.Where(x => x.Name == "PerVertexLighting"))
                for (var c = 0; c < v.Length / o.Stride; c++)
                    Array.Copy(e.Vertices, c * e.Stride + st.First, v, c * o.Stride + st.First, st.Count);
            var attr = BitConverter.ToUInt64(e.Raw, 0x1b0);
            if (e.Raw[0x1a5] != 0 || (attr & 0x400000000) != 0 || (attr & 0x400) != 0 || (attr & 0x1000000000) != 0)
                continue;
            meshes.Add((new TJunctionFix.Mesh
            {
                Vertices = [.. v], Stride = o.Stride, Indices = [.. o.Indices],
                Group = o.NodeId,
                Excluded = (attr & 1) != 0 || e.Raw[0x1a4] != 0,
                SkipOwnNode = e.Raw[0x1a0] != 0,
            }, e, o));
        }
        TJunctionFix.Run([.. meshes.Select(m => m.Mesh)]);
        Assert.Equal(valveOut.Count, meshes.Count);
        int exact = 0, shown = 0;
        for (var i = 0; i < meshes.Count; i++)
        {
            var (m, e, o) = meshes[i];
            var welds = !(e.Raw[0x1a0] != 0 && e.Raw[0x98] != 0 && (e.Raw[0xc0] & 0x10) != 0);
            var (v, idx) = welds ? Physics.MeshWeld.Weld([.. m.Vertices], m.Stride, [.. m.Indices], o.Streams, 1f / 32f, true) : ([.. m.Vertices], [.. m.Indices]);
            var want = valveOut[i];
            var same = idx.SequenceEqual(want.Indices) && v.Select(BitConverter.SingleToInt32Bits).SequenceEqual(want.Vertices.Select(BitConverter.SingleToInt32Bits));
            if (same)
                exact++;
            else if (shown++ < 6)
                output.WriteLine($"entry {i} {Path.GetFileName(want.Material)}: ours {v.Length / m.Stride}v {idx.Length / 3}t, valve {want.Vertices.Length / want.Stride}v {want.Indices.Length / 3}t");
        }
        output.WriteLine($"{meshes.Count} entries out of BuildNode, {exact} exact");
        Assert.Equal(meshes.Count, exact);
    }
}
