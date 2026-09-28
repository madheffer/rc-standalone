using System.Globalization;
using System.Numerics;
using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler;

/// <summary>
/// Every entity lump a map compiles to, not just <c>default_ents</c>.
///
/// <para>The compile builds default_ents' entities first, then runs the template
/// pass over them (FUN_18024d710): each entity whose class asks for
/// create_entity_template_lumps gets a lump of its own holding COPIES of the
/// entities it names, and afterwards the originals are removed unless the
/// template says to keep them. atixref has ten templates and ten child lumps;
/// comparing only default_ents makes their members look invented.</para>
/// </summary>
public static class EntityLumpSet
{
    /// <summary>One compiled lump and where it belongs in the map's VPK.</summary>
    /// <param name="Path">Path inside the package, which is lower case.</param>
    /// <param name="Name">The lump's own <c>m_name</c>, which is not.</param>
    /// <param name="Bytes">The compiled lump.</param>
    public sealed record Lump(string Path, string Name, byte[] Bytes);

    /// <summary>The ids of the map's nodes: the world and everything under it.
    /// Other elements carry a nodeID too (the visibility manager's is 0) but are
    /// no node of the map.</summary>
    private static HashSet<int> MapNodeIds(DmxBinary.Document document)
    {
        var ids = new HashSet<int>();
        var stack = new Stack<DmxBinary.Element>(document.OfType("CMapWorld"));
        while (stack.TryPop(out var node))
        {
            if (node.GetValue<int>("nodeID") is { } id)
                ids.Add(id);
            foreach (var child in node.GetElements("children"))
                stack.Push(child);
        }
        return ids;
    }

    /// <summary>
    /// An entity that names a map node in snapshot_mesh gets the particle
    /// snapshot the compile generates from that node (FUN_180f1d770,
    /// FUN_180f8ee20): snapshot_file becomes
    /// <c>maps/&lt;map&gt;/particle_snapshots/node_&lt;id&gt;.vsnap</c>, the id the found
    /// node's own. Id 1 is the world; an id no node has leaves the key alone.
    /// The node's type is not checked. The .vsnap itself is not generated here.
    /// A source without snapshot_file gets it appended; only the case with the
    /// key present is measured (the probe map).
    /// </summary>
    /// <remarks>An entity inside an instance looks the id up in the instance's
    /// map and names the node by its id chain; that is not ported, so such an
    /// entity with a node id throws rather than ship a wrong path.</remarks>
    private static MapEntities.Entity WithParticleSnapshot(MapEntities.Entity entity, IReadOnlySet<int>? nodeIds,
                                                           string worldName)
    {
        var mesh = entity.Keys.FirstOrDefault(k => k.Key.Equals("snapshot_mesh", StringComparison.OrdinalIgnoreCase));
        if (nodeIds is null || mesh.Key is null)
            return entity;
        var id = (int)CNumbers.Atoi(mesh.Value);
        if (entity.Instanced && id >= 1)
            throw new NotSupportedException(
                $"{entity.ClassName} {entity.NodeId}: a particle snapshot node inside an instance is not ported");
        if (id != 1 && !nodeIds.Contains(id))
            return entity;
        var path = $"maps/{worldName}/particle_snapshots/node_{id.ToString(System.Globalization.CultureInfo.InvariantCulture)}.vsnap";
        var keys = entity.Keys.ToList();
        var at = keys.FindIndex(k => k.Key.Equals("snapshot_file", StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
            keys[at] = new(keys[at].Key, path);
        else
            keys.Add(new("snapshot_file", path));
        return entity with { Keys = keys };
    }

    /// <summary>Author the map's lumps: default_ents first, then the child lumps
    /// in the order the template pass made them.</summary>
    /// <param name="smartPropLocators">How many locators a smart prop definition
    /// creates, by its path (see <see cref="SmartProps"/>). They take node ids
    /// before any instance copy, so without it a map with smart props numbers its
    /// instanced entities too low.</param>
    /// <param name="settled">The physics settle's results by node id
    /// (<see cref="Maps.SettleWorld.Run"/>); without them props keep their
    /// authored placement.</param>
    public static IReadOnlyList<Lump> Author(
        IReadOnlyList<MapEntities.Entity> entities,
        FgdSchema? schema,
        string worldName,
        bool fixupEntityNames = false,
        DmxBinary.Document? document = null,
        Func<string, int>? smartPropLocators = null,
        IReadOnlyDictionary<int, Maps.SettleWorld.Settlement>? settled = null)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var context = EntityLumpAuthor.Context.For(entities, schema, worldName, fixupEntityNames);

        // Each world layer is a lump of its own, world_layer_<name>, holding the
        // entities under that CMapWorldLayer (instance copies go with the instance
        // that placed them). Numbering stays global: Mako's layer lumps carry
        // compile_source_ids from 1,481 to 4,732 among default_ents' 0 to 5,068.
        List<string> layers = document is null ? [] : [.. MapEntities.WorldLayers(document)];
        var worlds = new List<(string Name, List<Item> Items)> { ("default_ents", []) };
        worlds.AddRange(layers.Select(l => ("world_layer_" + l, new List<Item>())));
        var nodeIds = document is null ? null : MapNodeIds(document);
        foreach (var (walked, sourceId) in MainLump(entities, document, smartPropLocators, settled, schema))
            if (WithParticleSnapshot(walked, nodeIds, worldName) is var entity
                && EntityLumpAuthor.ReachesTheLump(entity, schema))
                worlds[entity.Layer is { } layer && layers.IndexOf(layer) is var at and >= 0 ? at + 1 : 0].Items
                    .Add(new Item(EntityLumpAuthor.BuildEntity(entity, sourceId, context), entity.Hidden));

        // The template pass runs over each world's list in turn, default_ents
        // first, and every lump it makes is a child of default_ents after the
        // layers: Mako lists its four layers, then default_ents' templates, then
        // the layers' own.
        var children = new List<(string Name, List<KVObject> Members)>();
        foreach (var (_, items) in worlds)
            children.AddRange(TemplatePass(items, schema, worldName));

        var lumps = new List<Lump>();
        foreach (var (name, items) in worlds)
            lumps.Add(new Lump(PathOf(worldName, name), name, EntityLumpAuthor.Compile(
                EntityLumpAuthor.Lump(name,
                    name == "default_ents"
                        ? [.. worlds.Skip(1).Select(w => w.Name).Concat(children.Select(c => c.Name))
                                 .Select(c => PathOf(worldName, c)[..^2])]         // m_childLumps drops the _c
                        : [],
                    items.Where(i => !i.Hidden).Select(i => i.Tree)))));
        foreach (var (name, members) in children)
            lumps.Add(new Lump(PathOf(worldName, name), name,
                               EntityLumpAuthor.Compile(EntityLumpAuthor.Lump(name, [], members))));
        return lumps;
    }

    /// <summary>An entity of default_ents while the lumps are being built. A node
    /// the map hides is in the list the templates search and is dropped when
    /// default_ents is written.</summary>
    private sealed record Item(KVObject Tree, bool Hidden);

    /// <summary>
    /// FUN_18024d710 and FUN_18024d9c0: build a lump for every entity whose class
    /// metadata asks for one, in list order, then remove the originals of every
    /// template that does not keep them. Returns the lumps in the order made.
    /// </summary>
    private static List<(string Name, List<KVObject> Members)> TemplatePass(
        List<Item> list, FgdSchema? schema, string worldName)
    {
        var lumps = new List<(string, List<KVObject>)>();
        var removals = new List<string>();
        foreach (var item in list.ToList())
        {
            var values = ValuesOf(item.Tree);
            foreach (var template in schema?.TemplateLumpsOf(Text(values, "classname")) ?? [])
            {
                var single = template.Mode.Equals("SingleTemplate", StringComparison.OrdinalIgnoreCase);
                if ((!single && !template.Mode.Equals("PointTemplate", StringComparison.OrdinalIgnoreCase))
                    || template.WorldKey.Length == 0 || template.LumpKey.Length == 0
                    || (single && template.SourceKey.Length == 0))
                    continue;

                // The lump is named after the entity's node: its hammerUniqueId with
                // any ':' made '_', a '#', and the key its name goes in.
                var name = Text(values, "hammerUniqueId").Replace(':', '_') + "#" + template.LumpKey;
                var members = new List<KVObject>();
                var patterns = new List<string>();

                // A PointTemplate's spawnflags: bit 0 keeps the originals, bit 1
                // preserves the members' names. A SingleTemplate always removes and
                // never renames.
                var flags = single ? 0u : Unsigned(values, "spawnflags");
                var origin = VectorOf(values, "origin");
                var angles = VectorOf(values, "angles");
                if (single)
                    Collect(list, Text(values, template.SourceKey), null, members, patterns);
                else
                    for (var n = 1; n <= 128; n++)
                        Collect(list, Text(values, "Template" + n.ToString("00", CultureInfo.InvariantCulture)),
                                (origin, angles), members, patterns);

                lumps.Add((name, members));
                if (members.Count > 0)
                {
                    // The world is recorded as the map's resource path without its
                    // extension and with BACKSLASHES, which is how Valve writes it.
                    values[template.WorldKey] = new KVObject("maps\\" + worldName);
                    values[template.LumpKey] = new KVObject(name);
                    if (!single && (flags & 2) == 0 && Rename(members))
                        values["TemplateFixup"] = new KVObject(true);
                }
                if (single || (flags & 1) == 0)
                    removals.AddRange(patterns);
            }
        }

        foreach (var pattern in removals)
            list.RemoveAll(i => Wildcard.Matches(Text(ValuesOf(i.Tree), "targetname"), pattern));
        return lumps;
    }

    /// <summary>
    /// FUN_18024ccd0 for one name: a COPY of every entity in the list it matches,
    /// in list order, placed relative to the template (or at the origin, for a
    /// SingleTemplate) and numbered by its place in the lump. A name listed twice
    /// is copied twice: Mako's bridge train template names Bridge_Train_Sound in
    /// two slots and its lump holds the sound twice.
    /// </summary>
    private static void Collect(List<Item> list, string pattern, (Vector3 Origin, Vector3 Angles)? relativeTo,
                                List<KVObject> members, List<string> patterns)
    {
        if (pattern.Length == 0)
            return;
        foreach (var item in list)
        {
            var source = ValuesOf(item.Tree);
            if (!Wildcard.Matches(Text(source, "targetname"), pattern))
                continue;

            var copy = KVObjectDeepClone.Clone(item.Tree);
            var values = ValuesOf(copy);
            var (origin, angles) = relativeTo is { } t
                ? TemplateTransform.Relative(t.Origin, t.Angles, VectorOf(values, "origin"), VectorOf(values, "angles"))
                : (Vector3.Zero, Vector3.Zero);
            values["origin"] = Floats(origin);
            values["angles"] = Floats(angles);
            values["_template_lump_ent_index"] = members.Count is 0 or 1 ? new KVObject((long)members.Count)
                                                                           : new KVObject(members.Count);
            members.Add(copy);
            patterns.Add(pattern);
        }
    }

    /// <summary>
    /// FUN_18024c910: give every member that has a name the suffix
    /// <c>&amp;0000</c>, then (FUN_1801fdae0) rewrite, in every member, each
    /// string value and each output target or parameter that equals one of the
    /// old names. It is by value, not by key type. True when anything was
    /// renamed, which is what sets the template's TemplateFixup.
    /// </summary>
    private static bool Rename(List<KVObject> members)
    {
        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in members)
        {
            var values = ValuesOf(member);
            var name = Text(values, "targetname");
            if (name.Length == 0)
                continue;
            if (!renamed.TryGetValue(name, out var suffixed))
                renamed[name] = suffixed = name + "&0000";
            values["targetname"] = new KVObject(suffixed);
        }
        if (renamed.Count == 0)
            return false;

        foreach (var member in members)
        {
            var values = ValuesOf(member);
            foreach (var key in values.Keys.ToList())
                if (!key.Equals("targetname", StringComparison.OrdinalIgnoreCase))
                    Replace(values, key, renamed);
            foreach (var connection in member["m_connections"].Values)
            {
                Replace(connection, "m_targetName", renamed);
                Replace(connection, "m_overrideParam", renamed);
            }
        }
        return true;
    }

    private static void Replace(KVObject owner, string key, Dictionary<string, string> renamed)
    {
        var value = owner[key];
        if (value is null || value.ValueType != KVValueType.String
            || !renamed.TryGetValue(value.ToString() ?? "", out var suffixed))
            return;
        owner[key] = new KVObject(suffixed) { Flag = value.Flag };
    }

    private static KVObject ValuesOf(KVObject entity) => entity["keyValues3Data"]["values"];

    private static string Text(KVObject values, string key)
        => values.ContainsKey(key) && values[key].ValueType == KVValueType.String ? values[key].ToString() ?? "" : "";

    private static uint Unsigned(KVObject values, string key)
        => values.ContainsKey(key) && uint.TryParse(values[key].ToString(), NumberStyles.Integer,
                                                   CultureInfo.InvariantCulture, out var u) ? u : 0;

    private static Vector3 VectorOf(KVObject values, string key)
    {
        var parts = values[key].Values.Select(v => (float)Convert.ToDouble(v.ToString(), CultureInfo.InvariantCulture)).ToArray();
        return new Vector3(parts[0], parts[1], parts[2]);
    }

    private static KVObject Floats(Vector3 v)
    {
        var array = KVObject.Array();
        foreach (var component in new[] { v.X, v.Y, v.Z })
            array.Add(new KVObject((double)component));
        return array;
    }

    /// <summary>
    /// What default_ents carries before the template pass: the walk, minus the
    /// instance TEMPLATES, with each instance's copies spliced in where its
    /// parent's subtree finishes.
    ///
    /// <para>Valve's atixref puts the 17 copies made under one group at walk 672,
    /// where that group ends, and the 126 made at the world root after every
    /// walked entity. Every copy carries its template's compile_source_id.</para>
    /// </summary>
    private static List<EntityLumpAuthor.Emission> MainLump(
        IReadOnlyList<MapEntities.Entity> entities, DmxBinary.Document? document, Func<string, int>? smartPropLocators,
        IReadOnlyDictionary<int, Maps.SettleWorld.Settlement>? settled, FgdSchema? schema)
    {
        // A settled prop, placed or copied, stands where the settle left it
        // (SettleWorld.Run), with the keys CMapEntity_SetStartAsleep changed.
        MapEntities.Entity Settle(MapEntities.Entity e)
            => settled is not null && schema is not null && settled.TryGetValue(e.NodeId, out var s)
                ? e with
                {
                    Origin = s.Moved ? s.Origin : e.Origin,
                    Angles = s.Moved ? s.Angles : e.Angles,
                    Keys = Maps.SettleWorld.SettledKeys(e.ClassName, e.Keys, schema, s.Asleep),
                }
                : e;

        var (copies, templates) = document is null
            ? ((IReadOnlyList<MapInstances.Copy>)[], (IReadOnlySet<int>)new HashSet<int>())
            : MapInstances.Expand(document, entities,
                smartPropLocators is null ? 0 : SmartProps.NodesCreatedOnLoad(document, smartPropLocators));

        var pending = new Dictionary<int, List<EntityLumpAuthor.Emission>>();
        foreach (var copy in copies)
        {
            if (!pending.TryGetValue(copy.EmitAt, out var here))
                pending[copy.EmitAt] = here = [];
            here.Add(new EntityLumpAuthor.Emission(
                Settle(entities[copy.Template] with
                {
                    NodeId = copy.NodeId,
                    Origin = copy.Origin,
                    Angles = copy.Angles,
                    Layer = copy.Layer,
                    Instanced = true,
                }),
                copy.Template));
        }

        var emit = new List<EntityLumpAuthor.Emission>();
        for (var i = 0; i <= entities.Count; i++)
        {
            if (pending.TryGetValue(i, out var here))
                emit.AddRange(here);
            if (i < entities.Count && !templates.Contains(i))
                emit.Add(new EntityLumpAuthor.Emission(Settle(entities[i]), i));
        }
        return emit;
    }

    private static string PathOf(string worldName, string lumpName)
        => $"maps/{worldName}/entities/{lumpName}.vents_c".ToLowerInvariant();
}
