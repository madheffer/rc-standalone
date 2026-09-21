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
/// resourcecompiler output rather than inferred - see docs/MAP_RESOURCES.md.</para>
/// </summary>
public static class EntityLumpAuthor
{
    /// <summary>Format GUID a compiled entity lump's DATA carries.</summary>
    private static readonly KV3ID Format = KV3IDLookup.Get("generic");

    /// <summary>
    /// What the compile puts in front of every entity name when the map asks for
    /// name fixup. The engine resolves it at spawn; the compile just writes it.
    /// </summary>
    private const string NameFixup = "[PR#]";

    /// <summary>Build the lump's DATA tree.</summary>
    /// <param name="entities">Entities in the order they should appear.</param>
    /// <param name="schema">The game's FGD, which types the values.</param>
    /// <param name="name">The lump's own name, e.g. <c>default_ents</c>.</param>
    /// <param name="childLumps">Names of lumps hanging off this one.</param>
    /// <param name="worldName">The map's name, which worldspawn records.</param>
    /// <param name="emit">What this lump carries, in order, each paired with the
    /// walk index it is numbered by. Null is every walked entity in walk order. The
    /// two cannot be one list: a child lump is a re-ordered subset, and an
    /// instanced copy is not in the walk at all yet carries its template's
    /// number.</param>
    /// <param name="lumpNames">Lump name per point_template node id, for the
    /// templates that own one.</param>
    /// <param name="template">Set when this is a CHILD lump, which places and
    /// may rename its members. Null for default_ents.</param>
    public static KVObject BuildTree(
        IReadOnlyList<MapEntities.Entity> entities,
        FgdSchema? schema,
        string name = "default_ents",
        IReadOnlyList<string>? childLumps = null,
        string? worldName = null,
        bool fixupEntityNames = false,
        IReadOnlyList<Emission>? emit = null,
        IReadOnlyDictionary<int, string>? lumpNames = null,
        TemplateLump? template = null)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var root = KVObject.Collection();
        root.Add("m_name", new KVObject(name));

        var lumps = KVObject.Array();
        foreach (var child in childLumps ?? [])
            lumps.Add(new KVObject(child));
        root.Add("m_childLumps", lumps);

        // An output's override parameter gets the fixup when it NAMES one of the
        // map's entities. resourcecompiler says so itself while compiling
        // ze_hold_em_p: "Parameter 'humans' corresponds to an entity target name,
        // but is sent to input 'SetDamageFilter' ... which is not marked as being
        // a target name. (FGD Error?)" - and it writes [PR#]humans regardless. So
        // the test is the map's own name set, not the FGD's input declaration.
        var entityNames = new HashSet<string>(
            entities.Select(NameOf).Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase);

        // The index is the ordinal in the WALK, not in the lump, so it is taken
        // before the filter and not after. Valve's atixref ships 764 entities out
        // of 4,601 walked nodes and its compile_source_id values run to 4,600 with
        // a gap wherever one was filtered.
        var array = KVObject.Array();
        var prefabs = HasPointPrefab(entities, schema);
        var ordinal = 0;
        foreach (var (entity, sourceId) in emit ?? [.. entities.Select((e, i) => new Emission(e, i))])
            if (ReachesTheLump(entity, schema))
                array.Add(BuildEntity(entity, schema, sourceId, worldName, fixupEntityNames,
                                      prefabs, entityNames, lumpNames,
                                      template is null ? null : ordinal++, template));
        root.Add("m_entityKeyValues", array);
        return root;
    }

    /// <summary>One entity to write, and the walk index it is numbered by.</summary>
    /// <param name="Entity">What to write. For an instanced copy this is the
    /// template placed by its instance, which is in no walk.</param>
    /// <param name="SourceId">Its compile_source_id. A copy carries its TEMPLATE's,
    /// which is why Valve's atixref repeats 18 ids across 199 entities.</param>
    public sealed record Emission(MapEntities.Entity Entity, int SourceId);

    /// <summary>
    /// The point_template a child lump belongs to.
    /// </summary>
    /// <param name="Origin">Members are stored in the template's local space.</param>
    /// <param name="Suffix">Appended to every member NAME, and to every reference
    /// to one, before the prefab prefix. A template whose spawnflags lack bit 1,
    /// "preserve entity names", gets <c>&amp;0000</c>: atixref's deadpool_temp and
    /// snake_temp are the two at spawnflags 0 and the only two whose members are
    /// renamed.</param>
    /// <param name="MemberNames">Member names as the SOURCE states them.</param>
    public sealed record TemplateLump(Vector3 Origin, string Suffix, IReadOnlySet<string> MemberNames)
    {
        /// <summary>A member name with its suffix; anything else unchanged.</summary>
        public string Rename(string name)
            => Suffix.Length > 0 && MemberNames.Contains(name) ? name + Suffix : name;
    }

    /// <summary>Build and compile the lump.</summary>
    public static byte[] Author(
        IReadOnlyList<MapEntities.Entity> entities,
        FgdSchema? schema,
        string name = "default_ents",
        IReadOnlyList<string>? childLumps = null,
        string? worldName = null,
        bool fixupEntityNames = false,
        IReadOnlyList<Emission>? emit = null,
        IReadOnlyDictionary<int, string>? lumpNames = null,
        TemplateLump? template = null)
        => Source2ContainerAuthor.AuthorKv3Tree(
            BuildTree(entities, schema, name, childLumps, worldName, fixupEntityNames,
                      emit, lumpNames, template),
            Format, ".vents");

    /// <summary>
    /// Whether a walked node is written to the lump at all.
    ///
    /// <para>Two FGD metadata flags say it is not. <c>static_prop</c> is
    /// prop_static, whose geometry the compile bakes into the world: atixref walks
    /// 3,952 of them and Valve's lump carries none. <c>editor_only</c> is the path
    /// node classes, which hold a path's shape for Hammer and are serialized into
    /// the path rather than spawned. Both still take their number.</para>
    /// </summary>
    private static bool ReachesTheLump(MapEntities.Entity entity, FgdSchema? schema)
        => !entity.Hidden
        && (schema is null
        || !(schema.HasFlag(entity.ClassName, "static_prop")
             || schema.HasFlag(entity.ClassName, "editor_only")
             || (schema.IsSolidClass(entity.ClassName) && !entity.HasGeometry && !entity.IsWorld)));

    /// <summary>
    /// True when any entity in the lump is a point prefab, which is what decides
    /// whether worldspawn carries prefab_has_runtime_entity_by_default: probe01
    /// places four and gets the key although its source never mentions it, and
    /// untitled_1 places none and does not.
    /// </summary>
    private static bool HasPointPrefab(IReadOnlyList<MapEntities.Entity> entities, FgdSchema? schema)
        => entities.Any(e => (schema?.GameKeysOf(e.ClassName) ?? [])
            .Any(g => g.Key.Equals("isPointPrefab", StringComparison.OrdinalIgnoreCase)));

    private static KVObject BuildEntity(
        MapEntities.Entity entity, FgdSchema? schema, int index, string? worldName, bool fixupEntityNames,
        bool mapHasPointPrefabs, HashSet<string>? entityNames, IReadOnlyDictionary<int, string>? lumpNames,
        int? templateIndex, TemplateLump? template)
    {
        var values = KVObject.Collection();

        // The compile writes the class's WHOLE key set, not the source's. A map
        // saved before a key existed still compiles with that key at its FGD
        // default, which is how probe01's worldspawn ships 30-odd steamaudio
        // settings its .vmap has never heard of.
        //
        // What is dropped is an EMPTY value, whether it came from the source or
        // from a default: Hammer writes an unset key as "", and handing the entity
        // system an empty vector or an empty target is what breaks it. A key still
        // at a non-empty default is kept, which is why a spawn point's priority 0
        // and enabled 1 are both in Valve's lump.
        var source = entity.Keys.ToDictionary(k => k.Key, k => k.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var key in Schema(entity, schema))
        {
            if (IsPlacement(key.Name))
                continue;
            var text = source.TryGetValue(key.Name, out var authored) ? authored : key.Default;
            if (string.IsNullOrEmpty(text))
            {
                // A BOOLEAN is the exception: empty means false and still ships.
                // point_soundevent declares startOnSpawn and toLocalPlayer with an
                // empty default and Valve's lump carries both as Boolean 0.
                if (key.Type != FgdSchema.FieldType.Boolean)
                    continue;
                text = "0";
            }
            values.Add(key.Name, Typed(key, Rename(key, text, template), fixupEntityNames));
        }

        // Anything the source carries that the schema does not know about is still
        // the mapper's data, and RC keeps it as the string it is - except that a
        // targetname is a NAME whatever the class is. ze_doom_p2_c_gameplay places
        // four classes the fgd never declares (func_physbox_multiplayer,
        // player_speedmod, prop_door_rotating_checkpoint, ambient_music) and Valve
        // prefixes every one of their names.
        foreach (var (key, text) in entity.Keys)
        {
            if (text.Length == 0 || IsPlacement(key) || values.ContainsKey(key)
                || schema?.KeyOf(entity.ClassName, key) is not null)
                continue;
            values.Add(key, key.Equals("targetname", StringComparison.OrdinalIgnoreCase)
                ? new KVObject(Fixup(template?.Rename(text) ?? text, fixupEntityNames))
                : new KVObject(text));
        }

        // A path aggregates its nodes into parallel arrays of its own, and records
        // whether it loops. closed_loop is not an FGD key; it is the CMapPath
        // node's own closedLoop, and the lump carries it as an integer.
        if (entity.PathNodes is not null && !values.ContainsKey("closed_loop"))
            values.Add("closed_loop", Integer(entity.ClosedLoop ? 1 : 0));
        foreach (var (key, value) in PathArrays(entity))
            if (!values.ContainsKey(key))
                values.Add(key, new KVObject(value));

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
        values.Add("compile_source_id", Integer(index));

        // A point_template's members are stored in the TEMPLATE's local space, and
        // each carries its ordinal in the lump. Valve's heli_template sits at
        // (299, 1091.37, 395) and every one of its four members is offset by
        // exactly that, on three maps and every lump measured.
        if (templateIndex is { } ordinal)
            values.Add("_template_lump_ent_index", Integer(ordinal));
        values.Add("origin", Vector(entity.Origin - (template?.Origin ?? Vector3.Zero)));
        values.Add("angles", Vector(entity.Angles));
        values.Add("scales", Vector(entity.Scales));
        values.Add("hammerUniqueId", new KVObject(entity.NodeId.ToString(CultureInfo.InvariantCulture)));

        // A brush entity's geometry is compiled into a model of its own, and the
        // entity is pointed at it by name. The path is derived, not looked up:
        // Valve's cardtest compile names the model of brush entity 2138
        // "ImpModel" maps/cardtest/entities/impmodel_2138.vmdl.
        if (entity.HasGeometry && worldName is { Length: > 0 })
            values.Add("model", new KVObject(BrushModelPath(entity, worldName)));

        // A point_template's members are compiled into a lump of their own, and the
        // template is what names it. The world is recorded with a BACKSLASH, which
        // is how Valve writes it and not how the model paths above are written.
        if (lumpNames is not null && lumpNames.TryGetValue(entity.NodeId, out var lump))
        {
            values.Add("entityLumpName", new KVObject(lump));
            if (worldName is { Length: > 0 })
                values.Add("worldName", new KVObject("maps\\" + worldName));
        }

        if (entity.IsWorld)
        {
            if (mapHasPointPrefabs && !values.ContainsKey("prefab_has_runtime_entity_by_default"))
                values.Add("prefab_has_runtime_entity_by_default", new KVObject("0"));
            if (worldName is { Length: > 0 })
                values.Add("worldname", new KVObject(worldName));
            values.Add("mapUsageType", new KVObject("standard"));
        }

        var keyValues = KVObject.Collection();
        keyValues.Add("version", new KVObject(1));
        keyValues.Add("values", values);
        keyValues.Add("attributes", KVObject.Collection());

        var result = KVObject.Collection();
        result.Add("m_connections", Connections(entity, fixupEntityNames, entityNames, template));
        result.Add("m_keyValuesData", KVObject.Blob([]));
        result.Add("keyValues3Data", keyValues);
        return result;
    }

    /// <summary>
    /// Apply the prefab name fixup to an entity name the map refers to.
    ///
    /// <para>An engine keyword is NOT a name and does not get one. Valve's lump
    /// for ze_hold_em_p prefixes 144 of its 145 connection targets; the single
    /// exception is <c>!activator</c>, and the same goes for its siblings
    /// <c>!self</c>, <c>!player</c> and <c>!caller</c>.</para>
    /// </summary>
    private static string Fixup(string name, bool enabled)
        => enabled && name.Length > 0 && name[0] != '!' ? NameFixup + name : name;

    private static KVObject Connections(MapEntities.Entity entity, bool fixupEntityNames,
                                        HashSet<string>? entityNames, TemplateLump? template)
    {
        var array = KVObject.Array();
        foreach (var c in entity.Connections)
        {
            var o = KVObject.Collection();
            o.Add("m_outputName", new KVObject(c.OutputName));
            o.Add("m_targetType", new KVObject("ENTITY_CONNECTION_TARGET_NAME"));
            o.Add("m_targetName", new KVObject(
                Fixup(template?.Rename(c.TargetName) ?? c.TargetName, fixupEntityNames)));
            o.Add("m_inputName", new KVObject(c.InputName));
            o.Add("m_overrideParam", new KVObject(
                entityNames?.Contains(c.OverrideParam) == true
                    ? Fixup(template?.Rename(c.OverrideParam) ?? c.OverrideParam, fixupEntityNames)
                    : c.OverrideParam));
            o.Add("m_flDelay", new KVObject(c.Delay));
            o.Add("m_nTimesToFire", new KVObject(c.TimesToFire));
            array.Add(o);
        }
        return array;
    }

    /// <summary>A member's name carries the lump's suffix; only a name-typed key
    /// is eligible, so a value that merely reads like one is left alone.</summary>
    private static string Rename(FgdSchema.Key key, string text, TemplateLump? template)
        => template is null || key.Type != FgdSchema.FieldType.EntityName ? text : template.Rename(text);

    /// <summary>
    /// The three arrays a path writes for its nodes, each as KV3 TEXT in a string
    /// value rather than as a real array.
    ///
    /// <para><c>pathNodes</c> is nine floats a node: its position relative to the
    /// path, then the in and out tangents. The tangents are not authored, they are
    /// derived, and it is one segment at a time rather than any smoothing across
    /// the path: each is a third of the way to the neighbour, and zero at an end.
    /// Checked against a five node rope where every one of the ten tangents is
    /// exactly (neighbour - node) / 3.</para>
    ///
    /// <para>The other two come from the node's own game keys, and the key is
    /// written only when a node declares it: c2m2 has ropes with radii and no
    /// pins, and ropes with pins and no radii.</para>
    /// </summary>
    private static IEnumerable<KeyValuePair<string, string>> PathArrays(MapEntities.Entity entity)
    {
        if (entity.PathNodes is not { Count: > 0 } nodes)
            yield break;

        var rows = new List<string>();
        for (var i = 0; i < nodes.Count; i++)
        {
            var here = nodes[i].Origin - entity.Origin;
            var back = i > 0 ? Handle(nodes[i - 1].Origin - nodes[i].Origin) : Vector3.Zero;
            var on = i < nodes.Count - 1 ? Handle(nodes[i + 1].Origin - nodes[i].Origin) : Vector3.Zero;
            rows.Add(Kv3Array([here.X, here.Y, here.Z, back.X, back.Y, back.Z, on.X, on.Y, on.Z], 2));
        }
        yield return new("pathNodes", Kv3Rows(rows));

        // A node that does not carry a radius scale counts as zero rather than as
        // the fgd's 1.0, and the array is skipped entirely when every node is at
        // 1.0 and it would say nothing. atixref writes [2.0, 2.0], c2m2 writes
        // [0.0 ...] for nodes of a class that has no such key, and c2m2's ropes
        // whose nodes are all at 1.0 carry no array at all.
        float[] radii = [.. nodes.Select(n => float.TryParse(NodeKey(n, "radius_scale"),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 0f)];
        if (radii.Any(r => r != 1f))
            yield return new("pathNodeRadiusScales", Kv3Array(radii, 1));

        if (nodes.Any(n => NodeKey(n, "pin_enabled") is not null))
            yield return new("pathNodePinsEnabled", Kv3Words(
                [.. nodes.Select(n => ParseBool(NodeKey(n, "pin_enabled") ?? "0") ? "true" : "false")]));
    }

    /// <summary>
    /// A node's control handle: a third of the way to its neighbour, but computed
    /// as a DIRECTION and a length rather than by dividing the offset.
    ///
    /// <para>That round trip is visible in the output and is the only way to land
    /// on Valve's floats. A rope leg of (-446, -171, 0) divided by three gives
    /// exactly -57 on the second component and Valve writes -57.000004; one of
    /// (191, 627, 170) gives exactly 209 and Valve writes 209.00002. Normalising
    /// and rescaling reproduces both, and every component of all four legs
    /// measured.</para>
    /// </summary>
    private static Vector3 Handle(Vector3 offset)
    {
        var length = offset.Length();
        if (length == 0f)
            return Vector3.Zero;
        var unit = offset * (1f / length);
        return unit * (length / 3f);
    }

    private static string? NodeKey(MapEntities.PathNode node, string name)
        => node.Keys.FirstOrDefault(k => k.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

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
        => entity.Keys.FirstOrDefault(k => k.Key.Equals("targetname", StringComparison.OrdinalIgnoreCase)).Value ?? "";

    /// <summary>Placement keys live on the map node, not in the game keys, and the
    /// compile writes its own; a stale copy in the props would fight it.</summary>
    /// <summary>
    /// Where the compile puts a brush entity's generated model:
    /// <c>maps/&lt;map&gt;/entities/&lt;name&gt;_&lt;node&gt;.vmdl</c>, the name lowercased,
    /// and <c>unnamed</c> when the entity has none.
    /// </summary>
    private static string BrushModelPath(MapEntities.Entity entity, string worldName)
    {
        var named = entity.Keys.FirstOrDefault(k => k.Key.Equals("targetname", StringComparison.OrdinalIgnoreCase)).Value;
        var name = string.IsNullOrWhiteSpace(named) ? "unnamed" : named.ToLowerInvariant();
        return $"maps/{worldName}/entities/{name}_{entity.NodeId.ToString(CultureInfo.InvariantCulture)}.vmdl";
    }

    private static bool IsPlacement(string key)
        => Placement.Contains(key);

    private static readonly HashSet<string> Placement =
        new(["origin", "angles", "scales"], StringComparer.OrdinalIgnoreCase);

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
    /// Int64. Same rule the KV3 compiler applies elsewhere (RC_PARITY C1).
    /// </summary>
    private static KVObject Integer(long value)
        => value is 0 or 1 || value is < int.MinValue or > int.MaxValue
            ? new KVObject(value)
            : new KVObject((int)value);

    /// <summary>
    /// The class's keys, and <c>classname</c>, which no FGD declares because it IS
    /// the class.
    /// </summary>
    private static IEnumerable<FgdSchema.Key> Schema(MapEntities.Entity entity, FgdSchema? schema)
    {
        yield return new FgdSchema.Key("classname", FgdSchema.FieldType.String, entity.ClassName);
        foreach (var key in schema?.KeysOf(entity.ClassName) ?? [])
            if (!key.Name.Equals("classname", StringComparison.OrdinalIgnoreCase))
                yield return key;
    }

    /// <summary>
    /// A key's value at the type its class declares. An unparseable number stays a
    /// string rather than becoming a guess.
    /// </summary>
    private static KVObject Typed(FgdSchema.Key key, string text, bool fixupEntityNames)
        => key.Type switch
        {
            // A map with entity-name fixup on has every NAME and every reference to
            // one rewritten by the compile. RC does it by TYPE, not by meaning: a
            // light_environment's ambient_occlusion_proxy_position_0 is declared
            // target_destination and holds "0 0 0", and ships as "[PR#]0 0 0".
            FgdSchema.FieldType.EntityName when fixupEntityNames => new KVObject(Fixup(text, true)),
            FgdSchema.FieldType.Boolean => new KVObject(ParseBool(text)),
            // A particle reference names a file, and the compile finishes the name
            // when the author gave a bare one.
            FgdSchema.FieldType.ParticleSystem
                => new KVObject(text.AsSpan(text.LastIndexOf('/') + 1).Contains('.') ? text : text + ".vpcf"),
            FgdSchema.FieldType.Integer when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                => Integer(i),
            // An integer key holding a decimal is TRUNCATED, not left a string. One
            // of atixref's fifteen func_door carries wait "0.600000" against a
            // wait(integer) declaration and Valve's lump has Int64 0.
            FgdSchema.FieldType.Integer when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                => Integer((long)d),
            // A flags field ships UNSIGNED: func_brush's spawnflags 2 is UInt32,
            // not the Int32 the ordinary rule would give it. But 0 and 1 keep the
            // ordinary rule and ship Int64, which is the same exception
            // <see cref="Integer"/> already makes. ze_hold_em_p shows both sides on
            // ONE class: func_door has six spawnflags 0 as Int64 and seven
            // spawnflags 6144 as UInt32, so this is the value's rule, not the
            // class's.
            FgdSchema.FieldType.Flags when uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u)
                => u is 0 or 1 ? Integer(u) : new KVObject(u),
            // A float key is parsed at 32-bit precision and widened, so 0.1 lands
            // as 0.10000000149011612 exactly as Valve's lump has it.
            FgdSchema.FieldType.Float when float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
                => new KVObject((double)f),
            FgdSchema.FieldType.Vector when Numbers(text) is { Length: > 0 } v
                => Array(v.Select(x => new KVObject((double)(float)x))),
            // A colour ships as THREE components even when the schema type carries
            // alpha and the value has four: point_worldtext's color is declared
            // color255alpha and defaults to "0 0 0 255", and Valve's lump has
            // [0, 0, 0].
            FgdSchema.FieldType.Color when Numbers(text) is { Length: > 0 } c
                => Array(c.Take(3).Select(x => Integer((long)x))),
            _ => new KVObject(text),
        };

    /// <summary>The numbers in a whitespace-separated value, or empty when any part
    /// is not one - a malformed vector stays the string the mapper typed.</summary>
    private static double[] Numbers(string text)
    {
        var parts = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                return [];
        return values;
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
