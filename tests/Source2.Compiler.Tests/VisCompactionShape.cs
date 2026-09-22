using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// What <c>180032670</c> leaves in the entry array for assignment to carry
/// through: per leaf, the enclosed union, the OUTSIDE union and the solid union,
/// each emitted only when it is non-empty. Behind <c>COMPACT=1</c>.
/// </summary>
public class VisCompactionShape(ITestOutputHelper output)
{
    [Fact]
    public void TheThreeUnionsPerLeaf()
    {
        if (Environment.GetEnvironmentVariable("COMPACT") is not { Length: > 0 })
            return;

        foreach (var (addon, map, assigned) in new[]
        {
            ("s2c_lighting", "ze_hold_em_p", 103_358),
            ("s2c_rc_probe", "cardtest", 30_817),
            ("s2c_rc_probe", "probe01", 30_665),
        })
        {
            if (VisFixtures.RayTraceScene(addon, map) is not var (rte, valve))
                continue;

            var tree = VisVoxelizer.Build(rte, valve.MinBounds, valve.MaxBounds, valve.GridSize);
            var side = VisVoxelizer.VoxelsPerRoot(valve.MinBounds, valve.MaxBounds, valve.GridSize)
                     / VisVoxelizer.VoxelsPerLeaf;
            var regions = VisRegions.Build(tree, side);
            var status = VisOutside.Detect(tree, regions, rte, valve.GridSize).Regions;

            var enclosed = new ulong[regions.Leaves.Count];
            var outside = new ulong[regions.Leaves.Count];
            for (var i = 0; i < regions.Regions.Count; i++)
            {
                var r = regions.Regions[i];
                if (status[i] == VisOutside.Status.Inside)
                    enclosed[r.Leaf] |= r.Open;
                else
                    outside[r.Leaf] |= r.Open;
            }

            var withEnclosed = enclosed.Count(m => m != 0);
            var withOutside = outside.Count(m => m != 0);
            var withSolid = regions.Leaves.Count(l => l.Solid != 0);

            output.WriteLine($"{map,-16} {regions.Leaves.Count,8:n0} leaves:"
                + $"  enclosed union {withEnclosed,8:n0}"
                + $"  OUTSIDE union {withOutside,8:n0}"
                + $"  solid union {withSolid,8:n0}"
                + $"   => compaction emits {withEnclosed + withOutside + withSolid,8:n0} entries");
            output.WriteLine($"{"",-16} assignment keeps the {withOutside + withSolid,8:n0}"
                + $" non-open ones and replaces the enclosed with a cluster's pairs;"
                + $" the compile's own total is {assigned,8:n0},"
                + $" leaving {assigned - withOutside - withSolid,8:n0} for pairs");
        }
    }
}
