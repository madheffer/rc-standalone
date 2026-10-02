using System.Numerics;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler.Maps;

/// <summary>
/// The node entries WRBNode_AddStaticProps bakes from prop_static entities,
/// after the world's (measured on all 102 atixref prop entries: order,
/// ids, materials, every stream and the indices).
/// <list type="bullet">
/// <item>A prop is baked with baketoworld or (with gameinfo's
/// BakePropsWithNonUniformScale) scales whose largest and smallest
/// magnitude differ by more than 0.0001. The materialoverride key is not
/// the record's +0x40 override list: atixref's 14 props with one are not
/// baked. The clutter, +0x40, deformer, extra-stream and +0x17c triggers
/// are not ported.</item>
/// <item>Order: by model path, then the props of a model last to first in
/// the walk, then each prop's draw calls in mesh, scene object and draw
/// call order.</item>
/// <item>An entry's id is the prop's node id; an instance copy's is the
/// copy's (<see cref="MapInstances.Expand"/>).</item>
/// <item>Vertices: the draw call's, from its lowest referenced vertex, as
/// meshsystem unpacks them (<see cref="PackedNormals"/>), placed by
/// <see cref="PropTransform"/>; the indices rebased. Streams: position,
/// normal, tangent, texcoord, VertexPaintTintColor (COLOR0 / 255) or a
/// zero color, a zero second texcoord.</item>
/// </list>
/// </summary>
internal static class NodePropEntries
{
    public static List<NodeMeshEntries.Entry> FromWorld(DmxBinary.Document doc, GameContent content, List<string>? notes = null)
    {
        var (_, entities) = MapMeshes.ReadWithEntities(doc);
        var copyIds = CopyIds(doc, content);
        var baked = new List<(MapMeshes.EntityNode Node, int Id, string Model, int Walk)>();
        for (var walk = 0; walk < entities.Count; walk++)
        {
            var ent = entities[walk];
            var keys = ent.Element.Get<DmxBinary.Element>("entity_properties");
            if (ent.Hidden || keys?.Get<string>("classname") != "prop_static")
                continue;
            var scales = ent.Element.GetValue<Vector3>("scales") ?? Vector3.One;
            var magnitudes = new[] { MathF.Abs(scales.X), MathF.Abs(scales.Y), MathF.Abs(scales.Z) };
            var bake = Flag(keys.Get<string>("baketoworld")) || magnitudes.Max() - magnitudes.Min() > 0.0001f;
            if (!bake)
                continue;
            var id = ent.Through.Count == 0 ? ent.Element.GetValue<int>("nodeID") ?? -1 : copyIds.GetValueOrDefault(Key(ent.Element, ent.Through), -1);
            baked.Add((ent, id, keys.Get<string>("model") ?? "", walk));
        }
        var entries = new List<NodeMeshEntries.Entry>();
        foreach (var group in baked.GroupBy(b => b.Model, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            foreach (var (node, id, model, _) in group.OrderByDescending(b => b.Walk))
                entries.AddRange(Entries(node, id, model, content, notes));
        return entries;
    }

    private static IEnumerable<NodeMeshEntries.Entry> Entries(MapMeshes.EntityNode node, int id, string modelName, GameContent content, List<string>? notes)
    {
        var keys = node.Element.Get<DmxBinary.Element>("entity_properties")!;
        if (content.LoadedModel(modelName + "_c") is not { } model)
        {
            notes?.Add($"prop {id}: model {modelName} not found");
            yield break;
        }
        if (!string.IsNullOrEmpty(keys.Get<string>("materialoverride")))
            notes?.Add($"prop {id}: materialoverride is not ported");
        var remap = MaterialGroup(model, keys.Get<string>("skin"));
        var lodLevel = int.TryParse(keys.Get<string>("lodlevel"), out var l) ? l : -1;
        var lodMask = lodLevel == -1 ? 0xff : 1 << lodLevel;
        var prop = Physics.WorldCollision.PropOf(node);
        var scales = node.Element.GetValue<Vector3>("scales") ?? Vector3.One;
        var matrix = PropTransform.Matrix(prop.Origin, prop.Angles, scales);
        foreach (var (mesh, _, _, lod) in model.GetEmbeddedMeshesAndLoD())
        {
            if ((lod & lodMask) == 0)
                continue;
            var vbib = mesh.VBIB;
            foreach (var sceneObject in mesh.Data.GetArray("m_sceneObjects"))
                foreach (var call in sceneObject.GetArray("m_drawCalls"))
                {
                    var vb = vbib.VertexBuffers[call.GetArray("m_vertexBuffers")[0].GetInt32Property("m_hBuffer")];
                    var ib = vbib.IndexBuffers[call.GetSubCollection("m_indexBuffer").GetInt32Property("m_hBuffer")];
                    int Index(int i) => ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, i * 2) : BitConverter.ToInt32(ib.Data, i * 4);
                    var start = call.GetInt32Property("m_nStartIndex");
                    var count = call.GetInt32Property("m_nIndexCount");
                    var bas = call.GetInt32Property("m_nBaseVertex");
                    var lo = Enumerable.Range(start, count).Min(Index) + bas;
                    // The call's vertices: from its lowest referenced vertex to its highest.
                    var vertexCount = Enumerable.Range(start, count).Max(Index) + bas - lo + 1;
                    var material = call.GetStringProperty("m_material") ?? "";
                    material = remap.GetValueOrDefault(material, material);
                    var info = content.Material(material);
                    var flags = PropTransform.Place | PropTransform.Texcoords;
                    if (Path.GetFileNameWithoutExtension(info?.Shader ?? "").Equals("csgo_simple_liquid", StringComparison.OrdinalIgnoreCase))
                        flags = PropTransform.LocalSpace;
                    else if (Path.GetFileNameWithoutExtension(info?.Shader ?? "").Equals("csgo_foliage", StringComparison.OrdinalIgnoreCase))
                        notes?.Add($"prop {id}: csgo_foliage's NeedsLocalSpaceVertices combo rule is not ported");
                    var (vertices, hasColor) = Vertices(vb, lo, vertexCount);
                    PropTransform.Apply(vertices, Stride, 0, 3, 6, 10, 16, matrix, PropTransform.AxesOf(info?.Shader, info?.Params), flags);
                    var indices = Enumerable.Range(start, count).Select(i => Index(i) + bas - lo).ToArray();
                    yield return new NodeMeshEntries.Entry(id, material, Stride, Streams(hasColor), vertices, indices) { Record = RecordOf(keys), Source = keys };
                }
        }
    }

    // The skin key (a group's name, or its index): each material of the first
    // group becomes the same slot of the chosen one (WRBNode_PropMaterials
    // through the prop's +0x188).
    private static Dictionary<string, string> MaterialGroup(ValveResourceFormat.ResourceTypes.Model model, string? skin)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groups = model.Data.GetArray("m_materialGroups");
        if (string.IsNullOrEmpty(skin) || groups == null || groups.Count == 0)
            return map;
        var chosen = groups.FirstOrDefault(g => string.Equals(g.GetStringProperty("m_name"), skin, StringComparison.OrdinalIgnoreCase))
            ?? (int.TryParse(skin, out var i) && i >= 0 && i < groups.Count ? groups[i] : null);
        if (chosen == null)
            return map;
        var from = groups[0].GetArray<string>("m_materials");
        var to = chosen.GetArray<string>("m_materials");
        for (var k = 0; k < Math.Min(from.Length, to.Length); k++)
            map.TryAdd(from[k], to[k]);
        return map;
    }

    // WRBNode_PropEntry's record from the prop record (180245770): the lighting
    // mode is disableshadows (+0x16f), bit 2 donotcollapse or disablemerging
    // (+0x1f0), the fade fademaxdist (+0x164).
    internal static MeshEntryFlags.Record RecordOf(DmxBinary.Element keys)
    {
        var mode = byte.TryParse(keys.Get<string>("disableshadows"), out var d) ? d : (byte)0;
        var merge = Flag(keys.Get<string>("donotcollapse")) || Flag(keys.Get<string>("disablemerging"));
        var fade = float.TryParse(keys.Get<string>("fademaxdist"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0f;
        return new MeshEntryFlags.Record(mode, merge ? 2UL : 0UL, fade, false, null, false);
    }

    private const int Stride = 18;

    private static IReadOnlyList<Physics.MeshWeld.Stream> Streams(bool color) =>
    [
        new("position", 0, 3, false, 0x2a), new("normal", 3, 3, false, 0x2a), new("tangent", 6, 4, false, 0x2b),
        new("texcoord", 10, 2, false, 0x29), new(color ? "VertexPaintTintColor" : "color", 12, 4, false, 0x2b), new("texcoord", 16, 2, false, 0x29),
    ];

    // WRB_LoadPropMeshes' 18 floats a vertex from meshsystem's geometry.
    private static (float[] Vertices, bool Color) Vertices(VBIB.OnDiskBufferData vb, int lo, int count)
    {
        var positions = VBIB.GetVector3AttributeArray(vb, vb.InputLayoutFields.First(f => f.SemanticName == "POSITION"));
        var nf = vb.InputLayoutFields.First(f => f.SemanticName == "NORMAL");
        var tf = vb.InputLayoutFields.First(f => f.SemanticName == "TEXCOORD");
        var cf = vb.InputLayoutFields.FirstOrDefault(f => f.SemanticName == "COLOR");
        var hasColor = cf.SemanticName != null;
        var v = new float[count * Stride];
        for (var k = 0; k < count; k++)
        {
            var at = k * Stride;
            var element = (lo + k) * (int)vb.ElementSizeInBytes;
            var raw = BitConverter.ToUInt32(vb.Data, element + (int)nf.Offset);
            var (n, t) = nf.Format.ToString() == "R32_UINT" ? PackedNormals.DecodeR32(raw) : PackedNormals.DecodeR8G8B8A8(raw);
            var p = positions[lo + k];
            var uv = Texcoord(vb, tf, element);
            float[] one = [p.X, p.Y, p.Z, n.X, n.Y, n.Z, t.X, t.Y, t.Z, t.W, uv.X, uv.Y];
            one.CopyTo(v, at);
            if (hasColor)
                for (var i = 0; i < 4; i++)
                    v[at + 12 + i] = vb.Data[element + (int)cf.Offset + i] / 255f;
        }
        return (v, hasColor);
    }

    // SNORM16 as max(v / 32767, -1), halves as they are, otherwise floats.
    internal static Vector2 Texcoord(VBIB.OnDiskBufferData vb, VBIB.RenderInputLayoutField f, int element)
    {
        var off = element + (int)f.Offset;
        return f.Format.ToString() switch
        {
            "R16G16_SNORM" => new Vector2(MathF.Max(BitConverter.ToInt16(vb.Data, off) / 32767f, -1f), MathF.Max(BitConverter.ToInt16(vb.Data, off + 2) / 32767f, -1f)),
            "R16G16_FLOAT" => new Vector2((float)BitConverter.ToHalf(vb.Data, off), (float)BitConverter.ToHalf(vb.Data, off + 2)),
            _ => new Vector2(BitConverter.ToSingle(vb.Data, off), BitConverter.ToSingle(vb.Data, off + 4)),
        };
    }

    private static bool Flag(string? value) => value is "1" or "true" or "True";

    private static string Key(DmxBinary.Element node, IReadOnlyList<DmxBinary.Element> through)
        => Key(node, through.Select(e => e.GetValue<int>("nodeID") ?? -1));

    internal static string Key(DmxBinary.Element node, IEnumerable<int> instances)
        => string.Join("/", instances) + ":" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(node);

    // The ids MapInstances.Expand gives each copy, by template node and instance path.
    internal static Dictionary<string, int> CopyIds(DmxBinary.Document doc, GameContent content)
    {
        var ids = new Dictionary<string, int>();
        var createdOnLoad = SmartProps.NodesCreatedOnLoad(doc, path => content.SmartProp(path) is { } definition ? SmartProps.LocatorsOf(definition) : 0);
        MapInstances.Expand(doc, MapEntities.From(doc), createdOnLoad, (node, id, through) => ids.TryAdd(Key(node, through), id));
        return ids;
    }
}
