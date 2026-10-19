using Source2.Compiler.Maps;
using ValveKeyValue;
using ValvePak;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// MapSubassets against the m_SubassetReferences of every map in s2c_rc_probe that
/// resourcecompiler has compiled: the pairs, their counts and their order.
/// </summary>
public class MapSubassetsTests(ITestOutputHelper output)
{
    [Fact]
    public void ReferencesEqualValves()
    {
        if (CS2Fixtures.StockPak() is not { } pak)
        {
            MapFixtures.Skip("CS2");
            return;
        }
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var schema = FgdSchema.Load(Path.Combine(game, "csgo", "csgo.fgd"), [Path.Combine(game, "core"), Path.Combine(game, "csgo")]);
        var maps = Path.Combine(game, "csgo_addons", "s2c_rc_probe", "maps");
        if (!Directory.Exists(maps))
        {
            MapFixtures.Skip("s2c_rc_probe's compiled maps");
            return;
        }
        var failed = new List<string>();
        var compared = 0;
        foreach (var vpk in Directory.GetFiles(maps, "*.vpk").Order(StringComparer.Ordinal))
        {
            var map = Path.GetFileNameWithoutExtension(vpk);
            if (MapFixtures.VmapSource("s2c_rc_probe", map) is not { } source)
                continue;
            using var package = new Package();
            package.Read(vpk);
            if (package.FindEntry($"maps/{map}.vmap_c") is not { } entry)
                continue;
            var trees = WorldPhysicsAuthorTests.Trees(Io.VpkEntries.Read(package, entry));
            var valve = new List<(string, string, int)>();
            if (trees["RED2"]["m_SubassetReferences"] is { ValueType: KVValueType.Collection } refs)
                foreach (var (type, names) in refs)
                    foreach (var (name, count) in names)
                        valve.Add((type, name, count.ToInt32(System.Globalization.CultureInfo.InvariantCulture)));
            var document = DmxBinary.ReadFile(source);
            MapPrefabs.Attach(document, MapPrefabs.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(source))!));
            var ours = MapSubassets.References(MapEntities.From(document), schema, document);
            // The RED2 groups by type, each in the order its names first appear.
            var grouped = ours.GroupBy(r => r.Type).SelectMany(g => g).ToList();
            compared++;
            var same = grouped.SequenceEqual(valve);
            output.WriteLine($"{map}: {valve.Count} pairs, {(same ? "equal" : "DIFFER")}");
            if (!same)
            {
                failed.Add(map);
                var o = grouped.ToDictionary(r => (r.Type, r.Name), r => r.Count);
                var v = valve.ToDictionary(r => (r.Item1, r.Item2), r => r.Item3);
                var counts = o.Keys.Union(v.Keys).Where(k => o.GetValueOrDefault(k) != v.GetValueOrDefault(k)).Select(k => $"{k.Item2} {o.GetValueOrDefault(k)}/{v.GetValueOrDefault(k)}").ToList();
                output.WriteLine($"  counts (ours/valve): {(counts.Count == 0 ? "same" : string.Join(", ", counts.Take(20)))}");
                if (counts.Count == 0)
                {
                    output.WriteLine("  ours  " + string.Join(" ", grouped.Select(r => r.Name)));
                    output.WriteLine("  valve " + string.Join(" ", valve.Select(r => r.Item2)));
                }
            }
        }
        output.WriteLine($"{compared} maps compared, {failed.Count} differ");
        Assert.Empty(failed);
    }
}
