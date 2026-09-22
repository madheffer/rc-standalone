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
    /// <param name="Bytes">The compiled lump.</param>
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
    /// the instance TEMPLATES, with each instance's copies spliced in where its
    /// parent's subtree finishes.
    ///
    /// <para>Valve's atixref puts the 17 copies made under one group at walk 672,
    /// where that group ends, and the 126 made at the world root after every
    /// walked entity. Every copy carries its template's compile_source_id.</para>
    /// </summary>
    private static List<EntityLumpAuthor.Emission> MainLump(
        IReadOnlyList<MapEntities.Entity> entities, HashSet<int> claimed, DmxBinary.Document? document)
    {
        var (copies, templates) = document is null
            ? ((IReadOnlyList<MapInstances.Copy>)[], (IReadOnlySet<int>)new HashSet<int>())
            : MapInstances.Expand(document, entities);

        var pending = new Dictionary<int, List<EntityLumpAuthor.Emission>>();
        foreach (var copy in copies)
        {
            if (!pending.TryGetValue(copy.EmitAt, out var here))
                pending[copy.EmitAt] = here = [];
            here.Add(new EntityLumpAuthor.Emission(
                entities[copy.Template] with
                {
                    NodeId = copy.NodeId,
                    Origin = copy.Origin,
                    Angles = copy.Angles,
                },
                copy.Template));
        }

        var emit = new List<EntityLumpAuthor.Emission>();
        for (var i = 0; i <= entities.Count; i++)
        {
            if (pending.TryGetValue(i, out var here))
                emit.AddRange(here);
            if (i < entities.Count && !claimed.Contains(i) && !templates.Contains(i))
                emit.Add(new EntityLumpAuthor.Emission(entities[i], i));
        }
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
