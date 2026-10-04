using Source2.Compiler.Io;
namespace Source2.Compiler.Physics;

/// <summary>
/// The collision group and interaction layer names vphysics2 registers in its
/// intersection dictionary (rubikon/intersectiondictionary.cpp; the name pool
/// in vphysics2.dll 0924 beside "Interaction Layers" and "Collision Groups").
///
/// <para>A prop's shapes carry their model's cooked collision attribute, and
/// physicsbuilder (180153d40) turns it back into strings through the
/// dictionary: the group index's registered name, and each interaction bit's.
/// So a prop registers "Default" and "ConditionallySolid" however its model
/// spells them, while a world material keeps its own spelling ("default",
/// "conditionallysolid"): atixref, ze_hold_em_p and c2m2 show both. Names the
/// game registers itself (csgo_thrown_grenade and the like) are not in
/// vphysics2's pool and keep the model's spelling.</para>
/// </summary>
public static class CollisionNames
{
    private static readonly string[] Registered =
    [
        "always", "never", "trigger", "ConditionallySolid", "hitboxes", "sky", "coarse", "hires", "Default",
        "interactive_debris", "breakable_glass", "vehicle", "player_movement", "npc", "in_vehicle", "weapon",
        "Projectile", "door_blocker", "passable_door", "dissolving", "pushaway", "npc_actor", "npc_scripted",
        "pz_clip", "props", "playerclip", "npcclip", "blocklos", "blocklight", "ladder", "pickup", "blocksound",
        "window", "water", "slime", "passbullets", "worldGeometry", "touchall", "player", "physics_prop",
        "CarriedObject", "serverentityonclient", "CarriedWeapon", "StaticLevel", "NavIgnore", "NavLocalIgnore",
        "PostProcessingVolume", "vehicleclip", "CONTENTS_SOLID", "CONTENTS_SOLID_NO_BLOCK_LOS",
    ];

    private static readonly Dictionary<string, string> ByName =
        Registered.ToDictionary(n => n, n => n, Tier0Strings.IgnoreCase);

    /// <summary>A name as the dictionary spells it, or as given when vphysics2 does not register it.</summary>
    public static string Canonical(string name) => ByName.TryGetValue(name, out var c) ? c : name;

    /// <summary>A space or comma separated list of names, each as the dictionary spells it.</summary>
    public static string CanonicalList(string list)
        => string.Join(" ", list.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(Canonical));
}
