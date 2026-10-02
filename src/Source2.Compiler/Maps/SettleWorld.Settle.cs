using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler.Maps;

public static partial class SettleWorld
{
    /// <summary>
    /// The bodies the settle makes dynamic (FUN_180f1e490's list, then
    /// FUN_1810593e0), as indices into <paramref name="bodies"/> in build
    /// order: an entity whose class inherits BasePhysicsSimulated, that is
    /// not in the exclusion set, has no skipPreSettle, neither the "Start
    /// asleep" nor the "Motion Disabled" spawnflag, and whose model's
    /// prop_data does not ask spawn_motion_disabled; and its body only when
    /// every shape is a sphere, capsule or hull (any triangle mesh keeps
    /// the prop static).
    /// </summary>
    /// <remarks>
    /// Not ported: the joint test on the model's physics (FUN_1819d5190),
    /// which a single-part model never meets, and the order: Valve walks
    /// the collected nodes in a hash map keyed by node pointer.
    /// </remarks>
    public static List<int> Settled(DmxBinary.Document document, IReadOnlyList<BodyBuild> bodies, IModels models, FgdSchema schema)
    {
        var eligible = Eligible(document, bodies, models, schema);
        return [.. eligible.Where(i => bodies[i].Shapes.All(s => s.Type <= Simulation.BroadphaseShape.HullType))];
    }

    /// <summary>
    /// The bodies of the nodes FUN_180f1e490 collects for the settle, before
    /// the shape test: every node among them whose objects all end asleep,
    /// the ones left static included, gets "Start asleep" afterwards. The
    /// candidates are PhysDoc_GetNodes', the nodes that have physics objects,
    /// so an entity without a body (a model with no physics) is never one:
    /// c2m2's 265 such props and Mako's 53 ship without the flag.
    /// </summary>
    public static List<int> Eligible(DmxBinary.Document document, IReadOnlyList<BodyBuild> bodies, IModels models, FgdSchema schema)
    {
        // Each map names its own entities: a prefab map's parents and templates are its own.
        var excluded = new HashSet<DmxBinary.Element>(AllDocuments(document).SelectMany(d => Excluded(d, schema)), ReferenceEqualityComparer.Instance);
        var result = new List<int>();
        for (var i = 0; i < bodies.Count; i++)
            if (bodies[i].Node is { } node && IsEligible(node, excluded, models, schema))
                result.Add(i);
        return result;
    }

    private static bool IsEligible(DmxBinary.Element node, HashSet<DmxBinary.Element> excluded, IModels models, FgdSchema schema)
    {
        if (node.Type != "CMapEntity" || excluded.Contains(node) || node.Get<DmxBinary.Element>("entity_properties") is not { } props)
            return false;
        var className = props.Get<string>("classname") ?? "";
        if (!schema.Inherits(className, "BasePhysicsSimulated"))
            return false;
        if (Int(props, "skipPreSettle") != 0)
            return false;
        var spawnflags = Int(props, "spawnflags");
        if (schema.HasSpawnflag(className, spawnflags, "Start asleep") || schema.HasSpawnflag(className, spawnflags, "Motion Disabled"))
            return false;
        return !(props.Get<string>("model") is { Length: > 0 } model && models.ModelKeyValues(model) is { } kv
                 && kv.ContainsKey("prop_data") && kv.GetSubCollection("prop_data") is { } propData
                 && propData.ContainsKey("spawn_motion_disabled")
                 && propData["spawn_motion_disabled"]?.ToString() is "1" or "true" or "True");
    }

    /// <summary>
    /// FUN_180f1c060: entities the settle leaves static. An entity whose
    /// parentname names another, with every entity of that name; the
    /// entities a point_template's template keys name; and the entities a
    /// BasePhysicsNoSettleAttached entity names in its entity-reference keys
    /// (FGD type ids 1, 2, 17 and 23).
    /// </summary>
    private static HashSet<DmxBinary.Element> Excluded(DmxBinary.Document document, FgdSchema schema)
    {
        var entities = document.Elements.Where(e => e.Type == "CMapEntity" && e.Get<DmxBinary.Element>("entity_properties") != null).ToList();
        var byName = entities.GroupBy(e => e.Get<DmxBinary.Element>("entity_properties")!.Get<string>("targetname") ?? "", StringComparer.OrdinalIgnoreCase)
                             .Where(g => g.Key.Length > 0).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var excluded = new HashSet<DmxBinary.Element>(ReferenceEqualityComparer.Instance);
        void Named(string? name)
        {
            if (!string.IsNullOrEmpty(name) && byName.TryGetValue(name, out var list))
                excluded.UnionWith(list);
        }
        foreach (var e in entities)
        {
            var props = e.Get<DmxBinary.Element>("entity_properties")!;
            var className = props.Get<string>("classname") ?? "";
            if (props.Get<string>("parentname") is { Length: > 0 } parent && byName.ContainsKey(parent))
            {
                excluded.Add(e);
                Named(parent);
            }
            if (className.Equals("point_template", StringComparison.OrdinalIgnoreCase))
                foreach (var (key, value) in props.Attributes)
                    if (key.Contains("template", StringComparison.OrdinalIgnoreCase) && value is string name)
                        Named(name);
            if (schema.Inherits(className, "BasePhysicsNoSettleAttached"))
                foreach (var key in schema.KeysOf(className))
                    if (key.TypeId is 1 or 2 or 17 or 23 && props.Get<string>(key.Name) is { Length: > 0 } name)
                        Named(name);
        }
        return excluded;
    }

    private static long Int(DmxBinary.Element props, string key)
        => props.Attributes.GetValueOrDefault(key) switch
        {
            string s when long.TryParse(s, out var v) => v,
            int i => i,
            long l => l,
            _ => 0,
        };
}
