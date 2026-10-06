using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// CWorldRendererBuilderNode::CompileNode (rc 180277ab0) from its mesh lists
/// to the node's render objects and aggregates, before any geometry is
/// written: which entries make which object, under which name.
/// <list type="number">
/// <item>Each entry is in the list <see cref="MeshLists.Assign"/> picks.</item>
/// <item>With visibility-guided clustering and three or more vis clusters,
/// the aggregate, overlay and base lists go through
/// <see cref="VisibilityMeshMerger"/>: a list keeps its unclustered entries,
/// each of them adds render cluster boxes (Step_BuildingRenderClusters,
/// 180282ef0: boxes appended, and every box so far grown by 1/32 again), and
/// each bucket is compiled at once: entries that can aggregate go to the
/// list's stash (vf 0x28, 18026e840), the rest become one object each
/// (vf 0x30, 18026e9c0: <c>vism%i_mt_&lt;material&gt;</c>, the cluster index a
/// running count per list).</item>
/// <item>Without boxes after that, the layer's entries make them; without
/// any still, the grid of 180283520 (not ported: refused).</item>
/// <item>Each list is compiled per render cluster (its entries cut per
/// triangle by <see cref="RenderClusters.Assign"/>, 18026cfd0 and
/// 181366f30), or whole with cluster -1 when the list is not clustered:
/// the stash takes what can aggregate (vf 0x28), the list's compile
/// (vf 0x20) makes objects of the rest.</item>
/// <item>The stash is then grouped into aggregates (GroupAggregates,
/// 18026f590); a run left alone goes back through vf 0x20, per cluster by
/// its bounds centre, the cluster index past the list's last.</item>
/// </list>
/// An object's name is <c>&lt;node&gt;_lr&lt;layer&gt;[_c&lt;k&gt;]_s</c> with the flag
/// suffixes of CreateRenderObject (18026bab0) and its base name; an
/// aggregate's <c>&lt;node&gt;_lr&lt;layer&gt;_&lt;list&gt;_&lt;material&gt;_&lt;n&gt;</c>
/// (180268e80, 180262150: n counts per prefix). Only one layer is ported.
/// </summary>
internal static class WorldNodeCompile
{
    /// <summary>A node mesh entry (0x238 bytes): its mesh and the facts the compile reads.</summary>
    public sealed record Entry(string Material, int Stride, float[] Vertices, int[] Indices, WrbMeshEntry Facts)
    {
        public int TriangleCount => Indices.Length / 3;
    }

    /// <summary>A render object: its name, layer, cluster, flags (CreateRenderObject's sixth argument) and meshes.</summary>
    public sealed record RenderObject(string Name, int Layer, int Cluster, uint Flags, IReadOnlyList<Entry> Meshes);

    /// <summary>An aggregate scene object: its name, layer and meshes in draw order.</summary>
    public sealed record AggregateObject(string Name, int Layer, string List, IReadOnlyList<Entry> Meshes);

    public sealed record Result(List<RenderObject> Objects, List<AggregateObject> Aggregates, List<(Vector3 Min, Vector3 Max)> RenderBoxes);

    /// <summary>Settings CompileNode reads: csgo_core's values by default.</summary>
    public sealed record Settings
    {
        /// <summary>Hammer/DefaultMinTrianglesPerCluster (node +0x304 overrides it when above 0).</summary>
        public int MinTrianglesPerCluster { get; init; } = 2048;
        /// <summary>The render cluster size: the builder's m_flMinDrawVolumeSize (node +0x300 overrides it when above 0).</summary>
        public float ClusterSize { get; init; } = 1024f;
        public bool UseAggregateInstances { get; init; } = true;
        public bool VisibilityGuidedMeshClustering { get; init; } = true;
        public bool AggregateRayTraceProxy { get; init; }
        public string NodePrefix { get; init; } = "n0";
    }

    // A list as CompileNode builds it: the class's switches and its compile.
    private sealed class List(MeshLists.Kind kind, bool clustered, bool stashes)
    {
        public MeshLists.Kind Kind { get; } = kind;
        public bool Clustered { get; } = clustered;
        public bool Stashes { get; } = stashes;
        public List<Entry> Entries { get; set; } = [];
        public List<Entry> Stash { get; } = [];
    }

    // Base names' global counters (DAT_183bb3760 and friends), one per compile.
    private sealed class Counters
    {
        public int BlockLight;
        public int BlockLightSkybox;
        public Dictionary<string, int> Aggregate { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static Result Compile(Dictionary<MeshLists.Kind, List<Entry>> byKind, IReadOnlyList<List<(Vector3 Min, Vector3 Max)>>? visFlat,
                                 float[][]? mutual, Settings? settings = null)
    {
        settings ??= new Settings();
        var counters = new Counters();
        var objects = new List<RenderObject>();
        var aggregates = new List<AggregateObject>();
        // CompileNode's lists in order, with +0x20 (clustered) and +0x21 (stashes what can aggregate).
        var lists = MeshLists.Order.Select(o => new List(o.Kind,
            clustered: o.Kind is not MeshLists.Kind.BakedPropLod,
            stashes: settings.UseAggregateInstances && o.Kind is MeshLists.Kind.BakedPropLod or MeshLists.Kind.NoSplit or MeshLists.Kind.Instanced or MeshLists.Kind.Aggregate)
        { Entries = [.. byKind.GetValueOrDefault(o.Kind) ?? []] }).ToList();
        var layerEntries = lists.SelectMany(l => l.Entries).ToList();

        var boxes = new List<(Vector3 Min, Vector3 Max)>();
        if (settings.VisibilityGuidedMeshClustering && visFlat is { Count: >= 3 } && mutual != null)
        {
            foreach (var list in lists.Where(l => MeshLists.Merged.Contains(l.Kind)))
            {
                var (buckets, unclustered) = Merge(list.Entries, visFlat, mutual);
                list.Entries = unclustered;
                AppendRenderBoxes(boxes, unclustered, settings);
                var vism = 0;
                foreach (var bucket in buckets.Where(b => b.Count > 0))
                {
                    var rest = StashAggregatable(list, bucket);
                    foreach (var e in rest)
                        objects.Add(CreateRenderObject(settings.NodePrefix, $"vism0_mt_{FileBase(e.Material, 0x18)}", 0, vism++,
                                                       e.Facts.ObjectFlags, e.Facts.Attributes, e.Facts.Field1a6 != 0, [e]));
                }
            }
        }
        if (boxes.Count == 0)
            AppendRenderBoxes(boxes, layerEntries, settings);
        if (boxes.Count == 0 && layerEntries.Count > 0)
            throw new NotSupportedException("a node with no render cluster boxes: the grid boxes of 180283520 are not ported");

        foreach (var list in lists)
        {
            // CompileNode's running maximum starts at 1 (iVar37), so what vf 0x40
            // leaves is numbered from max(1, last cluster used) + 1.
            var maxCluster = 1;
            if (list.Entries.Count > 0)
            {
                if (!list.Clustered)
                {
                    var rest = StashAggregatable(list, list.Entries);
                    CompileList(list, rest, 0, -1, settings, counters, objects);
                }
                else
                {
                    var perCluster = SplitByCluster(list.Entries, boxes);
                    for (var k = 0; k < perCluster.Count; k++)
                    {
                        if (perCluster[k].Count == 0)
                            continue;
                        var rest = StashAggregatable(list, perCluster[k]);
                        CompileList(list, rest, 0, k, settings, counters, objects);
                        maxCluster = Math.Max(maxCluster, k);
                    }
                }
            }
            // vf 0x40 (180270430): the stash into aggregates, what is left per cluster after the list's last.
            if (list.Stash.Count > 0)
            {
                var left = GroupAggregates(list, settings, counters, aggregates);
                if (left.Count > 0)
                {
                    var byBox = AssignByCentre(left, boxes);
                    for (var k = 0; k < byBox.Count; k++)
                        if (byBox[k].Count > 0)
                            CompileList(list, byBox[k], 0, maxCluster + 1 + k, settings, counters, objects);
                }
            }
        }
        return new Result(objects, aggregates, boxes);
    }

    // CVisibilityMeshMerger::MergeMeshes with csgo_core's minimums; each output
    // entry keeps its source entry's facts, its object flags the merger's.
    private static (List<List<Entry>> Buckets, List<Entry> Unclustered) Merge(List<Entry> entries, IReadOnlyList<List<(Vector3 Min, Vector3 Max)>> flat, float[][] mutual)
    {
        if (entries.Count == 0)
            return ([], []);
        var inputs = entries.Select((o, k) => new VisibilityMeshMerger.Entry
        {
            Mesh = new VisibilityMeshMerger.Mesh { Vertices = [.. o.Vertices], Stride = o.Stride, PositionOffset = PositionOffset(o), Indices = [.. o.Indices] },
            Origin = k, ObjectFlags = o.Facts.ObjectFlags,
        }).ToList();
        var merger = new VisibilityMeshMerger([.. flat], mutual,
            (a, b) => WrbMeshEntry.CanMerge(entries[a.Origin].Facts, a.Mesh.VertexCount, entries[b.Origin].Facts, b.Mesh.VertexCount))
        { MinTriangles = 2048, MinVertices = 2048, MinVolume = 1800, MaxMembership = 16 };
        var output = merger.MergeMeshes(inputs);
        Entry Back(VisibilityMeshMerger.Entry e)
        {
            var source = entries[e.Origin];
            return new Entry(source.Material, e.Mesh.Stride, [.. e.Mesh.Vertices], [.. e.Mesh.Indices], source.Facts with { ObjectFlags = e.ObjectFlags });
        }
        return ([.. output.Buckets.Select(b => b.Entries.Select(Back).ToList())], [.. output.Unclustered.Select(Back)]);
    }

    // vf 0x28 (18026e840): with the list's +0x21, entries that CanAggregate go
    // to the stash (walked from the end); the rest stay in order.
    private static List<Entry> StashAggregatable(List list, List<Entry> entries)
    {
        if (!list.Stashes)
            return entries;
        var rest = new List<Entry>(entries);
        for (var i = rest.Count - 1; i >= 0; i--)
            if (CanAggregate(rest[i].Facts))
            {
                list.Stash.Add(rest[i]);
                rest.RemoveAt(i);
            }
        return rest;
    }

    private static bool CanAggregate(WrbMeshEntry f)
        => MeshLists.CanAggregate(new MeshLists.Input(f.Attributes, f.ObjectFlags, f.OverlayOrder, f.FadeMax, false, false));

    // Each list's vf 0x20.
    private static void CompileList(List list, List<Entry> entries, int layer, int cluster, Settings settings, Counters counters, List<RenderObject> objects)
    {
        if (entries.Count == 0)
            return;
        switch (list.Kind)
        {
            case MeshLists.Kind.BlockLight:
                ShadowSplit($"mesh_blocklight{counters.BlockLight++}", entries, layer, cluster, settings, objects);
                break;
            case MeshLists.Kind.SkyboxBlockLight:
                ShadowSplit($"mesh_blocklight_skybox{counters.BlockLightSkybox++}", entries, layer, cluster, settings, objects);
                break;
            case MeshLists.Kind.Skybox:
                objects.Add(CreateRenderObject(settings.NodePrefix, "mesh_skybox", layer, cluster, 0x400, 0, false, entries));
                break;
            case MeshLists.Kind.Aggregate:
                // 180272ae0: std::sort by material name, then grouped as "mesh".
                var sorted = entries.ToArray();
                MsvcSort.Sort(sorted, (a, b) => string.Compare(a.Material, b.Material, StringComparison.OrdinalIgnoreCase) < 0);
                GroupRenderObjects("mesh", [.. sorted], layer, cluster, settings, objects);
                break;
            case MeshLists.Kind.Base:
                GroupRenderObjects("mesh_base", entries, layer, cluster, settings, objects);
                break;
            default:
                throw new NotSupportedException($"the {list.Kind} mesh list's compile is not ported ({entries.Count} entries)");
        }
    }

    // 180270a20: entries without attribute bit 17 make <name>_shadow, the rest <name>_noshadow, flags 0x410.
    private static void ShadowSplit(string name, List<Entry> entries, int layer, int cluster, Settings settings, List<RenderObject> objects)
    {
        var shadow = entries.Where(e => (e.Facts.Attributes & 0x20000) == 0).ToList();
        var noShadow = entries.Where(e => (e.Facts.Attributes & 0x20000) != 0).ToList();
        if (shadow.Count > 0)
            objects.Add(CreateRenderObject(settings.NodePrefix, name + "_shadow", layer, cluster, 0x410, 0, false, shadow));
        if (noShadow.Count > 0)
            objects.Add(CreateRenderObject(settings.NodePrefix, name + "_noshadow", layer, cluster, 0x410, 0x20000, false, noShadow));
    }

    /// <summary>
    /// GroupRenderObjects (18026c150): a stable sort by cubemap, light probe,
    /// object flags, attribute bits 0x10420000, emissive boost and +0x1a6,
    /// then runs of equal ones (and an equal +0x1c8 matrix within 1e-5)
    /// each one object named <c>&lt;base&gt;[_cm%02d_lp%02d][_nz][_nsh][_fw][_eb%d]</c>.
    /// </summary>
    private static void GroupRenderObjects(string baseName, List<Entry> entries, int layer, int cluster, Settings settings, List<RenderObject> objects)
    {
        const ulong mask = 0x10420000;
        static float Boost(WrbMeshEntry f) => f.Field1a3 != 0 && (f.ObjectFlags & 0x100000) != 0 ? f.Field_b8 : 1f;
        var sorted = entries.OrderBy(e => e.Facts.Cubemap).ThenBy(e => e.Facts.LightProbe).ThenBy(e => (int)e.Facts.ObjectFlags)
                            .ThenBy(e => e.Facts.Attributes & mask).ThenBy(e => Boost(e.Facts)).ThenBy(e => e.Facts.Field1a6).ToList();
        var run = new List<Entry>();
        void Flush()
        {
            if (run.Count == 0)
                return;
            var f = run[0].Facts;
            var name = f.Cubemap == 0 && f.LightProbe == 0 ? baseName : $"{baseName}_cm{f.Cubemap:00}_lp{f.LightProbe:00}";
            var bits = f.Attributes & mask;
            if ((bits & 0x400000) != 0)
                name += "_nz";
            if ((bits & 0x20000) != 0)
                name += "_nsh";
            if ((bits >> 28) != 0)
                name += "_fw";
            var boost = Boost(f);
            if (boost != 1f)
                name += $"_eb{(int)MathF.Round(boost * 10000f)}";
            objects.Add(CreateRenderObject(settings.NodePrefix, name, layer, cluster, f.ObjectFlags, bits, f.Field1a6 != 0, [.. run]));
            run.Clear();
        }
        foreach (var e in sorted)
        {
            if (run.Count > 0)
            {
                var (a, b) = (run[0].Facts, e.Facts);
                var same = a.Cubemap == b.Cubemap && a.LightProbe == b.LightProbe && (a.Attributes & mask) == (b.Attributes & mask)
                           && a.Field1a6 == b.Field1a6 && a.ObjectFlags == b.ObjectFlags && Boost(a) == Boost(b) && MatrixClose(a.Matrix, b.Matrix);
                if (!same)
                    Flush();
            }
            run.Add(e);
        }
        Flush();
    }

    private static bool MatrixClose(float[] a, float[] b)
    {
        for (var i = 0; i < 12; i++)
            if (MathF.Abs(a[i] - b[i]) > 1e-5f)
                return false;
        return true;
    }

    /// <summary>CreateRenderObject's name (18026bab0).</summary>
    public static string ObjectName(string node, string baseName, int layer, int cluster, uint flags, ulong attributes, bool kv)
    {
        var s = new System.Text.StringBuilder(node);
        s.Append("_lr").Append(layer);
        if (cluster >= 0)
            s.Append("_c").Append(cluster);
        s.Append((flags & 0x200) != 0 ? "_d" : "_s");
        if ((flags & 0x400) != 0)
            s.Append("_cb");
        if ((flags & 0x80) != 0)
            s.Append("_dl");
        if ((flags & 0x20000) != 0)
            s.Append("_b");
        if (kv)
            s.Append("_kv");
        if ((flags & 0x10000) != 0)
            s.Append("_nv");
        if ((flags & 0x10) != 0)
            s.Append("_bl");
        if ((flags & 0x100000) != 0)
            s.Append("_rtem");
        if (baseName.Length > 0)
            s.Append('_').Append(baseName);
        return s.ToString();
    }

    private static RenderObject CreateRenderObject(string node, string baseName, int layer, int cluster, uint flags, ulong attributes, bool kv, List<Entry> meshes)
    {
        // The object's flags gain 0x20 with attribute bit 17 and 0x8000 with bit 21.
        var objectFlags = flags;
        if ((attributes & 0x20000) != 0)
            objectFlags |= 0x20;
        if ((attributes & 0x200000) != 0)
            objectFlags |= 0x8000;
        return new RenderObject(ObjectName(node, baseName, layer, cluster, flags, attributes, kv), layer, cluster, objectFlags, meshes);
    }

    /// <summary>
    /// GroupAggregates (18026f590) on a list's stash: a stable sort by the
    /// instance streams' kinds, attribute bits 0x6000000000 and 0x100000,
    /// material name and mesh size; runs of one material and equal bits (at
    /// most 0x7ffb) become aggregates unless a run is one entry of at most
    /// 0x360 vertices without instance streams, which is left (attribute bit
    /// 30 cleared) for the list's compile. Returns what is left.
    /// </summary>
    private static List<Entry> GroupAggregates(List list, Settings settings, Counters counters, List<AggregateObject> aggregates)
    {
        var name = list.Kind switch
        {
            MeshLists.Kind.Aggregate => "agg_merge",
            MeshLists.Kind.Instanced => "agg_nomerge",
            MeshLists.Kind.NoSplit => "agg_inst",
            MeshLists.Kind.BakedPropLod => "agg_prop",
            _ => throw new NotSupportedException($"aggregates of the {list.Kind} list"),
        };
        var sorted = list.Stash.OrderBy(e => e.Facts.Attributes & 0x6000000000).ThenBy(e => e.Facts.Attributes & 0x100000)
                               .ThenBy(e => e.Material, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Vertices.Length / e.Stride).ToList();
        var left = new List<Entry>();
        for (var i = 0; i < sorted.Count;)
        {
            var run = new List<Entry> { sorted[i] };
            var vertices = sorted[i].Vertices.Length / sorted[i].Stride;
            var j = i + 1;
            for (; j < sorted.Count && run.Count < 0x7ffb; j++)
            {
                var (a, b) = (sorted[i], sorted[j]);
                if (!string.Equals(a.Material, b.Material, StringComparison.Ordinal)
                    || ((a.Facts.Attributes ^ b.Facts.Attributes) & 0x6000000000) != 0 || ((a.Facts.Attributes ^ b.Facts.Attributes) & 0x100000) != 0
                    || !SameStreams(a.Facts.Streams, b.Facts.Streams))
                    break;
                run.Add(b);
                vertices += b.Vertices.Length / b.Stride;
            }
            if (settings.AggregateRayTraceProxy || run.Count > 1 || vertices > 0x360)
            {
                var prefix = $"{name}_{FileBase(run[0].Material, int.MaxValue)}_";
                var n = counters.Aggregate.GetValueOrDefault(prefix);
                counters.Aggregate[prefix] = n + 1;
                aggregates.Add(new AggregateObject($"{settings.NodePrefix}_lr0_{prefix}{n}", 0, name, run));
            }
            else
                left.AddRange(run.Select(e => e with { Facts = e.Facts with { Attributes = e.Facts.Attributes & ~0x40000000UL } }));
            i = j;
        }
        list.Stash.Clear();
        return left;
    }

    private static bool SameStreams(IReadOnlyList<WrbMeshEntry.Stream> a, IReadOnlyList<WrbMeshEntry.Stream> b)
        => a.Count == b.Count && a.Zip(b).All(p => string.Equals(p.First.Name, p.Second.Name, StringComparison.OrdinalIgnoreCase)
               && p.First.Index == p.Second.Index && p.First.Count == p.Second.Count && p.First.Type == p.Second.Type && p.First.Precise == p.Second.Precise);

    // Step_BuildingRenderClusters (180282ef0): the centroids of the entries
    // whose attributes & 0x131 is not 1, boxes appended, every box grown by 1/32.
    private static void AppendRenderBoxes(List<(Vector3 Min, Vector3 Max)> boxes, List<Entry> entries, Settings settings)
    {
        var centroids = new List<Vector3>();
        foreach (var e in entries)
        {
            if (!RenderClusters.Contributes(e.Facts.Attributes))
                continue;
            var at = PositionOffset(e);
            Vector3 P(int v) => new(e.Vertices[(v * e.Stride) + at], e.Vertices[(v * e.Stride) + at + 1], e.Vertices[(v * e.Stride) + at + 2]);
            for (var t = 0; t < e.TriangleCount; t++)
                centroids.Add(RenderClusters.Centroid(P(e.Indices[t * 3]), P(e.Indices[(t * 3) + 1]), P(e.Indices[(t * 3) + 2])));
        }
        if (centroids.Count > 0)
            boxes.AddRange(RenderClusters.Split([.. centroids], settings.MinTrianglesPerCluster, settings.ClusterSize, false));
        const float grow = 0.03125f;
        for (var i = 0; i < boxes.Count; i++)
            boxes[i] = (boxes[i].Min - new Vector3(grow), boxes[i].Max + new Vector3(grow));
    }

    // 18026cfd0 / 181366f30: each entry's triangles by render cluster, a
    // sub-mesh per cluster renumbered by first use, clusters in order.
    private static List<List<Entry>> SplitByCluster(List<Entry> entries, List<(Vector3 Min, Vector3 Max)> boxes)
    {
        var result = Enumerable.Range(0, boxes.Count).Select(_ => new List<Entry>()).ToList();
        foreach (var e in entries)
        {
            var at = PositionOffset(e);
            Vector3 P(int v) => new(e.Vertices[(v * e.Stride) + at], e.Vertices[(v * e.Stride) + at + 1], e.Vertices[(v * e.Stride) + at + 2]);
            var clusters = RenderClusters.Assign(boxes, Enumerable.Range(0, e.TriangleCount)
                .Select(t => RenderClusters.Centroid(P(e.Indices[t * 3]), P(e.Indices[(t * 3) + 1]), P(e.Indices[(t * 3) + 2]))));
            for (var k = 0; k < boxes.Count; k++)
            {
                var tris = Enumerable.Range(0, e.TriangleCount).Where(t => clusters[t] == k).ToList();
                if (tris.Count == 0)
                    continue;
                result[k].Add(e.TriangleCount == tris.Count ? e : SubMesh(e, tris));
            }
        }
        return result;
    }

    // FUN_1812de310: the chosen triangles, vertices renumbered by first use.
    private static Entry SubMesh(Entry e, List<int> triangles)
    {
        var remap = new Dictionary<int, int>();
        var vertices = new List<float>();
        var indices = new int[triangles.Count * 3];
        var k = 0;
        foreach (var t in triangles)
            for (var c = 0; c < 3; c++)
            {
                var v = e.Indices[(t * 3) + c];
                if (!remap.TryGetValue(v, out var n))
                {
                    remap[v] = n = remap.Count;
                    vertices.AddRange(e.Vertices.AsSpan(v * e.Stride, e.Stride).ToArray());
                }
                indices[k++] = n;
            }
        return e with { Vertices = [.. vertices], Indices = indices };
    }

    // 180270430: each left entry to the box holding its bounds centre (the
    // +0x1c8 matrix applied), else the nearest box.
    private static List<List<Entry>> AssignByCentre(List<Entry> entries, List<(Vector3 Min, Vector3 Max)> boxes)
    {
        if (boxes.Count == 0)
            return [entries];
        var result = Enumerable.Range(0, boxes.Count).Select(_ => new List<Entry>()).ToList();
        foreach (var e in entries)
        {
            var (lo, hi) = Bounds(e);
            var c = (lo + hi) * 0.5f;
            var pick = -1;
            var best = float.MaxValue;
            for (var k = 0; k < boxes.Count; k++)
            {
                var (bmin, bmax) = boxes[k];
                if (bmin.X <= c.X && bmin.Y <= c.Y && bmin.Z <= c.Z && c.X <= bmax.X && c.Y <= bmax.Y && c.Z <= bmax.Z)
                {
                    pick = k;
                    break;
                }
                var d = Vector3.Distance(Vector3.Clamp(c, bmin, bmax), c);
                if (d < best)
                    (best, pick) = (d, k);
            }
            result[pick].Add(e);
        }
        return result;
    }

    private static (Vector3 Min, Vector3 Max) Bounds(Entry e)
    {
        var at = PositionOffset(e);
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(-float.MaxValue);
        for (var v = 0; v < e.Vertices.Length / e.Stride; v++)
        {
            var p = new Vector3(e.Vertices[(v * e.Stride) + at], e.Vertices[(v * e.Stride) + at + 1], e.Vertices[(v * e.Stride) + at + 2]);
            (lo, hi) = (Vector3.Min(lo, p), Vector3.Max(hi, p));
        }
        return (lo, hi);
    }

    private static int PositionOffset(Entry e)
    {
        var offset = 0;
        foreach (var s in e.Facts.Streams)
        {
            if (string.Equals(s.Name, "position", StringComparison.OrdinalIgnoreCase))
                return offset;
            offset += s.Count;
        }
        return 0;
    }

    // V_FileBase into a buffer of the given size: the name without folder or
    // extension, cut to size - 1 characters.
    private static string FileBase(string path, int size)
    {
        var name = Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
        return size != int.MaxValue && name.Length > size - 1 ? name[..(size - 1)] : name;
    }
}
