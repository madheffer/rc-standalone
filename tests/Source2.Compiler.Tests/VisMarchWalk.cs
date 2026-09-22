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

        // How the seed treats a leaf depending on its size and how open it is.
        // The marches that promote a region run through big, entirely open
        // level 1 leaves, so what the seed makes of THOSE is the question.
        var seeded = new VisOutside.Status[regions.Regions.Count];
        var tally = new VisSeed.Counters[regions.Regions.Count];
        Parallel.For(0, regions.Regions.Count, i =>
        {
            var r = regions.Regions[i];
            var lf = regions.Leaves[r.Leaf];
            var e = tree.LeafSize * (1 << lf.Level);
            var c = tree.Origin + new System.Numerics.Vector3(lf.Cell.X, lf.Cell.Y, lf.Cell.Z) * e;
            var (lo2, hi2) = VisClusters.Box(e * VisClusters.SubCell, r.Open);
            tally[i] = VisSeed.Gather(rte, c + lo2, c + hi2, VisSeed.Quality);
            seeded[i] = VisSeed.Decide(tally[i]);
        });

        foreach (var band in regions.Regions
            .Select((r, i) => (Level: regions.Leaves[r.Leaf].Level, Full: r.Open == ulong.MaxValue,
                               C: tally[i]))
            .Where(x => x.Full)
            .GroupBy(x => x.Level).OrderBy(g => g.Key))
        {
            var all = band.ToList();
            output.WriteLine($"counters level {band.Key} full {all.Count,7:n0}:"
                + $"  facing {all.Average(x => x.C.Facing),6:F1}"
                + $"  behind {all.Average(x => x.C.Behind),6:F1}"
                + $"  insub {all.Average(x => x.C.Insubstantial),5:F1}"
                + $"  escaped {all.Average(x => x.C.Escaped),6:F1}"
                + $"  edge {tree.LeafSize * (1 << band.Key),6}");
        }

        foreach (var band in regions.Regions
            .Select((r, i) => (Level: regions.Leaves[r.Leaf].Level, Full: r.Open == ulong.MaxValue,
                               Status: seeded[i]))
            .GroupBy(x => (x.Level, x.Full))
            .OrderBy(g => g.Key.Level).ThenBy(g => g.Key.Full))
        {
            var all = band.ToList();
            output.WriteLine($"level {band.Key.Level} {(band.Key.Full ? "FULL " : "part ")}"
                + $" {all.Count,7:n0} regions:"
                + $"  inside {all.Count(x => x.Status == VisOutside.Status.Inside),7:n0}"
                + $"  outside {all.Count(x => x.Status == VisOutside.Status.Outside),7:n0}"
                + $"  unknown {all.Count(x => x.Status == VisOutside.Status.Unknown),7:n0}");
        }

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
