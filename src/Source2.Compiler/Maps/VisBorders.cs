using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// <c>CVoxelSampler3::AdaptivelySampleBorders</c>: the border records (kind 2)
/// carry no cluster, so each is sampled from the open records around it. From
/// every neighbouring record's centre a segment is traced to each of 294 points
/// spread over the border record's box; what lands inside that box, whether
/// the sample point itself or the front face a segment stopped on, is boxed,
/// and a box wider than 0.1 on every axis is claimed for the neighbour's
/// cluster. When the traces met fewer than two distinct surfaces, only the
/// nearest neighbour's claim is kept.
/// </summary>
public static class VisBorders
{
    /// <summary>Triangles a border trace passes straight through, the tracer's <c>0x811</c>.</summary>
    public const ushort Ignored = 0x811;

    /// <summary>One claim: a cluster and the part of the border box it reaches, the 0x1c byte record.</summary>
    public readonly record struct Claim(Vector3 Mins, Vector3 Maxs, int Cluster);

    /// <summary>
    /// <c>FUN_18002a260</c> over the box (-1,-1,-1) to (1,1,1): per axis a 7 by 7
    /// grid, edges included, on both faces. 294 points, duplicates on the edges
    /// kept.
    /// </summary>
    public static Vector3[] CubePoints()
    {
        var found = new List<Vector3>(294);
        for (var axis = 0; axis < 3; axis++)
        {
            for (var i = 0; i < 7; i++)
            {
                var u = ((float)i * 0.16666667f) + 0f;
                u = (u + u) - 1f;
                for (var j = 0; j < 7; j++)
                {
                    var v = ((float)j * 0.16666667f) + 0f;
                    v = (v + v) - 1f;
                    var p = axis switch
                    {
                        0 => new Vector3(1f, u, v),
                        1 => new Vector3(u, 1f, v),
                        _ => new Vector3(u, v, 1f),
                    };
                    found.Add(p);
                    found.Add(axis switch
                    {
                        0 => p with { X = -1f },
                        1 => p with { Y = -1f },
                        _ => p with { Z = -1f },
                    });
                }
            }
        }
        return [.. found];
    }

    /// <summary>
    /// Sample every border record. Returns their entry indices in array order
    /// and, per border record, its claims in the order they were first made.
    /// </summary>
    public static (int[] Borders, List<Claim>[] Claims) Sample(VisPvs.State s, RayTraceEnvironment rte)
    {
        var borders = Enumerable.Range(0, s.Entries.Length).Where(e => (s.Entries[e].Packed & 2) != 0).ToArray();
        var cube = CubePoints();
        var claims = new List<Claim>[borders.Length];
        Parallel.For(0, borders.Length, k => claims[k] = One(s, rte, cube, borders[k]));
        return (borders, claims);
    }

    // FUN_18003cbc0, the job body for one border record.
    private static List<Claim> One(VisPvs.State s, RayTraceEnvironment rte, Vector3[] cube, int border)
    {
        var (bmin, bmax) = VisPvs.RegionBox(s, s.Entries[border]);
        var cy = (bmax.Y + bmin.Y) * 0.5f;
        var cz = (bmax.Z + bmin.Z) * 0.5f;
        var hy = (bmax.Y - cy) - 0.1f;
        var hz = (bmax.Z - cz) - 0.1f;
        var hx = (bmax.X - ((bmax.X + bmin.X) * 0.5f)) - 0.1f;
        hy = hy <= 0f ? 0f : hy;
        hz = hz <= 0f ? 0f : hz;
        hx = hx <= 0f ? 0f : hx;
        var points = new Vector3[cube.Length];
        for (var i = 0; i < cube.Length; i++)
        {
            points[i] = new Vector3(((bmax.X + bmin.X) * 0.5f) + (hx * cube[i].X),
                                    (hy * cube[i].Y) + cy, (hz * cube[i].Z) + cz);
        }

        var neighbours = Neighbours(s, border);
        var claims = new List<Claim>();
        var normals = new List<Vector3>();
        var hits = new List<Vector3>();
        var closest = 0;
        var nearest = 2f;
        foreach (var n in neighbours)
        {
            var nbox = VisPvs.RegionBox(s, s.Entries[n]);
            var centre = new Vector3((nbox.Maxs.X + nbox.Mins.X) * 0.5f, (nbox.Maxs.Y + nbox.Mins.Y) * 0.5f,
                                     (nbox.Maxs.Z + nbox.Mins.Z) * 0.5f);
            Trace(rte, centre, points, hits, normals);
            if (hits.Count == 0)
                continue;
            var mn = new Vector3(float.MaxValue);
            var mx = new Vector3(-float.MaxValue);
            foreach (var p in hits)
            {
                if (!(bmin.X <= p.X && bmin.Y <= p.Y && bmin.Z <= p.Z && p.X <= bmax.X && p.Y <= bmax.Y && p.Z <= bmax.Z))
                    continue;
                mn = new Vector3(p.X <= mn.X ? p.X : mn.X, p.Y <= mn.Y ? p.Y : mn.Y, p.Z <= mn.Z ? p.Z : mn.Z);
                mx = new Vector3(mx.X <= p.X ? p.X : mx.X, mx.Y <= p.Y ? p.Y : mx.Y, mx.Z <= p.Z ? p.Z : mx.Z);
            }
            if (!(mn.X <= mx.X && mn.Y <= mx.Y && mn.Z <= mx.Z))
                continue;
            var extent = MathF.Abs(mx.X - mn.X);
            if (MathF.Abs(mx.Y - mn.Y) <= extent)
                extent = MathF.Abs(mx.Y - mn.Y);
            if (MathF.Abs(mx.Z - mn.Z) <= extent)
                extent = MathF.Abs(mx.Z - mn.Z);
            if (!(0.1f < extent))
                continue;
            var at = Claimed(claims, s.Entries[n].Cluster, mn, mx);
            var gap = VisMergeCost.Proximity(nbox, (bmin, bmax));
            if (gap < nearest)
            {
                closest = at;
                nearest = gap;
            }
        }
        if (normals.Count < 2 && claims.Count > 1 && closest >= 0 && closest < claims.Count)
        {
            (claims[0], claims[closest]) = (claims[closest], claims[0]);
            claims.RemoveRange(1, claims.Count - 1);
        }
        return claims;
    }

    // FUN_18002e8b0: the open records within the base voxel size of this one's
    // box, in the walk's order, adjacent repeats dropped.
    private static List<int> Neighbours(VisPvs.State s, int entry)
    {
        var (lo, hi) = VisPvs.RegionBox(s, s.Entries[entry]);
        var grow = s.BaseVoxelSize;
        var found = VisPvs.Entries(s, new Vector3(lo.X - grow, lo.Y - grow, lo.Z - grow),
                                   new Vector3(hi.X + grow, hi.Y + grow, hi.Z + grow));
        var unique = new List<int>(found.Count);
        foreach (var e in found)
        {
            if (unique.Count == 0 || unique[^1] != e)
                unique.Add(e);
        }
        return unique;
    }

    // FUN_18002a6e0: a claim for a cluster already claimed grows that one's box.
    private static int Claimed(List<Claim> claims, int cluster, Vector3 mn, Vector3 mx)
    {
        for (var i = 0; i < claims.Count; i++)
        {
            if (claims[i].Cluster != cluster)
                continue;
            var c = claims[i];
            claims[i] = new Claim(
                new Vector3(c.Mins.X <= mn.X ? c.Mins.X : mn.X, c.Mins.Y <= mn.Y ? c.Mins.Y : mn.Y, c.Mins.Z <= mn.Z ? c.Mins.Z : mn.Z),
                new Vector3(mx.X <= c.Maxs.X ? c.Maxs.X : mx.X, mx.Y <= c.Maxs.Y ? c.Maxs.Y : mx.Y, mx.Z <= c.Maxs.Z ? c.Maxs.Z : mx.Z),
                cluster);
            return i;
        }
        claims.Add(new Claim(mn, mx, cluster));
        return claims.Count - 1;
    }

    // FUN_18004c0e0: a segment from the origin to each point. A miss keeps the
    // point; a front face keeps where it was met, clamped to the segment, and
    // its unit normal joins the list unless one within 0.9999 to 1.001 of it is
    // there already. A back face keeps nothing. The normals are not reset here.
    private static void Trace(RayTraceEnvironment rte, Vector3 o, Vector3[] points, List<Vector3> hits, List<Vector3> normals)
    {
        hits.Clear();
        foreach (var p in points)
        {
            if (rte.Segment(o, p, Ignored) is not { } hit)
            {
                hits.Add(p);
                continue;
            }
            var t = hit.Distance;
            var n = hit.Normal;
            float dx = p.X - o.X, dy = p.Y - o.Y, dz = p.Z - o.Z;
            var near = (n.Z * o.Z) + (o.Y * n.Y) + (n.X * o.X);
            var far = (((dz * t) + o.Z) * n.Z) + (((dy * t) + o.Y) * n.Y) + (((dx * t) + o.X) * n.X);
            if (!(0f <= near - far))
                continue;
            var length = MathF.Sqrt((dz * dz) + (dy * dy) + (dx * dx));
            if (!(0f < length))
                continue;
            var f = t / length;
            var q = new Vector3(o.X + dx, o.Y + dy, o.Z + dz);
            if (f < 1f)
            {
                f = 0f <= f ? (f <= 1f ? f : 1f) : 0f;
                q = new Vector3((f * dx) + o.X, (f * dy) + o.Y, (f * dz) + o.Z);
            }
            hits.Add(q);

            var size = MathF.Sqrt((n.Z * n.Z) + (n.Y * n.Y) + (n.X * n.X));
            Vector3 unit;
            if (size < 1e-17f || size > 1e17f)
            {
                unit = size == 0f ? Vector3.Zero : Vector3.Normalize(n);
            }
            else
            {
                var r = 1f / size;
                unit = new Vector3(n.X * r, n.Y * r, n.Z * r);
            }
            var known = false;
            foreach (var e in normals)
            {
                var dot = (e.Z * unit.Z) + (e.Y * unit.Y) + (e.X * unit.X);
                if (dot < 1.001f && 0.9999f < dot)
                {
                    known = true;
                    break;
                }
            }
            if (!known)
                normals.Add(unit);
        }
    }

    /// <summary>
    /// <c>FUN_18003a630</c>: the entry array rebuilt leaf by leaf in node order.
    /// An open record stays; a border record becomes one open record per cluster
    /// that claimed it, with its whole cell mask; an open record whose cluster
    /// the leaf already holds ORs into that one; blocking records, and borders
    /// nobody claimed, are gone. Every node's short at <c>+6</c> becomes 0xffff.
    /// </summary>
    public static VisPvs.State Rewrite(VisPvs.State s, int[] borders, List<Claim>[] claims)
    {
        var slot = new Dictionary<int, int>(borders.Length);
        for (var k = 0; k < borders.Length; k++)
            slot[borders[k]] = k;
        var entries = new List<VisVisibility.Entry>(s.Entries.Length);
        var words = (uint[])s.NodeWords.Clone();
        var counts = (ushort[])s.NodeCounts.Clone();
        var pending = new List<(int Cluster, int Leaf, ulong Cells)>();
        for (var node = 0; node < words.Length; node++)
        {
            var word = s.NodeWords[node];
            if ((word & 1) == 0)
                continue;
            var start = entries.Count;
            words[node] = (word & 1) | ((uint)start << 1);
            var first = (int)(word >> 1);
            for (var i = 0; i < s.NodeCounts[node]; i++)
            {
                var e = s.Entries[first + i];
                if ((e.Packed & 1) != 0)
                    continue;
                pending.Clear();
                if ((e.Packed & 2) == 0)
                    pending.Add((e.Cluster, e.Packed >> 2, e.Cells));
                else if (slot.TryGetValue(first + i, out var k))
                {
                    foreach (var claim in claims[k])
                        pending.Add((claim.Cluster, e.Packed >> 2, e.Cells));
                }
                // The leaf is searched for the ORIGINAL record's cluster, not the
                // claim's: a border record carries 0, which nothing open holds
                // after the vis-cluster renumbering, so every claim is appended.
                foreach (var (cluster, leaf, cells) in pending)
                {
                    var merged = false;
                    for (var j = start; j < entries.Count; j++)
                    {
                        if (entries[j].Cluster != e.Cluster)
                            continue;
                        entries[j] = entries[j] with { Cells = entries[j].Cells | cells };
                        merged = true;
                        break;
                    }
                    if (!merged)
                        entries.Add(new VisVisibility.Entry(cluster, leaf * 4, cells));
                }
            }
            counts[node] = (ushort)(entries.Count - start);
        }
        return s with { Entries = [.. entries], NodeWords = words, NodeCounts = counts };
    }

    /// <summary>
    /// <c>AssignClusters2</c>, which runs straight after: within each leaf, a
    /// record with the cluster and kind bits of one already kept ORs its cells
    /// into it; any other is kept as it is.
    /// </summary>
    public static VisPvs.State Consolidate(VisPvs.State s)
    {
        var entries = new List<VisVisibility.Entry>(s.Entries.Length);
        var words = (uint[])s.NodeWords.Clone();
        var counts = (ushort[])s.NodeCounts.Clone();
        for (var node = 0; node < words.Length; node++)
        {
            var word = s.NodeWords[node];
            if ((word & 1) == 0)
                continue;
            var start = entries.Count;
            words[node] = (word & 1) | ((uint)start << 1);
            var first = (int)(word >> 1);
            for (var i = 0; i < s.NodeCounts[node]; i++)
            {
                var e = s.Entries[first + i];
                var merged = false;
                for (var j = start; j < entries.Count; j++)
                {
                    if (entries[j].Cluster != e.Cluster || ((entries[j].Packed ^ e.Packed) & 3) != 0)
                        continue;
                    entries[j] = entries[j] with { Cells = entries[j].Cells | e.Cells };
                    merged = true;
                    break;
                }
                if (!merged)
                    entries.Add(e);
            }
            counts[node] = (ushort)(entries.Count - start);
        }
        return s with { Entries = [.. entries], NodeWords = words, NodeCounts = counts };
    }
}
