using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>What the compiled vis file actually holds, behind <c>SHAPE=1</c>.</summary>
public class VisShippedShape(ITestOutputHelper output)
{
    [Fact]
    public void TheShippedCounts()
    {
        if (Environment.GetEnvironmentVariable("SHAPE") is not { Length: > 0 })
            return;

        foreach (var map in new[] { "ze_hold_em_p", "cardtest", "probe01" })
        {
            var addon = map == "ze_hold_em_p" ? "s2c_lighting" : "s2c_rc_probe";
            if (VisFixtures.RayTraceScene(addon, map) is not var (_, valve))
                continue;

            var leaves = valve.Nodes.Count(n => n.IsLeaf);
            var withRegions = valve.Nodes.Count(n => n.IsLeaf && n.RegionCount > 0);
            var clusters = valve.Regions.Select(r => r.ClusterId).Distinct().Count();
            var geometry = valve.Regions.Count(r => r.IntersectsGeometry);
            output.WriteLine($"{map,-16} nodes {valve.Nodes.Length,7:n0}  leaves {leaves,7:n0}"
                + $"  leaves with regions {withRegions,7:n0}"
                + $"  regions {valve.Regions.Length,7:n0}"
                + $"  masks {valve.Masks.Length,6:n0}"
                + $"  clusters {clusters,6:n0} (base {valve.BaseClusterCount:n0})"
                + $"  intersecting geometry {geometry,7:n0}");
            output.WriteLine($"{"",-16} regions per leaf that has any:"
                + $" {(double)valve.Regions.Length / Math.Max(1, withRegions):F2}"
                + $"   bounds {valve.MinBounds} .. {valve.MaxBounds}  grid {valve.GridSize}");
        }
    }
}
