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
        new("s2c_lighting", "ze_hold_em_p", 10_554, 0),
        new("s2c_rc_probe", "cardtest", 7_416, 0.024),
        new("s2c_rc_probe", "probe01", 7_316, 0.024),
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

            var error = (double)result.Inside / specimen.Target - 1;
            output.WriteLine($"{specimen.Map,-16} inside {result.Inside,7:n0}  outside {result.Outside,7:n0}"
                + $"  of {regions.Regions.Count,7:n0}   compile {specimen.Target,7:n0}  {error,8:P2}"
                + $"   ({result.Passes} passes)");

            Assert.True(result.Inside > 0, $"{specimen.Map}: nothing came out enclosed");
            Assert.True(Math.Abs(error) <= specimen.Tolerance,
                $"{specimen.Map}: {result.Inside:n0} enclosed regions against the compile's "
              + $"{specimen.Target:n0}, {error:P2} off");
        }
    }

    /// <summary>
    /// The reach a ray is given is OURS, not the compile's, so the answer must not
    /// hinge on it. Over a factor of 32 it moves by well under a percent, which is
    /// what makes the number above a measurement rather than a fit.
    /// </summary>
    [Fact]
    public void TheAnswerDoesNotHingeOnTheReachWeChose()
    {
        if (VisFixtures.RayTraceScene("s2c_lighting", "ze_hold_em_p") is not var (rte, valve))
            return;

        var tree = VisVoxelizer.Build(rte, valve.MinBounds, valve.MaxBounds, valve.GridSize);
        var side = VisVoxelizer.VoxelsPerRoot(valve.MinBounds, valve.MaxBounds, valve.GridSize)
                 / VisVoxelizer.VoxelsPerLeaf;
        var regions = VisRegions.Build(tree, side);

        var counts = new List<int>();
        foreach (var reach in new[] { 64f, 512f, 2048f })
            counts.Add(VisOutside.Detect(tree, regions, rte, valve.GridSize,
                                         VisOutside.AxialDirections, reach).Inside);

        output.WriteLine("enclosed regions at reach 64, 512, 2048: " + string.Join(", ", counts));
        var spread = (counts.Max() - counts.Min()) / (double)counts.Min();
        Assert.True(spread < 0.02, $"the reach moved the count by {spread:P2}, so it is carrying the answer");
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
            counts.Add(result.Inside);
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
