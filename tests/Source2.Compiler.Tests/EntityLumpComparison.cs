using System.Globalization;
using System.Text;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler.Tests;

/// <summary>
/// Compares two compiled entity lumps key by key, with value TYPES, and reports
/// every difference at once.
///
/// <para>Reporting all of them matters more than it sounds. An entity lump is a
/// few thousand keys and the rules behind them are per-class, so a comparison that
/// stops at the first mismatch turns rule discovery into one rule per test run.
/// Types are compared all the way down, flags included, because
/// <c>enabled = true</c> and <c>enabled = "1"</c> print the same and are not the
/// same to the entity system, and a colour of Int32 channels is not the colour
/// of UInt32 channels Valve writes. Connections compare every field, and key
/// ORDER is compared too, since the lump is only byte-comparable when it holds.
/// </para>
/// </summary>
internal static class EntityLumpComparison
{
    /// <summary>One entity, flattened to what the engine reads.</summary>
    internal sealed record Entity(
        string ClassName,
        string HammerId,
        IReadOnlyDictionary<string, (KVValueType Type, string Value)> Values,
        IReadOnlyList<string> KeyOrder,
        IReadOnlyList<string> Connections);

    /// <summary>Read the entities of a compiled lump.</summary>
    public static IReadOnlyList<Entity> Read(byte[] lump, string name = "lump.vents_c")
    {
        using var resource = ResourceTrees.Read(lump, name);
        var data = resource.DataBlock as EntityLump
                   ?? throw new InvalidOperationException($"{name} has no entity lump DATA block.");

        var entities = new List<Entity>();
        foreach (var entity in data.Data.GetArray("m_entityKeyValues"))
        {
            var values = new Dictionary<string, (KVValueType, string)>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            var tree = entity.GetSubCollection("keyValues3Data");
            foreach (var kv in tree?.GetSubCollection("values")?.Children ?? [])
            {
                values[kv.Key] = (kv.Value.ValueType, Render(kv.Value));
                order.Add(kv.Key);
            }
            // version and the attributes block are part of every entity too.
            if (tree?.ContainsKey("version") == true)
                values["<version>"] = (tree["version"].ValueType, Render(tree["version"]));

            var connections = entity.GetArray("m_connections").Select(Render).ToList();
            entities.Add(new Entity(
                values.TryGetValue("classname", out var cls) ? cls.Item2[(cls.Item2.IndexOf(':') + 1)..] : "<no classname>",
                values.TryGetValue("hammerUniqueId", out var id) ? id.Item2[(id.Item2.IndexOf(':') + 1)..] : "",
                values, order, connections));
        }
        return entities;
    }

    /// <summary>
    /// Every way <paramref name="mine"/> differs from <paramref name="valve"/>,
    /// one line each. Empty means the two lumps say the same thing.
    /// </summary>
    public static IReadOnlyList<string> Diff(IReadOnlyList<Entity> valve, IReadOnlyList<Entity> mine)
    {
        var report = new List<string>();
        if (valve.Count != mine.Count)
            report.Add($"entity count: valve {valve.Count}, ours {mine.Count}");

        // Match on Hammer's node id, occurrence by occurrence: a template that
        // names one entity twice ships two copies with one id.
        var byId = new Dictionary<string, Queue<Entity>>();
        foreach (var e in mine.Where(e => e.HammerId.Length > 0))
        {
            if (!byId.TryGetValue(e.HammerId, out var queue))
                byId[e.HammerId] = queue = new();
            queue.Enqueue(e);
        }
        if (!valve.Select(e => e.HammerId).SequenceEqual(mine.Select(e => e.HammerId)))
            report.Add("entity order differs");

        for (var i = 0; i < valve.Count; i++)
        {
            var theirs = valve[i];
            var ours = theirs.HammerId.Length > 0 && byId.TryGetValue(theirs.HammerId, out var queue) && queue.Count > 0
                ? queue.Dequeue()
                : null;
            if (ours is null)
            {
                report.Add($"[{Label(theirs)}] missing from ours");
                continue;
            }

            foreach (var (key, (type, value)) in theirs.Values)
            {
                if (!ours.Values.TryGetValue(key, out var got))
                    report.Add($"[{Label(theirs)}] {key}: missing (valve {value})");
                else if (got.Type != type || !SameValue(got.Value, value))
                    report.Add($"[{Label(theirs)}] {key}: value valve {value}, ours {got.Value}");
            }
            // LUMPDIFF_KEYS=<class#id>: both key orders of that entity, whatever else differs.
            if (Environment.GetEnvironmentVariable("LUMPDIFF_KEYS") is { Length: > 0 } one && Label(theirs).StartsWith(one, StringComparison.Ordinal))
                report.Add($"[{Label(theirs)}] keys: valve {string.Join(",", theirs.KeyOrder)} ours {string.Join(",", ours.KeyOrder)}");
            foreach (var key in ours.Values.Keys.Where(k => !theirs.Values.ContainsKey(k)))
                report.Add($"[{Label(theirs)}] {key}: ours only ({ours.Values[key].Value})");
            if (theirs.KeyOrder.Count == ours.KeyOrder.Count && theirs.Values.Keys.All(ours.Values.ContainsKey)
                && !theirs.KeyOrder.SequenceEqual(ours.KeyOrder, StringComparer.Ordinal))
                report.Add($"[{Label(theirs)}] key order differs" + (Environment.GetEnvironmentVariable("LUMPDIFF_ORDER") is null ? ""
                           : $": valve {string.Join(",", theirs.KeyOrder)} ours {string.Join(",", ours.KeyOrder)}"));

            if (!theirs.Connections.SequenceEqual(ours.Connections))
                for (var c = 0; c < Math.Max(theirs.Connections.Count, ours.Connections.Count); c++)
                {
                    var a = c < theirs.Connections.Count ? theirs.Connections[c] : "-";
                    var b = c < ours.Connections.Count ? ours.Connections[c] : "-";
                    if (a != b)
                        report.Add($"[{Label(theirs)}] connection {c}: valve {a}, ours {b}");
                }
        }
        // What ours has that Valve's lacks: ids left unmatched, and entities with no id.
        foreach (var left in byId.Values.SelectMany(q => q))
            report.Add($"[{Label(left)}] ours only");
        var theirsWithoutId = valve.Count(e => e.HammerId.Length == 0);
        var oursWithoutId = mine.Where(e => e.HammerId.Length == 0).ToList();
        if (oursWithoutId.Count != theirsWithoutId)
            report.Add($"entities without an id: valve {theirsWithoutId}, ours {oursWithoutId.Count} ({string.Join(", ", oursWithoutId.Select(e => e.ClassName).Take(5))})");
        return report;
    }

    /// <summary>A report trimmed for an assertion message, with the total kept.</summary>
    public static string Summarize(IReadOnlyList<string> report, int show = 30)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{report.Count} difference(s):");
        foreach (var line in report.Take(show))
            sb.AppendLine("  " + line);
        if (report.Count > show)
            sb.AppendLine($"  ... and {report.Count - show} more");
        return sb.ToString();
    }

    private static string Label(Entity e) => $"{e.ClassName}#{e.HammerId}";

    /// <summary>
    /// Values print with their type and flag at every level, so a Resource flag or
    /// an array element's width is part of the comparison.
    /// </summary>
    private static string Render(KVObject v)
        => v.IsArray ? "[" + string.Join(", ", v.Values.Select(Render)) + "]"
         : v.ValueType == KVValueType.Collection ? "{" + string.Join(", ", v.Children.Select(c => c.Key + "=" + Render(c.Value))) + "}"
         : v.ValueType == KVValueType.BinaryBlob ? $"<blob {v.AsBlob().Length}>"
         : $"{v.ValueType}{(v.Flag != 0 ? "/" + v.Flag : "")}:{v}";

    /// <summary>
    /// Exact, except that the two sides may print one double from different paths:
    /// "8.100571" against "8.10057067871094" is the same placement. The numbers are
    /// compared as doubles to the last bit, so this forgives printing and nothing
    /// else.
    /// </summary>
    private static bool SameValue(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
            return true;
        var left = a.Split([' ', ',', '[', ']'], StringSplitOptions.RemoveEmptyEntries);
        var right = b.Split([' ', ',', '[', ']'], StringSplitOptions.RemoveEmptyEntries);
        if (left.Length != right.Length || left.Length == 0)
            return false;
        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] == right[i])
                continue;
            var (lt, lv) = Split(left[i]);
            var (rt, rv) = Split(right[i]);
            if (lt != rt || !double.TryParse(lv, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !double.TryParse(rv, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                || BitConverter.DoubleToInt64Bits(x) != BitConverter.DoubleToInt64Bits(y))
                return false;
        }
        return true;
    }

    private static (string Type, string Value) Split(string token)
    {
        var colon = token.IndexOf(':');
        return colon < 0 ? ("", token) : (token[..colon], token[(colon + 1)..]);
    }
}
