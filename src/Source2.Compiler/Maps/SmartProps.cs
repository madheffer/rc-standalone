using ValveKeyValue;

namespace Source2.Compiler;

/// <summary>
/// What a map's smart props cost the node id count before the instance bake.
///
/// <para>The loader evaluates every CMapSmartProp and gives each locator its
/// definition creates a CMapSmartPropLocator node with a fresh id, before any
/// instance is collapsed; those ids come first, so every instanced entity's
/// hammerUniqueId moves with them. Measured by reading the nodes in memory when
/// the bake starts: atixref's one radiator_01 makes one locator (7248), Mako's
/// 19 train ladders and 14 wall AC units make 33 (33776 to 33808), and its two
/// floodlights and one industrial lamp make none.</para>
///
/// <para>A locator comes from a CreateLocator or CreateSizer operation, and
/// each of those definitions holds exactly one; whether an operation under an
/// unselected branch still makes one has no specimen.</para>
/// </summary>
public static class SmartProps
{
    private static readonly HashSet<string> LocatorOperations = new(StringComparer.Ordinal)
    {
        "CSmartPropOperation_CreateLocator",
        "CSmartPropOperation_CreateSizer",
    };

    /// <summary>The locators a compiled smart prop definition's DATA creates.</summary>
    public static int LocatorsOf(KVObject definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var count = 0;
        Visit(definition);
        return count;

        void Visit(KVObject value)
        {
            if (value.IsArray)
                foreach (var item in value.Values)
                    Visit(item);
            else if (value.ValueType == KVValueType.Collection)
                foreach (var (key, child) in value.Children)
                {
                    if (key == "_class" && child.ValueType == KVValueType.String
                        && LocatorOperations.Contains(child.ToString() ?? ""))
                        count++;
                    Visit(child);
                }
        }
    }

    /// <summary>
    /// The nodes the loader creates for a map's smart props, given a way to count
    /// one definition's locators by its path.
    /// </summary>
    public static int NodesCreatedOnLoad(DmxBinary.Document document, Func<string, int> locatorsOf)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(locatorsOf);
        return document.OfType("CMapSmartProp")
            .Select(e => e.Get<string>("smartPropFilename") ?? "")
            .Where(p => p.Length > 0)
            .Sum(locatorsOf);
    }
}
