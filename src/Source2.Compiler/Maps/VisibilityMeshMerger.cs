using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// <c>CVisibilityMeshMerger::MergeMeshes</c> (resourcecompiler 180234180,
/// visdrivenclustering.cpp), the world renderer builder's merge when
/// <c>VisibilityGuidedMeshClustering</c> is on (CS2's csgo_core sets it). The
/// mesh entries of a list are grouped by <c>CanMerge</c>, each member is put
/// in the bucket of the vis clusters that see it (whole, or triangle by
/// triangle when several do), small bucket entries are merged into
/// neighbouring buckets, and every bucket comes out as one list of entries
/// whose precomputed cluster set is the bucket's key. The buckets live in
/// Valve's <c>CUtlHashTable</c>, whose slot order is the output order, so the
/// table is ported with its growth and chain rules.
/// </summary>
public sealed class VisibilityMeshMerger
{
    /// <summary>A <c>CMesh</c> as the merger uses it: the vertex floats and the triangle list.</summary>
    public sealed class Mesh
    {
        public List<float> Vertices = [];
        public int Stride;
        /// <summary>The first float of the stream named "position" with three floats (the bounds read it).</summary>
        public int PositionOffset;
        public List<int> Indices = [];

        public int VertexCount => Stride == 0 ? 0 : Vertices.Count / Stride;

        public Mesh Copy() => new() { Vertices = [.. Vertices], Stride = Stride, PositionOffset = PositionOffset, Indices = [.. Indices] };

        /// <summary><c>FUN_1812dfd10</c>: min and max of the position stream over every vertex, a tie keeping the value held.</summary>
        public (Vector3 Min, Vector3 Max) Bounds()
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue;
            float x1 = -float.MaxValue, y1 = -float.MaxValue, z1 = -float.MaxValue;
            for (var v = 0; v < VertexCount; v++)
            {
                var at = v * Stride + PositionOffset;
                float x = Vertices[at], y = Vertices[at + 1], z = Vertices[at + 2];
                x0 = x0 <= x ? x0 : x; y0 = y0 <= y ? y0 : y; z0 = z0 <= z ? z0 : z;
                x1 = x <= x1 ? x1 : x; y1 = y <= y1 ? y1 : y; z1 = z <= z1 ? z1 : z;
            }
            return (new Vector3(x0, y0, z0), new Vector3(x1, y1, z1));
        }

        /// <summary><c>FUN_1812d6c70</c> without renumbering: <paramref name="other"/>'s vertices appended, its indices offset.</summary>
        public void Append(Mesh other)
        {
            var offset = VertexCount;
            Vertices.AddRange(other.Vertices);
            foreach (var i in other.Indices)
                Indices.Add(i + offset);
        }

        /// <summary><c>FUN_1812de310</c>: the triangles of <paramref name="indices"/>, vertices renumbered by first use.</summary>
        public Mesh Subset(List<int> indices)
        {
            var result = new Mesh { Stride = Stride, PositionOffset = PositionOffset };
            var remap = new Dictionary<int, int>();
            foreach (var i in indices)
            {
                if (!remap.TryGetValue(i, out var n))
                {
                    n = remap.Count;
                    remap.Add(i, n);
                    for (var k = 0; k < Stride; k++)
                        result.Vertices.Add(Vertices[i * Stride + k]);
                }
                result.Indices.Add(n);
            }
            return result;
        }
    }

    /// <summary>
    /// A node mesh entry (0x238 bytes in the builder). The fields
    /// <c>CanMerge</c> reads are the source entry's, so an entry carries the
    /// index of the input it came from; a copy shares its mesh as the
    /// builder's ref-counted pointer does.
    /// </summary>
    public sealed class Entry
    {
        public required Mesh Mesh;
        public int Origin;
        /// <summary>Object flags (+0xbc); a triangle no vis cluster sees comes out with 0x10000.</summary>
        public uint ObjectFlags;
        /// <summary>The precomputed vis cluster set (+0x218), the bucket's key on output.</summary>
        public ushort[] Clusters = [];

        public Entry Share() => new() { Mesh = Mesh, Origin = Origin, ObjectFlags = ObjectFlags, Clusters = Clusters };
    }

    public class Bucket
    {
        public ushort[] Key = [];
        public List<Entry> Entries = [];
    }

    public sealed record Output(List<Bucket> Buckets, List<Entry> Unclustered);

    readonly List<(Vector3 Min, Vector3 Max)>[] _flat;
    readonly (Vector3 Min, Vector3 Max)[] _bounds;
    readonly float[][] _mutual;
    readonly Func<Entry, Entry, bool> _canMerge;

    public int MinTriangles { get; init; } = 64;
    public int MinVertices { get; init; } = 32;
    public int MinVolume { get; init; } = 216;
    public int MaxMembership { get; init; } = 16;

    /// <summary>
    /// <c>CVisibilityMeshMerger::Init</c> (180230480): each cluster's bound is
    /// the union of its box list. The minimums default to the code's 64, 32,
    /// 216 and 16; csgo_core's gameinfo sets 2048, 2048, 1800 and 16.
    /// <paramref name="canMerge"/> is <c>WRBMeshEntry_CanMerge</c>.
    /// </summary>
    public VisibilityMeshMerger(List<(Vector3 Min, Vector3 Max)>[] flatClusterBoxes, float[][] mutualVisibility, Func<Entry, Entry, bool> canMerge)
    {
        _flat = flatClusterBoxes;
        _mutual = mutualVisibility;
        _canMerge = canMerge;
        _bounds = new (Vector3, Vector3)[_flat.Length];
        for (var c = 0; c < _flat.Length; c++)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue;
            float x1 = -float.MaxValue, y1 = -float.MaxValue, z1 = -float.MaxValue;
            foreach (var (mn, mx) in _flat[c])
            {
                x0 = x0 <= mn.X ? x0 : mn.X; y0 = y0 <= mn.Y ? y0 : mn.Y; z0 = z0 <= mn.Z ? z0 : mn.Z;
                x1 = mx.X <= x1 ? x1 : mx.X; y1 = mx.Y <= y1 ? y1 : mx.Y; z1 = mx.Z <= z1 ? z1 : mx.Z;
            }
            _bounds[c] = (new Vector3(x0, y0, z0), new Vector3(x1, y1, z1));
        }
    }

    sealed class Member
    {
        public required Entry Entry;
        public ushort[] Clusters = [];
    }

    /// <summary>
    /// The merge of one mesh list. Groups: the last remaining entry seeds a
    /// group, then the remaining list is swept from the front for entries
    /// <c>CanMerge</c> accepts, each taken out by moving the last entry into
    /// its place, until the taken indices pass 299,999 (the seed's not
    /// counted). Each member is bucketed, each group's buckets merged, and
    /// the buckets written group by group in slot order, empty ones too.
    /// </summary>
    public Output MergeMeshes(IReadOnlyList<Entry> entries)
    {
        var remaining = entries.ToList();
        var groups = new List<(List<Member> Members, HashTable Map)>();
        while (remaining.Count > 0)
        {
            var seed = remaining[^1];
            remaining.RemoveAt(remaining.Count - 1);
            var members = new List<Member> { new() { Entry = seed.Share() } };
            var taken = 0;
            for (var i = 0; i < remaining.Count;)
            {
                if (!_canMerge(seed, remaining[i]))
                {
                    i++;
                    continue;
                }
                members.Add(new Member { Entry = remaining[i].Share() });
                taken += remaining[i].Mesh.Indices.Count;
                remaining[i] = remaining[^1];
                remaining.RemoveAt(remaining.Count - 1);
                if (taken > 299999)
                    break;
            }
            var map = new HashTable();
            map.Reserve(members.Count);
            groups.Add((members, map));
        }

        foreach (var (members, _) in groups)
        {
            foreach (var m in members)
                m.Clusters = MeshMembership(m.Entry.Mesh.Bounds());
        }

        var unclustered = new List<Entry>();
        foreach (var (members, map) in groups)
        {
            foreach (var m in members)
            {
                if (m.Clusters.Length == 1)
                    AddToBucket(m.Entry, map.FindOrInsert(m.Clusters).Entries);
                else
                    SplitByTriangles(m, map, unclustered);
            }
        }

        foreach (var (_, map) in groups)
            MergeGroup(map);

        var buckets = new List<Bucket>();
        foreach (var (_, map) in groups)
        {
            foreach (var b in map.Slots())
            {
                foreach (var e in b.Entries)
                    e.Clusters = b.Key;
                buckets.Add(b);
            }
        }
        return new Output(buckets, unclustered);
    }

    /// <summary>
    /// <c>FUN_1802307f0</c>: the clusters, ascending, with a box list whose
    /// bound and then one box overlap <paramref name="b"/>, edges included.
    /// </summary>
    ushort[] MeshMembership((Vector3 Min, Vector3 Max) b)
    {
        var list = new List<ushort>();
        for (var c = 0; c < _flat.Length; c++)
        {
            if (_flat[c].Count == 0)
                continue;
            var cb = _bounds[c];
            if (!(cb.Min.X <= b.Max.X && b.Min.X <= cb.Max.X && cb.Min.Y <= b.Max.Y && b.Min.Y <= cb.Max.Y
                  && cb.Min.Z <= b.Max.Z && b.Min.Z <= cb.Max.Z))
                continue;
            foreach (var (mn, mx) in _flat[c])
            {
                if (b.Max.X < mn.X || mx.X < b.Min.X || b.Max.Y < mn.Y || mx.Y < b.Min.Y || b.Max.Z < mn.Z || mx.Z < b.Min.Z)
                    continue;
                list.Add((ushort)c);
                break;
            }
        }
        return [.. list];
    }

    /// <summary>
    /// <c>FUN_180230aa0</c>: of the member's clusters, those whose bound and
    /// then one box pass <see cref="TriBoxOverlap"/> at 0.001. Null when more
    /// than <see cref="MaxMembership"/> would.
    /// </summary>
    List<ushort>? TriangleMembership(ushort[] clusters, Vector3 a, Vector3 b, Vector3 c)
    {
        var list = new List<ushort>();
        foreach (var cl in clusters)
        {
            var cb = _bounds[cl];
            var half = new Vector3((cb.Max.X - cb.Min.X) * 0.5f, (cb.Max.Y - cb.Min.Y) * 0.5f, (cb.Max.Z - cb.Min.Z) * 0.5f);
            var centre = new Vector3((cb.Min.X + cb.Max.X) * 0.5f, (cb.Min.Y + cb.Max.Y) * 0.5f, (cb.Min.Z + cb.Max.Z) * 0.5f);
            if (!TriBoxOverlap(centre, half, a, b, c, 0.001f))
                continue;
            foreach (var (mn, mx) in _flat[cl])
            {
                var h = new Vector3((mx.X - mn.X) * 0.5f, (mx.Y - mn.Y) * 0.5f, (mx.Z - mn.Z) * 0.5f);
                var o = new Vector3((mx.X + mn.X) * 0.5f, (mx.Y + mn.Y) * 0.5f, (mx.Z + mn.Z) * 0.5f);
                if (!TriBoxOverlap(o, h, a, b, c, 0.001f))
                    continue;
                if (list.Count == MaxMembership)
                    return null;
                list.Add(cl);
                break;
            }
        }
        return list;
    }

    /// <summary>
    /// <c>FUN_180231200</c>, a member several clusters (or none) see: each
    /// triangle to the bucket of its own cluster set, via a local table
    /// grown to the cluster count before it is read in slot order, each
    /// bucket's triangles becoming a mesh of their own. Triangles past the cap
    /// and then triangles no cluster sees (object flag 0x10000) go to the
    /// unclustered list, a mesh each. The triangle's corners are its first
    /// three vertex floats.
    /// </summary>
    void SplitByTriangles(Member m, HashTable map, List<Entry> unclustered)
    {
        var mesh = m.Entry.Mesh;
        var local = new HashTable();
        var over = new List<int>();
        var none = new List<int>();
        var tris = mesh.Indices.Count / 3;
        for (var t = 0; t < tris; t++)
        {
            int i0 = mesh.Indices[t * 3], i1 = mesh.Indices[t * 3 + 1], i2 = mesh.Indices[t * 3 + 2];
            var set = TriangleMembership(m.Clusters, Corner(mesh, i0), Corner(mesh, i1), Corner(mesh, i2));
            var target = set == null ? over : set.Count == 0 ? none : local.FindOrInsert([.. set]).Indices;
            target.Add(i0);
            target.Add(i1);
            target.Add(i2);
        }
        local.Reserve(_flat.Length);
        foreach (var b in local.Slots())
        {
            if (b.Indices.Count == 0)
                continue;
            var piece = new Entry { Mesh = mesh.Subset(b.Indices), Origin = m.Entry.Origin, ObjectFlags = m.Entry.ObjectFlags, Clusters = m.Entry.Clusters };
            AddToBucket(piece, map.FindOrInsert(b.Key).Entries);
        }
        if (over.Count != 0)
            unclustered.Add(new Entry { Mesh = mesh.Subset(over), Origin = m.Entry.Origin, ObjectFlags = m.Entry.ObjectFlags, Clusters = m.Entry.Clusters });
        if (none.Count != 0)
            unclustered.Add(new Entry { Mesh = mesh.Subset(none), Origin = m.Entry.Origin, ObjectFlags = m.Entry.ObjectFlags | 0x10000, Clusters = m.Entry.Clusters });
    }

    static Vector3 Corner(Mesh mesh, int i)
    {
        var at = i * mesh.Stride;
        return new Vector3(mesh.Vertices[at], mesh.Vertices[at + 1], mesh.Vertices[at + 2]);
    }

    /// <summary>
    /// <c>FUN_180230df0</c>: appended to the first entry <c>CanMerge</c>
    /// accepts, else a new entry with its own copy of the mesh.
    /// </summary>
    void AddToBucket(Entry e, List<Entry> bucket)
    {
        foreach (var b in bucket)
        {
            if (_canMerge(e, b))
            {
                b.Mesh.Append(e.Mesh);
                return;
            }
        }
        bucket.Add(new Entry { Mesh = e.Mesh.Copy(), Origin = e.Origin, ObjectFlags = e.ObjectFlags, Clusters = e.Clusters });
    }

    /// <summary>
    /// <c>FUN_180235b50</c>, one group's merge passes: at the minimums times
    /// 4 (volume 8) with factor 1.1, then at the minimums with factor 1000
    /// and the membership cap as slack, repeating both while the second
    /// merges; once at twice the minimums with factor 1.25 and slack 1; then
    /// at 16 times with factor 1.1 until nothing merges.
    /// </summary>
    int MergeGroup(HashTable map)
    {
        int t = MinTriangles, v = MinVertices, vol = MinVolume;
        var merged = MergeSmall(map, t * 4, v * 4, vol * 8, 1.1f, 0);
        var n = MergeSmall(map, t, v, vol, 1000f, MaxMembership);
        while (n != 0)
        {
            merged += n;
            merged += MergeSmall(map, t * 4, v * 4, vol * 8, 1.1f, 0);
            n = MergeSmall(map, t, v, vol, 1000f, MaxMembership);
        }
        merged += MergeSmall(map, t * 2, v * 2, vol * 2, 1.25f, 1);
        n = MergeSmall(map, t * 16, v * 16, vol * 16, 1.1f, 0);
        while (n != 0)
        {
            merged += n;
            n = MergeSmall(map, t * 16, v * 16, vol * 16, 1.1f, 0);
        }
        return merged;
    }

    /// <summary>
    /// <c>FUN_1802334e0</c>, one pass: the keys are taken in slot order, and
    /// each bucket's entries walked last to first. An entry under the
    /// minimum volume, triangles or vertices whose <see cref="ChooseTarget"/>
    /// finds a bucket absorbs the first entry there <c>CanMerge</c> accepts,
    /// then leaves its bucket for the bucket of the joined set (it stays when
    /// that is its own bucket).
    /// </summary>
    int MergeSmall(HashTable map, int minTriangles, int minVertices, int minVolume, float factor, int slack)
    {
        var merged = 0;
        var keys = map.Slots().Select(b => b.Key).ToList();
        foreach (var key in keys)
        {
            var source = map.FindOrInsert(key);
            for (var i = source.Entries.Count - 1; i >= 0; i--)
            {
                var e = source.Entries[i];
                var (mn, mx) = e.Mesh.Bounds();
                var volume = (mx.Y - mn.Y) * (mx.X - mn.X) * (mx.Z - mn.Z);
                if (!(volume < minVolume || e.Mesh.Indices.Count / 3 < minTriangles || e.Mesh.VertexCount < minVertices))
                    continue;
                var choice = ChooseTarget(map, key, e, factor, slack);
                if (choice == null)
                    continue;
                merged++;
                var copy = e.Share();
                var joined = map.FindOrInsert(choice.Value.Joined);
                var target = map.Find(choice.Value.Key);
                source = map.FindOrInsert(key);
                if (target != null)
                {
                    for (var k = 0; k < target.Entries.Count; k++)
                    {
                        if (!_canMerge(target.Entries[k], copy))
                            continue;
                        copy.Mesh.Append(target.Entries[k].Mesh);
                        target.Entries.RemoveAt(k);
                        break;
                    }
                }
                if (source != joined)
                {
                    source.Entries.RemoveAt(i);
                    AddToBucket(copy, joined.Entries);
                }
            }
        }
        return merged;
    }

    sealed class Candidate
    {
        public ushort[] Key = [];
        public ushort[] Joined = [];
        public float Score;
        public uint Vertices;
        public Vector3 Min, Max;
    }

    /// <summary>
    /// <c>FUN_1802321b0</c>: where a small entry goes. The clusters near it
    /// are those its bounds grown by 240 touch. Each other bucket touching one
    /// joins its key with the entry's own (the join stops growing at the cap;
    /// a join past it is refused); every entry there that <c>CanMerge</c>
    /// accepts with at most 0xffff vertices together is a candidate unless the
    /// join exceeds both keys by more than <paramref name="slack"/> or the
    /// union box is more than <paramref name="factor"/> times the larger of
    /// the two boxes. A candidate's score multiplies, for each cluster the
    /// join adds, its mutual visibility with each of the target's clusters,
    /// rounded to 1/20. MSVC's std::sort with <see cref="Before"/> picks.
    /// </summary>
    (ushort[] Key, ushort[] Joined)? ChooseTarget(HashTable map, ushort[] srcKey, Entry e, float factor, int slack)
    {
        var eb = e.Mesh.Bounds();
        var grown = (new Vector3(eb.Min.X + -240f, eb.Min.Y + -240f, eb.Min.Z + -240f), new Vector3(eb.Max.X + 240f, eb.Max.Y + 240f, eb.Max.Z + 240f));
        var near = MeshMembership(grown);
        if (near.Length == 0)
            return null;
        var nearBits = new HashSet<ushort>(near);
        var cands = new List<Candidate>();
        foreach (var bucket in map.Slots())
        {
            var k = bucket.Key;
            if (k.AsSpan().SequenceEqual(srcKey))
                continue;
            var joined = new List<ushort>(srcKey);
            var total = srcKey.Length;
            var touches = false;
            foreach (var c in k)
            {
                touches |= nearBits.Contains(c);
                if (joined.Contains(c))
                    continue;
                if (total < MaxMembership)
                    joined.Add(c);
                total++;
            }
            if (!touches || total > MaxMembership)
                continue;
            joined.Sort();
            foreach (var t in bucket.Entries)
            {
                if (t.Mesh.VertexCount + e.Mesh.VertexCount > 0xffff || !_canMerge(e, t))
                    continue;
                var tb = t.Mesh.Bounds();
                var score = 1.0f;
                if (_mutual.Length != 0)
                {
                    foreach (var c in joined)
                    {
                        if (k.Contains(c))
                            continue;
                        var p = 1.0f;
                        foreach (var d in k)
                            p *= _mutual[c][d];
                        score *= p;
                    }
                    score = MathF.Round(score * 20.0f, MidpointRounding.AwayFromZero) / 20.0f;
                }
                if (k.Length + slack < total && srcKey.Length + slack < total)
                    continue;
                var volT = (tb.Max.X - tb.Min.X) * (tb.Max.Y - tb.Min.Y) * (tb.Max.Z - tb.Min.Z);
                var volE = (eb.Max.X - eb.Min.X) * (eb.Max.Y - eb.Min.Y) * (eb.Max.Z - eb.Min.Z);
                var denom = volE <= volT ? volT : volE;
                float ux0 = eb.Min.X <= tb.Min.X ? eb.Min.X : tb.Min.X, uy0 = eb.Min.Y <= tb.Min.Y ? eb.Min.Y : tb.Min.Y, uz0 = eb.Min.Z <= tb.Min.Z ? eb.Min.Z : tb.Min.Z;
                float ux1 = tb.Max.X <= eb.Max.X ? eb.Max.X : tb.Max.X, uy1 = tb.Max.Y <= eb.Max.Y ? eb.Max.Y : tb.Max.Y, uz1 = tb.Max.Z <= eb.Max.Z ? eb.Max.Z : tb.Max.Z;
                if (factor < (ux1 - ux0) * (uy1 - uy0) * (uz1 - uz0) / denom)
                    continue;
                cands.Add(new Candidate { Key = k, Joined = [.. joined], Score = score, Vertices = (uint)t.Mesh.VertexCount, Min = tb.Min, Max = tb.Max });
            }
        }
        if (cands.Count == 0)
            return null;
        var arr = cands.ToArray();
        var verts = e.Mesh.VertexCount;
        MsvcSort.Sort(arr, (a, b) => Before(a, b, eb, verts));
        return (arr[0].Key, arr[0].Joined);
    }

    /// <summary>
    /// <c>FUN_180233190</c>: the higher score; then the join adding fewer
    /// clusters; then (unless within 0.0001) the smaller ratio of the union
    /// box's surface to the candidate's own; then a candidate that keeps the
    /// entry under 0xffff vertices; then fewer vertices.
    /// </summary>
    static bool Before(Candidate a, Candidate b, (Vector3 Min, Vector3 Max) e, int entryVertices)
    {
        if (a.Score != b.Score)
            return b.Score < a.Score;
        int da = a.Joined.Length - a.Key.Length, db = b.Joined.Length - b.Key.Length;
        if (da != db)
            return da < db;
        var ra = Ratio(a, e);
        var rb = Ratio(b, e);
        if (0.0001f <= MathF.Abs(ra - rb))
            return ra < rb;
        var fa = a.Vertices < 0xffff && 0xffff < entryVertices + (int)a.Vertices;
        var fb = b.Vertices < 0xffff && 0xffff < entryVertices + (int)b.Vertices;
        if (fa == fb)
            return a.Vertices < b.Vertices;
        return fb && !fa;
    }

    static float Ratio(Candidate c, (Vector3 Min, Vector3 Max) e)
    {
        var x0 = c.Min.X <= e.Min.X ? c.Min.X : e.Min.X;
        var y0 = c.Min.Y <= e.Min.Y ? c.Min.Y : e.Min.Y;
        var z0 = c.Min.Z <= e.Min.Z ? c.Min.Z : e.Min.Z;
        var x1 = e.Max.X <= c.Max.X ? c.Max.X : e.Max.X;
        var y1 = e.Max.Y <= c.Max.Y ? c.Max.Y : e.Max.Y;
        var z1 = e.Max.Z <= c.Max.Z ? c.Max.Z : e.Max.Z;
        float dx = x1 - x0, dy = y1 - y0, dz = z1 - z0;
        var union = dz * dx + dy * dx + dz * dy;
        float ox = c.Max.X - c.Min.X, oy = c.Max.Y - c.Min.Y, oz = c.Max.Z - c.Min.Z;
        var own = oz * ox + oy * ox + oz * oy;
        return (union + union) / (own + own);
    }

    /// <summary>
    /// <c>TriBoxOverlap</c> (181265950): Akenine-Moller's test with each edge
    /// normalised and <paramref name="eps"/> added to every radius: the box
    /// axes, the nine edge cross axes, then the plane. Sums of squares run
    /// z, y, x; an edge shorter than 1e-17 (or longer than 1e17) is
    /// normalised in double (<c>FUN_18125d000</c>), a zero one left zero.
    /// </summary>
    public static bool TriBoxOverlap(Vector3 centre, Vector3 h, Vector3 p0, Vector3 p1, Vector3 p2, float eps)
    {
        float v0x = p0.X - centre.X, v1x = p1.X - centre.X, v2x = p2.X - centre.X;
        if (!(Min3(v0x, v1x, v2x) <= eps + h.X) || !(-(eps + h.X) <= Max3(v0x, v1x, v2x)))
            return false;
        float v1y = p1.Y - centre.Y, v2y = p2.Y - centre.Y, v0y = p0.Y - centre.Y;
        if (!(Min3(v0y, v1y, v2y) <= eps + h.Y) || !(-(eps + h.Y) <= Max3(v0y, v1y, v2y)))
            return false;
        float v1z = p1.Z - centre.Z, v2z = p2.Z - centre.Z, v0z = p0.Z - centre.Z;
        if (!(Min3(v0z, v1z, v2z) <= eps + h.Z) || !(-(eps + h.Z) <= Max3(v0z, v1z, v2z)))
            return false;

        var a = Normalise(v1x - v0x, v1y - v0y, v1z - v0z);
        float ax = MathF.Abs(a.X), ay = MathF.Abs(a.Y), az = MathF.Abs(a.Z);
        if (!Axis(v0y * a.Z - v0z * a.Y, v2y * a.Z - v2z * a.Y, h.Y * az + ay * h.Z + eps, secondFirst: true))
            return false;
        if (!Axis(v0z * a.X - v0x * a.Z, v2z * a.X - v2x * a.Z, ax * h.Z + h.X * az + eps, secondFirst: true))
            return false;
        if (!Axis(v1x * a.Y - v1y * a.X, v2x * a.Y - v2y * a.X, h.Y * ax + h.X * ay + eps, secondFirst: false))
            return false;

        var b = Normalise(v2x - v1x, v2y - v1y, v2z - v1z);
        float bx = MathF.Abs(b.X), by = MathF.Abs(b.Y), bz = MathF.Abs(b.Z);
        if (!Axis(v0y * b.Z - v0z * b.Y, v1y * b.Z - v1z * b.Y, h.Y * bz + by * h.Z + eps, secondFirst: true))
            return false;
        if (!Axis(v0z * b.X - v0x * b.Z, v1z * b.X - v1x * b.Z, h.X * bz + bx * h.Z + eps, secondFirst: true))
            return false;
        if (!Axis(v0x * b.Y - v0y * b.X, v2x * b.Y - v2y * b.X, h.Y * bx + h.X * by + eps, secondFirst: true))
            return false;

        var c = Normalise(v0x - v2x, v0y - v2y, v0z - v2z);
        float cx = MathF.Abs(c.X), cy = MathF.Abs(c.Y), cz = MathF.Abs(c.Z);
        if (!Axis(v0y * c.Z - v0z * c.Y, v1y * c.Z - v1z * c.Y, h.Y * cz + cy * h.Z + eps, secondFirst: true))
            return false;
        if (!Axis(v0z * c.X - v0x * c.Z, v1z * c.X - v1x * c.Z, h.X * cz + cx * h.Z + eps, secondFirst: true))
            return false;
        if (!Axis(v1x * c.Y - v1y * c.X, v2x * c.Y - v2y * c.X, h.Y * cx + h.X * cy + eps, secondFirst: false))
            return false;

        var n = Normalise(c.Z * a.Y - c.Y * a.Z, c.X * a.Z - c.Z * a.X, c.Y * a.X - c.X * a.Y);
        var d = n.Z * v0z + n.Y * v0y + n.X * v0x;
        var r = MathF.Abs(n.Y * h.Y) + MathF.Abs(n.X * h.X) + MathF.Abs(n.Z * h.Z) + eps;
        return d <= r && -r <= d;
    }

    static float Min3(float a, float b, float c)
    {
        var m = a <= b ? a : b;
        return m <= c ? m : c;
    }

    static float Max3(float a, float b, float c)
    {
        var m = b <= a ? a : b;
        return c <= m ? m : c;
    }

    /// <summary>
    /// One separating axis: projections <paramref name="p"/> and
    /// <paramref name="q"/> against radius <paramref name="r"/>. The binary
    /// orders the pair either as (q &lt;= p) or (p &lt;= q); with equal
    /// projections both read the same, so only the rejections matter.
    /// </summary>
    static bool Axis(float p, float q, float r, bool secondFirst)
    {
        if (secondFirst ? q <= p : !(p <= q))
            return !(r < q) && !(p < -r);
        return !(r < p) && !(q < -r);
    }

    static Vector3 Normalise(float x, float y, float z)
    {
        var len = MathF.Sqrt(z * z + y * y + x * x);
        if (len < 1e-17f || 1e17f < len)
        {
            if (len == 0f)
                return Vector3.Zero;
            double dx = x, dy = y, dz = z;
            var l = Math.Sqrt(dy * dy + dx * dx + dz * dz);
            var inv = 1.0 / l;
            return new Vector3((float)(inv * dx), (float)(inv * dy), (float)(inv * dz));
        }
        var s = 1.0f / len;
        return new Vector3(x * s, y * s, z * s);
    }

    /// <summary>
    /// Valve's <c>CUtlHashTable</c> over cluster sets (entries 0x38 bytes):
    /// open addressing whose chains start in their home slot, a flags word per
    /// slot (bit 31 free, bit 30 last in chain, the low 30 bits the hash). An
    /// insert takes the home slot, bumping its occupant to the next free slot
    /// (<c>FUN_180237840</c>); the table grows to (used * 4 + 4) / 3 rounded
    /// to a power of two before it would pass three quarters, and a rehash
    /// reinserts from the last slot down.
    /// </summary>
    sealed class HashTable
    {
        const uint Free = 0x80000000, Last = 0x40000000, HashMask = 0x3fffffff;
        uint[] _flags = [];
        Bucket?[] _data = [];
        int _used;

        public sealed class Slot : Bucket
        {
            public List<int> Indices = [];
        }

        static uint Hash(ushort[] key)
        {
            var h = 0x3501a674u;
            foreach (var c in key)
                h ^= h * 0x40 + (h >> 2) + 0x9e3779b9u + c;
            return h;
        }

        int Ideal(int slot, uint mask)
        {
            var f = _flags[slot];
            return (f & Free) != 0 ? -1 : (int)(f & mask);
        }

        public IEnumerable<Slot> Slots()
        {
            for (var s = 0; s < _flags.Length; s++)
            {
                if ((_flags[s] & Free) == 0)
                    yield return (Slot)_data[s]!;
            }
        }

        public Slot? Find(ushort[] key)
        {
            var i = FindSlot(key, Hash(key));
            return i < 0 ? null : (Slot)_data[i]!;
        }

        int FindSlot(ushort[] key, uint h)
        {
            if (_used == 0)
                return -1;
            var mask = (uint)_flags.Length - 1;
            var home = (int)(h & mask);
            if (Ideal(home, mask) != home)
                return -1;
            for (var s = home; ; s = (int)((s + 1) & mask))
            {
                if (Ideal(s, mask) != home)
                    continue;
                if (((_flags[s] ^ h) & HashMask) == 0 && _data[s]!.Key.AsSpan().SequenceEqual(key))
                    return s;
                if ((_flags[s] & Last) != 0)
                    return -1;
            }
        }

        public Slot FindOrInsert(ushort[] key)
        {
            var h = Hash(key);
            var i = FindSlot(key, h);
            if (i >= 0)
                return (Slot)_data[i]!;
            var need = (uint)(_used * 4 + 4);
            if ((uint)(_flags.Length * 3) < need)
                Realloc((int)need / 3);
            var slot = new Slot { Key = [.. key] };
            _data[Insert(h)] = slot;
            return slot;
        }

        /// <summary>The <c>if (used &lt; n) realloc(n * 4 / 3)</c> both callers make.</summary>
        public void Reserve(int n)
        {
            if (_used < n)
                Realloc(n * 4 / 3);
        }

        int Insert(uint h)
        {
            _used++;
            var mask = (uint)_flags.Length - 1;
            var home = (int)(h & mask);
            var flags = (h & HashMask) | Last;
            if (Ideal(home, mask) == home)
            {
                Bump(home);
                flags = h & HashMask;
            }
            else if ((_flags[home] & Free) == 0)
            {
                Bump(home);
            }
            _flags[home] = flags;
            return home;
        }

        void Bump(int idx)
        {
            var mask = (uint)_flags.Length - 1;
            var moved = _flags[idx] & 0x7fffffff;
            var ideal = (int)(moved & mask);
            var j = ideal;
            while (true)
            {
                var s = _flags[j];
                if (Ideal(j, mask) == ideal)
                {
                    if ((s & Last) != 0)
                    {
                        moved |= Last;
                        _flags[j] = s & ~Last;
                    }
                    j = (int)((j + 1) & mask);
                    continue;
                }
                if ((s & Free) != 0)
                    break;
                j = (int)((j + 1) & mask);
            }
            if ((_flags[idx] & Last) != 0)
            {
                for (var p = idx; ;)
                {
                    p = (int)((p + mask) & mask);
                    if (p == j)
                        break;
                    if (Ideal(p, mask) != ideal)
                        continue;
                    _flags[p] |= Last;
                    moved &= ~Last;
                    break;
                }
            }
            _flags[j] = moved;
            _data[j] = _data[idx];
            _data[idx] = null;
            _flags[idx] = Free;
        }

        void Realloc(int min)
        {
            var n = (uint)Math.Max(min, 1) - 1;
            n |= n >> 1;
            n |= n >> 2;
            n |= n >> 4;
            n |= n >> 8;
            n = (n | n >> 16) + 1;
            var oldFlags = _flags;
            var oldData = _data;
            _flags = new uint[n];
            _data = new Bucket?[n];
            Array.Fill(_flags, Free);
            _used = 0;
            for (var s = oldFlags.Length - 1; s >= 0; s--)
            {
                if ((oldFlags[s] & Free) != 0)
                    continue;
                _data[Insert(oldFlags[s])] = oldData[s];
            }
        }
    }
}
