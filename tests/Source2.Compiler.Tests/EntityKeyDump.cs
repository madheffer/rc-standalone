using System.Text.RegularExpressions;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: every key of a compiled map's entities whose class matches, one entity per line, in lump order.
/// <c>ENTDUMP=&lt;compiled .vpk&gt;|&lt;class regex&gt;</c> (e.g. <c>atixref.vpk|^light_barn$</c>).
/// </summary>
public class EntityKeyDump(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("ENTDUMP") is not { Length: > 0 } spec)
            return;
        var (vpk, pattern) = spec.Split('|') is [var v, var p] ? (v, p) : (spec, ".");
        using var package = new Package();
        package.Read(vpk);
        var index = 0;
        foreach (var entry in (package.Entries!.GetValueOrDefault("vents_c") ?? []).OrderBy(e => e.GetFullPath(), StringComparer.Ordinal))
        {
            package.ReadEntry(entry, out var bytes);
            using var resource = new Resource { FileName = entry.GetFullPath() };
            resource.Read(new MemoryStream(bytes));
            foreach (var entity in ((EntityLump)resource.DataBlock!).Data.GetArray("m_entityKeyValues"))
            {
                var values = entity.GetSubCollection("keyValues3Data")?.GetSubCollection("values")?.Children.ToDictionary(kv => kv.Key, kv => kv.Value) ?? [];
                var cls = values.TryGetValue("classname", out var c) ? c.ToString() : "";
                if (!Regex.IsMatch(cls ?? "", pattern))
                    continue;
                string Text(ValveKeyValue.KVObject o) => o.IsArray ? "[" + string.Join(" ", o.Values.Select(Text)) + "]" : o.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
                output.WriteLine($"{index++} {entry.GetFullPath()} {string.Join(" ", values.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={Text(kv.Value)}"))}");
            }
        }
    }
}
