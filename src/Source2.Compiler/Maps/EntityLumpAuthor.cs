using System.Globalization;
using System.Numerics;
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
    public static KVObject BuildTree(
        IReadOnlyList<MapEntities.Entity> entities,
        FgdSchema? schema,
        string name = "default_ents",
        IReadOnlyList<string>? childLumps = null,
        string? worldName = null,
        bool fixupEntityNames = false)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var root = KVObject.Collection();
        root.Add("m_name", new KVObject(name));

        var lumps = KVObject.Array();
        foreach (var child in childLumps ?? [])
            lumps.Add(new KVObject(child));
        root.Add("m_childLumps", lumps);

        var array = KVObject.Array();
        for (var i = 0; i < entities.Count; i++)
            array.Add(BuildEntity(entities[i], schema, i, worldName, fixupEntityNames));
        root.Add("m_entityKeyValues", array);
        return root;
    }

    /// <summary>Build and compile the lump.</summary>
    public static byte[] Author(
        IReadOnlyList<MapEntities.Entity> entities,
        FgdSchema? schema,
        string name = "default_ents",
        IReadOnlyList<string>? childLumps = null,
        string? worldName = null,
        bool fixupEntityNames = false)
        => Source2ContainerAuthor.AuthorKv3Tree(
            BuildTree(entities, schema, name, childLumps, worldName, fixupEntityNames), Format, ".vents");

    private static KVObject BuildEntity(
        MapEntities.Entity entity, FgdSchema? schema, int index, string? worldName, bool fixupEntityNames)
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
                continue;
            values.Add(key.Name, Typed(key, text, fixupEntityNames));
        }

        // Anything the source carries that the schema does not know about is still
        // the mapper's data, and RC keeps it as the string it is.
        foreach (var (key, text) in entity.Keys)
        {
            if (text.Length == 0 || IsPlacement(key) || values.ContainsKey(key)
                || schema?.KeyOf(entity.ClassName, key) is not null)
                continue;
            values.Add(key, new KVObject(text));
        }

        // The compile's own identity for the entity: its ordinal in the lump, and
        // the Hammer node it came from. The id is a number and the node is a
        // string, which is Valve's split, not a slip.
        values.Add("compile_source_id", Integer(index));
        values.Add("origin", Vector(entity.Origin));
        values.Add("angles", Vector(entity.Angles));
        values.Add("scales", Vector(entity.Scales));
        values.Add("hammerUniqueId", new KVObject(entity.NodeId.ToString(CultureInfo.InvariantCulture)));

        // A brush entity's geometry is compiled into a model of its own, and the
        // entity is pointed at it by name. The path is derived, not looked up:
        // Valve's cardtest compile names the model of brush entity 2138
        // "ImpModel" maps/cardtest/entities/impmodel_2138.vmdl.
        if (entity.HasGeometry && worldName is { Length: > 0 })
            values.Add("model", new KVObject(BrushModelPath(entity, worldName)));

        if (entity.IsWorld)
        {
            if (worldName is { Length: > 0 })
                values.Add("worldname", new KVObject(worldName));
            values.Add("mapUsageType", new KVObject("standard"));
        }

        var keyValues = KVObject.Collection();
        keyValues.Add("version", new KVObject(1));
        keyValues.Add("values", values);
        keyValues.Add("attributes", KVObject.Collection());

        var result = KVObject.Collection();
        result.Add("m_connections", Connections(entity));
        result.Add("m_keyValuesData", KVObject.Blob([]));
        result.Add("keyValues3Data", keyValues);
        return result;
    }

    private static KVObject Connections(MapEntities.Entity entity)
    {
        var array = KVObject.Array();
        foreach (var c in entity.Connections)
        {
            var o = KVObject.Collection();
            o.Add("m_outputName", new KVObject(c.OutputName));
            o.Add("m_targetType", new KVObject("ENTITY_CONNECTION_TARGET_NAME"));
            o.Add("m_targetName", new KVObject(c.TargetName));
            o.Add("m_inputName", new KVObject(c.InputName));
            o.Add("m_overrideParam", new KVObject(c.OverrideParam));
            o.Add("m_flDelay", new KVObject(c.Delay));
            o.Add("m_nTimesToFire", new KVObject(c.TimesToFire));
            array.Add(o);
        }
        return array;
    }

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
            FgdSchema.FieldType.EntityName when fixupEntityNames => new KVObject(NameFixup + text),
            FgdSchema.FieldType.Boolean => new KVObject(ParseBool(text)),
            FgdSchema.FieldType.Integer when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                => Integer(i),
            // A flags field ships UNSIGNED: func_brush's spawnflags 2 is UInt32,
            // not the Int32 the ordinary rule would give it.
            FgdSchema.FieldType.Flags when uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u)
                => new KVObject(u),
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
