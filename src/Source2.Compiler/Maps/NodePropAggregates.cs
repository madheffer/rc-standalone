using Source2.Compiler.Io;
using System.Numerics;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler.Maps;

/// <summary>
/// The static props folded into aggregate models (<c>agg_prop_*</c>):
/// WRBNode_BuildPropAggregates (180272ce0), with UseAggregateInstances on
/// (GROUND_TRUTH 43).
/// <list type="number">
/// <item>Models in CDefStringLess order (ordinal) of their path as the
/// entity names it; a prop that is baked (<see cref="NodePropEntries.Baked"/>)
/// is not here.</item>
/// <item>A prop is a candidate (18026d7a0) when it is a prop_static, has
/// lodlevel -1, no renderwithdynamic, no fademindist or fademaxdist above
/// 0, no lighting origin (objects with one use the static env map) and no
/// extra vertex streams; its materials' SupportsAggregateInstancing is
/// then also CanAggregate's attribute bit 30.</item>
/// <item>One entry per prop and draw call of every LOD (lodlevel -1 loads
/// them all; 180255470, its flags by <see cref="MeshEntryFlags"/>); the
/// model joins only when every entry CanAggregate
/// (<see cref="MeshLists.CanAggregate"/>). A model with several LODs gives
/// each prop a LOD setup and each fragment its LOD's mask (atixref's
/// power_outlet_campground: 9 props, 3 LODs, 27 fragments).</item>
/// <item>Entries bucketed by material; when any joined and their
/// index total passes <see cref="MinimumIndices"/>, each bucket is put in Morton
/// order of the entry's centre (<see cref="MortonKey"/>, std::sort on the
/// key), then stably by (attribute bits 0x6000000000, mesh), and cut into
/// runs of equal bits of at most 0x7ffb entries: one aggregate each.</item>
/// </list>
/// Valve orders meshes, and buckets, by heap address (a mesh pointer, a
/// hashed material pointer), which no data reproduces: here a mesh ranks by
/// first creation (model order, then draw call) and a bucket by its first
/// entry.
/// </summary>
internal static class NodePropAggregates
{
    /// <summary>
    /// The summed index count the joined entries must pass (0x35f): 180272ce0 sums each entry's
    /// CMesh +0x24, which CMesh_Alloc (1812d6a90) sets to the index count (+0x18 is the vertex count).
    /// </summary>
    public const int MinimumIndices = 0x35f;

    /// <summary>A draw: one prop mesh (model, draw call, material) built as an aggregate draw in model space.</summary>
    public sealed record Draw(string Model, int Call, string Material, NodeDraw.Result Geometry)
    {
        /// <summary>The prop mesh's floats a vertex and streams (<see cref="NodePropEntries.Load"/>), and its material's attribute bits.</summary>
        public int Stride { get; init; } = NodePropEntries.Stride;
        public IReadOnlyList<Physics.MeshWeld.Stream> Streams { get; init; } = NodePropEntries.Streams("color");
        public ulong Attributes { get; init; }
    }

    /// <summary>A prop's LOD setup: its LOD origin (the placed bounds centre), largest scale and the model's switch distances.</summary>
    public sealed record LodSetup(Vector3 Origin, float MaxScale, IReadOnlyList<float> SwitchDistances);

    /// <summary>One placed instance of a draw: the prop's matrix (3x4 row-major), tint and entry flags.</summary>
    public sealed record Fragment(int Draw, float[] Matrix, Vector4 Tint, uint ObjectFlags, ulong Attributes, int NodeId, Vector3 Centre, int LodMask, int LodSetup)
    {
        /// <summary>The fragment's own mesh bounds placed by the prop matrix (its env map point).</summary>
        public (Vector3 Min, Vector3 Max) MeshBounds { get; init; }

        /// <summary>
        /// The aggregate mesh's object flags: the entry's without the baked
        /// prop's 0x20000, through <see cref="FragmentFlags"/>, and 0x800 for a
        /// mesh of a model with LODs.
        /// </summary>
        public uint AggregateFlags => FragmentFlags(ObjectFlags & ~0x20000u, Attributes) | (LodSetup >= 0 ? 0x800u : 0u);
    }

    /// <summary>
    /// What BuildAggregate (180268e80) adds to every aggregate mesh's object
    /// flags: 0x20 with attribute bit 17; 0x40000 (needs dynamic shadows) with
    /// bit 19, or with bit 16 or 18 while 17 is clear (a merged mesh keeps its
    /// 0x20000: ze_hold_em_p's lit fluorescent tubes, 0x20028).
    /// </summary>
    public static uint FragmentFlags(uint objectFlags, ulong attributes)
    {
        var flags = objectFlags;
        if ((attributes >> 17 & 1) != 0)
            flags |= 0x20;
        if (((attributes >> 17 & 1) == 0 && ((attributes >> 16 & 1) != 0 || (attributes >> 18 & 1) != 0)) || (attributes >> 19 & 1) != 0)
            flags |= 0x40000;
        return flags;
    }

    /// <summary>
    /// An aggregate model: its material, the run's attribute bits, draws and
    /// fragments in order, and its first and second texcoord's vertex format
    /// (GROUND_TRUTH 44): R32G32_FLOAT when the run is flagged (a source draw
    /// with float texcoords, or a value past 16), else R16G16_SNORM when
    /// every value lies in [-1, 1], else R16G16_FLOAT.
    /// </summary>
    public sealed record Aggregate(string Material, ulong AttributeBits, IReadOnlyList<Draw> Draws, IReadOnlyList<Fragment> Fragments,
                                   IReadOnlyList<string> TexcoordFormats)
    {
        /// <summary>The LOD setups the fragments name, in order.</summary>
        public IReadOnlyList<LodSetup> LodSetups { get; init; } = [];
    }

    /// <summary>
    /// A static prop left in the node's prop lists: neither baked nor in an
    /// aggregate (not a candidate, a model that cannot aggregate, or every
    /// prop when the joined entries stay under <see cref="MinimumIndices"/>).
    /// The node ships it as a scene object of its own model.
    /// </summary>
    /// <remarks>A prop whose model does not load still ships, with no bounds (Valve's handshakes are then 0).</remarks>
    public sealed record StandaloneProp(string Model, DmxBinary.Element Keys, float[] Matrix, int NodeId, string? Skin, (Vector3 Min, Vector3 Max)? Bounds);

    // An entry before bucketing: its mesh (index into the mesh list) and fields.
    private sealed record Entry(int Mesh, string Material, float[] Matrix, Vector4 Tint, uint ObjectFlags, ulong Attributes, int NodeId, Vector3 Centre, int Indices,
                                int Prop, int LodMask, bool Lods)
    {
        public IReadOnlyList<float> Switch { get; init; } = [];
        public float MaxScale { get; init; } = 1f;
        public (Vector3 Min, Vector3 Max) MeshBounds { get; init; }
    }

    // A mesh: a model's draw call under its final material, unpacked in model space.
    private sealed record Mesh(string Model, int Call, string Material, float[] Vertices, int Stride, IReadOnlyList<Physics.MeshWeld.Stream> Streams, int[] Indices,
                               ulong Attributes, bool FloatTexcoords, int Lod);

    /// <summary>
    /// The aggregates of a .vmap. <paramref name="attributes"/> answers a
    /// material's attributes; <paramref name="nodeMin"/> is BuildNode's
    /// closing bounds minimum (x, y), the Morton origin.
    /// </summary>
    public static List<Aggregate> FromWorld(DmxBinary.Document doc, GameContent content, Func<string, MeshEntryFlags.IAttributes> attributes,
                                            Vector2 nodeMin, List<string>? notes = null, List<StandaloneProp>? standalone = null)
    {
        var (_, entities) = MapMeshes.ReadWithEntities(doc);
        var copyIds = NodePropEntries.CopyIds(doc, content);
        var byModel = new SortedDictionary<string, List<Placed>>(StringComparer.Ordinal);
        void Add(Placed p)
        {
            if (!byModel.TryGetValue(p.Model, out var list))
                byModel[p.Model] = list = [];
            list.Add(p);
        }
        foreach (var ent in entities)
        {
            if (ent.Hidden)
                continue;
            var keys = ent.Element.Get<DmxBinary.Element>("entity_properties");
            var id = ent.Through.Count == 0 ? ent.Element.GetValue<int>("nodeID") ?? -1 : copyIds.GetValueOrDefault(NodePropEntries.Key(ent.Element, ent.Through.Select(e => e.GetValue<int>("nodeID") ?? -1)), -1);
            if (ent.Element.Type == "CMapSmartProp")
            {
                foreach (var p in SmartPropChildren(ent, keys, id, content, notes))
                    Add(p);
                continue;
            }
            if (keys?.Get<string>("classname") != "prop_static" || NodePropEntries.Baked(ent.Element, keys))
                continue;
            var prop = Physics.WorldCollision.PropOf(ent);
            Add(new Placed(keys.Get<string>("model") ?? "", keys, PropTransform.Matrix(prop.Origin, prop.Angles, prop.Scales), id, keys.Get<string>("skin")));
        }
        var meshes = new List<Mesh>();
        var meshIndex = new Dictionary<(string, int, string), int>();
        var joined = new List<Entry>();
        var propCount = 0;
        // Per model, in model order: the props left standalone, and the ones joined.
        var leftOver = new List<(StandaloneProp Prop, bool Joined)>();
        foreach (var (modelName, props) in byModel)
        {
            if (content.LoadedModel(modelName + "_c") is not { } model)
            {
                notes?.Add($"prop model {modelName} not found");
                if (standalone != null)
                    foreach (var p in props)
                        leftOver.Add((new StandaloneProp(modelName, p.Keys, p.Matrix, p.NodeId, p.Skin, null), false));
                continue;
            }
            StandaloneProp Standalone(Placed p)
            {
                var lo = new Vector3(float.MaxValue);
                var hi = new Vector3(float.MinValue);
                foreach (var c in Calls(model, content))
                    (lo, hi) = Place(p.Matrix, c.Lo, c.Hi, lo, hi);
                return new StandaloneProp(modelName, p.Keys, p.Matrix, p.NodeId, p.Skin, (lo, hi));
            }
            var entries = new List<Entry>();
            var all = true;
            foreach (var (_, keys, matrix, id, skin) in props.Where(p => Candidate(p.Keys)))
            {
                var remap = NodePropEntries.MaterialGroup(model, skin);
                var materialOverride = keys.Get<string>("materialoverride");
                var record = NodePropEntries.RecordOf(keys);
                var calls = Calls(model, content).ToList();
                var lods = calls.Select(c => c.Lod).Distinct().Count() > 1;
                var propIndex = propCount++;
                // The prop's centre: each loaded mesh's bounds placed by its matrix (Matrix3x4_TransformAABB), unioned.
                var lo = new Vector3(float.MaxValue);
                var hi = new Vector3(float.MinValue);
                foreach (var c in calls)
                    (lo, hi) = Place(matrix, c.Lo, c.Hi, lo, hi);
                var centre = (lo + hi) * 0.5f;
                var switchDistances = model.Data.GetArray<double>("m_lodGroupSwitchDistances")?.Select(x => (float)x).ToArray() ?? [];
                for (var call = 0; call < calls.Count; call++)
                {
                    var c = calls[call];
                    var material = !string.IsNullOrEmpty(materialOverride) ? materialOverride : remap.GetValueOrDefault(c.Material, c.Material);
                    var flags = MeshEntryFlags.Compute(attributes(material), record);
                    var header = NodeEntryHeader.Prop(keys, id, modelName, record, attributes(material));
                    var input = new MeshLists.Input(flags.Flags, header.ObjectFlags, 0, header.FadeMax, false, false);
                    if (!MeshLists.CanAggregate(input))
                        all = false;
                    var key = (modelName, call, material);
                    if (!meshIndex.TryGetValue(key, out var mi))
                    {
                        meshIndex[key] = mi = meshes.Count;
                        meshes.Add(new Mesh(modelName, call, material, c.Vertices, c.Stride, c.Streams, c.Indices, flags.Flags, c.FloatTexcoords, c.Lod));
                    }
                    entries.Add(new Entry(mi, material, matrix, header.Tint, header.ObjectFlags, flags.Flags, id, centre, c.Indices.Length,
                        propIndex, c.Lod, lods) { Switch = switchDistances, MaxScale = MaxScale(matrix), MeshBounds = Place(matrix, c.Lo, c.Hi, new Vector3(float.MaxValue), new Vector3(float.MinValue)) });
                }
            }
            if (all)
                joined.AddRange(entries);
            if (standalone != null)
                foreach (var p in props)
                    leftOver.Add((Standalone(p), Candidate(p.Keys) && all));
        }
        // The streams PrepareForMerge leaves a mesh; it passes over an entry with attribute bit 0 or 11 (& 0x801).
        var keptStreams = new Dictionary<int, List<int>>();
        List<int> Kept(int mesh)
        {
            if (keptStreams.TryGetValue(mesh, out var kept))
                return kept;
            var m = meshes[mesh];
            kept = Enumerable.Range(0, m.Streams.Count).ToList();
            if ((m.Attributes & 0x801) == 0 && NodePropEntries.Inputs(m.Material, content.Read) is { } inputs)
                kept = MaterialStreams.Kept(m.Streams, inputs, false);
            return keptStreams[mesh] = kept;
        }
        // BuildAggregate's sort key of a mesh (the CMesh after PrepareForMerge, Step_SplittingMeshWith's weld at 0
        // and the stream union's relayout: <paramref name="pad"/> zero floats after each vertex).
        var sortKeys = new Dictionary<(int, int), SortKey>();
        SortKey SortKey(int mesh, int pad)
        {
            if (sortKeys.TryGetValue((mesh, pad), out var key))
                return key;
            var m = meshes[mesh];
            var kept = Kept(mesh);
            var stride = kept.Sum(i => m.Streams[i].Count);
            var count = m.Vertices.Length / m.Stride;
            var v = new float[count * stride];
            var streams = new List<Physics.MeshWeld.Stream>();
            var at = 0;
            foreach (var i in kept)
            {
                for (var k = 0; k < count; k++)
                    Array.Copy(m.Vertices, (k * m.Stride) + m.Streams[i].First, v, (k * stride) + at, m.Streams[i].Count);
                streams.Add(m.Streams[i] with { First = at });
                at += m.Streams[i].Count;
            }
            var (welded, _) = Physics.MeshWeld.Weld(v, stride, m.Indices, streams, 0f, true);
            var n = welded.Length / stride;
            var padded = new float[n * (stride + pad)];
            for (var k = 0; k < n; k++)
                Array.Copy(welded, k * stride, padded, k * (stride + pad), stride);
            return sortKeys[(mesh, pad)] = new SortKey((byte)m.Lod, n, System.Runtime.InteropServices.MemoryMarshal.AsBytes(padded.AsSpan()).ToArray());
        }
        var result = new List<Aggregate>();
        var aggregated = joined.Count > 0 && joined.Sum(e => e.Indices) > MinimumIndices;
        standalone?.AddRange(leftOver.Where(x => !x.Joined || !aggregated).Select(x => x.Prop));
        if (!aggregated)
            return result;
        foreach (var bucket in joined.GroupBy(e => e.Material, Tier0Strings.IgnoreCase))
        {
            var sorted = Morton(bucket.ToList(), nodeMin);
            // std::stable_sort by (attribute bits 0x6000000000, mesh).
            sorted = [.. sorted.Select((e, i) => (e, i)).OrderBy(x => x.e.Attributes & 0x6000000000).ThenBy(x => x.e.Mesh).ThenBy(x => x.i).Select(x => x.e)];
            for (var start = 0; start < sorted.Count;)
            {
                var bits = sorted[start].Attributes & 0x6000000000;
                var end = start;
                while (end < sorted.Count && end - start < 0x7ffb && (sorted[end].Attributes & 0x6000000000) == bits)
                    end++;
                var built = Build(bucket.Key, bits, sorted.GetRange(start, end - start), meshes, SortKey, Kept, notes);
                result.Add(built);
                start = end;
            }
        }
        return result;
    }

    // A prop as the node's prop map holds it: model as named, keys, placement, node id, skin.
    private sealed record Placed(string Model, DmxBinary.Element Keys, float[] Matrix, int NodeId, string? Skin);

    /// <summary>
    /// A smart prop's models as the world's props take them (<see cref="SmartPropChildren"/>): model,
    /// the keys that stand in for an entity's, the placement and the material group as the skin.
    /// </summary>
    public static IEnumerable<(string Model, DmxBinary.Element Keys, float[] Matrix, string? Skin)> SmartPropModels(
        MapMeshes.EntityNode entity, GameContent content, List<string>? notes = null)
        => SmartPropChildren(entity, entity.Element.Get<DmxBinary.Element>("entity_properties"), entity.Element.GetValue<int>("nodeID") ?? -1, content, notes)
            .Select(p => (p.Model, p.Keys, p.Matrix, p.Skin));

    // A smart prop's models (CMapSmartProp, evaluated as the collision does),
    // each a prop_static with the node's keys and the element's material
    // group as its skin; non-uniformly scaled ones are baked instead (not
    // measured on a smart prop).
    private static IEnumerable<Placed> SmartPropChildren(MapMeshes.EntityNode entity, DmxBinary.Element? keys, int id, GameContent content, List<string>? notes)
    {
        var e = entity.Element;
        var file = e.Get<string>("smartPropFilename") ?? "";
        // A smart prop node has no entity_properties: its own keys stand in.
        keys ??= e;
        if (content.SmartProp(file) is not { } definition)
        {
            notes?.Add($"smart prop {id} ({file}): no definition");
            yield break;
        }
        var (configuration, parameters) = SmartPropEvaluator.NodeData(e);
        var nodeWorld = MapMeshes.Local(e);
        if (entity.Through.Count > 0)
            nodeWorld = MapMeshes.Concat(entity.Path, nodeWorld);
        var node = SmartPropEvaluator.NodeTransform(nodeWorld);
        List<SmartPropEvaluator.Placement> placements;
        try
        {
            placements = SmartPropEvaluator.Evaluate(definition, configuration, parameters, node);
        }
        catch (NotSupportedException ex)
        {
            notes?.Add($"smart prop {id} ({file}): {ex.Message}");
            yield break;
        }
        foreach (var placement in placements)
        {
            var (origin, angles, scales) = SmartPropEvaluator.PropPlacement(node, placement);
            var magnitudes = new[] { MathF.Abs(scales.X), MathF.Abs(scales.Y), MathF.Abs(scales.Z) };
            if (magnitudes.Max() - magnitudes.Min() > 0.0001f)
                continue;
            yield return new Placed(placement.Model, keys, PropTransform.Matrix(origin, angles, scales), id, placement.MaterialGroup);
        }
    }

    /// <summary>18026d7a0's per-prop test, the keys it reads (UseStaticEnvMapForObjectsWithLightingOrigin on).</summary>
    public static bool Candidate(DmxBinary.Element keys)
    {
        float Number(string name) => float.TryParse(keys.Get<string>(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0f;
        var lod = int.TryParse(keys.Get<string>("lodlevel"), out var l) ? l : -1;
        return lod == -1 && !NodePropEntries.Flag(keys.Get<string>("renderwithdynamic")) && Number("fademindist") <= 0f && Number("fademaxdist") <= 0f
            && string.IsNullOrEmpty(keys.Get<string>("lightingorigin"));
    }

    /// <summary>
    /// WRBMeshList_SortMorton's key (180274be0): the centre less the node's
    /// minimum, truncated, over 16, 16 bits each, x in the even bits and y in
    /// the odd ones.
    /// </summary>
    public static uint MortonKey(Vector3 centre, Vector2 nodeMin)
    {
        static uint Spread(long value)
        {
            var v = (uint)(value >> 4) & 0xffff;
            v = (v << 8 ^ v) & 0xff00ff;
            v = (v << 4 ^ v) & 0xf0f0f0f;
            v = (v * 4 ^ v) & 0x33333333;
            return (v * 2 ^ v) & 0x55555555;
        }
        return Spread((long)(centre.X - nodeMin.X)) + (Spread((long)(centre.Y - nodeMin.Y)) * 2);
    }

    private static List<Entry> Morton(List<Entry> entries, Vector2 nodeMin)
    {
        var keyed = entries.Select((e, i) => (Key: MortonKey(e.Centre, nodeMin), Index: i)).ToArray();
        MsvcSort.Sort(keyed, (a, b) => a.Key < b.Key);
        return [.. keyed.Select(k => entries[k.Index])];
    }

    /// <summary>
    /// A mesh's place in WRB_BuildAggregate's sorts (180296ed0 on the meshes,
    /// 180296c80 on the entries, comparator as 1802a0ff0): CMesh byte +0x58
    /// ascending (the embedded mesh's LOD mask, 18174e370, which
    /// WRB_LoadPropMeshes writes for BuildPropAggregates' LOD -1), then vertex
    /// count descending, then memcmp of count x stride floats.
    /// </summary>
    private sealed record SortKey(byte Lod, int Count, byte[] Bytes)
    {
        public bool Before(SortKey b)
        {
            if (Lod != b.Lod)
                return Lod < b.Lod;
            if (Count != b.Count)
                return b.Count <= Count;
            var n = Math.Min(Bytes.Length, b.Bytes.Length);
            return Bytes.AsSpan(0, n).SequenceCompareTo(b.Bytes.AsSpan(0, n)) < 0;
        }
    }

    // A kept stream's identity in the union: name (case-blind), index (its place among the mesh's streams of
    // that name, WRB_LoadPropMeshes' +0x10) and float count. +0x1d is the run's (KeepTexcoordPrecision).
    private readonly record struct StreamKey(string Name, int Index, int Count);

    private static List<(StreamKey Key, Physics.MeshWeld.Stream Stream)> KeptKeys(Mesh m, List<int> kept)
        => [.. kept.Select(i => (new StreamKey(m.Streams[i].Name.ToLowerInvariant(), m.Streams.Take(i).Count(s => Tier0Strings.EqualsIgnoreCase(s.Name, m.Streams[i].Name)),
                                                m.Streams[i].Count), m.Streams[i]))];

    /// <summary>
    /// WRB_BuildAggregate's stream union (1802619c0, after Step_SplittingMeshWith, before the sorts;
    /// ledger 246): over the run's meshes, each one's streams in order, a stream is new when no earlier one
    /// has its name (case-blind), index and float count. Every mesh not already in that layout is relaid
    /// out (1812e4040): the union's streams in order, each filled from the mesh's stream of the same name,
    /// index and count, else zeros. Returns, per mesh, the union streams it lacks (appended as zeros).
    /// Valve's mesh list is in pointer-hash order; ours is the run's: a union whose order depends on it,
    /// or a mesh whose own streams are not in the union's order, is a note.
    /// </summary>
    private static Dictionary<int, List<Physics.MeshWeld.Stream>> StreamUnion(string material, int[] unique, List<Mesh> meshes, Func<int, List<int>> kept,
                                                                              List<string>? notes)
    {
        List<StreamKey> Union(IEnumerable<int> order)
        {
            var union = new List<StreamKey>();
            foreach (var mesh in order)
                foreach (var (key, _) in KeptKeys(meshes[mesh], kept(mesh)))
                    if (!union.Contains(key))
                        union.Add(key);
            return union;
        }
        var forward = Union(unique);
        var source = new Dictionary<StreamKey, Physics.MeshWeld.Stream>();
        foreach (var mesh in unique)
            foreach (var (key, stream) in KeptKeys(meshes[mesh], kept(mesh)))
                source.TryAdd(key, stream);
        if (!forward.SequenceEqual(Union(unique.Reverse())))
            notes?.Add($"prop aggregate {material}: the stream union's order depends on Valve's pointer-hash mesh order");
        var missing = new Dictionary<int, List<Physics.MeshWeld.Stream>>();
        foreach (var mesh in unique)
        {
            var own = KeptKeys(meshes[mesh], kept(mesh)).Select(x => x.Key).ToList();
            var lacks = forward.Where(k => !own.Contains(k)).ToList();
            if (!own.Concat(lacks).SequenceEqual(forward))
                notes?.Add($"prop aggregate {material}: {meshes[mesh].Model} draw {meshes[mesh].Call}'s streams are out of the union's order (not modelled)");
            missing[mesh] = [.. lacks.Select(k => source[k])];
        }
        return missing;
    }

    private static Aggregate Build(string material, ulong bits, List<Entry> run, List<Mesh> meshes, Func<int, int, SortKey> sortKeyPadded, Func<int, List<int>> kept,
                                   List<string>? notes)
    {
        var unique = run.Select(e => e.Mesh).Distinct().ToArray();
        var missing = StreamUnion(material, unique, meshes, kept, notes);
        SortKey sortKey(int mesh) => sortKeyPadded(mesh, missing[mesh].Sum(s => s.Count));
        var setups = new Dictionary<int, int>();
        var lodSetups = new List<LodSetup>();
        foreach (var e in run)
            if (e.Lods && !setups.ContainsKey(e.Prop))
            {
                setups[e.Prop] = setups.Count;
                lodSetups.Add(new LodSetup(e.Centre, e.MaxScale, e.Switch));
            }
        // The meshes (std::sort, 180296ed0) and the entries (std::sort, 180296c80) by their CMesh: each
        // mesh one draw, its fragments in the entries' order.
        MsvcSort.Sort(unique, (a, b) => sortKey(a).Before(sortKey(b)));
        var ordered = run.ToArray();
        MsvcSort.Sort(ordered, (a, b) => sortKey(a.Mesh).Before(sortKey(b.Mesh)));
        var draws = new List<Draw>();
        var drawOf = new Dictionary<int, int>();
        foreach (var mesh in unique)
        {
            var m = meshes[mesh];
            drawOf[mesh] = draws.Count;
            var cone = NodeDraw.ConeWeight((m.Attributes & 0x100000000) != 0);
            var (vertices, stride, streams) = (m.Vertices, m.Stride, m.Streams);
            if (missing[mesh].Count > 0)
            {
                // The union's relayout: the streams the mesh lacks after its own, as zeros.
                var pad = missing[mesh].Sum(s => s.Count);
                stride = m.Stride + pad;
                var count = m.Vertices.Length / m.Stride;
                vertices = new float[count * stride];
                for (var k = 0; k < count; k++)
                    Array.Copy(m.Vertices, k * m.Stride, vertices, k * stride, m.Stride);
                var added = new List<Physics.MeshWeld.Stream>(m.Streams);
                var at = m.Stride;
                foreach (var s in missing[mesh])
                {
                    added.Add(s with { First = at });
                    at += s.Count;
                }
                streams = added;
            }
            draws.Add(new Draw(m.Model, m.Call, m.Material,
                NodeDraw.Build(vertices, stride, 0, m.Indices, streams, true, cone)) { Stride = stride, Streams = streams, Attributes = m.Attributes });
        }
        var fragments = new List<Fragment>();
        foreach (var e in ordered)
            // LodMask is the LOD group mask meshsystem gives the mesh.
            fragments.Add(new Fragment(drawOf[e.Mesh], e.Matrix, e.Tint, e.ObjectFlags, e.Attributes, e.NodeId, e.Centre, e.Lods ? e.LodMask : 0,
                                       e.Lods ? setups[e.Prop] : -1) { MeshBounds = e.MeshBounds });
        // The run's texcoord precision (inlined 180258310, then 1802f6840).
        float Max(int slot) => draws.SelectMany(d => Enumerable.Range(0, d.Geometry.VertexCount).Select(v =>
            MathF.Max(MathF.Abs(d.Geometry.Vertices[(v * d.Stride) + slot]), MathF.Abs(d.Geometry.Vertices[(v * d.Stride) + slot + 1])))).DefaultIfEmpty(0f).Max();
        var flagged = NodeDraw.RunPrecise([.. run.Select(e => (meshes[e.Mesh].FloatTexcoords, 0f)), (false, Max(10)), (false, Max(16))]);
        string Format(int slot) => NodeDraw.TexcoordFormat(flagged, [Max(slot)]);
        return new Aggregate(material, bits, draws, fragments, [Format(10), Format(16)]) { LodSetups = lodSetups };
    }

    // The matrix's largest axis length (m_fMaxObjectScale).
    private static float MaxScale(float[] m)
    {
        float Axis(int c) => MathF.Sqrt((m[c] * m[c]) + (m[4 + c] * m[4 + c]) + (m[8 + c] * m[8 + c]));
        return MathF.Max(Axis(0), MathF.Max(Axis(1), Axis(2)));
    }

    // A model's draw calls, every LOD: material, unpacked vertices (model space) from the lowest referenced vertex, rebased indices, bounds, LOD mask.
    private static IEnumerable<(string Material, float[] Vertices, int Stride, IReadOnlyList<Physics.MeshWeld.Stream> Streams, int[] Indices, Vector3 Lo, Vector3 Hi, int Lod, bool FloatTexcoords)> Calls(
        ValveResourceFormat.ResourceTypes.Model model, GameContent content)
    {
        foreach (var (mesh, _, _, lod) in model.GetEmbeddedMeshesAndLoD())
        {
            foreach (var sceneObject in mesh.Data.GetArray("m_sceneObjects"))
                foreach (var call in sceneObject.GetArray("m_drawCalls"))
                {
                    var vb = mesh.VBIB.VertexBuffers[call.GetArray("m_vertexBuffers")[0].GetInt32Property("m_hBuffer")];
                    var ib = mesh.VBIB.IndexBuffers[call.GetSubCollection("m_indexBuffer").GetInt32Property("m_hBuffer")];
                    int Index(int i) => ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, i * 2) : BitConverter.ToInt32(ib.Data, i * 4);
                    var start = call.GetInt32Property("m_nStartIndex");
                    var count = call.GetInt32Property("m_nIndexCount");
                    var bas = call.GetInt32Property("m_nBaseVertex");
                    var lo = Enumerable.Range(start, count).Min(Index) + bas;
                    var vertexCount = Enumerable.Range(start, count).Max(Index) + bas - lo + 1;
                    var own = call.GetStringProperty("m_material") ?? "";
                    var loaded = NodePropEntries.Load(vb, lo, vertexCount, NodePropEntries.Attributes(own, content.Read));
                    var (vertices, stride) = (loaded.Vertices, loaded.Stride);
                    var min = new Vector3(float.MaxValue);
                    var max = new Vector3(float.MinValue);
                    for (var v = 0; v < vertexCount; v++)
                    {
                        var p = new Vector3(vertices[v * stride], vertices[(v * stride) + 1], vertices[(v * stride) + 2]);
                        min = Vector3.Min(min, p);
                        max = Vector3.Max(max, p);
                    }
                    // A float texcoord in the model is what WRB_LoadPropMeshes' geometry flag (+0x66) marks: kept float.
                    var floatTexcoords = vb.InputLayoutFields.Any(f => f.SemanticName == "TEXCOORD" && f.Format.ToString() == "R32G32_FLOAT");
                    yield return (own, vertices, stride, loaded.Streams, [.. Enumerable.Range(start, count).Select(i => Index(i) + bas - lo)], min, max, (int)lod, floatTexcoords);
                }
        }
    }

    // A box's eight corners through the matrix, into the running bounds.
    private static (Vector3, Vector3) Place(float[] m, Vector3 lo, Vector3 hi, Vector3 min, Vector3 max)
    {
        for (var c = 0; c < 8; c++)
        {
            var p = new Vector3((c & 1) != 0 ? hi.X : lo.X, (c & 2) != 0 ? hi.Y : lo.Y, (c & 4) != 0 ? hi.Z : lo.Z);
            var w = new Vector3((m[0] * p.X) + (m[1] * p.Y) + (m[2] * p.Z) + m[3], (m[4] * p.X) + (m[5] * p.Y) + (m[6] * p.Z) + m[7], (m[8] * p.X) + (m[9] * p.Y) + (m[10] * p.Z) + m[11]);
            min = Vector3.Min(min, w);
            max = Vector3.Max(max, w);
        }
        return (min, max);
    }
}
