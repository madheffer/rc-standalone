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
            }
        }
    }
}
