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
        new("s2c_rc_probe", "cardtest", 7_416, 0.50),
        new("s2c_rc_probe", "probe01", 7_316, 0.50),
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
            var result = VisOutside.Detect(tree, regions, rte.Mins, rte.Maxs, valve.GridSize);

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
            counts.Add(VisOutside.Detect(tree, regions, rte.Mins, rte.Maxs, valve.GridSize,
                                         VisOutside.AxialDirections, reach).Inside);

        output.WriteLine("enclosed regions at reach 64, 512, 2048: " + string.Join(", ", counts));
        var spread = (counts.Max() - counts.Min()) / (double)counts.Min();
        Assert.True(spread < 0.02, $"the reach moved the count by {spread:P2}, so it is carrying the answer");
    }

    /// <summary>
    /// And it does not hinge on the seed's thresholds either.
    ///
    /// <para>The compile's seed is a threshold tree over four counters from a
    /// gather whose record type is not decoded, so the two ratios here are OURS.
    /// A number we chose landing on Valve's to the unit is either the answer or a
    /// fit, and the way to tell them apart is to move the knob: if a wide range
    /// of thresholds all land on 10,554, the thresholds are not what is producing
    /// it.</para>
    /// </summary>
    [Fact]
    public void TheAnswerDoesNotHingeOnTheSeedThresholdsEither()
    {
        var specimen = Maps[0];
        if (VisFixtures.RayTraceScene(specimen.Addon, specimen.Map) is not var (rte, valve))
            return;

        var tree = VisVoxelizer.Build(rte, valve.MinBounds, valve.MaxBounds, valve.GridSize);
        var side = VisVoxelizer.VoxelsPerRoot(valve.MinBounds, valve.MaxBounds, valve.GridSize)
                 / VisVoxelizer.VoxelsPerLeaf;
        var regions = VisRegions.Build(tree, side);

        var counts = new List<int>();
        foreach (var sealedAt in new[] { 0.70, 0.80, 5d / 6, 0.90, 0.96 })
            foreach (var openAt in new[] { 0.10, 0.25, 1d / 3, 0.45 })
            {
                var result = VisOutside.Detect(tree, regions, rte.Mins, rte.Maxs, valve.GridSize,
                                               sealedAt: sealedAt, openAt: openAt);
                counts.Add(result.Inside);
                output.WriteLine($"  sealed at {sealedAt:P0}, open at {openAt:P0}: {result.Inside,7:n0}");
            }

        var spread = (double)(counts.Max() - counts.Min()) / counts.Min();
        output.WriteLine($"{specimen.Map,-16} {counts.Min():n0} to {counts.Max():n0} over"
                       + $" {counts.Count} threshold pairs, {spread:P2} spread");
        Assert.True(spread <= 0.02,
            $"the seed thresholds move the enclosed count by {spread:P2}, so they are carrying the answer");
    }
}
