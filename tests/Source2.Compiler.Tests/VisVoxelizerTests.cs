using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Stage 2 of visibility, scored against the compile's own log.
///
/// <para>resourcecompiler prints <c>Voxelize (8 units) took 0.49 seconds (81,625
/// nodes)</c>, so the stage has a number to hit that does not depend on a geometry
/// pipeline existing: the <c>.rte</c> it voxelizes survives the compile in
/// <c>%TEMP%</c>. The maps below are skipped when that file is not there, because a
/// missing intermediate is not evidence either way.</para>
/// </summary>
public class VisVoxelizerTests(ITestOutputHelper output)
{
    /// <param name="Target">The node count the compile logged for this map.</param>
    /// <param name="Tolerance">How far off we are allowed to be. ze_hold_em_p is
    /// exact and pinned at zero; the two probe maps sit six branches over, which
    /// docs/VIS.md records as open.</param>
    private sealed record Specimen(string Addon, string Map, int Target, double Tolerance);

    private static readonly Specimen[] Maps =
    [
        new("s2c_lighting", "ze_hold_em_p", 81_625, 0),
        new("s2c_rc_probe", "cardtest", 17_297, 0.003),
        new("s2c_rc_probe", "probe01", 17_169, 0.003),
    ];

    [Fact]
    public void VoxelizeLandsOnTheNodeCountTheCompileLogged()
    {
        var measured = 0;
        foreach (var specimen in Maps)
        {
            if (Load(specimen) is not var (rte, valve))
                continue;

            var tree = VisVoxelizer.Build(rte, valve.MinBounds, valve.MaxBounds, valve.GridSize);
            var error = (double)tree.Nodes / specimen.Target - 1;
            output.WriteLine($"{specimen.Map,-16} ours {tree.Nodes,8:n0}  compile {specimen.Target,8:n0}  {error,8:P2}"
                           + $"   ({tree.Branches:n0} branches, {tree.LeafMasks.Count:n0} occupied leaves, depth {tree.Depth})");
            Assert.True(Math.Abs(error) <= specimen.Tolerance,
                $"{specimen.Map}: {tree.Nodes:n0} nodes against the compile's {specimen.Target:n0}, {error:P2} off");
            measured++;
        }
        Assert.True(measured > 0 || CS2Fixtures.StockPak() is null,
            "no .rte was available, so the voxelizer was not measured against anything");
    }

    /// <summary>
    /// Every branch of the SHIPPED octree has to be a branch of ours.
    ///
    /// <para>This is the check that does not depend on a tolerance. The shipped tree
    /// is this one collapsed, and a collapse only ever removes nodes, so anything it
    /// still subdivides we must subdivide too. Nodes we have and it does not are
    /// expected and say nothing.</para>
    /// </summary>
    [Fact]
    public void EveryBranchTheCompiledTreeKeepsIsOneWeAlsoSubdivide()
    {
        foreach (var specimen in Maps)
        {
            if (Load(specimen) is not var (rte, valve))
                continue;

            var tree = VisVoxelizer.Build(rte, valve.MinBounds, valve.MaxBounds, valve.GridSize);
            var side = VisVoxelizer.VoxelsPerRoot(valve.MinBounds, valve.MaxBounds, valve.GridSize)
                     / VisVoxelizer.VoxelsPerLeaf;
            var theirs = Branches(valve, (int)Math.Log2(side));

            var ours = tree.BranchCells;
            var missing = theirs.Count(t => !ours.Contains(t));
            var share = 1 - (double)missing / theirs.Count;
            output.WriteLine($"{specimen.Map,-16} {theirs.Count,6:n0} branches in the shipped tree, "
                           + $"{missing} not in ours, {share:P2} covered; "
                           + $"{ours.Count - (theirs.Count - missing)} of ours are not in it");
            Assert.True(share >= 1.0,
                $"{specimen.Map}: only {share:P2} of the shipped tree's branches are branches of ours");
        }
    }

    private static (RayTraceEnvironment Rte, VoxelVisibility Valve)? Load(Specimen specimen)
    {
        var rtePath = Path.Combine(Path.GetTempPath(), "csgo_addons", specimen.Addon, "maps", specimen.Map + ".rte");
        var cs2 = CS2Fixtures.StockPak();
        if (!File.Exists(rtePath) || cs2 is null)
            return null;

        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cs2)!, ".."));
        var vpk = Path.Combine(root, "csgo_addons", specimen.Addon, "maps", specimen.Map + ".vpk");
        if (!File.Exists(vpk))
            return null;

        using var package = new ValvePak.Package();
        package.Read(vpk);
        var entry = package.Entries.GetValueOrDefault("vvis_c")?.FirstOrDefault();
        if (entry is null)
            return null;
        package.ReadEntry(entry, out var bytes);
        return (RayTraceEnvironment.ReadFile(rtePath), VoxelVisibilityReader.Read(bytes));
    }

    /// <summary>The shipped tree's branch nodes, as (level above leaf, cell).</summary>
    private static HashSet<(int Level, (int X, int Y, int Z) Cell)> Branches(VoxelVisibility vis, int depth)
    {
        var found = new HashSet<(int, (int, int, int))>();
        var stack = new Stack<(int Index, int Level, (int X, int Y, int Z) Cell)>();
        stack.Push((0, depth, (0, 0, 0)));
        while (stack.Count > 0)
        {
            var (index, level, cell) = stack.Pop();
            if (vis.Nodes[index].IsLeaf || level == 0)
                continue;
            found.Add((level, cell));
            for (var octant = 0; octant < 8; octant++)
                stack.Push(((int)vis.Nodes[index].Offset + octant, level - 1,
                    (cell.X * 2 + (octant & 1), cell.Y * 2 + ((octant >> 1) & 1), cell.Z * 2 + ((octant >> 2) & 1))));
        }
        return found;
    }
}
