using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// How close the regions the second pass promotes sit to each of the seed's own
/// thresholds. Every stage between the seed and the count is byte faithful to the
/// binary, so if the 831 are a rounding away from Outside the fault is upstream,
/// in what the ray trace returns. Behind <c>MARGIN=1</c>.
/// </summary>
public class VisSeedMargins(ITestOutputHelper output)
{
    [Fact]
    public void HowFarTheyAreFromOutside()
    {
        if (Environment.GetEnvironmentVariable("MARGIN") is not { Length: > 0 })
            return;
        if (VisFixtures.RayTraceScene("s2c_lighting", "ze_hold_em_p") is not var (rte, valve))
            return;

        var tree = VisVoxelizer.Build(rte, valve.MinBounds, valve.MaxBounds, valve.GridSize);
        var side = VisVoxelizer.VoxelsPerRoot(valve.MinBounds, valve.MaxBounds, valve.GridSize)
                 / VisVoxelizer.VoxelsPerLeaf;
        var regions = VisRegions.Build(tree, side);
        var count = regions.Regions.Count;

        var counters = new VisSeed.Counters[count];
        Parallel.For(0, count, i =>
        {
            var r = regions.Regions[i];
            var lf = regions.Leaves[r.Leaf];
            var e = tree.LeafSize * (1 << lf.Level);
            var corner = tree.Origin + new Vector3(lf.Cell.X, lf.Cell.Y, lf.Cell.Z) * e;
            var (a, b) = VisClusters.Box(e * VisClusters.SubCell, r.Open);
            counters[i] = VisSeed.Gather(rte, corner + a, corner + b, VisSeed.Quality);
        });

        var judged = new List<VisOutside.Judged>();
        var result = VisOutside.Detect(tree, regions, rte, valve.GridSize, VisSeed.Quality, judged.Add);
        var seeded = result.Seeded!;
        var promoted = judged.Where(j => j.Answer == (int)VisOutside.Status.Inside)
                             .Select(j => j.Region).Order().ToArray();
        var rejected = judged.Where(j => j.Answer != (int)VisOutside.Status.Inside)
                             .Select(j => j.Region).Order().ToArray();

        void Bands(string label, IReadOnlyList<int> ids)
        {
            if (ids.Count == 0)
                return;
            var c = ids.Select(i => counters[i]).ToList();
            output.WriteLine($"{label,-24} {ids.Count,6:n0}   facing {c.Average(x => x.Facing),6:F1}"
                + $"  behind {c.Average(x => x.Behind),6:F1}"
                + $"  insub {c.Average(x => x.Insubstantial),5:F1}"
                + $"  escaped {c.Average(x => x.Escaped),6:F1}"
                + $"  voxels {ids.Select(i => (double)System.Numerics.BitOperations.PopCount(regions.Regions[i].Open)).Average(),5:F1}");

            // n is 25. Outside instead of Unknown needs behind at or above it.
            var behind = c.Select(x => x.Behind).Order().ToList();
            output.WriteLine($"{"",-24}        behind percentiles:"
                + $" p10 {behind[behind.Count / 10],3}  p25 {behind[behind.Count / 4],3}"
                + $"  median {behind[behind.Count / 2],3}  p75 {behind[behind.Count * 3 / 4],3}"
                + $"  p90 {behind[behind.Count * 9 / 10],3}  max {behind[^1],3}"
                + $"   at or over 25: {behind.Count(x => x >= 25),5:n0}");
            for (var lo = 0; lo < 30; lo += 5)
                if (behind.Count(x => x >= lo && x < lo + 5) > 0)
                    output.WriteLine($"{"",-24}          behind {lo,2}..{lo + 4,2}:"
                        + $" {behind.Count(x => x >= lo && x < lo + 5),6:n0}");
        }

        // Inside, for a region with behind at or under 4, is reached only by
        // facing + insubstantial being over 50. That is the boundary the two
        // populations actually sit on, so histogram it.
        var insideIds = Enumerable.Range(0, count)
            .Where(i => seeded[i] == VisOutside.Status.Inside).ToList();
        var hist = insideIds.Select(i => counters[i].Facing + counters[i].Insubstantial)
                            .Order().ToList();
        output.WriteLine($"seed inside, facing+insub: min {hist[0]}  p1 {hist[hist.Count / 100]}"
            + $"  p5 {hist[hist.Count / 20]}  p10 {hist[hist.Count / 10]}"
            + $"  median {hist[hist.Count / 2]}");
        foreach (var lo in new[] { 51, 60, 70, 80, 90, 100, 110, 120, 130, 140 })
            output.WriteLine($"     at or under {lo,3}: {hist.Count(x => x <= lo),6:n0}");
        var esc = insideIds.Select(i => counters[i].Escaped).Order().ToList();
        output.WriteLine($"seed inside, escaped: median {esc[esc.Count / 2]}"
            + $"  p90 {esc[esc.Count * 9 / 10]}  p99 {esc[esc.Count * 99 / 100]}  max {esc[^1]}"
            + $"   over 30: {esc.Count(x => x > 30):n0}   over 60: {esc.Count(x => x > 60):n0}");

        Bands("promoted", promoted);
        Bands("rejected", rejected);
        Bands("seed inside", Enumerable.Range(0, count)
            .Where(i => seeded[i] == VisOutside.Status.Inside).ToList());
        Bands("seed outside lvl0 part", Enumerable.Range(0, count)
            .Where(i => seeded[i] == VisOutside.Status.Outside
                     && regions.Leaves[regions.Regions[i].Leaf].Level == 0
                     && regions.Regions[i].Open != ulong.MaxValue).ToList());
    }
}
