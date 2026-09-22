using System.Buffers.Binary;
using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The kd traversal against a scan of every triangle, on the rays that matter:
/// the seed's own grid cast from the regions the second pass promotes. A
/// traversal is correct exactly when it returns what the scan returns, whatever
/// it does inside, so this is the ground truth for <c>Trace</c>.
/// </summary>
public class VisTraceAgainstBruteForce(ITestOutputHelper output)
{
    private static RayTraceEnvironment.Hit? Scan(
        RayTraceEnvironment rte, Vector3 origin, Vector3 direction, float reach, ushort ignore)
    {
        RayTraceEnvironment.Hit? best = null;
        for (var i = 0; i < rte.TriangleCount; i++)
        {
            if ((rte.Flags(i) & ignore) != 0)
                continue;
            if (rte.Meets(i, origin, direction, 0f, best?.Distance ?? reach) is { } hit
                && (best is null || hit.Distance < best.Value.Distance))
                best = hit;
        }
        return best;
    }

    /// <summary>
    /// The same descent written against the raw file, so a disagreement with
    /// <c>Trace</c> says the defect is in the implementation rather than the
    /// algorithm. Returns the distance, the triangle, and the leaves it read.
    /// </summary>
    private static (float Distance, int Triangle, int Leaves)? Replica(
        RayTraceEnvironment rte, byte[] file, Vector3 origin, Vector3 direction,
        float reach, ushort ignore)
    {
        const int Header = 60;
        var nodes = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(12));
        var triangles = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16));
        var indexAt = Header + (nodes * 8) + (triangles * 48);

        float enter = 0f, leave = reach;
        for (var axis = 0; axis < 3; axis++)
        {
            var d = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
            var o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            var lo = axis == 0 ? rte.Mins.X : axis == 1 ? rte.Mins.Y : rte.Mins.Z;
            var hi = axis == 0 ? rte.Maxs.X : axis == 1 ? rte.Maxs.Y : rte.Maxs.Z;
            if (d == 0f)
            {
                if (o < lo || o > hi)
                    return null;
                continue;
            }
            var one = (lo - o) / d;
            var two = (hi - o) / d;
            if (one > two)
                (one, two) = (two, one);
            enter = MathF.Max(enter, one);
            leave = MathF.Min(leave, two);
        }
        if (enter > leave)
            return null;

        (float Distance, int Triangle)? best = null;
        var read = 0;
        var stack = new Stack<(int Node, float From, float To)>();
        stack.Push((0, enter, leave));
        while (stack.Count > 0)
        {
            var (node, from, to) = stack.Pop();
            var word = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(Header + (node * 8)));
            var axis = (int)(word & 3);
            var payload = (int)(word >> 2);
            if (axis == 3)
            {
                read++;
                var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(
                    file.AsSpan(Header + (node * 8) + 4));
                for (var i = 0; i < count; i++)
                {
                    var triangle = (int)BinaryPrimitives.ReadUInt32LittleEndian(
                        file.AsSpan(indexAt + ((payload + i) * 4)));
                    if ((rte.Flags(triangle) & ignore) != 0)
                        continue;
                    if (rte.Meets(triangle, origin, direction, 0f, best?.Distance ?? reach) is { } hit
                        && (best is null || hit.Distance < best.Value.Distance))
                        best = (hit.Distance, triangle);
                }
                continue;
            }

            var split = BitConverter.ToSingle(file, Header + (node * 8) + 4);
            var along = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
            var start = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            if (along == 0f)
            {
                stack.Push((start <= split ? payload : payload + 1, from, to));
                continue;
            }
            var (near, far) = along > 0f ? (payload, payload + 1) : (payload + 1, payload);
            var at = (split - start) / along;
            if (at >= to)
                stack.Push((near, from, to));
            else if (at <= from)
                stack.Push((far, from, to));
            else
            {
                stack.Push((far, at, to));
                stack.Push((near, from, at));
            }
        }
        return best is null ? null : (best.Value.Distance, best.Value.Triangle, read);
    }

    [Fact]
    public void TheKdTreeFindsWhatAScanOfEveryTriangleFinds()
    {
        if (VisFixtures.RayTraceScene("s2c_lighting", "ze_hold_em_p") is not var (rte, valve))
            return;
        var path = Path.Combine(Path.GetTempPath(), "csgo_addons", "s2c_lighting",
                                "maps", "ze_hold_em_p.rte");
        var file = File.ReadAllBytes(path);

        var tree = VisVoxelizer.Build(rte, valve.MinBounds, valve.MaxBounds, valve.GridSize);
        var side = VisVoxelizer.VoxelsPerRoot(valve.MinBounds, valve.MaxBounds, valve.GridSize)
                 / VisVoxelizer.VoxelsPerLeaf;
        var regions = VisRegions.Build(tree, side);

        var judged = new List<VisOutside.Judged>();
        VisOutside.Detect(tree, regions, rte, valve.GridSize, VisSeed.Quality, judged.Add);
        // Whatever the second pass looked at, cast its rays. Its verdict is not
        // the point here; the rays are, because they are the ones that exposed
        // the clamp.
        var probed = judged.Select(j => j.Region).ToList();
        Assert.NotEmpty(probed);

        var reach = (rte.Maxs - rte.Mins).Length();
        int rays = 0, treeOff = 0, replicaOff = 0, shown = 0;

        foreach (var region in probed.Where((_, i) => i % 20 == 0))
        {
            var r = regions.Regions[region];
            var lf = regions.Leaves[r.Leaf];
            var edge = tree.LeafSize * (1 << lf.Level);
            var corner = tree.Origin + new Vector3(lf.Cell.X, lf.Cell.Y, lf.Cell.Z) * edge;
            var (lo, hi) = VisClusters.Box(edge * VisClusters.SubCell, r.Open);
            var centre = corner + ((lo + hi) * 0.5f);

            foreach (var direction in VisSeed.Directions(corner + lo, corner + hi, VisSeed.Quality))
            {
                rays++;
                var fast = rte.Trace(centre, direction, reach, VisSeed.Ignored);
                var slow = Scan(rte, centre, direction, reach, VisSeed.Ignored);
                var mine = Replica(rte, file, centre, direction, reach, VisSeed.Ignored);

                var fastOff = (fast is null) != (slow is null)
                    || (fast is not null && slow is not null
                        && MathF.Abs(fast.Value.Distance - slow.Value.Distance) > 0.01f);
                var mineOff = (mine is null) != (slow is null)
                    || (mine is not null && slow is not null
                        && MathF.Abs(mine.Value.Distance - slow.Value.Distance) > 0.01f);
                if (fastOff)
                    treeOff++;
                if (mineOff)
                    replicaOff++;
                if (fastOff && !mineOff && shown++ < 4)
                    output.WriteLine($"   Trace {fast?.Distance.ToString("F2") ?? "escaped"}"
                        + $" on {fast?.Triangle}, replica {mine?.Distance.ToString("F2") ?? "escaped"}"
                        + $" on {mine?.Triangle} ({mine?.Leaves} leaves),"
                        + $" scan {slow?.Distance.ToString("F2") ?? "escaped"} on {slow?.Triangle}");
            }
        }

        output.WriteLine($"{rays:n0} rays: Trace disagrees with a scan {treeOff:n0} times,"
            + $" a replica of the same descent disagrees {replicaOff:n0} times");
        Assert.Equal(0, replicaOff);
        Assert.Equal(0, treeOff);
    }
}
