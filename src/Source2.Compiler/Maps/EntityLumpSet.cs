using System.Globalization;

namespace Source2.Compiler;

/// <summary>
/// Every entity lump a map compiles to, not just <c>default_ents</c>.
///
/// <para>A <c>point_template</c> names its members in <c>Template01</c>..
/// <c>TemplateNN</c>, and the compile moves each of those entities OUT of
/// default_ents into a lump of the template's own. atixref has ten templates and
/// ten child lumps holding 57 entities between them; comparing only default_ents
/// makes those 57 look like entities we invented.</para>
/// </summary>
public static class EntityLumpSet
{
    /// <summary>One compiled lump and where it belongs in the map's VPK.</summary>
    /// <param name="Path">Path inside the package, which is lower case.</param>
    /// <param name="Name">The lump's own <c>m_name</c>, which is not.</param>
    public sealed record Lump(string Path, string Name, byte[] Bytes);

    /// <summary>
    /// Author the map's lumps: default_ents first, then one per point_template in
    /// the order the templates were walked.
    /// </summary>
    public static IReadOnlyList<Lump> Author(
        IReadOnlyList<MapEntities.Entity> entities,
        FgdSchema? schema,
        string worldName,
        bool fixupEntityNames = false,
        DmxBinary.Document? document = null)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var templates = Templates(entities);
        var lumpNames = templates.ToDictionary(t => entities[t.Index].NodeId, t => t.Name);

        // Claim members before anything is written. An entity belongs to the FIRST
        // template that names it, so a name two templates list does not land twice.
        var claimed = new HashSet<int>();
        foreach (var template in templates)
            foreach (var index in template.Members)
                claimed.Add(index);

        var lumps = new List<Lump>();
        foreach (var template in templates)
            lumps.Add(new Lump(
                PathOf(worldName, template.Name), template.Name,
                EntityLumpAuthor.Author(entities, schema, template.Name, childLumps: null,
                                        worldName, fixupEntityNames,
                                        [.. template.Members.Select(m => new EntityLumpAuthor.Emission(entities[m], m))],
                                        lumpNames,
                                        new EntityLumpAuthor.TemplateLump(
                                            entities[template.Index].Origin,
                                            SuffixOf(entities[template.Index]),
                                            template.Members.Select(m => NameOf(entities[m]))
                                                    .ToHashSet(StringComparer.OrdinalIgnoreCase)))));

        var children = lumps.Select(l => l.Path[..^2]).ToList();       // m_childLumps drops the _c
        lumps.Insert(0, new Lump(
            PathOf(worldName, "default_ents"), "default_ents",
            EntityLumpAuthor.Author(entities, schema, "default_ents", children,
                                    worldName, fixupEntityNames,
                                    MainLump(entities, claimed, document), lumpNames)));
        return lumps;
    }

    /// <summary>
    /// The suffix a template puts on its members' names.
    ///
    /// <para>spawnflags bit 1 is "preserve entity names". Without it the compile
    /// renames every member so an instantiation cannot collide with the map:
    /// atixref's deadpool_temp and snake_temp are its only two templates at
    /// spawnflags 0, and theirs are the only two lumps whose names carry
    /// <c>&amp;0000</c>.</para>
    /// </summary>
    private static string SuffixOf(MapEntities.Entity template)
    {
        var flags = template.Keys.FirstOrDefault(
            k => k.Key.Equals("spawnflags", StringComparison.OrdinalIgnoreCase)).Value;
        return int.TryParse(flags, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits)
            && (bits & 2) != 0 ? "" : "&0000";
    }

    /// <summary>
    /// What default_ents carries: the walk, minus what a child lump took and minus
    /// the instance TEMPLATES, with each instance's copies spliced in where the
    /// walk reached that instance.
    ///
    /// <para>Valve's atixref puts the 30 copies of one group at lump positions 76
    /// onward, in the middle of the walk rather than at its end, and every copy
    /// carries its template's compile_source_id.</para>
    /// </summary>
    private static List<EntityLumpAuthor.Emission> MainLump(
        IReadOnlyList<MapEntities.Entity> entities, HashSet<int> claimed, DmxBinary.Document? document)
    {
        var (placements, templates) = document is null
            ? ((IReadOnlyList<MapInstances.Placement>)[], (IReadOnlySet<int>)new HashSet<int>())
            : MapInstances.Find(document, entities);

        // Copies need an id of their own, and the compile gives each placement a
        // block as wide as its group plus one. WHERE the blocks start and in what
        // order is not solved: Valve's atixref runs them from 7268 over a map whose
        // own ids stop at 7247, in an order that is neither the walk's nor the
        // group's. These are ours, and they are the one thing about an instanced
        // copy that does not match.
        var nextId = entities.Count == 0 ? 1 : entities.Max(e => e.NodeId) + 1;
        var byPosition = new Dictionary<int, List<EntityLumpAuthor.Emission>>();
        foreach (var placement in placements)
        {
            var block = nextId;
            nextId += placement.Nodes + 1;
            if (!byPosition.TryGetValue(placement.AfterEntity, out var here))
                byPosition[placement.AfterEntity] = here = [];
            for (var i = 0; i < placement.Templates.Count; i++)
            {
                var template = placement.Templates[i];
                here.Add(new EntityLumpAuthor.Emission(
                    MapInstances.Place(entities[template], placement) with { NodeId = block + i },
                    template));
            }
        }

        var emit = new List<EntityLumpAuthor.Emission>();
        for (var i = 0; i < entities.Count; i++)
        {
            if (byPosition.TryGetValue(i, out var copies))
                emit.AddRange(copies);
            if (!claimed.Contains(i) && !templates.Contains(i))
                emit.Add(new EntityLumpAuthor.Emission(entities[i], i));
        }
        if (byPosition.TryGetValue(entities.Count, out var last))
            emit.AddRange(last);
        return emit;
    }

    private static string PathOf(string worldName, string lumpName)
        => $"maps/{worldName}/entities/{lumpName}.vents_c".ToLowerInvariant();

    private sealed record Template(int Index, string Name, List<int> Members);

    /// <summary>
    /// The point_templates that own a lump, in walk order, each with the walk
    /// indices of its members in the order its Template keys list them.
    ///
    /// <para>Both orders were read off Valve's own output: default_ents lists the
    /// ten child lumps by ascending template compile_source_id, and lump 187 holds
    /// its eighteen members in Template01..Template18 order rather than in the
    /// order the map walks them.</para>
    /// </summary>
    private static List<Template> Templates(IReadOnlyList<MapEntities.Entity> entities)
    {
        var byName = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < entities.Count; i++)
        {
            var name = NameOf(entities[i]);
            if (name.Length == 0)
                continue;
            if (!byName.TryGetValue(name, out var list))
                byName[name] = list = [];
            list.Add(i);
        }

        var templates = new List<Template>();
        for (var i = 0; i < entities.Count; i++)
        {
            if (!entities[i].ClassName.Equals("point_template", StringComparison.OrdinalIgnoreCase))
                continue;

            var members = new List<int>();
            foreach (var (key, value) in entities[i].Keys)
            {
                if (!key.StartsWith("Template", StringComparison.OrdinalIgnoreCase)
                    || !int.TryParse(key.AsSpan("Template".Length), NumberStyles.Integer,
                                     CultureInfo.InvariantCulture, out _)
                    || value.Length == 0)
                    continue;
                foreach (var member in byName.GetValueOrDefault(value) ?? [])
                    if (!members.Contains(member))
                        members.Add(member);
            }
            templates.Add(new Template(
                i, entities[i].NodeId.ToString(CultureInfo.InvariantCulture) + "#entityLumpName", members));
        }
        return templates;
    }

    /// <summary>The entity's targetname as the SOURCE states it, before any prefab
    /// fixup: a template names its members the way the mapper typed them.</summary>
    private static string NameOf(MapEntities.Entity entity)
        => entity.Keys.FirstOrDefault(k => k.Key.Equals("targetname", StringComparison.OrdinalIgnoreCase)).Value
           ?? string.Empty;
}
