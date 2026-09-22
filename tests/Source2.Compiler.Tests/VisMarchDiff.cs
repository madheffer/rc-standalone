using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The march the second pass runs, side by side with a brute force scan of every
/// region box against the same segment. Behind <c>DIFF=1</c>.
/// </summary>
public class VisMarchDiff(ITestOutputHelper output)
{
    private static bool Crosses(Vector3 from, Vector3 to, Vector3 mins, Vector3 maxs)
    {
        var along = to - from;
        float enter = 0f, leave = 1f;
        for (var axis = 0; axis < 3; axis++)
        {
            var d = axis == 0 ? along.X : axis == 1 ? along.Y : along.Z;
            var o = axis == 0 ? from.X : axis == 1 ? from.Y : from.Z;
            var a = axis == 0 ? mins.X : axis == 1 ? mins.Y : mins.Z;
            var b = axis == 0 ? maxs.X : axis == 1 ? maxs.Y : maxs.Z;
            if (d == 0f) { if (o < a || o > b) return false; continue; }
            var one = (a - o) / d;
            var two = (b - o) / d;
            if (one > two) (one, two) = (two, one);
            enter = MathF.Max(enter, one);
            leave = MathF.Min(leave, two);
        }
        return enter <= leave;
    }

    [Fact]
    public void TheMarchAgainstAScan()
    {
        if (Environment.GetEnvironmentVariable("DIFF") is not { Length: > 0 })
            return;
        if (VisFixtures.RayTraceScene("s2c_lighting", "ze_hold_em_p") is not var (rte, valve))
            return;

        var tree = VisVoxelizer.Build(rte, valve.MinBounds, valve.MaxBounds, valve.GridSize);
        var side = VisVoxelizer.VoxelsPerRoot(valve.MinBounds, valve.MaxBounds, valve.GridSize)
                 / VisVoxelizer.VoxelsPerLeaf;
        var regions = VisRegions.Build(tree, side);
        var count = regions.Regions.Count;
        var depth = tree.BranchesPerLevel.Count;

        var lo = new Vector3[count];
        var hi = new Vector3[count];
        for (var i = 0; i < count; i++)
        {
            var r = regions.Regions[i];
            var lf = regions.Leaves[r.Leaf];
            var e = tree.LeafSize * (1 << lf.Level);
            var corner = tree.Origin + new Vector3(lf.Cell.X, lf.Cell.Y, lf.Cell.Z) * e;
            var (a, b) = VisClusters.Box(e * VisClusters.SubCell, r.Open);
            lo[i] = corner + a;
            hi[i] = corner + b;
        }

        var byCell = new Dictionary<(int, int, int, int), int>();
        for (var i = 0; i < regions.Leaves.Count; i++)
        {
            var lf = regions.Leaves[i];
            byCell[(lf.Level, lf.Cell.X, lf.Cell.Y, lf.Cell.Z)] = i;
        }
        var byLeaf = new List<int>[regions.Leaves.Count];
        for (var i = 0; i < count; i++)
            (byLeaf[regions.Regions[i].Leaf] ??= []).Add(i);

        output.WriteLine($"{regions.Leaves.Count:n0} leaves, {count:n0} regions, depth {depth}");
        foreach (var g in regions.Leaves.Select((l, i) => (l.Level, N: byLeaf[i]?.Count ?? 0))
                                        .GroupBy(x => x.Level).OrderBy(g => g.Key))
            output.WriteLine($"  level {g.Key}: {g.Count():n0} leaves,"
                + $" {g.Average(x => x.N):F2} regions each");

        var judged = new List<VisOutside.Judged>();
        var result = VisOutside.Detect(tree, regions, rte, valve.GridSize, VisSeed.Quality, judged.Add);
        var seeded = result.Seeded!;
        var flagged = new bool[count];
        for (var i = 0; i < count; i++)
            flagged[i] = seeded[i] == VisOutside.Status.Outside;

        var voted = judged.Where(j => j.Marched > 0).ToList();
        output.WriteLine($"{voted.Count:n0} regions reached a vote;"
            + $" outside votes: mean {voted.Average(j => j.Outside):F2},"
            + $" zero for {voted.Count(j => j.Outside == 0):n0}");

        var promoted = judged.Where(j => j.Answer == (int)VisOutside.Status.Inside).ToList();
        var pick = promoted.OrderBy(j => j.Inside).ElementAt(promoted.Count / 2);
        var region = pick.Region;
        var centre = (lo[region] + hi[region]) * 0.5f;
        output.WriteLine($"region {region} leaf {regions.Regions[region].Leaf}"
            + $" level {regions.Leaves[regions.Regions[region].Leaf].Level}"
            + $" box {lo[region]}..{hi[region]} centre {centre}"
            + $" siblings {byLeaf[regions.Regions[region].Leaf]!.Count}");

        var reach = (rte.Maxs - rte.Mins).Length();
        var ray = 0;
        foreach (var direction in VisSeed.Directions(lo[region], hi[region], VisSeed.Quality))
        {
            if (rte.Trace(centre, direction, reach, VisSeed.Ignored) is not { } first)
                continue;
            var hit = VisSeed.Behind(rte, centre, direction, reach, first);
            if (Vector3.Dot(hit.Normal, centre)
                < Vector3.Dot(hit.Normal, centre + (direction * hit.Distance)))
                continue;
            if (ray++ % 11 != 0)
                continue;

            var stop = MathF.Max(hit.Distance - VisOutside.MarchBackOff, VisOutside.MarchShortest);
            var to = centre + (direction * stop);

            var inverse = VisVisibility.Reciprocal(to - centre);
            var seen = new List<int>();
            var visited = new List<(int Leaf, int Level)>();
            var queue = new Queue<(int Level, (int X, int Y, int Z) Cell)>();
            queue.Enqueue((depth, (0, 0, 0)));
            while (queue.Count > 0)
            {
                var (level, cell) = queue.Dequeue();
                var size = tree.LeafSize * (1 << level);
                var corner = tree.Origin + new Vector3(cell.X, cell.Y, cell.Z) * size;
                if (level > 0 && tree.BranchCells.Contains((level, cell)))
                {
                    var octants = VisVisibility.Crossed(centre, inverse, corner, size, 2);
                    for (var o = 0; o < 8; o++)
                        if ((octants & (1UL << o)) != 0)
                            queue.Enqueue((level - 1, (cell.X * 2 + (o & 1), cell.Y * 2 + ((o >> 1) & 1),
                                                       cell.Z * 2 + ((o >> 2) & 1))));
                    continue;
                }
                if (!byCell.TryGetValue((level, cell.X, cell.Y, cell.Z), out var leaf))
                    continue;
                var parts = byLeaf[leaf];
                if (parts is null)
                    continue;
                visited.Add((leaf, level));
                var crossed = VisVisibility.Crossed(centre, inverse, corner, size, 4);
                foreach (var r in parts)
                    if ((regions.Regions[r].Open & crossed) != 0)
                        seen.Add(r);
            }

            var scanned = new List<int>();
            for (var r = 0; r < count; r++)
                if (Crosses(centre, to, lo[r], hi[r]))
                    scanned.Add(r);

            var missed = scanned.Except(seen).ToList();
            output.WriteLine($"ray {ray - 1} dir {direction} stop {stop:F1}");
            output.WriteLine($"   march: {visited.Count} leaves"
                + $" (levels {string.Join(",", visited.Select(v => v.Level).Distinct().Order())}),"
                + $" {seen.Count} regions, {seen.Count(r => flagged[r])} flagged");
            output.WriteLine($"   scan : {scanned.Count} regions, {scanned.Count(r => flagged[r])} flagged;"
                + $" march missed {missed.Count} ({missed.Count(r => flagged[r])} of them flagged)");
            if (missed.Count(r => flagged[r]) > 0)
            {
                var m = missed.First(r => flagged[r]);
                var lf = regions.Leaves[regions.Regions[m].Leaf];
                output.WriteLine($"   first missed flagged: region {m} leaf {regions.Regions[m].Leaf}"
                    + $" level {lf.Level} cell {lf.Cell} box {lo[m]}..{hi[m]}"
                    + $" open {regions.Regions[m].Open:x16}"
                    + $" leaf visited {visited.Any(v => v.Leaf == regions.Regions[m].Leaf)}");
            }
            if (ray > 40) break;
        }
    }
}
