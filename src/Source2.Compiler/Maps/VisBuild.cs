namespace Source2.Compiler.Maps;

/// <summary>
/// Visibility end to end, as visbuilder's VisBuild runs it: the root cube
/// from the scene, voxelize, outside detection, compaction, cluster
/// generation and the pre-merge, merging, assignment, the PVS scan, the
/// vis-cluster merge, borders, sky, sun, the collapse and the output. Each
/// stage is ported and checked on its own (docs/VISIBILITY.md); this only runs them
/// in the compile's order on one scene.
/// </summary>
public static class VisBuild
{
    /// <summary>
    /// The map's world_visibility VXVS from its trace scene and the settings
    /// the world stage hands visibility (<see cref="VisConfig"/>).
    /// </summary>
    /// <param name="baseVoxelSize">ResourceCompiler/VisBuilder/BaseVoxelSize, 8 unless the game overrides it.</param>
    /// <param name="stage">Told the name of each stage as it finishes, for progress.</param>
    public static VoxelVisibility Run(RayTraceEnvironment rte, VisConfig config, float baseVoxelSize = 8f, Action<string>? stage = null)
        => RunWithBlocks(rte, config, baseVoxelSize, stage).Vxvs;

    /// <summary>
    /// <see cref="Run"/>, with the <c>FlatVisClusterVector</c> the world
    /// renderer and the light vis membership read (<see cref="VisOutput.FlatClusterBoxes"/>).
    /// </summary>
    public static (VoxelVisibility Vxvs, List<(System.Numerics.Vector3 Min, System.Numerics.Vector3 Max)>[] FlatClusterBoxes) RunWithBlocks(
        RayTraceEnvironment rte, VisConfig config, float baseVoxelSize = 8f, Action<string>? stage = null, Action<string, object>? inspect = null)
    {
        ArgumentNullException.ThrowIfNull(rte);
        ArgumentNullException.ThrowIfNull(config);
        var (mins, maxs) = rte.TracedBounds;
        var (min, max) = VisVoxelizer.RootCube(mins, maxs, baseVoxelSize);

        var hints = VisVoxelizer.VoxelHints(config.Hints, mins, maxs, min, max, baseVoxelSize);
        var tree = VisVoxelizer.Build(rte, min, max, baseVoxelSize, hints);
        stage?.Invoke("voxelize");
        var side = VisVoxelizer.VoxelsPerRoot(min, max, baseVoxelSize) / VisVoxelizer.VoxelsPerLeaf;
        var regions = VisRegions.Build(tree, side);
        var inside = VisOutside.Detect(tree, regions, rte, baseVoxelSize);
        stage?.Invoke("outside");
        var compact = VisRegions.Compact(regions, inside.Regions);
        var sets = VisClusters.Generate(rte, tree, compact, VisClusters.SplitHints.From(config.Hints));
        stage?.Invoke("clusters");
        var pre = VisPreMerge.Run(sets);
        VisClusterSet.MergeAll(rte, sets, VisClusters.PassTarget(tree, compact), VisClusters.Cubes(tree, compact),
                               entering: inspect == null ? null : (pass, at) => inspect("merge-pass", (pass, at)));
        inspect?.Invoke("merge-pass", (VisClusterSet.Passes.Length, (IReadOnlyList<VisClusterSet.Set>)sets));
        stage?.Invoke("merge");
        var collapsedRegions = VisRegions.Collapse(regions, inside.Regions);
        var assigned = VisAssign.Run(sets, compact.Leaves.Count, collapsedRegions, _ => true);
        var sizes = sets.SelectMany(set => set.Clusters).Select(c => c.VoxelSize).ToArray();
        var s = VisPvs.Build(tree, max, compact, assigned, sets, baseVoxelSize);
        stage?.Invoke("assign");
        inspect?.Invoke("assign", s);

        var matrix = VisPvs.Scan(s, rte, config);
        stage?.Invoke("scan");
        inspect?.Invoke("scan", matrix);
        var merged = VisClusterList.Run(s, matrix, sizes, VisClusterList.Volume(s, pre.Volume, pre.After), baseVoxelSize);
        stage?.Invoke("vis-cluster merge");
        inspect?.Invoke("vis-cluster merge", merged);
        var open = VisSun.OpenCells(merged.State);
        var (borders, claims) = VisBorders.Sample(merged.State, rte);
        var flat = VisOutput.FlatClusterBoxes(merged.State, claims);
        var state = VisBorders.Consolidate(VisBorders.Rewrite(merged.State, borders, claims));
        stage?.Invoke("borders");
        inspect?.Invoke("borders", (borders, claims, flat, state));
        var sky = VisSky.Visible(state, rte, matrix);
        var sun = config.DirToSun is { } dir ? VisSun.Visible(state, rte, dir, open) : null;
        inspect?.Invoke("sky", (sky, sun));
        var (collapsed, _) = VisCollapse.Run(state, Enumerable.Repeat((ushort)0xffff, state.NodeWords.Length).ToArray());
        return (VisOutput.Build(collapsed, matrix, sky, sun, min, max), flat);
    }
}
