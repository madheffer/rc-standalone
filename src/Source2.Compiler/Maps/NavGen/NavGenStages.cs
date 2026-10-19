namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// navsystem's polygon stage list (20261015, 180022b60) and the convar values it reads (the value at
/// *(convar) + 0x58, captured from a default run by tools/re/capture_navgen.py).
/// </summary>
public static class NavGenStages
{
    /// <summary>180134b20: WeldVertices' and RemoveDegens' tolerance, LimitMaxPolyEdges' too.</summary>
    public const float WeldTolerance = 0.001f;

    /// <summary>180134b10: added to the hull radius by Recast and RemovePolysMarkedNoNav.</summary>
    public const float RadiusPad = 0.75f;

    /// <summary>180134bf0: RemoveStrayVerticalPolys keeps polygons steeper than this many degrees from vertical... (|nz| above its cosine).</summary>
    public const float StrayVerticalAngle = 88f;

    /// <summary>MergePolys (180073ba0 through its invoker 180027dc0).</summary>
    public const float MergeAngle = 8f;               // 180134c30
    public const float MergeMaxDeviation = 4f;        // 180134c00
    public const float MergeTolStart = 1e-5f;         // 180134a40
    public const float MergeTolEnd = 0.1f;            // 180134ba0
    public const float MergeWeldStart = 0f;           // 180134a00
    public const float MergeWeldEnd = 0.01f;          // 1801349d0
    public const int MergeRounds = 6;                 // 180134c20

    /// <summary>LimitMaxPolyEdgeLen (180073a90 via 180027d90): nav_gen_max_edge_len_split_tol (180134a20) and nav_gen_max_edge_len (180134b30).</summary>
    public const float EdgeLenLimitA = 24f;
    public const float EdgeLenLimitB = 512f;

    /// <summary>nav_gen_max_bottleneck_width (180134ae0): SplitBottleneckPolys' width.</summary>
    public const float BottleneckWidth = 128f;

    /// <summary>nav_gen_split_multi_connection_polys_tol (180134a30).</summary>
    public const float MultiConnectionTol = 0.01f;

    /// <summary>
    /// Stages 023-027 without the world: GenerateConnections, SplitBottleneckPolys,
    /// SplitPolysWithMultipleConnections, and RemoveSmallAreasOnEdge (its threshold the settings' +0x2c,
    /// skipped when their +0x58 byte is set). Not run here: AddJumps (026), which casts the hull through
    /// the physics world (vfn 0x580) and is not ported, and the stages before 023 that need markups
    /// (019) or the world (022).
    /// </summary>
    public static void ConnectStages(NavGenPolys g, NavGenSettings settings)
    {
        g.GenerateConnections(new NavGenPolys.ConnectSettings());
        g.SplitBottleneckPolys(BottleneckWidth);
        g.SplitPolysWithMultipleConnections(MultiConnectionTol);
        g.RemoveSmallAreasOnEdge(settings.SmallAreaOnEdgeRemovalSize, settings.Byte58 != 0);
    }

    /// <summary>
    /// Stage 029, IslandRemoval in its all-hulls form (nav_gen_island_removal_all_hulls 1; the
    /// single-hull stage before it is off, nav_gen_island_removal 0), skipped with the settings' +0x58
    /// byte. LadderConnect (028) before it is not ported.
    /// </summary>
    public static void IslandStage(NavGenPolys g, NavIslandInput input, NavGenSettings settings)
        => g.IslandRemoval(input, allHulls: true, skip: settings.Byte58 != 0);

    /// <summary>
    /// Stages 000-004 on the extracted polygons: ValidateVertices, WeldVertices, RemoveDegens,
    /// RemovePolysMarkedNoNav (no NONAV markups: nothing; markups not ported), TransformRecastNonZup
    /// (Z-up generation: nothing).
    /// </summary>
    public static bool BeforeRecast(NavGenPolys g)
    {
        if (!g.ValidateVertices())
            return false;
        var tol = (double)WeldTolerance;
        g.Weld(tol, tol);
        g.RemoveDegens(tol);
        return true;
    }

    /// <summary>
    /// Stages 006-018 on Recast's polygons: UnTransformRecastNonZup (Z-up: nothing), WeldVertices,
    /// RemoveDegens, RemovePolysMarkedNoNav, RemoveStrayVerticalPolys, WeldVertices, RemoveDegens,
    /// MergePolys, LimitMaxPolyEdges (4; the polygons pass through with an info_nav_oriented_mesh),
    /// LimitMaxPolyEdgeLen, RemoveStrayVerticalPolys, WeldVertices, RemoveDegens.
    /// </summary>
    public static void AfterRecast(NavGenPolys g, bool orientedMesh)
    {
        var tol = (double)WeldTolerance;
        g.Weld(tol, tol);
        g.RemoveDegens(tol);
        g.RemoveStrayVerticalPolys(StrayVerticalAngle, orientedMesh);
        g.Weld(tol, tol);
        g.RemoveDegens(tol);
        g.MergePolys(orientedMesh);
        if (!orientedMesh)
            g.Polys = g.SplitToFans(4);
        g.LimitMaxPolyEdgeLen(EdgeLenLimitB, EdgeLenLimitA);
        g.RemoveStrayVerticalPolys(StrayVerticalAngle, orientedMesh);
        g.Weld(tol, tol);
        g.RemoveDegens(tol);
    }
}
