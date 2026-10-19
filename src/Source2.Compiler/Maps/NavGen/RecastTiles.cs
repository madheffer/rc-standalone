namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// The tiled Recast path (180084da0): the input and config (1800852a0), then one CNavGenRecastTile
/// per tile (180081710; x outer, z inner), each built (180082090) and collected in that order
/// (180082280) whatever order the thread pool ran them in.
/// </summary>
public sealed class RecastTiles
{
    public RecastConfig Config;
    public RecastInput Input;
    public ChunkyTriMesh Chunky;
    public int TilesX, TilesZ;

    public RecastTiles(RecastConfig config, RecastInput input)
    {
        Config = config;
        Input = input;
        Chunky = ChunkyTriMesh.Create(input.Verts, input.Tris, input.TriCount, 256);
        // rcCalcGridSize as navsystem calls it for tiles (1800a8870): (int)(extent / tile) + 1.
        var tcs = (float)config.TileSize * config.Cs;
        TilesX = (int)((input.BMax[0] - input.BMin[0]) / tcs) + 1;
        TilesZ = (int)((input.BMax[2] - input.BMin[2]) / tcs) + 1;
    }

    public IEnumerable<RecastTile> Tiles()
    {
        for (var x = 0; x < TilesX; x++)
            for (var z = 0; z < TilesZ; z++)
                yield return new RecastTile(this, x, z);
    }
}

/// <summary>One tile's build (CNavGenRecastTile, 180082090), step by step.</summary>
public sealed class RecastTile
{
    public readonly RecastTiles Owner;
    public readonly int TileX, TileZ;
    public float[] BMin = new float[3], BMax = new float[3];
    public RecastHeightfield? Solid;

    public RecastTile(RecastTiles owner, int tx, int tz)
    {
        Owner = owner;
        TileX = tx;
        TileZ = tz;
        var c = owner.Config;
        var bmin = owner.Input.BMin;
        var bmax = owner.Input.BMax;
        var tcs = (float)c.TileSize * c.Cs;
        BMin[0] = bmin[0] + (float)tx * tcs;
        BMin[1] = bmin[1];
        BMin[2] = (float)tz * tcs + bmin[2];
        BMax[0] = (float)(tx + 1) * tcs + bmin[0];
        BMax[1] = bmax[1];
        BMax[2] = (float)(tz + 1) * tcs + bmin[2];
        var pad = (float)c.BorderSize * c.Cs;
        BMin[0] -= pad;
        BMin[2] -= pad;
        BMax[0] = pad + BMax[0];
        BMax[2] = pad + BMax[2];
    }

    /// <summary>1800814b0: rasterise the overlapping chunks, then the three span filters (180082090).</summary>
    public void Rasterize()
    {
        var c = Owner.Config;
        var cm = Owner.Chunky;
        var ids = cm.ChunksOverlappingRect(BMin[0], BMin[2], BMax[0], BMax[2], Owner.Input.TriCount);
        if (ids.Count == 0)
            return;
        Solid = new RecastHeightfield(c.Width, c.Height, BMin, BMax, c.Cs, c.Ch);
        foreach (var id in ids)
        {
            var node = cm.Nodes[id];
            var areas = new byte[node.N];
            RecastHeightfield.MarkWalkableTriangles(c.WalkableSlope, Owner.Input.Verts, cm.Tris, node.I, node.N, areas);
            Solid.RasterizeTriangles(Owner.Input.Verts, cm.Tris, node.I, areas, node.N, c.WalkableClimb);
        }
        Solid.FilterLowHangingWalkableObstacles(c.WalkableClimb);
        Solid.FilterLedgeSpans(c.WalkableHeight, c.WalkableClimb);
        Solid.FilterWalkableLowHeightSpans(c.WalkableHeight, c.WalkableHeight2, c.WalkableHeight3);
    }

    public RecastCompact? Chf;

    /// <summary>
    /// 1800850a0: the compact heightfield, Valve's erosion, then (region convar 0, median filter off)
    /// the distance field and watershed regions.
    /// </summary>
    public void BuildRegions()
    {
        if (Solid is null || Solid.WalkableSpanCount() == 0)
            return;
        var c = Owner.Config;
        Chf = RecastCompact.Build(Solid, c.WalkableHeight, c.WalkableHeight2, c.WalkableHeight3, c.WalkableClimb);
        Chf.Erode(c.Unknown104, c.WalkableRadius, Solid, c.Unknown100);
        Chf.BuildDistanceField();
        Chf.BuildRegions(c.BorderSize, c.MinRegionArea, c.MergeRegionArea);
    }

    public RecastContourSet? Contours;
    public RecastPolyMesh? PolyMesh;

    /// <summary>180082090's tail: contours (wall edges tessellated), then the poly mesh when any contour came out.</summary>
    public void BuildPolyMesh()
    {
        if (Chf is null)
            return;
        var c = Owner.Config;
        Contours = RecastContourSet.Build(Chf, c.MaxSimplificationError, c.MaxEdgeLenCells, 1);
        if (Contours.Contours.Count == 0)
            return;
        PolyMesh = RecastPolyMesh.Build(Contours, c.Nvp);
    }
}
