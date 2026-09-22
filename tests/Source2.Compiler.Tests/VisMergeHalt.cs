using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Why each bucket's merge loop stops. A run that hits its budget and one that
/// runs out of pairs to make are the same number on the way out and completely
/// different problems. Behind <c>HALT=1</c>.
/// </summary>
public class VisMergeHalt(ITestOutputHelper output)
{
    [Fact]
    public void WhyTheBucketsStop()
    {
        if (Environment.GetEnvironmentVariable("HALT") is not { Length: > 0 })
            return;

        foreach (var (addon, map, target) in new[]
        {
            ("s2c_lighting", "ze_hold_em_p", 1_342),
            ("s2c_rc_probe", "probe01", 862),
        })
        {
            if (VisFixtures.RayTraceScene(addon, map) is not var (rte, valve))
                continue;

            var tree = VisVoxelizer.Build(rte, valve.MinBounds, valve.MaxBounds, valve.GridSize);
            var side = VisVoxelizer.VoxelsPerRoot(valve.MinBounds, valve.MaxBounds, valve.GridSize)
                     / VisVoxelizer.VoxelsPerLeaf;
            var regions = VisRegions.Build(tree, side);
            var inside = VisOutside.Detect(tree, regions, rte, valve.GridSize);
            var compact = VisRegions.Compact(regions, inside.Regions);
            var sets = VisClusters.Generate(rte, tree, compact);
            VisPreMerge.Run(sets);

            var halts = new List<VisMerge.Halt>();
            var pass = 0;
            var perPass = new List<List<VisMerge.Halt>>();
            VisClusterSet.MergeAll(rte, sets, target, VisClusters.Cubes(tree, compact),
                (_, _) => { perPass.Add([.. halts]); halts.Clear(); pass++; },
                h => { lock (halts) halts.Add(h); });

            output.WriteLine($"{map}");
            for (var i = 0; i < perPass.Count; i++)
            {
                var all = perPass[i];
                if (all.Count == 0)
                    continue;
                var exhausted = all.Count(h => h.Cost >= float.MaxValue);
                var onBudget = all.Count(h => h.Cost < float.MaxValue && h.Live <= h.Budget
                                            && h.Cost >= h.Limit);
                output.WriteLine($"  pass {i + 1}: {all.Count,4} buckets ended;"
                    + $"  RAN OUT of pairs {exhausted,4}"
                    + $"  stopped on cost {onBudget,4}"
                    + $"  other {all.Count - exhausted - onBudget,4}"
                    + $"   live {all.Sum(h => h.Live),6:n0}"
                    + $"  limit {all.Average(h => h.Limit),10:F1}");
                var priced = all.Where(h => h.Cost < float.MaxValue).ToList();
                if (priced.Count > 0)
                    output.WriteLine($"{"",10} of the {priced.Count} that still had a pair,"
                        + $" the cheapest one left averaged {priced.Average(h => h.Cost),12:F1}"
                        + $" against a limit of {priced.Average(h => h.Limit),10:F1}");
                var merged = all.Where(h => h.Best >= 0f).ToList();
                if (merged.Count > 0)
                    output.WriteLine($"{"",10} the LAST pair each of {merged.Count} actually merged"
                        + $" cost {merged.Average(h => h.Best),12:F1} on average,"
                        + $" dearest {merged.Max(h => h.Best),12:F1}");
                output.WriteLine($"{"",10} the x128 penalty needs two voxel sizes in a bucket:"
                    + $" {all.Count(h => h.Sizes > 1),4} of {all.Count,4} buckets have any"
                    + $"  (mean {all.Average(h => h.Sizes),4:F1} distinct);"
                    + $" the x32 needs a short side: {all.Average(h => (double)h.Short / Math.Max(1, h.Started)),7:P1}"
                    + $" of clusters span 80 or less in z");
                var width = all.Average(h => h.Bits);
                var share = width > 0 ? all.Average(h => h.Seen) / width : 0;
                output.WriteLine($"{"",10} as SAMPLED each cluster saw {all.Average(h => h.Seen),8:F1}"
                    + $" of {width,8:F0} in the bucket  ({share,6:P1})");
            }
        }
    }
}
