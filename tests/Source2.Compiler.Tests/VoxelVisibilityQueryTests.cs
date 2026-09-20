using System.Numerics;
using Source2.Compiler.Maps;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;
using Vis = Source2.Compiler.Maps.VoxelVisibility;

namespace Source2.Compiler.Tests;

/// <summary>
/// Our octree traversal against an independent implementation of the same
/// traversal, on real maps.
///
/// <para>ValveResourceFormat's <c>VoxelVisibility</c> is not Valve's code, but it
/// is a separate reading of the same structure that renders CS2 maps correctly,
/// so agreeing with it on hundreds of thousands of sampled points is real
/// evidence that our descent, our octant order and our 4x4x4 mask bit are right.
/// Those are exactly the parts a hand-written traversal gets subtly wrong, and a
/// wrong one would silently poison every comparison built on top of it.</para>
/// </summary>
public sealed class VoxelVisibilityQueryTests(ITestOutputHelper output)
{
    /// <summary>Both readings of the same file: ours, and the viewer's.</summary>
    private static IEnumerable<(string Map, Vis Ours, ValveResourceFormat.Blocks.VoxelVisibility Theirs)>
        Pairs(int limit)
    {
        foreach (var (map, bytes) in MapFixtures.AllResources(VisFixtures.Suffix, limit))
        {
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));

            if (resource.GetBlockByType(BlockType.VXVS) is not ValveResourceFormat.Blocks.VoxelVisibility theirs
                || theirs.BaseClusterCount == 0)
            {
                continue;
            }

            yield return (map, VoxelVisibilityReader.Read(bytes, resource), theirs);
        }
    }

    [Fact]
    public void AgreesWithAnIndependentTraversalOnSampledPoints()
    {
        var maps = 0;
        long compared = 0;
        long inside = 0;

        foreach (var (map, ours, theirs) in Pairs(limit: 12))
        {
            var query = new VoxelVisibilityQuery(ours);

            // A fixed seed keeps a failure reproducible, and sampling the stated
            // bounds rather than the whole world keeps the points on the map.
            var random = new Random(20260920);
            var min = ours.MinBounds;
            var size = ours.MaxBounds - ours.MinBounds;

            for (var i = 0; i < 20000; i++)
            {
                var point = min + new Vector3(
                    (float)random.NextDouble() * size.X,
                    (float)random.NextDouble() * size.Y,
                    (float)random.NextDouble() * size.Z);

                var mine = query.ClusterAt(point);
                compared++;
                if (mine < 0)
                    continue;

                inside++;
                Assert.Equal(theirs.GetClusterForPosition(point), mine);
            }
            maps++;
        }

        VisFixtures.RequireCorpus(maps);
        if (maps == 0)
            return;
        Assert.True(inside > 1000, $"only {inside} points landed in a cluster, too few to prove anything");
        output.WriteLine($"{maps} maps, {compared:N0} points sampled, {inside:N0} in a cluster, all agree");
    }

    /// <summary>
    /// A cluster that cannot see itself would hide the room the player stands in,
    /// so every OCCUPIED cluster must. The exception is real and was found here
    /// rather than assumed away: a build can allocate a cluster id that nothing
    /// ends up occupying, and its PVS row is then entirely zero.
    /// </summary>
    [Fact]
    public void EveryOccupiedClusterSeesItself()
    {
        var maps = 0;
        long clusters = 0;
        long unused = 0;

        foreach (var specimen in VisFixtures.All(limit: 20))
        {
            var vis = specimen.Vis;
            if (vis.BaseClusterCount == 0)
                continue;

            var empty = 0;
            for (var c = 0u; c < vis.BaseClusterCount; c++)
            {
                if (vis.ClusterIsUnused(c))
                {
                    empty++;
                    continue;
                }
                Assert.True(vis.CanSee(c, c), $"{specimen.Map}: occupied cluster {c} cannot see itself");
            }

            var fraction = new VoxelVisibilityQuery(vis).MeanVisibleFraction();
            Assert.InRange(fraction, 0.0, 1.0);
            output.WriteLine($"{specimen.Map,-40} {vis.BaseClusterCount,6} clusters, "
                           + $"{empty,4} unused, mean visible {fraction * 100,5:F1}%");
            clusters += vis.BaseClusterCount;
            unused += empty;
            maps++;
        }
        VisFixtures.RequireCorpus(maps);
        if (maps > 0)
            output.WriteLine($"{maps} maps, {clusters:N0} clusters, {unused:N0} unused ({unused * 100.0 / clusters:F2}%)");
    }
}
