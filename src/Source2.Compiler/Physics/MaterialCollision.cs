namespace Source2.Compiler.Physics;

/// <summary>
/// What the map builders read off a material for collision: physicsbuilder's
/// reader for world collision (0924: 1800132f0) and resourcecompiler's copy
/// for the settle (0923: FUN_1802e3120) are the same code over the same table.
///
/// <para>The attributes go through the loaded material (its vfn 0x48), which
/// answers the vmat_c's int attributes and the shader's own attributes for
/// the material's static combo. Of the table's keys only "translucent" is a
/// shader attribute (<see cref="Translucent"/>).</para>
/// </summary>
public static class MaterialCollision
{
    /// <summary>
    /// A material's collision: whether it is solid, its collision group and
    /// its interact-as, -with and -exclude lists (", " joined), and the two
    /// comma lists of string attributes 0xbc105ab and 0x58df1f2b (unnamed).
    /// </summary>
    public sealed record Result(bool Solid, string Group, string InteractAs, string InteractWith, string InteractExclude, string[] ListA, string[] ListB)
    {
        public static readonly Result Default = new(true, "", "", "", "", [], []);
    }

    // The table (physicsbuilder 0924: 180ba72b0): attribute, collision group,
    // interact-as tag, whether a match leaves the material solid, whether it
    // forces it solid, and whether it applies only to drawn materials.
    private static readonly (string Attribute, string Group, string Tag, bool Keeps, bool Forces, bool DrawnOnly)[] Table =
    [
        ("mapbuilder.nodraw", "", "", true, false, false),
        ("mapbuilder.nonsolid", "", "", false, false, false),
        ("mapbuilder.ladder", "", "ladder", true, false, false),
        ("mapbuilder.blocklos", "conditionallysolid", "blocklos", true, false, false),
        ("mapbuilder.blocksound", "conditionallysolid", "blocksound", true, false, false),
        ("mapbuilder.passbullets", "conditionallysolid", "passbullets", true, false, false),
        ("mapbuilder.npcclip", "conditionallysolid", "npcclip", true, false, false),
        ("mapbuilder.playerclip", "conditionallysolid", "playerclip", true, false, false),
        ("mapbuilder.sky", "conditionallysolid", "sky", true, false, false),
        ("mapbuilder.water", "conditionallysolid", "water", true, true, false),
        ("mapbuilder.teleportclip", "conditionallysolid", "teleportclip", true, false, false),
        ("mapbuilder.navclip", "conditionallysolid", "navclip", true, false, false),
        ("translucent", "conditionallysolid", "window", true, false, true),
    ];

    private const uint SecondDrawnKey = 0x84ce10dd;
    private const uint ListAKey = 0x0bc105ab;
    private const uint ListBKey = 0x58df1f2b;

    /// <summary>
    /// Whether a shader declares "translucent" for the material's features
    /// (<see cref="ShaderAttributes.Translucent"/>, csgo and csgo_core shaders).
    /// </summary>
    public static bool Translucent(string shader, IReadOnlyDictionary<string, long> features)
        => ShaderAttributes.Translucent(shader, features);

    /// <summary>
    /// The reader. A missing material is solid with nothing else. Otherwise:
    /// <list type="number">
    /// <item>"Drawn" is not nodraw and int attribute 0x84ce10dd zero.</item>
    /// <item>The table in order: a match that does not keep the material solid
    /// clears it unless a forcing match came first; a group replaces the
    /// last; tags join with ", ".</item>
    /// <item><c>mapbuilder.collisionproperties</c> names an entry of
    /// scripts/collision_properties.txt: its group replaces the group, the
    /// material is solid, and its three lists are appended.</item>
    /// <item>Tags holding "water" (any case) lose ", window".</item>
    /// <item>The two unnamed string lists, split on commas; either one
    /// non-empty makes the material solid.</item>
    /// </list>
    /// </summary>
    /// <param name="shaderTranslucency">Whether a shader's translucency counts (the world's
    /// pieces); a brush entity's does not: Mako's baggage glass breakables are
    /// default, atixref's glass is window only through its collision property.</param>
    public static Result Read(Maps.SettleWorld.MaterialInfo? info, Func<string, Maps.SettleWorld.CollisionProperty?> collisionProperty,
        bool shaderTranslucency = true)
    {
        if (info == null)
            return Result.Default;
        var translucent = shaderTranslucency && Translucent(info.Shader, info.Params);
        bool On(string key) => key == "translucent" ? translucent : info.Ints.TryGetValue(key, out var v) && v != 0;
        var drawn = !On("mapbuilder.nodraw") && IntByHash(info, SecondDrawnKey) == 0;
        bool solid = true, forced = false;
        string group = "", tags = "", with = "", exclude = "";
        foreach (var (attribute, g, tag, keeps, forces, drawnOnly) in Table)
        {
            if ((drawnOnly && !drawn) || !On(attribute))
                continue;
            if (!keeps && !forced)
                solid = false;
            else if (forces)
            {
                solid = true;
                forced = true;
            }
            if (g.Length > 0)
                group = g;
            if (tag.Length > 0)
                tags = Join(tags, tag);
        }
        if (StringByHash(info, Maps.SettleWorld.NameHash("mapbuilder.collisionproperties")) is { } named && collisionProperty(named) is { } p)
        {
            group = p.Group;
            solid = true;
            tags = Join(tags, p.InteractAs);
            with = Join(with, p.InteractWith);
            exclude = Join(exclude, p.InteractExclude);
        }
        // V_stristr_fast, then CUtlString::Remove with case sensitivity off
        // (physicsbuilder 180013bc1 passes 0): every ", window", A-Z folded.
        if (Io.Tier0Strings.StriStr(tags, "water") >= 0)
            tags = Io.Tier0Strings.RemoveIgnoreCase(tags, ", window");
        // V_SplitString(list, ",", false): empty pieces dropped, none trimmed.
        var listA = StringByHash(info, ListAKey) is { } a ? Io.Tier0Strings.SplitString(a, ",", false).ToArray() : [];
        var listB = StringByHash(info, ListBKey) is { } b ? Io.Tier0Strings.SplitString(b, ",", false).ToArray() : [];
        if (listA.Length != 0 || listB.Length != 0)
            solid = true;
        return new Result(solid, group, tags, with, exclude, listA, listB);
    }

    // Appending to a non-empty list adds ", " and the text, even empty text.
    private static string Join(string a, string b) => a.Length == 0 ? b : a + ", " + b;

    private static long IntByHash(Maps.SettleWorld.MaterialInfo info, uint hash)
        => info.Ints.FirstOrDefault(kv => Maps.SettleWorld.NameHash(kv.Key) == hash).Value;

    private static string? StringByHash(Maps.SettleWorld.MaterialInfo info, uint hash)
        => info.Strings.FirstOrDefault(kv => Maps.SettleWorld.NameHash(kv.Key) == hash).Value;
}
