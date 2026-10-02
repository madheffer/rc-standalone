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
/// <item>Entries bucketed by material; when any joined and their vertex
/// total passes <see cref="MinimumVertices"/>, each bucket is put in Morton
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
    /// <summary>The summed size the joined entries must pass (0x35f; summed from CMesh +0x24, taken as its vertex count, not read).</summary>
    public const int MinimumVertices = 0x35f;

    /// <summary>A draw: one prop mesh (model, draw call, material) built as an aggregate draw in model space.</summary>
    public sealed record Draw(string Model, int Call, string Material, NodeDraw.Result Geometry);

    /// <summary>One placed instance of a draw: the prop's matrix (3x4 row-major), tint and entry flags.</summary>
    public sealed record Fragment(int Draw, float[] Matrix, Vector4 Tint, uint ObjectFlags, ulong Attributes, int NodeId, Vector3 Centre, int LodMask, int LodSetup);

    /// <summary>
    /// An aggregate model: its material, the run's attribute bits, draws and
    /// fragments in order, and its first and second texcoord's vertex format
    /// (GROUND_TRUTH 44): R32G32_FLOAT when the run is flagged (a source draw
    /// with float texcoords, or a value past 16), else R16G16_SNORM when
    /// every value lies in [-1, 1], else R16G16_FLOAT.
    /// </summary>
    public sealed record Aggregate(string Material, ulong AttributeBits, IReadOnlyList<Draw> Draws, IReadOnlyList<Fragment> Fragments,
                                   IReadOnlyList<string> TexcoordFormats);

    // An entry before bucketing: its mesh (index into the mesh list) and fields.
    private sealed record Entry(int Mesh, string Material, float[] Matrix, Vector4 Tint, uint ObjectFlags, ulong Attributes, int NodeId, Vector3 Centre, int Vertices,
                                int Prop, int LodMask, bool Lods);

    // A mesh: a model's draw call under its final material, unpacked in model space.
    private sealed record Mesh(string Model, int Call, string Material, float[] Vertices, int[] Indices, bool Color, ulong Attributes, bool FloatTexcoords);

    /// <summary>
    /// The aggregates of a .vmap. <paramref name="attributes"/> answers a
    /// material's attributes; <paramref name="nodeMin"/> is BuildNode's
    /// closing bounds minimum (x, y), the Morton origin.
    /// </summary>
    public static List<Aggregate> FromWorld(DmxBinary.Document doc, GameContent content, Func<string, MeshEntryFlags.IAttributes> attributes,
                                            Vector2 nodeMin, List<string>? notes = null)
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
        foreach (var (modelName, props) in byModel)
        {
            if (content.LoadedModel(modelName + "_c") is not { } model)
            {
                notes?.Add($"prop model {modelName} not found");
                continue;
            }
            var entries = new List<Entry>();
            var all = true;
            foreach (var (_, keys, matrix, id, skin) in props.Where(p => Candidate(p.Keys)))
            {
                var remap = NodePropEntries.MaterialGroup(model, skin);
                var materialOverride = keys.Get<string>("materialoverride");
                var record = NodePropEntries.RecordOf(keys);
                var calls = Calls(model).ToList();
                var lods = calls.Select(c => c.Lod).Distinct().Count() > 1;
                var propIndex = propCount++;
                // The prop's centre: each loaded mesh's bounds placed by its matrix (Matrix3x4_TransformAABB), unioned.
                var lo = new Vector3(float.MaxValue);
                var hi = new Vector3(float.MinValue);
                foreach (var c in calls)
                    (lo, hi) = Place(matrix, c.Lo, c.Hi, lo, hi);
                var centre = (lo + hi) * 0.5f;
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
                        meshes.Add(new Mesh(modelName, call, material, c.Vertices, c.Indices, c.Color, flags.Flags, c.FloatTexcoords));
                    }
                    entries.Add(new Entry(mi, material, matrix, header.Tint, header.ObjectFlags, flags.Flags, id, centre, c.Vertices.Length / NodePropEntries.Stride,
                        propIndex, c.Lod, lods));
                }
            }
            if (all)
                joined.AddRange(entries);
        }
        var result = new List<Aggregate>();
        if (joined.Count == 0 || joined.Sum(e => e.Vertices) <= MinimumVertices)
            return result;
        foreach (var bucket in joined.GroupBy(e => e.Material, StringComparer.OrdinalIgnoreCase))
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
                result.Add(Build(bucket.Key, bits, sorted.GetRange(start, end - start), meshes));
                start = end;
            }
        }
        return result;
    }

    // A prop as the node's prop map holds it: model as named, keys, placement, node id, skin.
    private sealed record Placed(string Model, DmxBinary.Element Keys, float[] Matrix, int NodeId, string? Skin);

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

    private static Aggregate Build(string material, ulong bits, List<Entry> run, List<Mesh> meshes)
    {
        var draws = new List<Draw>();
        var drawOf = new Dictionary<int, int>();
        var fragments = new List<Fragment>();
        var setups = new Dictionary<int, int>();
        foreach (var e in run)
        {
            if (!drawOf.TryGetValue(e.Mesh, out var d))
            {
                var m = meshes[e.Mesh];
                drawOf[e.Mesh] = d = draws.Count;
                var cone = NodeDraw.ConeWeight((m.Attributes & 0x100000000) != 0);
                draws.Add(new Draw(m.Model, m.Call, m.Material,
                    NodeDraw.Build(m.Vertices, NodePropEntries.Stride, 0, m.Indices, NodePropEntries.Streams(m.Color), true, cone)));
            }
            var setup = -1;
            if (e.Lods && !setups.TryGetValue(e.Prop, out setup))
                setups[e.Prop] = setup = setups.Count;
            fragments.Add(new Fragment(d, e.Matrix, e.Tint, e.ObjectFlags, e.Attributes, e.NodeId, e.Centre, e.Lods ? e.LodMask : 0, setup));
        }
        // The aggregate's meshes list each draw's fragments together, by draw.
        fragments = [.. fragments.Select((f, i) => (f, i)).OrderBy(x => x.f.Draw).ThenBy(x => x.i).Select(x => x.f)];
        // The run's texcoord precision (inlined 180258310, then 1802f6840).
        float Max(int slot) => draws.SelectMany(d => Enumerable.Range(0, d.Geometry.VertexCount).Select(v =>
            MathF.Max(MathF.Abs(d.Geometry.Vertices[(v * NodePropEntries.Stride) + slot]), MathF.Abs(d.Geometry.Vertices[(v * NodePropEntries.Stride) + slot + 1])))).DefaultIfEmpty(0f).Max();
        var flagged = run.Any(e => meshes[e.Mesh].FloatTexcoords) || Max(10) > 16f || Max(16) > 16f;
        string Format(int slot) => flagged ? "R32G32_FLOAT" : Max(slot) <= 1f ? "R16G16_SNORM" : "R16G16_FLOAT";
        return new Aggregate(material, bits, draws, fragments, [Format(10), Format(16)]);
    }

    // A model's draw calls, every LOD: material, unpacked vertices (model space) from the lowest referenced vertex, rebased indices, bounds, LOD mask.
    private static IEnumerable<(string Material, float[] Vertices, int[] Indices, bool Color, Vector3 Lo, Vector3 Hi, int Lod, bool FloatTexcoords)> Calls(ValveResourceFormat.ResourceTypes.Model model)
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
                    var (vertices, color) = NodePropEntries.Vertices(vb, lo, vertexCount);
                    var min = new Vector3(float.MaxValue);
                    var max = new Vector3(float.MinValue);
                    for (var v = 0; v < vertexCount; v++)
                    {
                        var p = new Vector3(vertices[v * NodePropEntries.Stride], vertices[(v * NodePropEntries.Stride) + 1], vertices[(v * NodePropEntries.Stride) + 2]);
                        min = Vector3.Min(min, p);
                        max = Vector3.Max(max, p);
                    }
                    // A float texcoord in the model is what WRB_LoadPropMeshes' geometry flag (+0x66) marks: kept float.
                    var floatTexcoords = vb.InputLayoutFields.Any(f => f.SemanticName == "TEXCOORD" && f.Format.ToString() == "R32G32_FLOAT");
                    yield return (call.GetStringProperty("m_material") ?? "", vertices, [.. Enumerable.Range(start, count).Select(i => Index(i) + bas - lo)], color, min, max, (int)lod, floatTexcoords);
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
