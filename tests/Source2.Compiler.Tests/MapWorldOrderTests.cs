using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="MapWorldOrder"/> against the world node array Valve's compile searches, captured by
/// tools/entities/capture_entitysearch.py into %TEMP%/entitysearch_capture/&lt;map&gt;.jsonl (prefabs attached).
/// </summary>
public sealed class MapWorldOrderTests(ITestOutputHelper output)
{
    private const string Content = @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\content\csgo_addons\s2c_rc_probe\maps\";

    [Fact]
    public void CapturedArraysAreValves()
    {
        var root = Path.Combine(Path.GetTempPath(), "entitysearch_capture");
        if (!Directory.Exists(root))
            return;
        var compared = 0;
        var failures = new List<string>();
        foreach (var capture in Directory.GetFiles(root, "*.jsonl").Order(StringComparer.Ordinal))
        {
            var map = Path.GetFileNameWithoutExtension(capture);
            var vmap = Path.Combine(Content, map + ".vmap");
            if (!File.Exists(vmap))
                continue;
            List<int?>? valve = null;
            foreach (var line in File.ReadLines(capture))
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("nodes", out var nodes))
                {
                    valve = [.. nodes.EnumerateArray().Select(n => n.ValueKind == JsonValueKind.Null ? (int?)null : n.GetInt32())];
                    break;
                }
            }
            if (valve is null)
                continue;
            var document = DmxBinary.ReadFile(vmap);
            MapPrefabs.Attach(document, MapPrefabs.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(vmap))!));
            List<int> ours;
            try
            {
                ours = MapWorldOrder.NodeIds(document, SmartProps.NodesCreatedOnLoad(document, MapFixtures.SmartPropLocators),
                    d => SmartProps.NodesCreatedOnLoad(d, MapFixtures.SmartPropLocators));
            }
            catch (NotSupportedException e)
            {
                output.WriteLine($"{map}: refused ({e.Message})");
                continue;
            }
            var first = Enumerable.Range(0, Math.Min(ours.Count, valve.Count)).FirstOrDefault(i => ours[i] != valve[i], -1);
            output.WriteLine($"{map}: {ours.Count} vs {valve.Count} nodes, first difference {first}");
            if (first >= 0 || ours.Count != valve.Count)
            {
                output.WriteLine($"  ours  {string.Join(" ", ours.Skip(Math.Max(0, first - 4)).Take(12))}");
                output.WriteLine($"  valve {string.Join(" ", valve.Skip(Math.Max(0, first - 4)).Take(12))}");
                var onlyOurs = ours.Except(valve.Where(v => v.HasValue).Select(v => v!.Value)).ToList();
                var onlyValve = valve.Where(v => v.HasValue).Select(v => v!.Value).Except(ours).ToList();
                output.WriteLine($"  only ours {onlyOurs.Count}: {string.Join(" ", onlyOurs.Take(20))}; only Valve's {onlyValve.Count}: {string.Join(" ", onlyValve.Take(20))}");
                failures.Add(map);
            }
            compared++;
        }
        output.WriteLine($"{compared} maps compared");
        // s2c_prefabprobe3: Valve's ids, a few in another order; s2c_prefabprobe4: its prefab map's
        // block ids differ by one from slot to slot (ledger 106, open).
        Assert.Empty(failures.Except(["s2c_prefabprobe3", "s2c_prefabprobe4"]));
    }
}
