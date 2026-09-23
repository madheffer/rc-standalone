using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// <c>Logs_ComputedClustersVisibleToSky</c>: a cluster sees the sky outright
/// when a box of one of its records, grown by 0.5, reaches a sky triangle (the
/// trace scene's triangles flagged 0x1000); it is visible to sky when its
/// matrix row holds any cluster that sees it outright.
/// </summary>
public static class VisSky
{
    /// <summary>The flag a sky triangle carries.</summary>
    public const ushort Sky = RayTraceEnvironment.CoarseOccupancyOnly;

    /// <summary>
    /// One bit per cluster, or null when the scene has no sky triangle, in which
    /// case the build stores no sky vector at all.
    /// </summary>
    public static uint[]? Visible(VisPvs.State s, RayTraceEnvironment rte, VisPvs.Matrix matrix)
    {
        var triangles = new List<Vector3[]>();
        foreach (var i in Enumerable.Range(0, rte.TriangleCount))
        {
            if (!rte.Traced(i) || (rte.Flags(i) & Sky) == 0 || rte.TracedCorners(i) is not { } corners)
                continue;
            if (corners.Any(c => !float.IsFinite(c.X) || !float.IsFinite(c.Y) || !float.IsFinite(c.Z)))
                continue;
            triangles.Add(corners);
        }
        if (triangles.Count == 0)
            return null;

        var n = s.Clusters;
        var words = (n + 31) >> 5;
        var byCluster = new List<int>[n];
        for (var c = 0; c < n; c++)
            byCluster[c] = [];
        for (var e = 0; e < s.Entries.Length; e++)
        {
            if ((uint)s.Entries[e].Cluster < (uint)n)
                byCluster[s.Entries[e].Cluster].Add(e);
        }

        var direct = new uint[words];
        Parallel.For(0, n, c =>
        {
            foreach (var e in byCluster[c])
            {
                var (lo, hi) = VisPvs.RegionBox(s, s.Entries[e]);
                lo = new Vector3(lo.X - 0.5f, lo.Y - 0.5f, lo.Z - 0.5f);
                hi = new Vector3(hi.X + 0.5f, hi.Y + 0.5f, hi.Z + 0.5f);
                var centre = new Vector3((hi.X + lo.X) * 0.5f, (hi.Y + lo.Y) * 0.5f, (hi.Z + lo.Z) * 0.5f);
                var half = new Vector3(hi.X - centre.X, hi.Y - centre.Y, hi.Z - centre.Z);
                if (triangles.Any(t => VisVoxelizer.Overlaps(t[0], t[1], t[2], centre, half)))
                {
                    Interlocked.Or(ref direct[c >> 5], 1u << (c & 31));
                    break;
                }
            }
        });

        var visible = new uint[words];
        for (var c = 0; c < n; c++)
        {
            var row = matrix.Rows[c];
            for (var w = 0; w < words; w++)
            {
                if ((row[w] & direct[w]) != 0)
                {
                    visible[c >> 5] |= 1u << (c & 31);
                    break;
                }
            }
        }
        return visible;
    }
}
