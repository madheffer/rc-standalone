using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Whether one 512 unit merge cell really is as open as the sampler says. It
/// asks the question independently of the merge machinery: trace centre to
/// centre between the clusters that fall in a cell and count the lines geometry
/// stops. Behind <c>OPEN=1</c>.
/// </summary>
public class VisBucketOpenness(ITestOutputHelper output)
{
    [Fact]
    public void HowMuchOfACellSeesItself()
    {
        if (Environment.GetEnvironmentVariable("OPEN") is not { Length: > 0 })
            return;

        foreach (var (addon, map) in new[]
        {
            ("s2c_lighting", "ze_hold_em_p"),
            ("s2c_rc_probe", "probe01"),
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

            // Bucket every cluster the way Regrid does at 512, then take the
            // fullest cell.
            const float Cell = 512f;
            var byKey = new Dictionary<(int, int), List<VisMerge.Cluster>>();
            foreach (var cluster in sets.SelectMany(s => s.Clusters))
            {
                var mid = cluster.Centre;
                var key = ((int)MathF.Floor(mid.X / Cell), (int)MathF.Floor(mid.Y / Cell));
                (byKey.TryGetValue(key, out var list) ? list : byKey[key] = []).Add(cluster);
            }
            var bucket = byKey.Values.OrderByDescending(x => x.Count).First();

            // A sample, because the full square is millions of traces.
            var random = new Random(20260923);
            var picked = bucket.OrderBy(_ => random.Next()).Take(200).ToList();
            var far = RayTraceEnvironment.MaxCoord;
            int clear = 0, blocked = 0;
            foreach (var a in picked)
            {
                foreach (var b in picked)
                {
                    if (ReferenceEquals(a, b))
                        continue;
                    var from = a.Centre;
                    var to = b.Centre;
                    var gap = (to - from).Length();
                    if (gap <= 0f)
                        continue;
                    var hit = rte.Trace(from, (to - from) / gap, far, VisSeed.Ignored);
                    if (hit is { } h && h.Distance < gap - 1f)
                        blocked++;
                    else
                        clear++;
                }
            }

            var box = bucket.Aggregate(
                (Lo: new Vector3(float.MaxValue), Hi: new Vector3(float.MinValue)),
                (acc, c) => (Vector3.Min(acc.Lo, c.Mins), Vector3.Max(acc.Hi, c.Maxs)));
            output.WriteLine($"{map,-16} fullest 512 cell holds {bucket.Count,6:n0} clusters"
                + $"  spanning {box.Hi - box.Lo}");
            output.WriteLine($"{"",16} of {clear + blocked,7:n0} centre to centre lines,"
                + $" {clear,7:n0} are CLEAR ({(double)clear / (clear + blocked),7:P1})"
                + $" and {blocked,7:n0} are stopped by geometry");
        }
    }
}
