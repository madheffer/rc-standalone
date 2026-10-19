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

    /// <summary>One output polygon of the Recast stage: a detail triangle (CNavGenPoly via 18004fe30).</summary>
    public readonly record struct OutputPoly(int A, int B, int C, byte Kind, int Flags);

    /// <summary>
    /// The whole tiled stage (180084da0): each tile built, then collected in order (180082280). Every
    /// detail vertex goes back through the inverted matrix and onto the pool as doubles; each detail
    /// triangle becomes a three-index polygon over the pool, kind 1 for area 0x3d, 2 for 0x3e, else
    /// 0, flags 4 for area 0x20, else 1. The input polygons and the input pool are both dropped:
    /// the output pool holds the detail vertices only.
    /// </summary>
    public (List<OutputPoly> Polys, List<(double X, double Y, double Z)> Pool) Run()
    {
        var polys = new List<OutputPoly>();
        var outPool = new List<(double X, double Y, double Z)>();
        foreach (var tile in Tiles())
        {
            tile.Rasterize();
            tile.BuildRegions();
            tile.BuildPolyMesh();
            if (tile.DetailMesh is not { } dm || tile.PolyMesh is not { } pm)
                continue;
            var baseIndex = outPool.Count;
            for (var i = 0; i < dm.NVerts; i++)
            {
                var (x, y, z) = RecastInput.FromRecast(dm.Verts[i * 3], dm.Verts[i * 3 + 1], dm.Verts[i * 3 + 2]);
                outPool.Add((x, y, z));
            }
            for (var m = 0; m < dm.NMeshes; m++)
            {
                var vbase = (int)dm.Meshes[m * 4] + baseIndex;
                var tbase = (int)dm.Meshes[m * 4 + 2];
                var ntris = (int)dm.Meshes[m * 4 + 3];
                var area = pm.Areas[m];
                var kind = area == 0x3d ? (byte)1 : area == 0x3e ? (byte)2 : (byte)0;
                var flags = area == 0x20 ? 4 : 1;
                for (var t = 0; t < ntris; t++)
                {
                    var tri = (tbase + t) * 4;
                    polys.Add(new OutputPoly(dm.Tris[tri] + vbase, dm.Tris[tri + 1] + vbase, dm.Tris[tri + 2] + vbase, kind, flags));
                }
            }
        }
        return (polys, outPool);
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
        DetailMesh = RecastDetailMesh.Build(PolyMesh, Chf, c.SampleDist, c.SampleMaxError);
    }

    public RecastDetailMesh? DetailMesh;
}
