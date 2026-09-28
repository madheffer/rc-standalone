using System.Globalization;

namespace Source2.Compiler.Maps;

/// <summary>
/// A map compile's command line as resourcecompiler reads it, and the builders
/// it selects. Hammer's build dialog writes this line (hammer.dll's command
/// line assembler); the parse and the selection are resourcecompiler's.
/// </summary>
public sealed class MapCompileArgs
{
    /// <summary>The builder names in resourcecompiler's table (FUN_1801f7840
    /// reads each with GetBool), in the order the compile runs their steps.</summary>
    public static readonly string[] Builders =
        ["world", "phys", "vis", "lod", "gridnav", "nav", "bakedlighting", "sareverb", "sapaths", "sacustomdata"];

    /// <summary>
    /// Which builders the game allows: world, phys and vis by the code's own
    /// default, the rest from csgo_core's gameinfo DefaultMapBuilders
    /// (bakedlighting, nav, sareverb, sapaths, sacustomdata). CS2 has no LOD
    /// or grid nav builder.
    /// </summary>
    public static readonly HashSet<string> GameBuilders =
        ["world", "phys", "vis", "bakedlighting", "nav", "sareverb", "sapaths", "sacustomdata"];

    /// <summary>The switches the parser handles itself (FUN_18018b630), with
    /// whether each takes a value. Everything else is a compile argument.</summary>
    private static readonly Dictionary<string, bool> Special = new(StringComparer.OrdinalIgnoreCase)
    {
        ["?"] = false, ["h"] = false, ["help"] = false, ["telemetry_level"] = true, ["allowdebug"] = false,
        ["novpk"] = false, ["norevert"] = false, ["vpkincr"] = false, ["f"] = false, ["fshallow"] = false,
        ["fshallow2"] = false, ["dependency_check_only"] = false, ["v"] = false, ["r"] = false, ["nop4"] = false,
        ["pauseiferror"] = false, ["pause"] = false, ["filelist"] = true, ["i"] = true, ["game"] = true,
        ["outroot"] = true, ["html"] = false, ["crc"] = false, ["breakpad"] = false, ["logwarnings"] = false,
        ["changelist"] = true, ["skiptype"] = true,
    };

    /// <summary>The .vmap to compile (<c>-i</c>).</summary>
    public string? Input { get; private set; }

    /// <summary>Where the compiled map goes (<c>-outroot</c>); null means the game folder.</summary>
    public string? OutRoot { get; private set; }

    /// <summary>The parser's own switches that were given, lower case.</summary>
    public HashSet<string> Switches { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The compile arguments, as the parser types them: a value that reads as
    /// a float is a float, else as an int an int, else a string; a switch
    /// with no value is the int 1.
    /// </summary>
    public Dictionary<string, object> Arguments { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static MapCompileArgs Parse(IReadOnlyList<string> args)
    {
        var parsed = new MapCompileArgs();
        for (var k = 0; k < args.Count; k++)
        {
            var token = args[k];
            if (!token.StartsWith('-') || token.Length < 2)
                throw new ArgumentException($"unexpected '{token}': every argument is a -switch");
            var name = token[1..];
            var next = k + 1 < args.Count && !args[k + 1].StartsWith('-') ? args[k + 1] : null;
            if (Special.TryGetValue(name, out var takesValue))
            {
                parsed.Switches.Add(name);
                if (!takesValue)
                    continue;
                if (next is null)
                    throw new ArgumentException($"-{name} needs a value");
                k++;
                if (name.Equals("i", StringComparison.OrdinalIgnoreCase))
                    parsed.Input = next;
                else if (name.Equals("outroot", StringComparison.OrdinalIgnoreCase))
                    parsed.OutRoot = next;
                continue;
            }
            if (next is null)
                parsed.Arguments[name] = 1;
            else
            {
                k++;
                parsed.Arguments[name] = float.TryParse(next, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f
                    : int.TryParse(next, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i
                    : next;
            }
        }
        return parsed;
    }

    /// <summary>GetBool: GetInt != 0, where an int argument is read as is,
    /// a float one truncated, and a string one (or none) is 0.</summary>
    public bool GetBool(string name) => Arguments.TryGetValue(name, out var v) && v switch
    {
        int i => i != 0,
        float f => (int)f != 0,
        _ => false,
    };

    /// <summary>
    /// The builders the compile runs (FUN_1801f7840): with <c>all</c>, or when
    /// no builder is named, every one the game allows; otherwise the named
    /// ones, <c>entities</c> counting as world. Each is limited to the game's.
    /// </summary>
    public HashSet<string> SelectedBuilders(out bool partial)
    {
        var chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!GetBool("all"))
        {
            foreach (var name in Builders)
                if (GetBool(name))
                    chosen.Add(name);
            if (GetBool("entities"))
                chosen.Add("world");
        }
        partial = chosen.Count > 0;
        if (!partial)
            chosen.UnionWith(Builders);
        chosen.IntersectWith(GameBuilders);
        return chosen;
    }

    /// <summary>Entities only: world not asked for, not all, and entities asked
    /// for (CompileMap's ctx+0x604).</summary>
    public bool EntitiesOnly => !GetBool("world") && !GetBool("all") && GetBool("entities");

    /// <summary>Whether the physics settle runs: nosettle clears it.</summary>
    public bool Settle => !GetBool("nosettle");

    /// <summary>
    /// The force mode: none or -fshallow keep the package already there and
    /// replace only what the builders write; -f and -fshallow2 start empty, and
    /// the compile refuses a partial build then.
    /// </summary>
    public bool KeepsPackage => !Switches.Contains("f") && !Switches.Contains("fshallow2");
}
