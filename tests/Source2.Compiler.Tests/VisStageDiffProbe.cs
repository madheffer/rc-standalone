using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>VISDIFF=&lt;addon&gt;|&lt;map&gt;|&lt;compiledIn&gt;[|clusters]</c>):
/// visibility's early stages on our trace scene (from the .vmap) and on
/// Valve's .rte side by side, to find the first stage that parts: the
/// octree (leaf masks), the regions, the outside test, and with
/// <c>clusters</c> the cluster generation (half an hour each on atixref).
/// Also the two scenes' triangle lists: count, and how many sit at the same
/// index.
/// </summary>
public class VisStageDiffProbe(ITestOutputHelper output)
{
    [Fact]
    public void FirstDifference()
    {
        if (Environment.GetEnvironmentVariable("VISDIFF") is not { } spec)
            return;
        var p = spec.Split('|');
        var (addon, map, compiledIn) = (p[0], p[1], p[2]);
        var source = MapFixtures.VmapSource(addon, map)!;
        var valveRte = RayTraceEnvironment.ReadFile(Path.Combine(Path.GetTempPath(), "csgo_addons", compiledIn, "maps", map + ".rte"));
        var pak = CS2Fixtures.StockPak()!;
        var schema = MapFixtures.GameSchema()!;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        var packages = new[] { "csgo", "core" }.Select(d => { var pk = new ValvePak.Package(); pk.Read(Path.Combine(game, d, "pak01_dir.vpk")); return pk; }).ToList();
        var visFlags = new MaterialVisFlags.Source([Path.Combine(game, "csgo_addons", addon)], packages);
        bool RendersAsWorld(string c) => schema.IsSolidClass(c) && schema.HasFlag(c, "render_as_world_but_physics_as_entity");
        var list = TraceScene.Triangles(MapMeshes.Read(DmxBinary.ReadFile(source)), content.Material, m => visFlags[m], RendersAsWorld);
        var ours = TraceScene.Environment(list);
        output.WriteLine($"triangles: ours {ours.TriangleCount:n0}, valve {valveRte.TriangleCount:n0}");
        output.WriteLine($"traced bounds: ours {ours.TracedBounds}, valve {valveRte.TracedBounds}");
        // The corners each scene's voxelizer and tracer read, as exact bits: same ordered
        // triple, same triple under rotation, or no counterpart.
        static string Key(System.Numerics.Vector3[] v) => string.Join(";", v.Select(x => $"{BitConverter.SingleToInt32Bits(x.X)},{BitConverter.SingleToInt32Bits(x.Y)},{BitConverter.SingleToInt32Bits(x.Z)}"));
        var valveOrdered = new Dictionary<string, int>();
        var valveRotated = new Dictionary<string, int>();
        for (var i = 0; i < valveRte.TriangleCount; i++)
            if (valveRte.TracedCorners(i) is { } v)
            {
                valveOrdered[Key(v)] = valveOrdered.GetValueOrDefault(Key(v)) + 1;
                foreach (var r in new[] { v, new[] { v[1], v[2], v[0] }, new[] { v[2], v[0], v[1] } })
                    valveRotated[Key(r)] = valveRotated.GetValueOrDefault(Key(r)) + 1;
            }
        int same = 0, rotated = 0, none = 0, sameIndex = 0;
        for (var i = 0; i < ours.TriangleCount; i++)
            if (ours.TracedCorners(i) is { } v)
            {
                if (valveOrdered.ContainsKey(Key(v))) same++;
                else if (valveRotated.ContainsKey(Key(v))) rotated++;
                else
                {
                    none++;
                    if (none <= 5) output.WriteLine($"  unmatched {i}: {string.Join(" ", v.Select(x => x.ToString("R", null)))}");
                }
                if (i < valveRte.TriangleCount && valveRte.TracedCorners(i) is { } w && Key(w) == Key(v)) sameIndex++;
            }
        output.WriteLine($"traced corners: same {same:n0}, rotated {rotated:n0}, unmatched {none:n0}, at the same index {sameIndex:n0}");
        var sameVertices = 0;
        var firstOff = -1;
        for (var i = 0; i < Math.Min(ours.TriangleCount, valveRte.TriangleCount); i++)
        {
            var (x, y) = (ours.Vertices(i), valveRte.Vertices(i));
            if (x != null && y != null && Key(x) == Key(y)) sameVertices++;
            else if (firstOff < 0) firstOff = i;
        }
        output.WriteLine($"vertices at the same index: {sameVertices:n0}, first differing index {firstOff}");
        var offNodes = Enumerable.Range(0, Math.Min(list.Count, valveRte.TriangleCount))
            .Where(i => ours.Vertices(i) is not { } x || valveRte.Vertices(i) is not { } y || Key(x) != Key(y))
            .GroupBy(i => list[i].Node).Select(g => $"{g.Key}x{g.Count()}");
        output.WriteLine("differing by node: " + string.Join(" ", offNodes.Take(40)));

        const float voxel = 8f;
        (VisVoxelizer.Octree Tree, VisRegions.Result Regions, VisOutside.Result Inside) Early(RayTraceEnvironment rte)
        {
            var (mins, maxs) = rte.TracedBounds;
            var (min, max) = VisVoxelizer.RootCube(mins, maxs, voxel);
            var tree = VisVoxelizer.Build(rte, min, max, voxel, VisVoxelizer.VoxelHints([], mins, maxs, min, max, voxel));
            var side = VisVoxelizer.VoxelsPerRoot(min, max, voxel) / VisVoxelizer.VoxelsPerLeaf;
            var regions = VisRegions.Build(tree, side);
            return (tree, regions, VisOutside.Detect(tree, regions, rte, voxel));
        }
        var a = Early(ours);
        var b = Early(valveRte);
        var maskDiff = a.Tree.LeafMasks.Count(kv => !b.Tree.LeafMasks.TryGetValue(kv.Key, out var m) || m != kv.Value)
                       + b.Tree.LeafMasks.Keys.Count(k => !a.Tree.LeafMasks.ContainsKey(k));
        output.WriteLine($"octree: leaf masks ours {a.Tree.LeafMasks.Count:n0} valve {b.Tree.LeafMasks.Count:n0}, differing {maskDiff:n0}; nodes {a.Tree.Nodes:n0} / {b.Tree.Nodes:n0}");
        output.WriteLine($"regions: ours {a.Regions.Regions.Count:n0} valve {b.Regions.Regions.Count:n0}");
        var statusDiff = a.Regions.Regions.Count == b.Regions.Regions.Count
            ? Enumerable.Range(0, a.Inside.Regions.Count).Count(i => a.Inside.Regions[i] != b.Inside.Regions[i]) : -1;
        output.WriteLine($"outside: inside {a.Inside.Inside:n0} / {b.Inside.Inside:n0}, outside {a.Inside.Outside:n0} / {b.Inside.Outside:n0}, statuses differing {statusDiff:n0}");
        if (p.Length > 3 && p[3] == "clusters" && maskDiff == 0 && statusDiff == 0)
        {
            var compactA = VisRegions.Compact(a.Regions, a.Inside.Regions);
            var compactB = VisRegions.Compact(b.Regions, b.Inside.Regions);
            var setsA = VisClusters.Generate(ours, a.Tree, compactA, VisClusters.SplitHints.From([]));
            var setsB = VisClusters.Generate(valveRte, b.Tree, compactB, VisClusters.SplitHints.From([]));
            var ca = setsA.SelectMany(s => s.Clusters).ToList();
            var cb = setsB.SelectMany(s => s.Clusters).ToList();
            output.WriteLine($"clusters: ours {ca.Count:n0} valve {cb.Count:n0}");
        }
    }
}
