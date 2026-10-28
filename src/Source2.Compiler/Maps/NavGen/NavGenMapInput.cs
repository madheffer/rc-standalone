using Source2.Compiler.Io;

namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// The RC side of nav generation's input (resourcecompiler 20260923, the collectors 1800fc370 runs
/// into CNavGenInput): what is ported so far.
/// </summary>
public static partial class NavGenMapInput
{
    /// <summary>
    /// 180f21760 into CNavGenInput +0x78: every point_nav_walkable's world origin (the translation of
    /// its instance step matrix times its own), one per copy. Taken here from the entity lump's walk:
    /// the map's entities in tree order, prefabs included, each instance copy before the entity the
    /// lump writes it ahead of, templates only as their copies, hidden entities left out. Unsettled:
    /// that FUN_180fc7220's search and FUN_180f88460's paths order several seeds this way.
    /// </summary>
    public static List<(float X, float Y, float Z)> Seeds(DmxBinary.Document document)
    {
        var entities = MapEntities.From(document);
        var (copies, templates) = MapInstances.Expand(document, entities, prefabs: true);
        static bool Walkable(MapEntities.Entity e) => string.Equals(e.ClassName, "point_nav_walkable", StringComparison.OrdinalIgnoreCase);
        var seeds = new List<(float X, float Y, float Z)>();
        var c = 0;
        for (var i = 0; i <= entities.Count; i++)
        {
            for (; c < copies.Count && copies[c].EmitAt == i; c++)
                if (Walkable(entities[copies[c].Template]))
                    seeds.Add((copies[c].Origin.X, copies[c].Origin.Y, copies[c].Origin.Z));
            if (i < entities.Count && !templates.Contains(i) && !entities[i].Hidden && Walkable(entities[i]))
                seeds.Add((entities[i].Origin.X, entities[i].Origin.Y, entities[i].Origin.Z));
        }
        return seeds;
    }

    /// <summary>
    /// 180f203b0 into CNavGenInput +0xc0: each CMapNavLink that the search for "ai_nav_link_area"
    /// finds (180fc7220, flags 3: its classname or its targetname by tier0's V_CompareNameWithWildcards
    /// with flag 0, case-blind, tools/re/tier0_wildcard_oracle.py), as (start, end). The start is the
    /// translation of each instance step matrix times the node's (one copy here), the end the start
    /// plus locatoroffset in world axes, neither rotated nor scaled. s2c_navlink: Valve's three
    /// records, link0 at yaw 90 included. Refused until measured: a link inside an instance or prefab,
    /// one under a group, a hidden or editorOnly one (the search, flag 0, does not test visibility).
    /// </summary>
    public static List<((float X, float Y, float Z) A, (float X, float Y, float Z) B)> Links(DmxBinary.Document document)
    {
        var links = new List<((float X, float Y, float Z) A, (float X, float Y, float Z) B)>();
        var world = document.OfType("CMapWorld").FirstOrDefault();
        if (world is null)
            return links;
        var topLevel = world.GetElements("children").ToHashSet();
        foreach (var e in SettleWorld.AllDocuments(document).SelectMany(d => d.Elements).Where(e => e.Type == "CMapNavLink"))
        {
            if (!topLevel.Contains(e))
                throw new NotSupportedException("a CMapNavLink inside an instance, prefab or group (180f88460's paths) is not ported");
            if (e.GetValue<bool>("force_hidden") == true || e.GetValue<bool>("editorOnly") == true)
                throw new NotSupportedException("a hidden or editorOnly CMapNavLink is not measured");
        }
        foreach (var child in world.GetElements("children"))
        {
            if (child.Type != "CMapNavLink" || MapEntities.ReadNode(child) is not { NavLink: { } link } entity)
                continue;
            var name = entity.Keys.FirstOrDefault(k => Tier0Strings.EqualsIgnoreCase(k.Key, "targetname")).Value ?? "";
            if (!Tier0Strings.EqualsIgnoreCase(entity.ClassName, "ai_nav_link_area") && !Tier0Strings.EqualsIgnoreCase(name, "ai_nav_link_area"))
                continue;
            var o = entity.Origin;
            links.Add(((o.X, o.Y, o.Z), (o.X + link.Offset.X, o.Y + link.Offset.Y, o.Z + link.Offset.Z)));
        }
        return links;
    }
}
