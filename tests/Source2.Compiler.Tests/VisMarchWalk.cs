using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Walk one of ze_hold_em_p's promoted regions march by march, leaf by leaf.
/// Behind <c>WALK=1</c>: it prints a lot and asserts nothing.
/// </summary>
public class VisMarchWalk(ITestOutputHelper output)
{
    [Fact]
    public void WalkOnePromotedRegion()
    {
        if (Environment.GetEnvironmentVariable("WALK") is not { Length: > 0 })
            return;
        if (VisFixtures.RayTraceScene("s2c_lighting", "ze_hold_em_p") is not var (rte, valve))
            return;

        var tree = VisVoxelizer.Build(rte, valve.MinBounds, valve.MaxBounds, valve.GridSize);
        var side = VisVoxelizer.VoxelsPerRoot(valve.MinBounds, valve.MaxBounds, valve.GridSize)
                 / VisVoxelizer.VoxelsPerLeaf;
        var regions = VisRegions.Build(tree, side);

        // First pass: find the promoted regions.
        var judged = new List<VisOutside.Judged>();
        VisOutside.Detect(tree, regions, rte, valve.GridSize, VisSeed.Quality, judged.Add);
        var promoted = judged.Where(j => j.Answer == (int)VisOutside.Status.Inside).ToList();
        output.WriteLine($"{promoted.Count} promoted of {judged.Count} judged");

        // Pick one in the middle of the pack rather than an outlier.
        var target = promoted.OrderBy(j => j.Inside).ElementAt(promoted.Count / 2);
        output.WriteLine($"walking region {target.Region}: {target.Marched} marches,"
                       + $" {target.Inside} inside, {target.Outside} outside,"
                       + $" mean stop {target.Stop:F1}, {target.Leaves:F1} leaves/march");

        // What the seed actually counted for it, and what each threshold says.
        foreach (var pick in promoted.OrderBy(j => j.Inside)
                                     .Where((_, i) => i % (promoted.Count / 6) == 0).Take(6))
        {
            var r = regions.Regions[pick.Region];
            var leaf = regions.Leaves[r.Leaf];
            var edge = tree.LeafSize * (1 << leaf.Level);
            var corner = tree.Origin
                       + new System.Numerics.Vector3(leaf.Cell.X, leaf.Cell.Y, leaf.Cell.Z) * edge;
            var (lo, hi) = VisClusters.Box(edge * VisClusters.SubCell, r.Open);
            var counted = VisSeed.Gather(rte, corner + lo, corner + hi, VisSeed.Quality);
            int n = counted.PerFace, rays = n * 6;
            output.WriteLine($"region {pick.Region,7} leaf {r.Leaf,7} level {leaf.Level}"
                + $"  facing {counted.Facing,4} behind {counted.Behind,4}"
                + $" insub {counted.Insubstantial,4} escaped {counted.Escaped,4} of {rays}"
                + $"  |  outer {(n <= counted.Escaped + counted.Behind)}"
                + $"/{(counted.Facing <= rays / 3 && counted.Insubstantial + counted.Facing <= rays / 2)}"
                + $"  second {(counted.Behind > 4)}"
                + $"/{(counted.Insubstantial + counted.Facing <= n * 2)}"
                + $"  outsideIf {(n <= counted.Behind)}"
                + $"  -> {VisSeed.Decide(counted)}");
        }

        // Second pass: same run, tracing only that region, and only its first rays.
        var lines = 0;
        VisOutside.Detect(tree, regions, rte, valve.GridSize, VisSeed.Quality, null,
            (region, line) =>
            {
                if (region != target.Region || lines++ > 160)
                    return;
                output.WriteLine(line);
            });
    }
}
