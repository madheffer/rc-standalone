using ValveKeyValue;
using ValveResourceFormat.ResourceTypes;

namespace Source2.Compiler.Maps;

/// <summary>
/// The <c>maps/&lt;map&gt;.nav</c> resourcecompiler's nav step writes (1800fc370) when it generates
/// nothing. The seeds are every point_nav_walkable's world origin, prefab instances included
/// (180f21760). With none, navsystem logs "&gt;&gt;NAVGEN: Skipped... no walkable seeds present"
/// and the mesh has no areas. The generation settings come from the map's CMapNavData node
/// (180fafae0), else the defaults ("Nav settings not found, using default settings.").
/// A map with neither gets one fixed 834-byte file, identical on every such map captured
/// (ze_hold_em_p, ze_doom_p2 and its gameplay map, c2m2's gameplay map, four probes; CS2
/// 1.41.9.1): magic 0xFEEDFACE, version 36, two empty KV3 blocks, the default generation
/// parameters, NavGenParams (the player hull, no flow map) and an empty arealist. The KV3
/// blocks come out of Kv3V5Writer as Valve's do. Generating a real mesh is not ported.
/// </summary>
public static class NavFile
{
    /// <summary>Whether the map's nav needs the generator: a point_nav_walkable anywhere, or a CMapNavData node.</summary>
    public static bool NeedsGeneration(DmxBinary.Document document)
        => SettleWorld.AllDocuments(document).SelectMany(d => d.Elements).Any(e =>
            e.Type == "CMapNavData"
            || string.Equals(e.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname"), "point_nav_walkable", StringComparison.OrdinalIgnoreCase));

    // The generic KV3 format the nav's blocks use (7c161274-e906-4698-aff2-e63eb59037e7).
    private static readonly Guid Format = new([0x7c, 0x16, 0x12, 0x74, 0xe9, 0x06, 0x98, 0x46, 0xaf, 0xf2, 0xe6, 0x3e, 0xb5, 0x90, 0x37, 0xe7]);

    // The area list's own KV3 format (as stored: 29 f0 00 73 00 8e f9 45 b9 c3 32 e1 40 3c 96 20).
    private static readonly Guid AreaListFormat = new([0x29, 0xf0, 0x00, 0x73, 0x00, 0x8e, 0xf9, 0x45, 0xb9, 0xc3, 0x32, 0xe1, 0x40, 0x3c, 0x96, 0x20]);

    /// <summary>The nav of a map with no seeds and no CMapNavData.</summary>
    public static byte[] EmptyDefault()
    {
        var m = new MemoryStream();
        var w = new BinaryWriter(m);
        w.Write(0xFEEDFACEu);
        w.Write(36);
        w.Write(1);
        w.Write(1);
        w.Write(Kv3V5Writer.Write(KVObject.Null(), Format, KV3BinaryCompressionMethod.Uncompressed));
        w.Write(new byte[23]);
        w.Write(Kv3V5Writer.Write(KVObject.Null(), Format, KV3BinaryCompressionMethod.Uncompressed));
        w.Write(DefaultGenerationParameters);
        var hull = KVObject.Collection();
        hull.Add("Name", new KVObject("player"));
        hull.Add("FlowMap_Enabled", new KVObject(false));
        hull.Add("FlowMap_NodeMaxRadius", new KVObject(400.0));
        var hulls = KVObject.Array();
        hulls.Add(hull);
        var generation = KVObject.Collection();
        generation.Add("HullParams", hulls);
        var parameters = KVObject.Collection();
        parameters.Add("NavGenParams", generation);
        w.Write(Kv3V5Writer.Write(parameters, Format, KV3BinaryCompressionMethod.Uncompressed));
        w.Write(new byte[5]);
        var areas = KVObject.Collection();
        areas.Add("arealist", KVObject.Array());
        w.Write(Kv3V5Writer.Write(areas, AreaListFormat, KV3BinaryCompressionMethod.Uncompressed));
        return m.ToArray();
    }

    // The 119 bytes between the second empty block and NavGenParams, as navsystem writes its
    // default settings (the hull's and the generator's numbers; not decoded field by field).
    private static readonly byte[] DefaultGenerationParameters = Convert.FromHexString(
        "0000000000000000000000000D00000001000000000000430000C03F0000004008000000140000000000F04200000040B004"
        + "00000000344204000000000080BF000001000000010000804100008E420100000E420000008C41000080413200000000001D43"
        + "000080420000884200000000000000000000");
}
