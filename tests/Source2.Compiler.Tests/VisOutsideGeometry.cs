using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Where the seed's Outside regions actually SIT relative to the ones the second
/// pass promotes. Behind <c>GEOM=1</c>: it prints and asserts nothing.
/// </summary>
public class VisOutsideGeometry(ITestOutputHelper output)
{
    [Fact]
    public void WhereTheOutsideRegionsSit()
    {
        if (Environment.GetEnvironmentVariable("GEOM") is not { Length: > 0 })
            return;
        if (VisFixtures.RayTraceScene("s2c_lighting", "ze_hold_em_p") is not var (rte, valve))
            return;

        var tree = VisVoxelizer.Build(rte, valve.MinBounds, valve.MaxBounds, valve.GridSize);
        var side = VisVoxelizer.VoxelsPerRoot(valve.MinBounds, valve.MaxBounds, valve.GridSize)
                 / VisVoxelizer.VoxelsPerLeaf;
        var regions = VisRegions.Build(tree, side);
        var count = regions.Regions.Count;

        // Box, centre and level for every region, the way VisOutside.Space does.
        var lo = new Vector3[count];
        var hi = new Vector3[count];
        var level = new int[count];
        var full = new bool[count];
        for (var i = 0; i < count; i++)
        {
            var r = regions.Regions[i];
            var lf = regions.Leaves[r.Leaf];
            var e = tree.LeafSize * (1 << lf.Level);
            var corner = tree.Origin + new Vector3(lf.Cell.X, lf.Cell.Y, lf.Cell.Z) * e;
            var (a, b) = VisClusters.Box(e * VisClusters.SubCell, r.Open);
            lo[i] = corner + a;
            hi[i] = corner + b;
            level[i] = lf.Level;
            full[i] = r.Open == ulong.MaxValue;
        }

        var judged = new List<VisOutside.Judged>();
        var result = VisOutside.Detect(tree, regions, rte, valve.GridSize, VisSeed.Quality, judged.Add);
        var seeded = result.Seeded!;
        var promoted = judged.Where(j => j.Answer == (int)VisOutside.Status.Inside)
                             .Select(j => j.Region).ToHashSet();

        output.WriteLine($"{count:n0} regions, seed: "
            + $"{seeded.Count(s => s == VisOutside.Status.Inside):n0} inside, "
            + $"{seeded.Count(s => s == VisOutside.Status.Outside):n0} outside, "
            + $"{seeded.Count(s => s == VisOutside.Status.Unknown):n0} unknown;"
            + $" pass two promoted {promoted.Count:n0}");

        foreach (var g in promoted.GroupBy(i => (level[i], full[i])).OrderBy(g => g.Key))
            output.WriteLine($"  promoted at level {g.Key.Item1} {(g.Key.Item2 ? "FULL" : "part")}: {g.Count():n0}");

        // Where the seed's Outside regions are, by level.
        foreach (var g in Enumerable.Range(0, count).Where(i => seeded[i] == VisOutside.Status.Outside)
                                    .GroupBy(i => level[i]).OrderBy(g => g.Key))
            output.WriteLine($"  seed Outside at level {g.Key}: {g.Count():n0}");

        // The question: from a promoted region, how far is the nearest region the
        // seed called Outside? A march runs about 363 units.
        var outsideIds = Enumerable.Range(0, count)
                                   .Where(i => seeded[i] == VisOutside.Status.Outside).ToArray();
        var mid = new Vector3[count];
        for (var i = 0; i < count; i++)
            mid[i] = (lo[i] + hi[i]) * 0.5f;

        var picks = promoted.Order().ToArray();
        var near = new float[picks.Length];
        var within = new int[picks.Length];
        Parallel.For(0, picks.Length, k =>
        {
            var c = mid[picks[k]];
            var best = float.MaxValue;
            var n = 0;
            foreach (var o in outsideIds)
            {
                // Distance from the promoted region's centre to the Outside box.
                var d = Vector3.Max(Vector3.Max(lo[o] - c, c - hi[o]), Vector3.Zero).Length();
                if (d < best) best = d;
                if (d <= 400f) n++;
            }
            near[k] = best;
            within[k] = n;
        });

        Array.Sort(near);
        output.WriteLine($"distance from a promoted region to the nearest seed-Outside box:"
            + $" min {near[0]:F0}  p25 {near[picks.Length / 4]:F0}"
            + $"  median {near[picks.Length / 2]:F0}  p75 {near[picks.Length * 3 / 4]:F0}"
            + $"  max {near[^1]:F0}");
        output.WriteLine($"seed-Outside boxes within 400 units of a promoted region:"
            + $" mean {within.Average():F1}  min {within.Min()}  max {within.Max()}"
            + $"  zero for {within.Count(x => x == 0):n0} of {picks.Length:n0}");

        // And the same for the regions the seed already called Inside, as a control:
        // if those are equally far from anything Outside, the 90% figure is a shell.
        var insideIds = Enumerable.Range(0, count)
                                  .Where(i => seeded[i] == VisOutside.Status.Inside).ToArray();
        var sample = insideIds.Where((_, i) => i % Math.Max(1, insideIds.Length / 400) == 0).ToArray();
        var ctrl = new float[sample.Length];
        Parallel.For(0, sample.Length, k =>
        {
            var c = mid[sample[k]];
            var best = float.MaxValue;
            foreach (var o in outsideIds)
            {
                var d = Vector3.Max(Vector3.Max(lo[o] - c, c - hi[o]), Vector3.Zero).Length();
                if (d < best) best = d;
            }
            ctrl[k] = best;
        });
        Array.Sort(ctrl);
        output.WriteLine($"control, same distance from a seed-Inside region ({sample.Length} sampled):"
            + $" min {ctrl[0]:F0}  median {ctrl[sample.Length / 2]:F0}  max {ctrl[^1]:F0}");

        // Bounding boxes, to see whether Outside is a shell around the map.
        static (Vector3, Vector3) Bounds(IEnumerable<int> ids, Vector3[] lo, Vector3[] hi)
        {
            var a = new Vector3(float.MaxValue);
            var b = new Vector3(float.MinValue);
            foreach (var i in ids) { a = Vector3.Min(a, lo[i]); b = Vector3.Max(b, hi[i]); }
            return (a, b);
        }
        var (oa, ob) = Bounds(outsideIds, lo, hi);
        var (ia, ib) = Bounds(insideIds, lo, hi);
        output.WriteLine($"world   {valve.MinBounds} .. {valve.MaxBounds}");
        output.WriteLine($"outside {oa} .. {ob}");
        output.WriteLine($"inside  {ia} .. {ib}");
    }
}
