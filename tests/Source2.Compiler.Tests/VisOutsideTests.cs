using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Stage 3's second half, scored against the compile's own log.
///
/// <para>The compile prints <c>Generated clusters for 10554 regions</c> after its
/// outside pass, so the count of ENCLOSED regions is the number to hit. The two
/// probe maps are scored far wider because 14% of their triangles mis-decode, and
/// that already puts stage 2 on them 41% out; see docs/VIS.md.</para>
/// </summary>
public class VisOutsideTests(ITestOutputHelper output)
{
    private sealed record Specimen(string Addon, string Map, int Target, double Tolerance);

    private static readonly Specimen[] Maps =
    [
        new("s2c_lighting", "ze_hold_em_p", 10_554, 0.08),
        new("s2c_rc_probe", "cardtest", 7_416, 0.004),
        new("s2c_rc_probe", "probe01", 7_316, 0.001),
    ];

    [Fact]
    public void TheEnclosedRegionCountLandsOnTheCompilesOwn()
    {
        foreach (var specimen in Maps)
        {
            if (VisFixtures.RayTraceScene(specimen.Addon, specimen.Map) is not var (rte, valve))
                continue;

            var tree = VisVoxelizer.Build(rte, valve.MinBounds, valve.MaxBounds, valve.GridSize);
            var side = VisVoxelizer.VoxelsPerRoot(valve.MinBounds, valve.MaxBounds, valve.GridSize)
                     / VisVoxelizer.VoxelsPerLeaf;
            var regions = VisRegions.Build(tree, side);
            var result = VisOutside.Detect(tree, regions, rte, valve.GridSize);

            // The compile compacts every leaf's regions to at most three BEFORE
            // it prints this count, so the number to score is the leaves holding
            // any enclosed space, not the enclosed boxes.
            var compact = VisRegions.Compact(regions, result.Regions);
            var counted = compact.Regions.Count;
            var error = (double)counted / specimen.Target - 1;
            var seed = result.Seeded!;
            output.WriteLine($"{specimen.Map,-16} seed: inside {seed.Count(x => x == VisOutside.Status.Inside),7:n0}"
                + $"  outside {seed.Count(x => x == VisOutside.Status.Outside),7:n0}"
                + $"  undecided {seed.Count(x => x == VisOutside.Status.Unknown),7:n0}");
            output.WriteLine($"{specimen.Map,-16} compacted {counted,7:n0}  from inside {result.Inside,7:n0}"
                + $"  of {regions.Regions.Count,7:n0}   compile {specimen.Target,7:n0}  {error,8:P2}"
                + $"   ({result.Passes} passes)");

            Assert.True(counted > 0, $"{specimen.Map}: nothing came out enclosed");
            Assert.True(Math.Abs(error) <= specimen.Tolerance,
                $"{specimen.Map}: {counted:n0} enclosed regions against the compile's "
              + $"{specimen.Target:n0}, {error:P2} off");
        }
    }

    /// <summary>
    /// There is no longer a knob in this stage to sweep.
    ///
    /// <para>The ray reach used to be ours and had its own spread test. It is
    /// gone: <c>18002e050</c> marches from the region's centre to a fixed
    /// <see cref="VisOutside.MarchBackOff"/> short of whatever the ray hit, and
    /// the vote is a pair of integer thresholds over the rays a face carries. The
    /// only number left that we chose is the seed's grid, and that is Valve's own
    /// five, swept below.</para>
    /// </summary>
    [Fact]
    public void NothingInOutsideDetectionIsOursToTuneAnyMore()
    {
        Assert.Equal(8f, VisOutside.MarchBackOff);
        Assert.Equal(0.1f, VisOutside.MarchShortest);
        Assert.Equal(5, VisOutside.OutsideVotesAllowed);
        Assert.Equal(5, VisSeed.Quality);
    }

    /// <summary>
    /// And it does not hinge on how finely the seed samples a face.
    ///
    /// <para>The grid is Valve's own five, passed as a literal at the call site,
    /// so this is not a knob we chose. It is swept anyway, because a stage that
    /// only lands on the right number at one sample count is fitting the sample
    /// count rather than reproducing the stage.</para>
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void TheAnswerDoesNotHingeOnHowFinelyTheSeedSamples(int which)
    {
        var specimen = Maps[which];
        if (VisFixtures.RayTraceScene(specimen.Addon, specimen.Map) is not var (rte, valve))
            return;

        var tree = VisVoxelizer.Build(rte, valve.MinBounds, valve.MaxBounds, valve.GridSize);
        var side = VisVoxelizer.VoxelsPerRoot(valve.MinBounds, valve.MaxBounds, valve.GridSize)
                 / VisVoxelizer.VoxelsPerLeaf;
        var regions = VisRegions.Build(tree, side);

        var counts = new List<int>();
        foreach (var quality in new[] { 3, 4, 5, 6, 8 })
        {
            var result = VisOutside.Detect(tree, regions, rte, valve.GridSize, quality: quality);
            counts.Add(VisRegions.Compact(regions, result.Regions).Regions.Count);
            output.WriteLine($"  {quality}x{quality} rays a face: {result.Inside,7:n0}"
                           + $"  {(double)result.Inside / specimen.Target - 1,8:P2}");
        }

        var spread = (double)(counts.Max() - counts.Min()) / counts.Min();
        output.WriteLine($"{specimen.Map,-16} {counts.Min():n0} to {counts.Max():n0} over"
                       + $" {counts.Count} grids, {spread:P2} spread");
        Assert.True(spread <= 0.05,
            $"{specimen.Map}: the seed's grid moves the enclosed count by {spread:P2},"
          + " so the sample count is carrying the answer");
    }
}
