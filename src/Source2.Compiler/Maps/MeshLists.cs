namespace Source2.Compiler.Maps;

/// <summary>
/// CWorldRendererBuilderNode::CompileNode's mesh lists (180277ab0): each
/// entry goes to the first list whose Accepts (vf 0x50) takes it; an entry
/// none takes (attribute bit 0, nodraw, past the skybox lists) is not
/// compiled. With visibility-guided clustering the aggregate, overlay and
/// base lists, in that order, go through CVisibilityMeshMerger::MergeMeshes.
/// </summary>
public static class MeshLists
{
    public enum Kind
    {
        SkyboxBlockLight, Skybox, BlockLight, NoSplitOverlay, Overlay, WorldSpaceTexture,
        SkyboxMaterial, BakedPropLod, NoSplit, Instanced, Aggregate, Base,
    }

    /// <summary>The lists in CompileNode's order: the attribute bits wanted (all) and excluded (any).</summary>
    public static readonly IReadOnlyList<(Kind Kind, ulong Want, ulong Exclude)> Order =
    [
        (Kind.SkyboxBlockLight, 0x48, 0), (Kind.Skybox, 0x40, 9), (Kind.BlockLight, 8, 0),
        (Kind.NoSplitOverlay, 0x1004, 0), (Kind.Overlay, 0x1000, 0), (Kind.WorldSpaceTexture, 0x8000, 0),
        (Kind.SkyboxMaterial, 0x80, 0), (Kind.BakedPropLod, 0x2000, 1), (Kind.NoSplit, 4, 1),
        (Kind.Instanced, 2, 1), (Kind.Aggregate, 0x40000000, 1), (Kind.Base, 0, 1),
    ];

    /// <summary>The lists merged by the visibility merger, in call order.</summary>
    public static readonly IReadOnlyList<Kind> Merged = [Kind.Aggregate, Kind.Overlay, Kind.Base];

    /// <summary>What the list tests read of an entry.</summary>
    /// <param name="Attributes">+0x1b0.</param>
    /// <param name="ObjectFlags">+0xbc.</param>
    /// <param name="OverlayOrder">+0xac.</param>
    /// <param name="FadeMax">+0xb4.</param>
    /// <param name="LightingOrigin">+0x90 is a non-empty name.</param>
    /// <param name="InstanceStream">+0x10 is set.</param>
    public sealed record Input(ulong Attributes, uint ObjectFlags, int OverlayOrder, float FadeMax, bool LightingOrigin, bool InstanceStream);

    /// <summary>The list an entry joins, or null when none takes it.</summary>
    public static Kind? Assign(Input e, bool useAggregateInstances = true, bool staticEnvMapWithLightingOrigin = true)
    {
        foreach (var (kind, want, exclude) in Order)
        {
            // CMeshList::Accepts (180270720); CAggregateMeshList::Accepts
            // (180272aa0) also needs UseAggregateInstances and CanAggregate.
            if ((e.Attributes & want) != want || (e.Attributes & exclude) != 0)
                continue;
            if (kind == Kind.Aggregate && !(useAggregateInstances && CanAggregate(e, staticEnvMapWithLightingOrigin)))
                continue;
            return kind;
        }
        return null;
    }

    /// <summary>
    /// WRBMeshEntry_CanAggregate (18026d4a0): none of attribute bits 0x800e01,
    /// bit 30 (SupportsAggregateInstancing), no object flag 0x200; no instance
    /// stream unless bit 31; no lighting origin when objects with one use the
    /// static env map; overlay order 0 and fade max 0.
    /// </summary>
    public static bool CanAggregate(Input e, bool staticEnvMapWithLightingOrigin = true)
    {
        if ((e.Attributes & 0x800e01) != 0 || (e.Attributes & 0x40000000) == 0 || (e.ObjectFlags & 0x200) != 0)
            return false;
        if (e.InstanceStream && (e.Attributes & 0x80000000) == 0)
            return false;
        if (staticEnvMapWithLightingOrigin && e.LightingOrigin)
            return false;
        return e.OverlayOrder == 0 && e.FadeMax == 0f;
    }
}
