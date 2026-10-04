using Source2.Compiler.Io;
using System.Globalization;
using System.Numerics;
using System.Text;
using ValveKeyValue;
using ValveKeyValue.KeyValues3;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler;

/// <summary>
/// Builds a <c>.vents_c</c>, the entity lump a map ships its entities in.
///
/// <para>The container is the easy half and is already pinned; the work is what
/// goes in it. Hammer writes every key as a string, and the compiled lump does
/// not: each value is typed through the game's FGD, empty values are dropped, and
/// the compile adds an identity of its own. Every rule below was measured against
/// resourcecompiler output rather than inferred - see docs/ENTITIES.md.</para>
/// </summary>
public static partial class EntityLumpAuthor
{
    /// <summary>Format GUID a compiled entity lump's DATA carries.</summary>
    private static readonly KV3ID Format = KV3IDLookup.Get("generic");

    /// <summary>
    /// What the compile puts in front of every entity name when the map asks for
    /// name fixup. The engine resolves it at spawn; the compile just writes it.
    /// </summary>
    private const string NameFixup = "[PR#]";

    /// <summary>Build the lump's DATA tree, with no template pass: every entity
    /// the walk (or <paramref name="emit"/>) ships, in order.</summary>
    /// <param name="entities">Entities in the order they should appear.</param>
    /// <param name="schema">The game's FGD, which types the values.</param>
    /// <param name="name">The lump's own name, e.g. <c>default_ents</c>.</param>
    /// <param name="childLumps">Names of lumps hanging off this one.</param>
    /// <param name="worldName">The map's name, which worldspawn records.</param>
    /// <param name="fixupEntityNames">Whether the map asks for the prefab fixup.</param>
    /// <param name="emit">What this lump carries, in order, each paired with the
    /// walk index it is numbered by. Null is every walked entity in walk order. An
    /// instanced copy is not in the walk at all yet carries its template's
    /// number, which is why the two are separate.</param>
    public static KVObject BuildTree(
        IReadOnlyList<MapEntities.Entity> entities,
        FgdSchema? schema,
        string name = "default_ents",
        IReadOnlyList<string>? childLumps = null,
        string? worldName = null,
        bool fixupEntityNames = false,
        IReadOnlyList<Emission>? emit = null)
    {
        ArgumentNullException.ThrowIfNull(entities);
        var context = Context.For(entities, schema, worldName, fixupEntityNames);
        return Lump(name, childLumps ?? [],
            (emit ?? [.. entities.Select((e, i) => new Emission(e, i))])
                .Where(e => ReachesTheLump(e.Entity, schema) && !e.Entity.Hidden)
                .Select(e => BuildEntity(e.Entity, e.SourceId, context)));
    }

    /// <summary>One entity to write, and the walk index it is numbered by.</summary>
    /// <param name="Entity">What to write. For an instanced copy this is the
    /// template placed by its instance, which is in no walk.</param>
    /// <param name="SourceId">Its compile_source_id. A copy carries its TEMPLATE's,
    /// which is why Valve's atixref repeats 18 ids across 199 entities.</param>
    public sealed record Emission(MapEntities.Entity Entity, int SourceId);

    /// <summary>What every entity of one map is built against.</summary>
    /// <param name="Schema">The game's FGD.</param>
    /// <param name="WorldName">The map's name.</param>
    /// <param name="FixupEntityNames">Whether the map asks for the prefab fixup.</param>
    /// <param name="EntityNames">Every targetname in the map. An output's override
    /// parameter gets the fixup when it NAMES one of them: resourcecompiler says
    /// so while compiling ze_hold_em_p ("Parameter 'humans' corresponds to an
    /// entity target name, but is sent to input 'SetDamageFilter' ... (FGD
    /// Error?)") and writes [PR#]humans regardless.</param>
    public sealed record Context(
        FgdSchema? Schema, string? WorldName, bool FixupEntityNames, IReadOnlySet<string> EntityNames)
    {
        /// <summary>The lighting keys' inputs; null leaves them out.</summary>
        public Lighting? LightingKeys { get; init; }

        public static Context For(IReadOnlyList<MapEntities.Entity> entities, FgdSchema? schema,
                                  string? worldName, bool fixupEntityNames)
            => new(schema, worldName, fixupEntityNames,
                   new HashSet<string>(entities.Select(NameOf).Where(n => n.Length > 0),
                                       Tier0Strings.IgnoreCase));
    }

    /// <summary>A lump's DATA tree around entities already built.</summary>
    public static KVObject Lump(string name, IEnumerable<string> childLumps, IEnumerable<KVObject> entities)
    {
        var root = KVObject.Collection();
        root.Add("m_name", new KVObject(name));
        // Each child is a resource reference, flagged as one: Valve's default_ents
        // carries every m_childLumps entry with the KV3 resource flag.
        var lumps = KVObject.Array();
        foreach (var child in childLumps)
            lumps.Add(new KVObject(child) { Flag = KVFlag.Resource });
        root.Add("m_childLumps", lumps);
        var array = KVObject.Array();
        foreach (var entity in entities)
            array.Add(entity);
        root.Add("m_entityKeyValues", array);
        return root;
    }

    /// <summary>Compile a lump's DATA tree into a <c>.vents_c</c>.</summary>
    public static byte[] Compile(KVObject lump) => Source2ContainerAuthor.AuthorKv3Tree(lump, Format, ".vents");

    /// <summary>Build and compile the lump.</summary>
    public static byte[] Author(
        IReadOnlyList<MapEntities.Entity> entities,
        FgdSchema? schema,
        string name = "default_ents",
        IReadOnlyList<string>? childLumps = null,
        string? worldName = null,
        bool fixupEntityNames = false,
        IReadOnlyList<Emission>? emit = null)
        => Compile(BuildTree(entities, schema, name, childLumps, worldName, fixupEntityNames, emit));

    /// <summary>
    /// Whether a walked node is written to the lump at all.
    ///
    /// <para>Two FGD metadata flags say it is not. <c>static_prop</c> is
    /// prop_static, whose geometry the compile bakes into the world: atixref walks
    /// 3,952 of them and Valve's lump carries none. <c>editor_only</c> is the path
    /// node classes, which hold a path's shape for Hammer and are serialized into
    /// the path rather than spawned. Both still take their number. A node the map
    /// HIDES is a separate case: it is still in the list a point_template searches
    /// (Mako's hidden Baha_Mat_Earth_Physic ships in its template's lump), so it is
    /// dropped only when default_ents is written.</para>
    /// </summary>
    public static bool ReachesTheLump(MapEntities.Entity entity, FgdSchema? schema)
        => entity.ClassName.Length > 0 && !Consumed.Contains(entity.ClassName)
        && (schema is null
        || !(schema.HasFlag(entity.ClassName, "static_prop")
             || schema.HasFlag(entity.ClassName, "editor_only")
             || (schema.IsSolidClass(entity.ClassName) && !entity.HasGeometry && !entity.IsWorld)));

    /// <summary>
    /// Classes the export consumes rather than writes (FUN_180240a60): the
    /// compile reads a visibility_hint into the world's visibility hints, an
    /// info_cull_triangles into its culling, and ships none of these as entities.
    /// Mako places seven visibility_hint and a point_scale_reference_human, and
    /// Valve's lumps carry none of them.
    /// </summary>
    private static readonly HashSet<string> Consumed = new(Tier0Strings.IgnoreCase)
    {
        "point_scale_reference_human", "visibility_hint", "info_cull_triangles",
        "prop_static", "env_world_lighting", "light_irradvolume", "func_deformable_density",
    };

    /// <summary>One entity's tree, as default_ents carries it before any
    /// template pass.</summary>
    public static KVObject BuildEntity(MapEntities.Entity entity, int index, Context context)
    {
        var (schema, worldName, fixupEntityNames, entityNames) = context;
        var values = KVObject.Collection();

        // worldspawn's exporter writes the compile's id before any key
        // (FUN_180fc1560); every other entity writes it after them (FUN_181004020).
        if (entity.IsWorld)
            values.Add("compile_source_id", Integer(index));

        // The node's key table is the source's keys in source order, then every key
        // of the class the source lacks, at its FGD default, in the class's
        // finalized order. The compile writes that table BACKWARDS, which is visible
        // on every entity: trigger_once 83 ships source1_brushmodel_index first and
        // classname last, and worldspawn's steamaudio defaults lead in reverse FGD
        // order. An empty value is typed like any other and ships as "" or a zero.
        var table = KeyTable(entity, schema);
        Preprocess(entity, table, schema, context.LightingKeys);
        if (context.LightingKeys?.LightWrites is { } lightWrites && lightWrites.TryGetValue(entity, out var written))
            foreach (var (key, value) in written)
                Set(table, key, value);
        foreach (var (key, text) in table.AsEnumerable().Reverse())
        {
            var declared = schema?.KeyOf(entity.ClassName, key);
            if (declared?.Type == FgdSchema.FieldType.Kv3)
                continue;
            // A key the schema does not know stays the mapper's string, except that
            // a targetname is a NAME whatever the class is. ze_doom_p2_c_gameplay
            // places four classes the fgd never declares (func_physbox_multiplayer,
            // player_speedmod, prop_door_rotating_checkpoint, ambient_music) and
            // Valve prefixes every one of their names.
            // A cable_dynamic node writes its rendercolor itself, after its path
            // keys (CMapCable::vf217): the tint's red, green and blue as the plain
            // string "%i %i %i", whatever the key held. Measured on probe_cable:
            // tintColor 1 2 3 against a rendercolor key of 12 34 56 ships "1 2 3".
            if (RewritesDirectLight(entity, context.LightingKeys) && key.EqualsAscii("directlight")
                && CNumbers.Atoi(text) != 0)
            {
                values.Add(key, new KVObject(2));
                continue;
            }
            if (entity.Tint is { } tint && key.EqualsAscii("rendercolor")
                && entity.ClassName.EqualsAscii("cable_dynamic"))
            {
                values.Add(key, new KVObject($"{tint[0]} {tint[1]} {tint[2]}"));
                continue;
            }
            values.Add(key, declared is not null
                ? Typed(declared, text, fixupEntityNames, schema)
                : key.EqualsAscii("targetname")
                    ? new KVObject(Fixup(text, fixupEntityNames, schema))
                    : new KVObject(text));
        }

        // Keys the CLASS ships rather than the entity. This is how a point prefab
        // works: counterterrorist_team_intro is an ordinary class whose FGD metadata
        // declares isPointPrefab and the targetMapName to load, so the compile reads
        // them off the schema rather than resolving anything.
        foreach (var (key, value) in schema?.GameKeysOf(entity.ClassName) ?? [])
            if (!values.ContainsKey(key))
                values.Add(key, new KVObject(value));

        // The compile's own identity for the entity: its ordinal in the lump, and
        // the Hammer node it came from. The id is a number and the node is a
        // string, which is Valve's split, not a slip.
        if (!entity.IsWorld)
            values.Add("compile_source_id", Integer(index));

        values.Add("origin", Vector(entity.Origin));
        values.Add("angles", Vector(entity.Angles));
        values.Add("scales", Vector(entity.Scales));
        values.Add("hammerUniqueId", new KVObject(entity.IdPath));
        ExportLighting(entity, values, table, context.LightingKeys);

        // A path's nodes are folded into keys of its own, written after the
        // entity's (FUN_1810b5480).
        foreach (var (key, value) in PathKeys(entity, schema))
            values.Add(key, value);

        // A brush entity's geometry is compiled into a model of its own, and the
        // entity is pointed at it by name. The path is derived, not looked up:
        // Valve's cardtest compile names the model of brush entity 2138
        // "ImpModel" maps/cardtest/entities/impmodel_2138.vmdl.
        if (entity.HasGeometry && worldName is { Length: > 0 })
        {
            var model = new KVObject(BrushModelPath(entity, worldName));
            model.Flag = KVFlag.ResourceName;
            values.Add("model", model);
        }

        if (entity.IsWorld)
        {
            if (worldName is { Length: > 0 })
                values.Add("worldname", new KVObject(worldName));
            values.Add("mapUsageType", new KVObject("standard"));
        }

        // An info_world_layer names its layer's lump (FUN_180240a60): its
        // layerName gains the lump's "world_layer_" prefix where it stands, and the
        // map's name follows every other key.
        if (entity.ClassName.EqualsAscii("info_world_layer"))
        {
            var layerKey = values.Keys.FirstOrDefault(k => k.EqualsAscii("layername"));
            if (layerKey is not null)
                values[layerKey] = new KVObject("world_layer_" + values[layerKey]);
            if (worldName is { Length: > 0 })
                values.Add("worldname", new KVObject(worldName));
        }

        AtlasKeys(entity, values, context.LightingKeys);
        VisClusterKey(entity, values, context.LightingKeys);

        // version is written through the 0/1 path like any other 1: Int64.
        var keyValues = KVObject.Collection();
        keyValues.Add("version", Integer(1));
        keyValues.Add("values", values);
        keyValues.Add("attributes", KVObject.Collection());

        var result = KVObject.Collection();
        result.Add("m_connections", Connections(entity, fixupEntityNames, entityNames, schema));
        result.Add("m_keyValuesData", KVObject.Blob([]));
        result.Add("keyValues3Data", keyValues);
        return result;
    }

    /// <summary>
    /// Apply the prefab name fixup to an entity name the map refers to
    /// (CMapGameDataNode::vf157, FUN_180fefb60, FUN_180dcab50).
    ///
    /// <para>A value is left alone when it is empty, when it starts with one of
    /// <c>!*?@</c> (an engine keyword such as <c>!activator</c>, or a wildcard),
    /// or when it is the name of an FGD class, looked up case-blind: a key that
    /// names a class names no entity.</para>
    /// </summary>
    private static string Fixup(string name, bool enabled, FgdSchema? schema)
        => enabled && name.Length > 0 && name[0] is not ('!' or '*' or '?' or '@')
           && schema?.HasClass(name) != true
            ? NameFixup + name
            : name;

    /// <summary>
    /// The key types the fixup rewrites (the bit mask 0x830006 in
    /// CMapGameDataNode::vf157): target_destination, target_name_or_class,
    /// npcclass, filterclass and pointentityclass. targetname is not among them;
    /// it takes the fixup as the entity's own name (CMapEntityIONode::vf157),
    /// so another target_source key keeps its value.
    /// </summary>
    private static bool TakesFixup(FgdSchema.Key key)
        => key.TypeId is 0x1 or 0x2 or 0x10 or 0x11 or 0x17
           || key.Name.EqualsAscii("targetname");

    /// <summary>
    /// ENTITY_CONNECTION_TARGET_NAME, the target type every connection carries.
    /// The lump stores the enum's value, not its name: 4,543 connections on Mako,
    /// 613 on atixref and 145 on ze_hold_em_p, <c>!activator</c> targets
    /// included, all UInt32 7.
    /// </summary>
    private const uint TargetByName = 7;

    /// <summary>
    /// The outputs, each serialized through EntityIOConnectionData_t's schema
    /// (FUN_1803670b0): four strings, the target type as its enum, the delay as a
    /// float widened to double, the fire count as an int32 (so -1 is Int32 and 1
    /// the KV3 one), and an empty parameter map written as null.
    /// </summary>
    private static KVObject Connections(MapEntities.Entity entity, bool fixupEntityNames,
                                        IReadOnlySet<string> entityNames, FgdSchema? schema)
    {
        var array = KVObject.Array();
        foreach (var c in entity.Connections)
        {
            var o = KVObject.Collection();
            o.Add("m_outputName", new KVObject(c.OutputName));
            o.Add("m_targetType", new KVObject(TargetByName));
            o.Add("m_targetName", new KVObject(Fixup(c.TargetName, fixupEntityNames, schema)));
            o.Add("m_inputName", new KVObject(c.InputName));
            o.Add("m_overrideParam", new KVObject(
                entityNames.Contains(c.OverrideParam) ? Fixup(c.OverrideParam, fixupEntityNames, schema) : c.OverrideParam));
            o.Add("m_flDelay", new KVObject((double)c.Delay));
            o.Add("m_nTimesToFire", Integer(c.TimesToFire));
            o.Add("m_paramMap", KVObject.Null());
            array.Add(o);
        }
        return array;
    }

    /// <summary>
    /// The keys a path writes for its nodes (FUN_1810b5480), in its order:
    /// pathNodes, pathNodeNames when any node is named, the two radius arrays when
    /// any node is off 1.0, closed_loop when the class supports loops, and one
    /// array per node class key that declares a write_to_path_key. The arrays are
    /// KV3 TEXT in string values, not real arrays.
    ///
    /// <para>pathNodeColors is also written when a node's colour is off white, but
    /// it comes from a node getter this port has not traced to its attribute, and
    /// no map measured writes one.</para>
    /// </summary>
    private static IEnumerable<KeyValuePair<string, KVObject>> PathKeys(MapEntities.Entity entity, FgdSchema? schema)
    {
        if (entity.PathNodes is not { } nodes)
            yield break;

        // A closed loop repeats its first node at the end, when the class allows
        // loops and there is more than one node (FUN_1810b7ca0).
        var loops = schema?.HasFlag(entity.ClassName, "supports_loop") == true;
        var rows = new List<string>();
        for (var i = 0; i < nodes.Count; i++)
            rows.Add(Kv3Array(PathNode(entity, nodes, i), 2));
        if (entity.ClosedLoop && loops && nodes.Count > 1)
            rows.Add(rows[0]);
        yield return new("pathNodes", new KVObject(Kv3Rows(rows)));

        var names = string.Concat(nodes.Select((n, i) => (Name: NodeKey(n, "node_name") ?? "", Index: i))
            .Where(n => n.Name.Length > 0).Select(n => $"{n.Index}: {n.Name};"));
        if (names.Length > 0)
            yield return new("pathNodeNames", new KVObject(names));

        var radii = nodes.Select(n => RadiusScales(n, schema)).ToList();
        if (radii.Any(r => r.Radius != 1f || r.Height != 1f))
        {
            yield return new("pathNodeRadiusScales", new KVObject(Kv3Array([.. radii.Select(r => r.Radius)], 1)));
            yield return new("pathNodeRadiusHeightScales", new KVObject(Kv3Array([.. radii.Select(r => r.Height)], 1)));
        }

        if (loops)
            yield return new("closed_loop", Integer(entity.ClosedLoop ? 1 : 0));

        // The node class is the path class's path_node_class, if that is a path node
        // class, and path_node_generic otherwise.
        var nodeClass = schema?.MetadataOf(entity.ClassName, "path_node_class") ?? "path_node_generic";
        if (schema is null || !schema.IsPathNodeClass(nodeClass))
            nodeClass = "path_node_generic";
        foreach (var key in schema?.KeysOf(nodeClass) ?? [])
            if (key.WriteToPathKey is { Length: > 0 } pathKey
                && nodes.Any(n => (NodeKey(n, key.Name) ?? "").Length > 0))
                yield return new(pathKey, new KVObject(Kv3Words(
                    [.. nodes.Select(n => PathValue(key, NodeKey(n, key.Name) ?? ""))])));
    }

    /// <summary>
    /// One node's nine floats: its position relative to the path, then its in and
    /// out tangents (FUN_1810b7ca0, FUN_181110ba0). The path's interpolationType 0
    /// or 1 decides the tangent type of every node; otherwise each node's own does.
    /// A missing neighbour at an end is the node itself.
    /// </summary>
    private static float[] PathNode(MapEntities.Entity path, IReadOnlyList<MapEntities.PathNode> nodes, int i)
    {
        var here = nodes[i].Origin;
        var previous = i > 0 ? nodes[i - 1].Origin : here;
        var next = i < nodes.Count - 1 ? nodes[i + 1].Origin : here;
        int TypeOf(int own) => path.InterpolationType is 0 or 1 ? path.InterpolationType : own;
        // FUN_1810b7ca0: the path's vtable 0xa8, the inverse (18125aba0) of its
        // node matrix (FUN_181255fb0), each node's stored origin through it
        // (Matrix3x4_TransformPoint, 18125d1f0) and its tangents rotated
        // (18125d1b0).
        var frame = Maps.MapMeshes.AngleMatrix(path.Angles);
        (frame[3], frame[7], frame[11]) = (path.Origin.X, path.Origin.Y, path.Origin.Z);
        var toLocal = InvertNodeMatrix(frame);
        var inward = Rotate(toLocal, Tangent(TypeOf(nodes[i].InTangentType), next, here, previous, nodes[i].InTangent));
        var outward = Rotate(toLocal, Tangent(TypeOf(nodes[i].OutTangentType), previous, here, next, nodes[i].OutTangent));
        var local = Maps.MapMeshes.Transform(toLocal, here);
        return [local.X, local.Y, local.Z, inward.X, inward.Y, inward.Z, outward.X, outward.Y, outward.Z];
    }

    /// <summary>
    /// FUN_18125aba0: the rotation transposed, the translation
    /// -((o.z r2 + o.y r1) + o.x r0) per row r of the transpose, and every
    /// entry divided by the first column's squared length when that is 0.001
    /// or more off 1.
    /// </summary>
    private static float[] InvertNodeMatrix(float[] m)
    {
        float[] r = [m[0], m[4], m[8], 0, m[1], m[5], m[9], 0, m[2], m[6], m[10], 0];
        float x = m[3], y = m[7], z = m[11];
        r[3] = -(((z * r[2]) + (y * r[1])) + (x * r[0]));
        r[7] = -(((r[6] * z) + (r[5] * y)) + (x * r[4]));
        r[11] = -(((r[10] * z) + (r[9] * y)) + (x * r[8]));
        var lengthSq = ((m[4] * m[4]) + (m[0] * m[0])) + (m[8] * m[8]);
        if (MathF.Abs(lengthSq - 1f) >= 0.001f)
        {
            var scale = lengthSq != 0f ? 1f / lengthSq : 1f;
            for (var k = 0; k < 12; k++)
                r[k] *= scale;
        }
        return r;
    }

    /// <summary>
    /// A vector through a path's world-to-local rotation, as the path export
    /// applies it to every position and tangent. It is visible even on a path with
    /// no rotation: the zero products it adds turn a tangent's -0 into the 0.0
    /// Valve prints for Mako's cable. Paths with angles have no specimen yet, so
    /// only the zero-rotation case is measured.
    /// </summary>
    private static Vector3 Rotate(float[] m, Vector3 v)
        => new(v.X * m[0] + v.Y * m[1] + v.Z * m[2],
               v.X * m[4] + v.Y * m[5] + v.Z * m[6],
               v.X * m[8] + v.Y * m[9] + v.Z * m[10]);

    /// <summary>
    /// A tangent by its type (FUN_1812806d0), toward <paramref name="toward"/>:
    /// type 0 points at it, type 1 runs from <paramref name="away"/> to it (so a
    /// middle node's handles are parallel), type 2 is the authored direction and
    /// type 3 the authored tangent untouched. The first three are scaled to a third
    /// of the distance to <paramref name="toward"/>.
    ///
    /// <para>The direction is normalised and then scaled rather than divided,
    /// which is visible in the output: a rope leg of (-446, -171, 0) divided by
    /// three is exactly -57 and Valve writes -57.000004. The squared lengths sum
    /// z, then y, then x, as the binary does.</para>
    /// </summary>
    private static Vector3 Tangent(int type, Vector3 away, Vector3 here, Vector3 toward, Vector3 authored)
    {
        if (type == 3)
            return authored;
        var direction = type switch
        {
            0 => Normalise(toward - here),
            1 => Normalise(toward - away),
            2 => Normalise(authored),
            _ => Vector3.Zero,
        };
        var offset = here - toward;
        var third = MathF.Sqrt(offset.Z * offset.Z + offset.Y * offset.Y + offset.X * offset.X) / 3f;
        return new Vector3(direction.X * third, direction.Y * third, direction.Z * third);
    }

    private static Vector3 Normalise(Vector3 v)
    {
        var length = MathF.Sqrt(v.Z * v.Z + v.Y * v.Y + v.X * v.X);
        if (length == 0f)
            return Vector3.Zero;
        var inverse = 1f / length;
        return new Vector3(inverse * v.X, inverse * v.Y, inverse * v.Z);
    }

    /// <summary>
    /// A node's radius and height scale (FUN_181111cc0): 1 and 1 unless its class
    /// declares radius_scale, then that key's value for both, or radius_scale and
    /// radius_scale_height when it declares both. A declared key the node does not
    /// carry reads as 0 (FUN_180f32050).
    /// </summary>
    private static (float Radius, float Height) RadiusScales(MapEntities.PathNode node, FgdSchema? schema)
    {
        var nodeClass = NodeKey(node, "classname") ?? "";
        if (schema?.KeyOf(nodeClass, "radius_scale") is null)
            return (1f, 1f);
        var radius = CNumbers.Atof(NodeKey(node, "radius_scale") ?? "");
        return schema.KeyOf(nodeClass, "radius_scale_height") is null
            ? (radius, radius)
            : (radius, CNumbers.Atof(NodeKey(node, "radius_scale_height") ?? ""));
    }

    /// <summary>One node's value for a write_to_path_key array, by the key's type
    /// (FUN_1810bfd50).</summary>
    private static string PathValue(FgdSchema.Key key, string text) => key.Type switch
    {
        FgdSchema.FieldType.Boolean => ParseBool(text) ? "true" : "false",
        FgdSchema.FieldType.Integer => ((int)CNumbers.Atoi(text)).ToString(CultureInfo.InvariantCulture),
        FgdSchema.FieldType.Float => Number(CNumbers.Atof(text)),
        _ => "\"" + text + "\"",
    };

    private static string? NodeKey(MapEntities.PathNode node, string name)
        => node.Keys.FirstOrDefault(k => k.Key.EqualsAscii(name)).Value;

    /// <summary>
    /// A KV3 array as the compile prints it: on one line up to four entries, and
    /// otherwise wrapped four to a line under one tab per level, every entry
    /// followed by a comma. Measured on ropes of four, five, six and seven nodes.
    /// </summary>
    private static string Kv3Words(IReadOnlyList<string> parts, int depth = 1)
    {
        if (parts.Count <= 4)
            return "[ " + string.Join(", ", parts) + " ]";

        var pad = new string('\t', depth);
        var text = new StringBuilder("[\n");
        for (var i = 0; i < parts.Count; i++)
            text.Append(i % 4 == 0 ? pad : " ").Append(parts[i]).Append(',')
                .Append(i % 4 == 3 || i == parts.Count - 1 ? "\n" : "");
        return text.Append(new string('\t', depth - 1)).Append(']').ToString();
    }

    private static string Kv3Array(IReadOnlyList<float> values, int depth)
        => Kv3Words([.. values.Select(Number)], depth);

    /// <summary>The rows of pathNodes, which is an array of arrays and so always
    /// breaks a line.</summary>
    private static string Kv3Rows(IReadOnlyList<string> rows)
    {
        var text = new StringBuilder("[\n");
        foreach (var row in rows)
            text.Append('\t').Append(row).Append(",\n");
        return text.Append(']').ToString();
    }

    /// <summary>A float as the compile prints it: shortest round trip, and never
    /// bare, so 0 is "0.0" and -446 is "-446.0".</summary>
    private static string Number(float value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        return text.AsSpan().IndexOfAny('.', 'E', 'e') >= 0 ? text : text + ".0";
    }

    /// <summary>An entity's targetname, or empty when it has none.</summary>
    private static string NameOf(MapEntities.Entity entity)
        => entity.Keys.FirstOrDefault(k => k.Key.EqualsAscii("targetname")).Value ?? "";

    /// <summary>Placement keys live on the map node, not in the game keys, and the
    /// compile writes its own; a stale copy in the props would fight it.</summary>
    /// <summary>
    /// Where the compile puts a brush entity's generated model:
    /// <c>maps/&lt;map&gt;/entities/&lt;name&gt;_&lt;node&gt;.vmdl</c>, the name lowercased,
    /// and <c>unnamed</c> when the entity has none.
    /// </summary>
    private static string BrushModelPath(MapEntities.Entity entity, string worldName)
        => BrushModelPath(entity.Keys.FirstOrDefault(k => k.Key.EqualsAscii("targetname")).Value, entity.IdPath.Replace(':', '_'), worldName);

    /// <summary>
    /// The model path rc's late entity export (180240a60) gives a brush entity:
    /// "entities/%s_%s" from the targetname ("unnamed" when it is empty, a
    /// leading "[PR#]" stripped, found by V_stristr_fast) and the id path, then
    /// V_FixupPathCharToUnderscore turns '.' and '+' into '_' (cs_script_demo:
    /// chess.q is chess_q_95.vmdl).
    /// </summary>
    public static string BrushModelPath(string? targetName, string idPath, string worldName)
    {
        var name = string.IsNullOrEmpty(targetName) ? "unnamed" : targetName;
        if (name.StartsWith("[PR#]", StringComparison.OrdinalIgnoreCase))
            name = name[5..];
        var file = $"entities/{name.ToLowerInvariant()}_{idPath}".Replace('.', '_').Replace('+', '_');
        return $"maps/{worldName}/{file}.vmdl";
    }

    private static bool IsPlacement(string key)
        => Placement.Contains(key);

    private static readonly HashSet<string> Placement =
        new(["origin", "angles", "scales"], Tier0Strings.IgnoreCase);

    /// <summary>
    /// A placement as the lump writes it: an array of three numbers, each the
    /// authored 32-bit float widened to double.
    ///
    /// <para>Older compiles wrote these as a fixed six-decimal STRING and the
    /// engine still reads those, so a lump from before the change looks different
    /// without being wrong. This follows the compiler as it is now, measured on a
    /// map compiled here rather than on whatever a shipped map happens to carry.</para>
    /// </summary>
    private static KVObject Vector(Vector3 v)
    {
        var array = KVObject.Array();
        foreach (var component in new[] { v.X, v.Y, v.Z })
            array.Add(new KVObject((double)component));
        return array;
    }

    /// <summary>
    /// An integer at the width resourcecompiler gives it: 0 and 1 keep the KV3
    /// singleton codes, anything that fits signed 32-bit is Int32, and the rest is
    /// Int64. Same rule the KV3 compiler applies elsewhere (docs/RESOURCES.md).
    /// </summary>
    private static KVObject Integer(long value)
        => value is 0 or 1 || value is < int.MinValue or > int.MaxValue
            ? new KVObject(value)
            : new KVObject((int)value);

    /// <summary>
    /// The entity node's key table in the order it was filled: the source's keys
    /// as the source lists them (placement lives on the node, not here), then
    /// every key of the class the source lacks, at its FGD default, in the class's
    /// finalized order.
    /// </summary>
    internal static List<KeyValuePair<string, string>> KeyTable(MapEntities.Entity entity, FgdSchema? schema)
    {
        var table = new List<KeyValuePair<string, string>>();
        var present = new HashSet<string>(Tier0Strings.IgnoreCase);

        // An instance's copy is a NEW entity of its class: its table starts with
        // classname and targetname, then every other key of the class in the
        // finalized order, with the template's values in. Mako's func_door copies
        // ship the class's order reversed, where the walked door ships its
        // source's; the template's keys the class does not declare follow.
        if (entity.Instanced && schema is not null && schema.KeysOf(entity.ClassName).Count > 0)
        {
            var source = new Dictionary<string, KeyValuePair<string, string>>(Tier0Strings.IgnoreCase);
            foreach (var pair in entity.Keys)
                if (!IsPlacement(pair.Key))
                    source.TryAdd(pair.Key, Masked(pair, entity.ClassName, schema));
            IEnumerable<string> order = ["classname", "targetname"];
            foreach (var name in order.Concat(schema.KeysOf(entity.ClassName).Select(k => k.Name)))
            {
                if (IsPlacement(name) || !present.Add(name))
                    continue;
                if (source.TryGetValue(name, out var pair))
                    table.Add(pair);
                else if (schema.KeyOf(entity.ClassName, name) is { } declared && !IsElementName(declared.Name))
                    table.Add(new(declared.Name, DefaultOf(entity, declared)));
                else
                    present.Remove(name);
            }
            // The template's keys the class does not declare come last, in the
            // reverse of the template's order: a node's keys are a list that
            // grows at its head (FUN_180ce08f0), so walking one from the head
            // and adding each key to another reverses them. atixref's 186
            // copied lights ship fog, fogstrength and fogshadows so.
            foreach (var pair in entity.Keys.Reverse())
                if (!IsPlacement(pair.Key) && present.Add(pair.Key))
                    table.Add(Masked(pair, entity.ClassName, schema));
            return table;
        }

        foreach (var pair in entity.Keys)
            if (!IsPlacement(pair.Key) && present.Add(pair.Key))
                table.Add(Masked(pair, entity.ClassName, schema));
        foreach (var key in schema?.KeysOf(entity.ClassName) ?? [])
            if (!IsPlacement(key.Name) && !IsElementName(key.Name) && present.Add(key.Name))
                table.Add(new(key.Name, DefaultOf(entity, key)));
        return table;
    }

    /// <summary>A key the source lacks: its class default, or what a later pass set in its place.</summary>
    private static string DefaultOf(MapEntities.Entity entity, FgdSchema.Key key)
        => entity.Defaults is { } set && set.TryGetValue(key.Name, out var value) ? value : key.Default ?? "";

    /// <summary>
    /// Whether a declared key is spelled like the DMX element's own name. The
    /// default fill looks keys up case-blind among the element's attributes
    /// (FUN_180f2d290), and every element carries a "name" attribute, so such a
    /// key always counts as present and never gets a default. Set in the map, it
    /// ships as the map spells it: point_gamestats_counter's Name.
    /// </summary>
    private static bool IsElementName(string key) => key.EqualsAscii("name");

    /// <summary>
    /// A source key as the entity holds it once its class is set at load
    /// (CMapGameDataNode::SetClass, FUN_180f2e4d0): a key named spawnflags, when
    /// the class declares spawnflags with choices, keeps only the declared bits,
    /// printed "%d". Every other key, and spawnflags on a class without choices,
    /// is unchanged. Only the name spawnflags: func_nav_markup's flags key is not
    /// masked.
    /// </summary>
    private static KeyValuePair<string, string> Masked(KeyValuePair<string, string> pair, string className,
                                                       FgdSchema? schema)
    {
        if (!pair.Key.EqualsAscii("spawnflags")
            || schema?.KeyOf(className, "spawnflags") is not { Flags.Count: > 0 } declared)
            return pair;
        var declaredBits = 0u;
        foreach (var (bit, _, _) in declared.Flags)
            declaredBits |= bit;
        var value = (uint)(int)CNumbers.Atoi(pair.Value) & declaredBits;
        return new(pair.Key, ((int)value).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A key's value at the type its class declares, case for case as the lump
    /// writer converts it (FUN_180fa84e0). Every case turns an empty or bad value
    /// into its zero rather than keeping the text.
    /// </summary>
    private static KVObject Typed(FgdSchema.Key key, string text, bool fixupEntityNames, FgdSchema? schema)
        => key.Type switch
        {
            // A map with entity-name fixup on has every NAME and every reference to
            // one rewritten by the compile. RC does it by TYPE, not by meaning: a
            // light_environment's ambient_occlusion_proxy_position_0 is declared
            // target_destination and holds "0 0 0", and ships as "[PR#]0 0 0".
            FgdSchema.FieldType.EntityName => new KVObject(TakesFixup(key) ? Fixup(text, fixupEntityNames, schema) : text),
            FgdSchema.FieldType.Boolean
                => new KVObject(text.EqualsAscii("true") || CNumbers.Atoi(text) != 0),
            // V_atoi, so a decimal is truncated: one of atixref's fifteen func_door
            // carries wait "0.600000" against wait(integer) and ships Int64 0.
            FgdSchema.FieldType.Integer => Integer((int)CNumbers.Atoi(text)),
            FgdSchema.FieldType.Int32 => Integer(CNumbers.ToInt32(text)),
            // A flags field ships UNSIGNED: func_brush's spawnflags 2 is UInt32. The
            // KV3 zero and one are still Int64, which ze_hold_em_p's func_door shows
            // on one class: six spawnflags 0 as Int64, seven 6144 as UInt32.
            FgdSchema.FieldType.Flags => Unsigned(CNumbers.ToUInt32(text)),
            // A 32-bit float widened, so 0.1 lands as 0.10000000149011612.
            FgdSchema.FieldType.Float => new KVObject((double)CNumbers.ToFloat32(text)),
            FgdSchema.FieldType.Vector2 => Floats(CNumbers.Scan(text, 2)),
            FgdSchema.FieldType.Vector or FgdSchema.FieldType.Angle => Floats(CNumbers.FloatArray(text, 3)),
            FgdSchema.FieldType.Vector4 => Floats(CNumbers.FloatArray(text, 4)),
            FgdSchema.FieldType.Color => Color(CNumbers.ToColor(text)),
            FgdSchema.FieldType.Resource => Resource(text, key.Extension),
            _ => new KVObject(text),
        };

    private static KVObject Unsigned(uint value) => value is 0 or 1 ? Integer(value) : new KVObject(value);

    private static KVObject Floats(float[] values)
        => Array(values.Select(f => new KVObject((double)f)));

    /// <summary>
    /// A colour as the KV3 colour setter writes it (FUN_181eb82c0): its channels
    /// as unsigned 32-bit, three of them, and the alpha only when it is not 255.
    /// point_worldtext's color255alpha "0 0 0 255" ships as [0, 0, 0].
    /// </summary>
    private static KVObject Color((byte R, byte G, byte B, byte A) c)
        => Array((c.A == 255 ? new uint[] { c.R, c.G, c.B } : [c.R, c.G, c.B, c.A]).Select(v => new KVObject(v)));

    /// <summary>
    /// A resource reference, fixed up the way FUN_181c1ee80 does it and flagged as
    /// a resource name.
    /// </summary>
    private static KVObject Resource(string text, string extension)
    {
        var value = new KVObject(ResourcePath.Fixup(text, extension));
        value.Flag = KVFlag.ResourceName;
        return value;
    }

    private static KVObject Array(IEnumerable<KVObject> items)
    {
        var array = KVObject.Array();
        foreach (var item in items)
            array.Add(item);
        return array;
    }

    private static bool ParseBool(string text)
        => text is "1" or "true" or "True" || (bool.TryParse(text, out var b) && b);
}
