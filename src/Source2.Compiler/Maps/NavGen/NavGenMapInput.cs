namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// The RC side of nav generation's input (resourcecompiler 20260923, the collectors 1800fc370 runs
/// into CNavGenInput): what is ported so far.
/// </summary>
public static class NavGenMapInput
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
}
