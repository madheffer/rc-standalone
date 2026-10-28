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
    /// 180f21760 into CNavGenInput +0x78: the search the links take (180fc6f40 for "point_nav_walkable", mode 2 and
    /// flags 3, <see cref="Found"/>) with no IsA test, so every listed node of that game class; per instance path (one,
    /// the identity, after the bake) the translation of the path's step matrix times the node's (vf 0xa0, 181255fb0),
    /// appended in the list's order (<see cref="MapEntityList"/>). After the bake a copy stands where its TransformBy
    /// left it (<see cref="SettleWorld.Placed"/>, the placement the lump's copies take).
    /// </summary>
    public static List<(float X, float Y, float Z)> Seeds(DmxBinary.Document document)
    {
        var seeds = new List<(float X, float Y, float Z)>();
        // The list is only built for a map that has one: the search finds nothing in a map with none.
        if (!SettleWorld.AllDocuments(document).SelectMany(d => d.Elements).Any(e => Tier0Strings.EqualsIgnoreCase(
                e.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname") ?? "", "point_nav_walkable")))
            return seeds;
        var parents = Parents(document);
        foreach (var (item, entity, copied) in Found(document, "point_nav_walkable"))
        {
            if (copied && SettleWorld.IsUnderDeformer(item.Node, parents, Targets(item)))
                throw new NotSupportedException("a point_nav_walkable under a deformer is not measured");
            var o = copied ? SettleWorld.Placed(entity.Origin, entity.Angles, item.Through, item.Prefabs).Origin : entity.Origin;
            seeds.Add((o.X, o.Y, o.Z));
        }
        return seeds;
    }

    /// <summary>
    /// 180f203b0 into CNavGenInput +0xc0: the search (<see cref="Found"/> for "ai_nav_link_area") and the collector keeps
    /// the CMapNavLinks. Start is the node's origin and end start + locatoroffset, a copy's both as its locators leave
    /// them (<see cref="MapEntities.NavLinkFields.Copied"/>); CMapNavLink's SetScales (181065610) keeps scale 1.
    /// s2c_navlink and s2c_navlink4 (GROUND_TRUTH 175, 177): Valve's records, instance copies, a nested copy and the
    /// list's order included; s2c_navlink3 (GROUND_TRUTH 178) adds a prefab's links.
    /// </summary>
    public static List<((float X, float Y, float Z) A, (float X, float Y, float Z) B)> Links(DmxBinary.Document document)
    {
        var links = new List<((float X, float Y, float Z) A, (float X, float Y, float Z) B)>();
        // The list is only built for a map that has a link: the search finds nothing in one that has none.
        if (!SettleWorld.AllDocuments(document).Any(d => d.OfType("CMapNavLink").Any()))
            return links;
        foreach (var (item, entity, copied) in Found(document, "ai_nav_link_area"))
        {
            if (item.Node.Type != "CMapNavLink" || entity.NavLink is not { } link)
                continue;
            var (o, offset) = copied && link.Copied(entity.Origin, entity.Angles, item.Through, item.Prefabs) is var (co, _, cl)
                ? (co, cl.Offset)
                : (entity.Origin, link.Offset);
            links.Add(((o.X, o.Y, o.Z), (o.X + offset.X, o.Y + offset.Y, o.Z + offset.Z)));
        }
        return links;
    }

    /// <summary>
    /// The search 180fc6f40 (FUN_180fc7220 with mode 2 and flags 3) walks the world's entity list in stored order once
    /// the bake is done (<see cref="MapEntityList"/>) and takes each node whose classname (180f2ddc0, the game class's
    /// name) V_CompareNameWithWildcards(<paramref name="className"/>, name, 0) matches, case-blind
    /// (tools/re/tier0_wildcard_oracle.py), and whose visibility state (+0x370, flag 1) is on. Refused until measured:
    /// a hidden node inside an instance or prefab, a node under a group that moves, and two or more matches when the
    /// list's order is not measured (<see cref="MapEntityList.Baked(DmxBinary.Document, out bool)"/>).
    /// </summary>
    private static IEnumerable<(MapEntityList.Item Item, MapEntities.Entity Entity, bool Copied)> Found(DmxBinary.Document document, string className)
    {
        if (document.OfType("CMapWorld").FirstOrDefault() is not { } world)
            yield break;
        var parents = Parents(document);
        var hidden = MapEntities.HiddenNodes(document);
        var found = new List<(MapEntityList.Item, MapEntities.Entity, bool)>();
        foreach (var item in MapEntityList.Baked(document, out var orderMeasured))
        {
            if (MapEntities.ReadNode(item.Node) is not { } entity || !Tier0Strings.EqualsIgnoreCase(entity.ClassName, className))
                continue;
            var copied = item.Through.Count > 0 || item.Prefabs.Count > 0;
            // A prefab map's node ids are its own document's: its visibility manager hides them.
            var hiddenIds = item.Prefabs.Count > 0 && item.Prefabs[^1].Get<DmxBinary.Document>(MapPrefabs.DocumentKey) is { } prefabDoc
                ? MapEntities.HiddenNodes(prefabDoc)
                : hidden;
            var hiddenHere = false;
            for (var at = item.Node; at != null && !ReferenceEquals(at, world); at = parents.GetValueOrDefault(at))
            {
                if (ReferenceEquals(at, item.Node) ? MapEntityList.Hidden(at, hiddenIds) : at.GetValue<bool>("force_hidden") == true)
                    hiddenHere = true;
                if (!ReferenceEquals(at, item.Node) && at.Type != "CMapWorld" && !IsTarget(at, item)
                    && ((at.GetValue<Vector3>("origin") ?? Vector3.Zero) != Vector3.Zero || (at.GetValue<Vector3>("angles") ?? Vector3.Zero) != Vector3.Zero))
                    throw new NotSupportedException($"a {className} under a group that moves is not measured");
                if (IsTarget(at, item))
                    break;
            }
            if (hiddenHere)
            {
                if (copied)
                    throw new NotSupportedException($"a hidden {className} inside an instance or prefab is not measured");
                continue;
            }
            found.Add((item, entity, copied));
            if (found.Count > 1 && !orderMeasured)
                throw new NotSupportedException($"two {className} nodes in a map whose entity list order is not measured (a prefab map with several instances)");
        }
        foreach (var f in found)
            yield return f;
    }

    private static Dictionary<DmxBinary.Element, DmxBinary.Element> Parents(DmxBinary.Document document)
    {
        var parents = new Dictionary<DmxBinary.Element, DmxBinary.Element>(ReferenceEqualityComparer.Instance);
        foreach (var e in SettleWorld.AllDocuments(document).SelectMany(d => d.Elements))
            foreach (var child in e.GetElements("children"))
                parents.TryAdd(child, e);
        return parents;
    }

    // The instances' targets and the prefabs' map worlds an item was copied through: where IsUnderDeformer stops.
    private static HashSet<DmxBinary.Element> Targets(MapEntityList.Item item)
    {
        var targets = new HashSet<DmxBinary.Element>(ReferenceEqualityComparer.Instance);
        foreach (var i in item.Through)
            if (i.Get<DmxBinary.Element>("target") is { } t)
                targets.Add(t);
        foreach (var p in item.Prefabs)
            if (p.Get<DmxBinary.Element>(MapPrefabs.WorldKey) is { } w)
                targets.Add(w);
        return targets;
    }

    // The group a copy was made from (the innermost instance's target) or the prefab's map world: where the copy's
    // placement starts.
    private static bool IsTarget(DmxBinary.Element at, MapEntityList.Item item)
        => (item.Through.Count > 0 && ReferenceEquals(at, item.Through[^1].Get<DmxBinary.Element>("target")))
           || (item.Through.Count == 0 && item.Prefabs.Count > 0 && ReferenceEquals(at, item.Prefabs[^1].Get<DmxBinary.Element>(MapPrefabs.WorldKey)));
}
