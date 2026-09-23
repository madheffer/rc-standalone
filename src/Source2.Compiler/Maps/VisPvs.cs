using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The half of the vis build after assignment, starting with the PVS scan
/// (<c>Logs_SampleVisForClusters</c> and what it drives).
///
/// <para>Everything here works on the voxel sampler as the compile holds it at
/// that point, so <see cref="State"/> rebuilds that shape exactly: the node pool
/// in slot order with every node's box, the flat entry array with each record
/// keyed by NODE slot rather than by leaf, and the final clusters' boxes. The
/// PVS capture (<c>tools/vis/capture_pvs.py</c>) dumps the same four arrays, so
/// the state is checked before anything is built on it.</para>
/// </summary>
public static class VisPvs
{
    /// <summary>The voxel sampler's arrays at the start of the scan.</summary>
    /// <param name="Entries">The flat array at <c>+0x48</c>; a record's key is its node slot.</param>
    /// <param name="NodeWords">The node array at <c>+0x30</c>: <c>payload &lt;&lt; 1 | isLeaf</c>.</param>
    /// <param name="NodeCounts">A leaf's entry count, the short at <c>+0x04</c>.</param>
    /// <param name="NodeMins">Node boxes, <c>+0x78</c>.</param>
    /// <param name="NodeMaxs">Node boxes, <c>+0x78</c>.</param>
    /// <param name="ClusterMins">The final clusters' boxes, <c>+0x1a0</c>.</param>
    /// <param name="ClusterMaxs">The final clusters' boxes, <c>+0x1a0</c>.</param>
    /// <param name="BaseVoxelSize">The sampler's <c>+0xf0</c>.</param>
    public sealed record State(
        VisVisibility.Entry[] Entries, uint[] NodeWords, ushort[] NodeCounts,
        Vector3[] NodeMins, Vector3[] NodeMaxs, Vector3[] ClusterMins, Vector3[] ClusterMaxs,
        float BaseVoxelSize)
    {
        /// <summary>Clusters assignment numbered, the sampler's <c>+0x198</c>.</summary>
        public int Clusters => ClusterMins.Length;
    }

    /// <summary>
    /// Build the sampler's arrays from our own stages. The node pool is
    /// allocated the way <c>Voxelize</c> allocates it (eight consecutive slots
    /// when a node splits, then recursion in octant order), node boxes are
    /// halved at midpoints, and a leaf's word points at its run of the
    /// assignment's array.
    /// </summary>
    public static State Build(
        VisVoxelizer.Octree tree, Vector3 rootMaxs, VisRegions.Result regions, VisAssign.Result assigned,
        IReadOnlyList<VisClusterSet.Set> sets, float baseVoxelSize = 8f)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(sets);

        var leafIndex = new Dictionary<(int, (int, int, int)), int>(regions.Leaves.Count);
        for (var i = 0; i < regions.Leaves.Count; i++)
            leafIndex[(regions.Leaves[i].Level, regions.Leaves[i].Cell)] = i;

        var words = new List<uint> { 0 };
        var counts = new List<ushort> { 0 };
        var mins = new List<Vector3> { tree.Origin };
        var maxs = new List<Vector3> { rootMaxs };
        var slotOfLeaf = new int[regions.Leaves.Count];
        var depth = tree.BranchesPerLevel.Count;

        void Leaf(int slot, int level, (int X, int Y, int Z) cell)
        {
            var leaf = leafIndex[(level, cell)];
            slotOfLeaf[leaf] = slot;
            var offset = assigned.Offsets[leaf];
            words[slot] = ((uint)Math.Max(offset, 0) << 1) | 1u;
            counts[slot] = offset < 0 ? (ushort)0 : (ushort)assigned.Counts[leaf];
        }

        void Split(int slot, int level, (int X, int Y, int Z) cell)
        {
            var first = words.Count;
            words[slot] = (uint)first << 1;
            var lo = mins[slot];
            var hi = maxs[slot];
            var mid = new Vector3((hi.X + lo.X) * 0.5f, (hi.Y + lo.Y) * 0.5f, (hi.Z + lo.Z) * 0.5f);
            for (var o = 0; o < 8; o++)
            {
                words.Add(0);
                counts.Add(0);
                mins.Add(new Vector3((o & 1) == 0 ? lo.X : mid.X, (o & 2) == 0 ? lo.Y : mid.Y, (o & 4) == 0 ? lo.Z : mid.Z));
                maxs.Add(new Vector3((o & 1) == 0 ? mid.X : hi.X, (o & 2) == 0 ? mid.Y : hi.Y, (o & 4) == 0 ? mid.Z : hi.Z));
            }
            for (var o = 0; o < 8; o++)
            {
                var child = (cell.X * 2 + (o & 1), cell.Y * 2 + ((o >> 1) & 1), cell.Z * 2 + ((o >> 2) & 1));
                if (level - 1 > 0 && tree.BranchCells.Contains((level - 1, child)))
                    Split(first + o, level - 1, child);
                else
                    Leaf(first + o, level - 1, child);
            }
        }

        if (depth > 0 && tree.BranchCells.Contains((depth, (0, 0, 0))))
            Split(0, depth, (0, 0, 0));
        else
            Leaf(0, depth, (0, 0, 0));

        var entries = assigned.Entries
            .Select(e => new VisVisibility.Entry(e.Cluster, (slotOfLeaf[e.Leaf] << 2) | e.Kind, e.Cells))
            .ToArray();
        var count = assigned.Clusters;
        var state = new State(entries, [.. words], [.. counts], [.. mins], [.. maxs],
                              new Vector3[count], new Vector3[count], baseVoxelSize);
        ClusterBoxes(state);
        return state;
    }

    /// <summary>
    /// The cluster boxes at <c>+0x1a0</c>: the union of the region box of every
    /// entry carrying that id, whatever its kind. Blocking and skipped records
    /// carry id 0, so cluster 0's box is most of the map; that is the compile's
    /// and it is kept.
    /// </summary>
    private static void ClusterBoxes(State s)
    {
        Array.Fill(s.ClusterMins, new Vector3(float.MaxValue));
        Array.Fill(s.ClusterMaxs, new Vector3(-float.MaxValue));
        foreach (var e in s.Entries)
        {
            if ((uint)e.Cluster >= (uint)s.Clusters)
                continue;
            var (lo, hi) = RegionBox(s, e);
            s.ClusterMins[e.Cluster] = Vector3.Min(s.ClusterMins[e.Cluster], lo);
            s.ClusterMaxs[e.Cluster] = Vector3.Max(s.ClusterMaxs[e.Cluster], hi);
        }
    }

    /// <summary>One cluster's line in <c>CNeighboringClustersList</c>.</summary>
    /// <param name="Voxels">The sum of its open entries' mask popcounts.</param>
    /// <param name="Neighbors">Clusters touching it, in the order they were found.</param>
    public sealed record Neighbor(long Voxels, List<int> Neighbors);

    /// <summary>
    /// <c>CNeighboringClustersList::Build</c>: for every open entry, find the
    /// entries whose region box, grown by the base voxel size, it overlaps, and
    /// pair its cluster with each whose voxels actually touch one of its own.
    /// A pair is recorded once from its lower cluster, into both lists.
    /// </summary>
    public static Neighbor[] Neighbors(State s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var lists = new Neighbor[s.Clusters];
        for (var c = 0; c < lists.Length; c++)
            lists[c] = new Neighbor(0, []);

        var found = new List<int>();
        for (var e = 0; e < s.Entries.Length; e++)
        {
            var entry = s.Entries[e];
            if (entry.Kind != VisVisibility.Open || (uint)entry.Cluster >= (uint)s.Clusters)
                continue;
            var c = entry.Cluster;
            lists[c] = lists[c] with { Voxels = lists[c].Voxels + BitOperations.PopCount(entry.Cells) };

            found.Clear();
            var (lo, hi) = RegionBox(s, entry);
            var grow = new Vector3(s.BaseVoxelSize);
            Query(s, 0, lo - grow, hi + grow, found);

            var last = -1;
            foreach (var n in found)
            {
                if (!Touching(s, e, n))
                    continue;
                var c2 = s.Entries[n].Cluster;
                if (c < c2 && c2 != last)
                {
                    AddOnce(lists[c].Neighbors, c2);
                    AddOnce(lists[c2].Neighbors, c);
                    last = c2;
                }
            }
        }
        return lists;
    }

    private static void AddOnce(List<int> list, int id)
    {
        if (!list.Contains(id))
            list.Add(id);
    }

    /// <summary><c>RegionBox</c>: the set cells' box, in world units.</summary>
    public static (Vector3 Mins, Vector3 Maxs) RegionBox(State s, VisVisibility.Entry entry)
    {
        var node = entry.Leaf;
        var lo = s.NodeMins[node];
        var sub = (s.NodeMaxs[node].X - lo.X) * 0.25f;
        var mn = new Vector3(float.MaxValue);
        var mx = new Vector3(-float.MaxValue);
        for (var i = 0; i < 64; i++)
        {
            if ((entry.Cells >> i & 1) == 0)
                continue;
            var at = new Vector3((i & 3) * sub, ((i >> 2) & 3) * sub, ((i >> 4) & 3) * sub);
            mn = Vector3.Min(mn, at);
            mx = Vector3.Max(mx, new Vector3(at.X + sub, at.Y + sub, at.Z + sub));
        }
        return (new Vector3(mn.X + lo.X, mn.Y + lo.Y, mn.Z + lo.Z), new Vector3(mx.X + lo.X, mx.Y + lo.Y, mx.Z + lo.Z));
    }

    // FUN_18002e390: the octree walk for a box, children taken in octant order
    // on an inclusive test, and at a leaf every open entry whose cells the box
    // overlaps STRICTLY, skipping an immediate repeat.
    private static void Query(State s, int node, Vector3 lo, Vector3 hi, List<int> found)
    {
        var word = s.NodeWords[node];
        if ((word & 1) == 0)
        {
            var first = (int)(word >> 1);
            for (var o = 0; o < 8; o++)
            {
                var c = first + o;
                var cmn = s.NodeMins[c];
                var cmx = s.NodeMaxs[c];
                if (cmn.X <= hi.X && lo.X <= cmx.X && cmn.Y <= hi.Y && lo.Y <= cmx.Y
                    && cmn.Z <= hi.Z && lo.Z <= cmx.Z)
                    Query(s, c, lo, hi, found);
            }
            return;
        }

        var cells = StrictCells(lo, hi, s.NodeMins[node], s.NodeMaxs[node]);
        var start = (int)(word >> 1);
        for (var k = 0; k < s.NodeCounts[node]; k++)
        {
            var at = start + k;
            var entry = s.Entries[at];
            if (entry.Kind == VisVisibility.Open && (entry.Cells & cells) != 0
                && (found.Count == 0 || found[^1] != at))
                found.Add(at);
        }
    }

    // FUN_18010dfc0: which of a leaf's 4x4x4 cells a box overlaps, open on both
    // ends: qmin < cell max and cell min < qmax on every axis.
    private static ulong StrictCells(Vector3 qlo, Vector3 qhi, Vector3 leafMin, Vector3 leafMax)
    {
        var sub = (leafMax.X - leafMin.X) * 0.25f;
        int Along(float ql, float qh, float m)
        {
            var bits = 0;
            for (var k = 0; k < 4; k++)
            {
                var start = (sub * k) + m;
                if (ql < start + sub && start < qh)
                    bits |= 1 << k;
            }
            return bits;
        }
        int xb = Along(qlo.X, qhi.X, leafMin.X), yb = Along(qlo.Y, qhi.Y, leafMin.Y), zb = Along(qlo.Z, qhi.Z, leafMin.Z);
        var mask = 0UL;
        for (var i = 0; i < 64; i++)
            if ((xb >> (i & 3) & 1) != 0 && (yb >> ((i >> 2) & 3) & 1) != 0 && (zb >> ((i >> 4) & 3) & 1) != 0)
                mask |= 1UL << i;
        return mask;
    }

    // FUN_18002c420: two entries touch when any cell of one is at distance 0
    // from any cell of the other; the same entry, or two full leaves, always do.
    private static bool Touching(State s, int a, int b)
    {
        if (a == b)
            return true;
        var ea = s.Entries[a];
        var eb = s.Entries[b];
        if (ea.Cells == ulong.MaxValue && eb.Cells == ulong.MaxValue)
            return true;
        for (var i = 0; i < 64; i++)
        {
            if ((ea.Cells >> i & 1) == 0)
                continue;
            var (ilo, ihi) = SubBox(s, ea.Leaf, i);
            for (var j = 0; j < 64; j++)
            {
                if ((eb.Cells >> j & 1) == 0)
                    continue;
                var (jlo, jhi) = SubBox(s, eb.Leaf, j);
                var gx = ilo.X - jhi.X;
                if (gx < 0f)
                    gx = jlo.X - ihi.X;
                var gy = ilo.Y - jhi.Y;
                if (gy < 0f)
                    gy = jlo.Y - ihi.Y;
                var gz = ilo.Z - jhi.Z;
                if (gz < 0f)
                    gz = jlo.Z - ihi.Z;
                gx = 0f <= gx ? gx : 0f;
                gy = 0f <= gy ? gy : 0f;
                gz = 0f <= gz ? gz : 0f;
                if (MathF.Sqrt((gy * gy) + (gz * gz) + (gx * gx)) <= 0f)
                    return true;
            }
        }
        return false;
    }

    /// <summary><c>SubBox</c>: one of a node's 64 cells.</summary>
    public static (Vector3 Mins, Vector3 Maxs) SubBox(State s, int node, int cell)
    {
        var lo = s.NodeMins[node];
        var sub = (s.NodeMaxs[node].X - lo.X) * 0.25f;
        float ox = (cell & 3) * sub, oy = ((cell >> 2) & 3) * sub, oz = ((cell >> 4) & 3) * sub;
        return (new Vector3(ox + lo.X, oy + lo.Y, oz + lo.Z),
                new Vector3(lo.X + (ox + sub), lo.Y + (oy + sub), lo.Z + (oz + sub)));
    }

    /// <summary>
    /// The MutualVisibilityMatrix: one row of ceil(n/32) words per cluster plus
    /// sky and sun, each row starting with its own diagonal bit
    /// (<c>FUN_18001c1f0</c>). The scan only ever ORs into it.
    /// </summary>
    public sealed class Matrix
    {
        public Matrix(int rows, int bits)
        {
            Bits = bits;
            Words = (bits + 31) >> 5;
            Rows = new uint[rows][];
            for (var i = 0; i < rows; i++)
            {
                Rows[i] = new uint[Words];
                if (i < bits)
                    Rows[i][i >> 5] |= 1u << (i & 31);
            }
        }

        public int Bits { get; }

        public int Words { get; }

        public uint[][] Rows { get; }

        public bool Sees(int from, int to) => (Rows[from][to >> 5] >> (to & 31) & 1) != 0;

        /// <summary><c>FUN_18001c3c0</c>: every listed cluster sees every other. Returns words changed.</summary>
        public int Or(IReadOnlyList<int> sorted)
        {
            if (sorted.Count == 0)
                return 0;
            var runs = new List<(int Word, uint Bits)>();
            foreach (var id in sorted)
            {
                if (runs.Count > 0 && runs[^1].Word == id >> 5)
                    runs[^1] = (id >> 5, runs[^1].Bits | (1u << (id & 31)));
                else
                    runs.Add((id >> 5, 1u << (id & 31)));
            }
            var changed = 0;
            foreach (var id in sorted)
            {
                var row = Rows[id];
                foreach (var (word, bits) in runs)
                {
                    if ((row[word] & bits) != bits)
                    {
                        row[word] |= bits;
                        changed++;
                    }
                }
            }
            return changed;
        }
    }

    /// <summary>One sampling ray: origin and unit direction (<c>FUN_18001c890</c>).</summary>
    public readonly record struct Ray(Vector3 Origin, Vector3 Direction);

    /// <summary>
    /// <c>FUN_18001c890</c>: a ray from one point toward another, the direction
    /// normalised with the length summed y, z, x and a reciprocal multiply.
    /// </summary>
    public static Ray Toward(Vector3 from, Vector3 to)
    {
        float dx = to.X - from.X, dy = to.Y - from.Y, dz = to.Z - from.Z;
        var length = MathF.Sqrt((dy * dy) + (dz * dz) + (dx * dx));
        if (length < 1e-17f || length > 1e17f)
        {
            if (length == 0f)
                return new Ray(from, Vector3.Zero);
            var l = Math.Sqrt(((double)dx * dx) + ((double)dy * dy) + ((double)dz * dz));
            return new Ray(from, new Vector3((float)(dx / l), (float)(dy / l), (float)(dz / l)));
        }
        var inverse = 1f / length;
        return new Ray(from, new Vector3(inverse * dx, inverse * dy, inverse * dz));
    }

    /// <summary>
    /// The per-cluster point a cluster-centre ray starts from (<c>FUN_18001fda0</c>):
    /// the cluster box's centre when one of its own voxels holds it, otherwise
    /// the centre of one of its voxels, chosen by the binary's comparison; both
    /// nudged by +-c/n per axis, the sign from c's low three bits.
    /// </summary>
    public static Vector3[] Centres(State s)
    {
        var lists = EntriesByCluster(s);
        var n = s.Clusters;
        var found = new Vector3[n];
        for (var c = 0; c < n; c++)
        {
            var boxes = VoxelBoxes(s, lists[c]);
            var lo = s.ClusterMins[c];
            var hi = s.ClusterMaxs[c];
            float cx = (lo.X + hi.X) * 0.5f, cy = (lo.Y + hi.Y) * 0.5f, cz = (lo.Z + hi.Z) * 0.5f;
            var f = (float)c / n;
            var jx = (c & 1) != 0 ? f * -1f : f;
            var jy = (c & 2) != 0 ? f * -1f : f;
            var jz = (c & 4) != 0 ? f * -1f : f;

            var inside = false;
            foreach (var (bl, bh) in boxes)
            {
                if (bl.X <= cx && bl.Y <= cy && bl.Z <= cz && cx <= bh.X && cy <= bh.Y && cz <= bh.Z)
                {
                    inside = true;
                    break;
                }
            }
            if (inside)
            {
                found[c] = new Vector3(jx + cx, cy + jy, cz + jz);
                continue;
            }

            float bx = float.MaxValue, by = float.MaxValue, bz = float.MaxValue;
            (Vector3 Lo, Vector3 Hi) chosen = default;
            foreach (var (bl, bh) in boxes)
            {
                var x = cx;
                if (bh.X <= cx)
                    x = bh.X;
                var y = cy;
                if (bh.Y <= cy)
                    y = bh.Y;
                var px = bl.X;
                if (bl.X <= x)
                    px = x;
                var z = cz;
                if (bh.Z <= cz)
                    z = bh.Z;
                var py = bl.Y;
                if (bl.Y <= y)
                    py = y;
                var pz = bl.Z;
                if (bl.Z <= z)
                    pz = z;
                if (((py - cy) * (py - cy)) + ((px - cx) * (px - cx)) + ((pz - cz) * (pz - cz))
                    < ((py - by) * (py - by)) + ((px - bx) * (px - bx)) + ((pz - bz) * (pz - bz)))
                {
                    chosen = (bl, bh);
                    (bx, by, bz) = (px, py, pz);
                }
            }
            found[c] = new Vector3(jx + ((chosen.Hi.X + chosen.Lo.X) * 0.5f),
                                   ((chosen.Hi.Y + chosen.Lo.Y) * 0.5f) + jy,
                                   ((chosen.Hi.Z + chosen.Lo.Z) * 0.5f) + jz);
        }
        return found;
    }

    /// <summary>The generators' per-cluster lists: every open entry, in array order.</summary>
    public static List<int>[] EntriesByCluster(State s)
    {
        var lists = new List<int>[s.Clusters];
        for (var c = 0; c < lists.Length; c++)
            lists[c] = [];
        for (var e = 0; e < s.Entries.Length; e++)
        {
            if (s.Entries[e].Kind == VisVisibility.Open && (uint)s.Entries[e].Cluster < (uint)s.Clusters)
                lists[s.Entries[e].Cluster].Add(e);
        }
        return lists;
    }

    // FUN_18003c070: a cluster's voxel boxes, one for a full leaf, else one a cell.
    private static List<(Vector3 Lo, Vector3 Hi)> VoxelBoxes(State s, List<int> entries)
    {
        var boxes = new List<(Vector3, Vector3)>();
        foreach (var e in entries)
        {
            var entry = s.Entries[e];
            if (entry.Cells == ulong.MaxValue)
            {
                boxes.Add(RegionBox(s, entry));
                continue;
            }
            for (var i = 0; i < 64; i++)
            {
                if ((entry.Cells >> i & 1) != 0)
                    boxes.Add(SubBox(s, entry.Leaf, i));
            }
        }
        return boxes;
    }

    /// <summary>
    /// The shared begin-pass (<c>FUN_18001edc0</c>): for every cluster from 2 on,
    /// the clusters its neighbours already see that it does not and that have
    /// not been tried, each becoming a pair; clusters 0 and 1 are never
    /// sources or targets. Returns the pairs in order.
    /// </summary>
    public static List<(int A, int B)> Pairs(Matrix matrix, Neighbor[] neighbours, uint[][] tested,
                                            Func<int, int, bool>? accept = null, int limit = int.MaxValue)
    {
        var pairs = new List<(int, int)>();
        var words = matrix.Words;
        var seen = new uint[words];
        for (var c = 2; c < neighbours.Length; c++)
        {
            Array.Clear(seen);
            foreach (var nb in neighbours[c].Neighbors)
            {
                if (nb <= 1 || nb == c)
                    continue;
                var row = matrix.Rows[nb];
                for (var w = 0; w < words; w++)
                    seen[w] |= row[w];
            }
            var mine = matrix.Rows[c];
            var tried = tested[c];
            var candidates = new uint[words];
            for (var w = 0; w < words; w++)
                candidates[w] = seen[w] & ~mine[w] & ~(w < tried.Length ? tried[w] : 0u);
            // The binary's set-bit scan takes the address of the LAST word as its
            // end and stops on reaching it, so a candidate in the final word is
            // never found from below; such pairs only arise from their higher
            // cluster. An off-by-one in the compile, kept because it decides
            // which rays are cast.
            for (var t = 2; t < (words - 1) * 32; t++)
            {
                if ((candidates[t >> 5] >> (t & 31) & 1) == 0)
                    continue;
                if (accept is not null && !accept(c, t))
                    continue;
                pairs.Add((c, t));
                if (t < tested.Length)
                    tested[t][c >> 5] |= 1u << (c & 31);
            }
            for (var w = 0; w < tried.Length && w < words; w++)
                tried[w] |= candidates[w];
            if (limit <= pairs.Count)
                break;
        }
        return pairs;
    }

    /// <summary>A clusters x clusters bit matrix with its diagonal set, as the generators' own "tested".</summary>
    public static uint[][] Diagonal(int n)
    {
        var words = (n + 31) >> 5;
        var rows = new uint[n][];
        for (var i = 0; i < n; i++)
        {
            rows[i] = new uint[words];
            rows[i][i >> 5] |= 1u << (i & 31);
        }
        return rows;
    }

    /// <summary>The scan's ray length, the tracer box's diagonal summed x, y, z.</summary>
    public static float Reach(RayTraceEnvironment rte)
    {
        var (lo, hi) = rte.TracedBounds;
        float dx = hi.X - lo.X, dy = hi.Y - lo.Y, dz = hi.Z - lo.Z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    /// <summary>
    /// <c>BatchTracer</c> for one sight ray: trace to <c>reach * dir + origin</c>
    /// with mask 0x4801. A miss gives the whole line; a hit the ray meets from
    /// the front gives the line to one unit short of it; a back face gives
    /// nothing. Returns the segment's end, or null.
    /// </summary>
    public static Vector3? Sight(RayTraceEnvironment rte, Ray ray, float reach)
    {
        var o = ray.Origin;
        var d = ray.Direction;
        var end = new Vector3((reach * d.X) + o.X, (reach * d.Y) + o.Y, (reach * d.Z) + o.Z);
        if (rte.Segment(o, end, 0x4801) is not { } hit)
            return end;
        var n = hit.Normal;
        if (!((n.Z * d.Z) + (d.Y * n.Y) + (n.X * d.X) < 0f))
            return null;
        var t = hit.Distance;
        return new Vector3(((d.X * t) + o.X) - d.X, ((t * d.Y) + o.Y) - d.Y, ((t * d.Z) + o.Z) - d.Z);
    }

    /// <summary>
    /// <c>FUN_18002ef60</c>: walk a segment through the octree breadth first and
    /// collect the cluster of every open entry whose cells it crosses, skipping
    /// an immediate repeat. A blocking entry ends the walk (null) unless
    /// <paramref name="through"/>.
    /// </summary>
    public static List<int>? Walk(State s, Vector3 o, Vector3 end, bool through)
    {
        var inv = new Vector3(1f / Guard(end.X - o.X), 1f / Guard(end.Y - o.Y), 1f / Guard(end.Z - o.Z));
        var ids = new List<int>();
        var last = -1;
        var queue = new Queue<(int Node, Vector3 Corner, float Size)>();
        var root = s.NodeMins[0];
        queue.Enqueue((0, root, s.NodeMaxs[0].X - root.X));
        while (queue.Count > 0)
        {
            var (node, c, size) = queue.Dequeue();
            var word = s.NodeWords[node];
            if ((word & 1) == 0)
            {
                var mask = OctantMask(o, inv, c, new Vector3(size + c.X, size + c.Y, size + c.Z));
                var first = (int)(word >> 1);
                for (var oct = 0; oct < 8; oct++)
                {
                    if ((mask >> oct & 1) == 0)
                        continue;
                    var at = new Vector3((size * ((oct & 1) != 0 ? 0.5f : 0f)) + c.X,
                                         (size * ((oct & 2) != 0 ? 0.5f : 0f)) + c.Y,
                                         (size * ((oct & 4) != 0 ? 0.5f : 0f)) + c.Z);
                    queue.Enqueue((first + oct, at, size * 0.5f));
                }
                continue;
            }
            var cells = CellMask(o, inv, c, size + c.X);
            var start = (int)(word >> 1);
            for (var k = 0; k < s.NodeCounts[node]; k++)
            {
                var e = s.Entries[start + k];
                var kind = e.Kind;
                if (kind == VisVisibility.Skipped)
                    continue;
                if ((kind == VisVisibility.Blocking || e.Cluster != last) && (e.Cells & cells) != 0)
                {
                    if (kind == VisVisibility.Open)
                    {
                        ids.Add(e.Cluster);
                        last = e.Cluster;
                    }
                    else if (!through)
                    {
                        return null;
                    }
                }
            }
        }
        return ids;
    }

    // divps against 1 with the FLT_EPSILON bits ORed in below FLT_MIN.
    private static float Guard(float x)
        => MathF.Abs(x) < 1.17549435e-38f
            ? BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(x) | 0x34000000)
            : x;

    // OctantMask: the node's two halves per axis, split in t space at (t_lo + t_hi) * 0.5.
    private static int OctantMask(Vector3 o, Vector3 inv, Vector3 lo, Vector3 hi)
    {
        Span<float> enter = stackalloc float[6];
        Span<float> leave = stackalloc float[6];
        for (var a = 0; a < 3; a++)
        {
            var t0 = (Axis(lo, a) - Axis(o, a)) * Axis(inv, a);
            var t1 = (Axis(hi, a) - Axis(o, a)) * Axis(inv, a);
            var tm = (t1 + t0) * 0.5f;
            enter[a * 2] = MathF.Min(t0, tm);
            leave[a * 2] = MathF.Max(t0, tm);
            enter[(a * 2) + 1] = MathF.Min(tm, t1);
            leave[(a * 2) + 1] = MathF.Max(tm, t1);
        }
        var mask = 0;
        for (var oct = 0; oct < 8; oct++)
        {
            int x = oct & 1, y = (oct >> 1) & 1, z = (oct >> 2) & 1;
            var into = MathF.Max(MathF.Max(MathF.Max(enter[x], enter[2 + y]), enter[4 + z]), 0f);
            var outOf = MathF.Min(MathF.Min(MathF.Min(leave[x], leave[2 + y]), leave[4 + z]), 1f);
            if (into <= outOf)
                mask |= 1 << oct;
        }
        return mask;
    }

    // CellMask: a leaf's 4x4x4 cells the segment crosses, cell k starting at (k * sub + min) - o.
    private static ulong CellMask(Vector3 o, Vector3 inv, Vector3 lo, float maxX)
    {
        var sub = (maxX - lo.X) * 0.25f;
        Span<float> enter = stackalloc float[12];
        Span<float> leave = stackalloc float[12];
        for (var a = 0; a < 3; a++)
        {
            for (var k = 0; k < 4; k++)
            {
                var from = ((sub * k) + Axis(lo, a)) - Axis(o, a);
                var near = Axis(inv, a) * from;
                var far = Axis(inv, a) * (from + sub);
                enter[(a * 4) + k] = MathF.Min(near, far);
                leave[(a * 4) + k] = MathF.Max(near, far);
            }
        }
        var mask = 0UL;
        for (var i = 0; i < 64; i++)
        {
            int x = i & 3, y = (i >> 2) & 3, z = (i >> 4) & 3;
            var into = MathF.Max(MathF.Max(MathF.Max(enter[x], enter[4 + y]), enter[8 + z]), 0f);
            var outOf = MathF.Min(MathF.Min(MathF.Min(leave[x], leave[4 + y]), leave[8 + z]), 1f);
            if (into <= outOf)
                mask |= 1UL << i;
        }
        return mask;
    }

    private static float Axis(Vector3 v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;

    /// <summary>
    /// Trace and fold a set of sight rays into the matrix: each ray's segment is
    /// walked through blockers, its clusters sorted and made unique, and ORed.
    /// Rays run in parallel; the ORs are applied after, since their order cannot
    /// matter.
    /// </summary>
    public static int Fold(State s, RayTraceEnvironment rte, Matrix matrix, IReadOnlyList<Ray> rays, float reach)
    {
        var found = new int[rays.Count][];
        Parallel.For(0, rays.Count, i =>
        {
            if (Sight(rte, rays[i], reach) is not { } end)
                return;
            if (Walk(s, rays[i].Origin, end, through: true) is not { Count: > 0 } ids)
                return;
            var sorted = ids.Distinct().ToArray();
            Array.Sort(sorted);
            found[i] = sorted;
        });
        var useful = 0;
        foreach (var ids in found)
        {
            if (ids is not null && matrix.Or(ids) > 0)
                useful++;
        }
        return useful;
    }

    /// <summary>
    /// <c>ClusterCenterRayGenerator</c>: passes of centre-to-centre rays between
    /// each cluster and what its neighbours see, until a pass finds no pair.
    /// </summary>
    public static int ClusterCentres(State s, Neighbor[] neighbours, RayTraceEnvironment rte, Matrix matrix,
                                     Action<int, int>? passDone = null)
    {
        var centres = Centres(s);
        var tested = Diagonal(s.Clusters);
        var reach = Reach(rte);
        var passes = 0;
        while (true)
        {
            var pairs = Pairs(matrix, neighbours, tested);
            if (pairs.Count == 0)
                return passes;
            var rays = pairs.Select(p => Toward(centres[Math.Min(p.A, p.B)], centres[Math.Max(p.A, p.B)])).ToList();
            var useful = Fold(s, rte, matrix, rays, reach);
            passes++;
            passDone?.Invoke(pairs.Count, useful);
        }
    }
}
