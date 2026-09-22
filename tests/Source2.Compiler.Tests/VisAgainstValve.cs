using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Our enclosed set against the one in the compiled file, by asking the shipped
/// octree what cluster sits at each of our region centres. The shipped tree is
/// the COLLAPSED one, so a yes is only as fine as the leaf that answered, and the
/// answering leaf's size is reported beside every score. Behind <c>VALVE=1</c>.
/// </summary>
public class VisAgainstValve(ITestOutputHelper output)
{
    [Fact]
    public void WhoIsRightAboutEachRegion()
    {
        if (Environment.GetEnvironmentVariable("VALVE") is not { Length: > 0 })
            return;

        foreach (var map in new[] { "ze_hold_em_p", "cardtest", "probe01" })
        {
            var addon = map == "ze_hold_em_p" ? "s2c_lighting" : "s2c_rc_probe";
            if (VisFixtures.RayTraceScene(addon, map) is not var (rte, valve))
                continue;

            var tree = VisVoxelizer.Build(rte, valve.MinBounds, valve.MaxBounds, valve.GridSize);
            var side = VisVoxelizer.VoxelsPerRoot(valve.MinBounds, valve.MaxBounds, valve.GridSize)
                     / VisVoxelizer.VoxelsPerLeaf;
            var regions = VisRegions.Build(tree, side);
            var count = regions.Regions.Count;

            var mid = new Vector3[count];
            var level = new int[count];
            for (var i = 0; i < count; i++)
            {
                var r = regions.Regions[i];
                var lf = regions.Leaves[r.Leaf];
                var e = tree.LeafSize * (1 << lf.Level);
                var corner = tree.Origin + new Vector3(lf.Cell.X, lf.Cell.Y, lf.Cell.Z) * e;
                var (a, b) = VisClusters.Box(e * VisClusters.SubCell, r.Open);
                mid[i] = corner + ((a + b) * 0.5f);
                level[i] = lf.Level;
            }

            var judged = new List<VisOutside.Judged>();
            var result = VisOutside.Detect(tree, regions, rte, valve.GridSize,
                                           VisSeed.Quality, judged.Add);
            var seeded = result.Seeded!;
            var promoted = judged.Where(j => j.Answer == (int)VisOutside.Status.Inside)
                                 .Select(j => j.Region).ToHashSet();
            var rejected = judged.Where(j => j.Answer != (int)VisOutside.Status.Inside)
                                 .Select(j => j.Region).ToHashSet();

            var query = new VoxelVisibilityQuery(valve);
            var known = new bool[count];
            var answeredBy = new float[count];
            Parallel.For(0, count, i =>
            {
                var min = valve.MinBounds;
                var max = valve.MaxBounds;
                var leaf = query.FindLeaf(mid[i], ref min, ref max);
                answeredBy[i] = leaf < 0 ? -1f : (max - min).X;
                known[i] = query.ClusterAt(mid[i]) >= 0;
            });

            void Report(string label, IEnumerable<int> ids)
            {
                var all = ids.ToList();
                if (all.Count == 0)
                    return;
                var yes = all.Where(i => known[i]).ToList();
                var sized = yes.Where(i => answeredBy[i] > 0).Select(i => answeredBy[i]).Order().ToList();
                output.WriteLine($"  {label,-22} {all.Count,7:n0}"
                    + $"   the file holds a cluster for {yes.Count,7:n0}"
                    + $"  ({(double)yes.Count / all.Count,7:P1})"
                    + (sized.Count == 0 ? ""
                        : $"   answering leaf edge: median {sized[sized.Count / 2],7:F0}"
                        + $"  p10 {sized[sized.Count / 10],6:F0}"
                        + $"  at or under 64 {sized.Count(x => x <= 64) * 100.0 / sized.Count,5:F1}%"));
            }

            output.WriteLine($"{map}");
            Report("every region", Enumerable.Range(0, count));
            Report("seed inside", Enumerable.Range(0, count).Where(i => seeded[i] == VisOutside.Status.Inside));
            Report("seed outside", Enumerable.Range(0, count).Where(i => seeded[i] == VisOutside.Status.Outside));
            Report("seed undecided", Enumerable.Range(0, count).Where(i => seeded[i] == VisOutside.Status.Unknown));
            Report("  of those, promoted", promoted);
            Report("  of those, rejected", rejected);
            Report("level 0 partial", Enumerable.Range(0, count)
                .Where(i => level[i] == 0 && regions.Regions[i].Open != ulong.MaxValue));
        }
    }
}
