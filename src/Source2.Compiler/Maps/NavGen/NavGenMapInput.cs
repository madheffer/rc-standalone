using System.Numerics;
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
    /// 180f203b0 into CNavGenInput +0xc0: the search (180fc6f40: FUN_180fc7220 with mode 2 and flags 3) walks the
    /// world's entity list in stored order once the bake is done (<see cref="MapEntityList"/>) and takes each CMapEntity
    /// whose classname (180f2ddc0, the game class's name) V_CompareNameWithWildcards("ai_nav_link_area", name, 0)
    /// matches, case-blind (tools/re/tier0_wildcard_oracle.py), and whose visibility state (+0x370, flag 1) is on; the
    /// collector keeps the CMapNavLinks. After the bake every node has one instance path, the identity, so start is
    /// the node's origin and end start + locatoroffset, a copy's both as its locators leave them
    /// (<see cref="MapEntities.NavLinkFields.Copied"/>); CMapNavLink's SetScales (181065610) keeps scale 1.
    /// s2c_navlink and s2c_navlink4 (GROUND_TRUTH 175, 177): Valve's records, instance copies, a nested copy and the
    /// list's order included; s2c_navlink3 (GROUND_TRUTH 178) adds a prefab's links. Refused until measured: a hidden
    /// link inside an instance or prefab, and a link under a group that moves.
    /// </summary>
    public static List<((float X, float Y, float Z) A, (float X, float Y, float Z) B)> Links(DmxBinary.Document document)
    {
        var links = new List<((float X, float Y, float Z) A, (float X, float Y, float Z) B)>();
        // The list is only built for a map that has a link: the search finds nothing in one that has none.
        if (document.OfType("CMapWorld").FirstOrDefault() is not { } world
            || !SettleWorld.AllDocuments(document).Any(d => d.OfType("CMapNavLink").Any()))
            return links;
        var parents = new Dictionary<DmxBinary.Element, DmxBinary.Element>(ReferenceEqualityComparer.Instance);
        foreach (var e in SettleWorld.AllDocuments(document).SelectMany(d => d.Elements))
            foreach (var child in e.GetElements("children"))
                parents.TryAdd(child, e);
        var hidden = MapEntities.HiddenNodes(document);
        foreach (var item in MapEntityList.Baked(document))
        {
            if (item.Node.Type != "CMapNavLink" || MapEntities.ReadNode(item.Node) is not { NavLink: { } link } entity)
                continue;
            if (!Tier0Strings.EqualsIgnoreCase(entity.ClassName, "ai_nav_link_area"))
                continue;
            var copied = item.Through.Count > 0 || item.Prefabs.Count > 0;
            var hiddenHere = false;
            for (var at = item.Node; at != null && !ReferenceEquals(at, world); at = parents.GetValueOrDefault(at))
            {
                if (ReferenceEquals(at, item.Node) ? MapEntityList.Hidden(at, hidden) : at.GetValue<bool>("force_hidden") == true)
                    hiddenHere = true;
                if (!ReferenceEquals(at, item.Node) && at.Type != "CMapWorld" && !IsTarget(at, item)
                    && ((at.GetValue<Vector3>("origin") ?? Vector3.Zero) != Vector3.Zero || (at.GetValue<Vector3>("angles") ?? Vector3.Zero) != Vector3.Zero))
                    throw new NotSupportedException("a CMapNavLink under a group that moves is not measured");
                if (IsTarget(at, item))
                    break;
            }
            if (hiddenHere)
            {
                if (copied)
                    throw new NotSupportedException("a hidden CMapNavLink inside an instance or prefab is not measured");
                continue;
            }
            var (o, offset) = copied && link.Copied(entity.Origin, entity.Angles, item.Through, item.Prefabs) is var (co, _, cl)
                ? (co, cl.Offset)
                : (entity.Origin, link.Offset);
            links.Add(((o.X, o.Y, o.Z), (o.X + offset.X, o.Y + offset.Y, o.Z + offset.Z)));
        }
        return links;
    }

    // The group a copy was made from (the innermost instance's target) or the prefab's map world: where the copy's
    // placement starts.
    private static bool IsTarget(DmxBinary.Element at, MapEntityList.Item item)
        => (item.Through.Count > 0 && ReferenceEquals(at, item.Through[^1].Get<DmxBinary.Element>("target")))
           || (item.Through.Count == 0 && item.Prefabs.Count > 0 && ReferenceEquals(at, item.Prefabs[^1].Get<DmxBinary.Element>(MapPrefabs.WorldKey)));
}
