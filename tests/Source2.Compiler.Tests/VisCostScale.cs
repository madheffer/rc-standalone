using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// What goes into the number the whole merge chain is steered by. The cost the
/// first pass hands on is the MEAN over buckets of what each one's first merge
/// returned, and a bucket handed back untouched returns the incoming limit -- so
/// how many buckets are too small to merge moves the average as much as the
/// merges do. Behind <c>SCALE=1</c>.
/// </summary>
public class VisCostScale(ITestOutputHelper output)
{
    [Fact]
    public void WhatTheFirstPassAverageIsMadeOf()
    {
        if (Environment.GetEnvironmentVariable("SCALE") is not { Length: > 0 })
            return;

        foreach (var (addon, map, target, compile) in new[]
        {
            ("s2c_lighting", "ze_hold_em_p", 1_342, 21_940.0f),
            ("s2c_rc_probe", "probe01", 862, 110_760.1f),
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

            var first = new List<VisMerge.Halt>();
            var seen = 0;
            VisClusterSet.MergeAll(rte, sets, target, VisClusters.Cubes(tree, compact),
                (_, _) =>
                {
                    if (seen++ != 0 || first.Count == 0)
                        return;
                    var untouched = first.Where(h => h.Best < 0f).ToList();
                    var merged = first.Where(h => h.Best >= 0f).ToList();
                    var average = first.Average(h => h.Returned);
                    output.WriteLine($"{map,-16} pass 1 first merge over {first.Count,4} buckets:"
                        + $"  average {average,10:F1}  compile {compile,10:F1}"
                        + $"  {average / compile - 1,8:P2}");
                    output.WriteLine($"{"",16} {untouched.Count,4} handed back UNTOUCHED"
                        + $" (too small for the doubled budget of {(merged.Count > 0 ? merged[0].Budget : 0),5:n0}),"
                        + $" each returning the incoming limit of"
                        + $" {(untouched.Count > 0 ? untouched[0].Limit : 0),8:F1};"
                        + $" they hold {untouched.Sum(h => h.Started),7:n0} clusters");
                    if (merged.Count > 0)
                        output.WriteLine($"{"",16} {merged.Count,4} actually merged, returning"
                            + $" {merged.Average(h => h.Returned),10:F1} on average"
                            + $"  (min {merged.Min(h => h.Returned),10:F1}"
                            + $"  max {merged.Max(h => h.Returned),10:F1})");
                    if (untouched.Count > 0 && merged.Count > 0)
                        output.WriteLine($"{"",16} were the untouched ones excluded the average"
                            + $" would be {merged.Average(h => h.Returned),10:F1}"
                            + $"  {merged.Average(h => h.Returned) / compile - 1,8:P2}");

                    // How many more buckets would have to clear the doubled
                    // budget for the average to land on the compile's.
                    if (merged.Count > 0)
                    {
                        var mean = merged.Average(h => h.Returned);
                        var need = (compile * first.Count - 20.0 * first.Count) / (mean - 20.0);
                        output.WriteLine($"{"",16} at {mean,10:F1} a merging bucket, the average"
                            + $" lands on {compile,9:F1} when {need,5:F1} of the {first.Count}"
                            + $" merge rather than {merged.Count}");
                    }

                    if (merged.Count > 0)
                        output.WriteLine($"{"",16} in the merging buckets each cluster saw"
                            + $" {merged.Average(h => h.Seen),10:F1} of {merged.Average(h => h.Bits),8:F0}"
                            + $"  ({merged.Average(h => h.Seen) / merged.Average(h => h.Bits),7:P1})"
                            + $" as sampled, and the cheapest pair left when they stopped was"
                            + $" {merged.Average(h => h.Cost),12:F1}");

                    var sizes = first.Select(h => h.Started).Order().ToList();
                    var threshold = merged.Count > 0 ? merged[0].Budget : 0;
                    output.WriteLine($"{"",16} bucket sizes: {string.Join(", ",
                        sizes.Select(x => x.ToString("n0")).Take(50))}");
                    output.WriteLine($"{"",16} the doubled budget is {threshold:n0};"
                        + $" {sizes.Count(x => x > threshold * 3 / 4 && x <= threshold),3} buckets sit"
                        + $" within a quarter of it underneath,"
                        + $" {sizes.Count(x => x > threshold && x <= threshold * 5 / 4),3} just over");
                },
                null, h => { lock (first) first.Add(h); });
        }
    }
}
