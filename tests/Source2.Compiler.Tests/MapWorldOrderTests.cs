using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="MapWorldOrder"/> against the world node array Valve's compile searches, captured by
/// tools/entities/capture_entitysearch.py into %TEMP%/entitysearch_capture/&lt;map&gt;.jsonl.
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
            List<int> ours;
            try
            {
                ours = MapWorldOrder.NodeIds(document, SmartProps.NodesCreatedOnLoad(document, MapFixtures.SmartPropLocators));
            }
            catch (NotSupportedException e)
            {
                output.WriteLine($"{map}: refused ({e.Message})");
                continue;
            }
            var first = Enumerable.Range(0, Math.Min(ours.Count, valve.Count)).FirstOrDefault(i => ours[i] != valve[i], -1);
            output.WriteLine($"{map}: {ours.Count} vs {valve.Count} nodes, first difference {first}");
            Assert.Equal(valve, ours.Select(i => (int?)i).ToList());
            compared++;
        }
        output.WriteLine($"{compared} maps compared");
    }
}
