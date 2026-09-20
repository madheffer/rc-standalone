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
/// The type is compared as well as the value because <c>enabled = true</c> and
/// <c>enabled = "1"</c> print the same and are not the same to the entity system.
/// </para>
/// </summary>
internal static class EntityLumpComparison
{
    /// <summary>One entity, flattened to what the engine reads.</summary>
    internal sealed record Entity(
        string ClassName,
        string HammerId,
        IReadOnlyDictionary<string, (KVValueType Type, string Value)> Values,
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
            var tree = entity.GetSubCollection("keyValues3Data")?.GetSubCollection("values");
            foreach (var kv in tree?.Children ?? [])
                values[kv.Key] = (kv.Value.ValueType, Render(kv.Value));

            var connections = entity.GetArray("m_connections")
                .Select(c => $"{c.GetStringProperty("m_outputName")} -> "
                           + $"{c.GetStringProperty("m_targetName")}.{c.GetStringProperty("m_inputName")}"
                           + $" '{c.GetStringProperty("m_overrideParam")}'")
                .ToList();

            entities.Add(new Entity(
                values.TryGetValue("classname", out var cls) ? cls.Item2 : "<no classname>",
                values.TryGetValue("hammerUniqueId", out var id) ? id.Item2 : "",
                values,
                connections));
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

        // Match on Hammer's node id where both sides have one, so a missing entity
        // does not report every later entity as different.
        var byId = mine.Where(e => e.HammerId.Length > 0).ToDictionary(e => e.HammerId, e => e);
        for (var i = 0; i < valve.Count; i++)
        {
            var theirs = valve[i];
            var ours = theirs.HammerId.Length > 0 && byId.TryGetValue(theirs.HammerId, out var matched)
                ? matched
                : i < mine.Count ? mine[i] : null;
            if (ours is null)
            {
                report.Add($"[{Label(theirs)}] missing from ours");
                continue;
            }

            foreach (var (key, (type, value)) in theirs.Values)
            {
                if (!ours.Values.TryGetValue(key, out var got))
                    report.Add($"[{Label(theirs)}] {key}: missing (valve {type} {value})");
                else if (got.Type != type)
                    report.Add($"[{Label(theirs)}] {key}: type valve {type} {value}, ours {got.Type} {got.Value}");
                else if (!SameValue(got.Value, value))
                    report.Add($"[{Label(theirs)}] {key}: value valve {value}, ours {got.Value}");
            }
            foreach (var key in ours.Values.Keys.Where(k => !theirs.Values.ContainsKey(k)))
                report.Add($"[{Label(theirs)}] {key}: ours only ({ours.Values[key].Type} {ours.Values[key].Value})");

            foreach (var c in theirs.Connections.Where(c => !ours.Connections.Contains(c)))
                report.Add($"[{Label(theirs)}] connection missing: {c}");
            foreach (var c in ours.Connections.Where(c => !theirs.Connections.Contains(c)))
                report.Add($"[{Label(theirs)}] connection ours only: {c}");
        }
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
    /// Floats are compared as numbers, because the two sides print a double from
    /// different paths and "8.100571" against "8.10057067871094" is the same
    /// placement. Anything else is compared exactly.
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
            if (!double.TryParse(left[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !double.TryParse(right[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                return false;
            // Six decimals is what the lump's own placement strings carry.
            if (Math.Abs(x - y) > 1e-5 * Math.Max(1, Math.Abs(x)))
                return false;
        }
        return true;
    }

    private static string Render(KVObject v)
        => v.IsArray ? "[" + string.Join(", ", v.Values.Select(x => x.ToString())) + "]" : v.ToString() ?? "";
}
