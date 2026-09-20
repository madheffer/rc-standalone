using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;
using Vis = Source2.Compiler.Maps.VoxelVisibility;

namespace Source2.Compiler.Tests;

/// <summary>
/// The point-pair sampler, checked on differences we constructed so the answer
/// is known in advance.
///
/// <para>A comparison instrument is only worth its readings if it reports zero
/// when there is no difference and the correct SIGN when there is. Holes and
/// overdraw are not interchangeable: a hole culls geometry the player should see
/// and shows up in game as the world vanishing, while overdraw only costs frame
/// time. An instrument that confused the two would make a broken builder look
/// merely slow.</para>
/// </summary>
public sealed class VisComparisonTests(ITestOutputHelper output)
{
    /// <summary>The same data with its PVS matrix replaced.</summary>
    private static Vis WithPvs(Vis source, byte[] pvs) => new()
    {
        BaseClusterCount = source.BaseClusterCount,
        PVSBytesPerCluster = source.PVSBytesPerCluster,
        MinBounds = source.MinBounds,
        MaxBounds = source.MaxBounds,
        GridSize = source.GridSize,
        SkyVisibilityCluster = source.SkyVisibilityCluster,
        SunVisibilityCluster = source.SunVisibilityCluster,
        Nodes = source.Nodes,
        Regions = source.Regions,
        EnclosedClusterList = source.EnclosedClusterList,
        EnclosedClusters = source.EnclosedClusters,
        Masks = source.Masks,
        VisBlocks = pvs,
    };

    /// <summary>A real map big enough for the pair statistics to mean something.</summary>
    private static Vis? Subject()
        => VisFixtures.All(limit: 40).FirstOrDefault(s => s.Vis.BaseClusterCount > 500)?.Vis;

    /// <summary>
    /// Sampling occupied cells has to actually land in them, or the comparison is
    /// measuring the sampler. Uniform bounding-box sampling placed 800 points in
    /// 139,636 draws on ze_eizures_b1_1, a 0.57% hit rate, which is what this
    /// replaced.
    /// </summary>
    [Fact]
    public void OccupiedSamplingLandsInClusteredSpace()
    {
        var maps = 0;
        foreach (var specimen in VisFixtures.All(limit: 10))
        {
            if (specimen.Vis.BaseClusterCount < 100)
                continue;

            var query = new VoxelVisibilityQuery(specimen.Vis);
            var points = query.SampleOccupiedPoints(2000, new Random(11));
            Assert.Equal(2000, points.Length);

            var placed = points.Count(p => query.ClusterAt(p) >= 0);
            Assert.True(placed >= 1980,
                $"{specimen.Map}: only {placed} of 2000 sampled points landed in a cluster");
            output.WriteLine($"{specimen.Map,-40} {query.OccupiedLeaves().Length,7:n0} occupied leaves, "
                           + $"{placed * 100.0 / points.Length:F2}% of draws placed");
            maps++;
        }
        VisFixtures.RequireCorpus(maps);
    }

    [Fact]
    public void AMapComparedWithItselfShowsNoDifferenceAtAll()
    {
        var vis = Subject();
        if (vis is null)
        {
            VisFixtures.RequireCorpus(0);
            return;
        }

        var report = VisComparison.Compare(vis, vis, points: 800);

        Assert.True(report.Pairs > 100_000, $"only {report.Pairs} pairs, too few to prove anything");
        Assert.Equal(0, report.Holes);
        Assert.Equal(0, report.Overdraw);
        Assert.Equal(0, report.ReferenceOnlyPoints);
        Assert.Equal(0, report.CandidateOnlyPoints);
        Assert.Equal(1.0, report.Agreement);
        output.WriteLine($"identical inputs: {report.Pairs:n0} pairs, {report.Agreement * 100:F4}% agreement, "
                       + $"{report.AgreeVisible:n0} visible / {report.AgreeHidden:n0} hidden");
    }

    [Fact]
    public void RemovingVisibilityReadsAsHolesAndNeverAsOverdraw()
    {
        var vis = Subject();
        if (vis is null)
        {
            VisFixtures.RequireCorpus(0);
            return;
        }

        // A strict subset of the reference's visibility: the candidate can only
        // ever be MORE conservative, so every disagreement must be a hole.
        var pvs = (byte[])vis.VisBlocks.Clone();
        for (var i = 0; i < pvs.Length; i += 7)
            pvs[i] = 0;

        var report = VisComparison.Compare(vis, WithPvs(vis, pvs), points: 800);

        Assert.Equal(0, report.Overdraw);
        Assert.True(report.Holes > 0, "clearing a seventh of the PVS produced no holes");
        Assert.Equal(0.0, report.PlacementDisagreement);
        output.WriteLine($"a seventh of the PVS cleared: {report.Holes:n0} holes "
                       + $"({report.HoleRate * 100:F2}% of visible), agreement {report.Agreement * 100:F2}%");
    }

    [Fact]
    public void AddingVisibilityReadsAsOverdrawAndNeverAsHoles()
    {
        var vis = Subject();
        if (vis is null)
        {
            VisFixtures.RequireCorpus(0);
            return;
        }

        // Everything visible from everything: the worst legal answer a builder
        // could give, correct in the sense that it hides nothing.
        var pvs = new byte[vis.VisBlocks.Length];
        Array.Fill(pvs, (byte)0xFF);

        var report = VisComparison.Compare(vis, WithPvs(vis, pvs), points: 800);

        Assert.Equal(0, report.Holes);
        Assert.Equal(0.0, report.HoleRate);
        Assert.True(report.Overdraw > 0, "a fully visible PVS produced no overdraw");
        output.WriteLine($"everything visible: {report.Overdraw:n0} overdraw "
                       + $"({report.OverdrawRate * 100:F2}% of hidden), agreement {report.Agreement * 100:F2}%");
    }

    [Fact]
    public void TheSamplerIsReproducible()
    {
        var vis = Subject();
        if (vis is null)
        {
            VisFixtures.RequireCorpus(0);
            return;
        }

        var pvs = (byte[])vis.VisBlocks.Clone();
        for (var i = 0; i < pvs.Length; i += 11)
            pvs[i] &= 0x0F;
        var candidate = WithPvs(vis, pvs);

        var first = VisComparison.Compare(vis, candidate, points: 400);
        var second = VisComparison.Compare(vis, candidate, points: 400);
        Assert.Equal(first, second);

        // A different seed must move the numbers a little but not the verdict.
        var other = VisComparison.Compare(vis, candidate, points: 400, seed: 7);
        Assert.NotEqual(first.Holes, 0);
        Assert.True(Math.Abs(first.HoleRate - other.HoleRate) < 0.05,
            $"hole rate swung from {first.HoleRate:P2} to {other.HoleRate:P2} on a reseed, "
            + "so 400 points is not enough for a stable reading");
        output.WriteLine($"seed {20260920}: {first.HoleRate * 100:F3}% holes, "
                       + $"seed 7: {other.HoleRate * 100:F3}%");
    }
}
