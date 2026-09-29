using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Where <see cref="RayTraceEnvironment.WithTracerTree"/> parts from the file
/// it was made from: the scene facts, then the vis stages up to cluster
/// generation, stopping at the first that differs. RTREE=&lt;map&gt; (an
/// s2c_rc_probe or s2c_lighting compile).
/// </summary>
public class RebuiltTreeProbe(ITestOutputHelper output)
{
    [Fact]
    public void FirstDifference()
    {
        if (Environment.GetEnvironmentVariable("RTREE") is not { Length: > 0 } map)
            return;
        var addon = map == "ze_hold_em_p" ? "s2c_lighting" : "s2c_rc_probe";
        if (VisFixtures.RayTraceScene(addon, map) is not var (file, _))
            return;
        var rebuilt = file.WithTracerTree();
        void Say(string what, object a, object b) => output.WriteLine($"{what}: file {a} rebuilt {b}{(Equals(a, b) ? "" : "   <-- differs")}");
        Say("triangles", file.TriangleCount, rebuilt.TriangleCount);
        Say("mins", file.Mins, rebuilt.Mins);
        Say("maxs", file.Maxs, rebuilt.Maxs);
        Say("traced mins", file.TracedBounds.Mins, rebuilt.TracedBounds.Mins);
        Say("traced maxs", file.TracedBounds.Maxs, rebuilt.TracedBounds.Maxs);
        Say("tracer order", string.Join(",", file.TracerOrder), string.Join(",", rebuilt.TracerOrder));

        const float voxel = 8f;
        (VisVoxelizer.Octree Tree, VisRegions.Result Regions, VisOutside.Result Inside, VisRegions.Result Compact, List<VisClusterSet.Set> Sets) Run(RayTraceEnvironment rte)
        {
            var (mins, maxs) = rte.TracedBounds;
            var (min, max) = VisVoxelizer.RootCube(mins, maxs, voxel);
            var tree = VisVoxelizer.Build(rte, min, max, voxel, VisVoxelizer.VoxelHints([], mins, maxs, min, max, voxel));
            var side = VisVoxelizer.VoxelsPerRoot(min, max, voxel) / VisVoxelizer.VoxelsPerLeaf;
            var regions = VisRegions.Build(tree, side);
            var inside = VisOutside.Detect(tree, regions, rte, voxel);
            var compact = VisRegions.Compact(regions, inside.Regions);
            var sets = VisClusters.Generate(rte, tree, compact, VisClusters.SplitHints.From([]));
            return (tree, regions, inside, compact, sets);
        }
        var a = Run(file);
        var b = Run(rebuilt);
        Say("octree nodes", a.Tree.Nodes, b.Tree.Nodes);
        Say("regions", a.Regions.Regions.Count, b.Regions.Regions.Count);
        Say("outside verdicts", string.Join("", a.Inside.Regions.Select(s => (int)s)), string.Join("", b.Inside.Regions.Select(s => (int)s)));
        Say("compact regions", a.Compact.Regions.Count, b.Compact.Regions.Count);
        Say("clusters", a.Sets.Sum(s => s.Clusters.Count), b.Sets.Sum(s => s.Clusters.Count));
    }
}
