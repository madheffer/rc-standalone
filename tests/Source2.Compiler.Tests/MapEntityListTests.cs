using System.Numerics;
using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="MapEntityList"/> against Valve's list at the nav link collector, from tools/re/capture_entitylist.py
/// (%TEMP%/navgen_capture/&lt;map&gt;_entitylist.json, the first "links" snapshot: each entry's vtable and origin +0xa0).
/// Entry by entry: the same count, each entry's DMX type on one vtable throughout, and the origin each node is left at
/// (<see cref="SettleWorld.Placed"/>; within 1e-3, since the order is what is checked here).
/// </summary>
public sealed class MapEntityListTests(ITestOutputHelper output)
{
    private static string Root => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(CS2Fixtures.StockPak()!)!, "..", ".."));

    [Theory]
    [InlineData("s2c_prefabprobe5")]
    [InlineData("s2c_prefabprobe3")]
    [InlineData("s2c_prefabprobe4")]
    public void ListIsValves(string map)
    {
        var capture = Path.Combine(Path.GetTempPath(), "navgen_capture", map + "_entitylist.json");
        if (CS2Fixtures.StockPak() is null || !File.Exists(capture))
            return;
        using var json = JsonDocument.Parse(File.ReadAllText(capture));
        var snap = json.RootElement.EnumerateArray().First(s => s.GetProperty("tag").GetString() == "links").GetProperty("list");
        if (!snap.EnumerateArray().First().TryGetProperty("o", out _))
            return; // a capture from before the tool recorded origins
        var valve = snap.EnumerateArray().Select(e => (Vt: e.GetProperty("vt").GetInt32(),
            O: new Vector3(e.GetProperty("o")[0].GetSingle(), e.GetProperty("o")[1].GetSingle(), e.GetProperty("o")[2].GetSingle()))).ToList();

        var addon = Path.Combine(Root, "content", "csgo_addons", "s2c_rc_probe");
        var document = DmxBinary.ReadFile(Path.Combine(addon, "maps", map + ".vmap"));
        MapPrefabs.Attach(document, MapPrefabs.FromContent(addon));
        var ours = MapEntityList.Baked(document, out var orderMeasured);

        var types = new Dictionary<int, string>();
        string? first = null;
        for (var i = 0; i < Math.Min(ours.Count, valve.Count); i++)
        {
            var item = ours[i];
            // Valve's settle moves a physics prop's node before the nav step.
            if (item.Node.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname") is "prop_physics" or "prop_physics_multiplayer")
                continue;
            var origin = item.Node.GetValue<Vector3>("origin") ?? Vector3.Zero;
            var angles = item.Node.GetValue<Vector3>("angles") ?? Vector3.Zero;
            var placed = item.Node.Type == "CMapWorld" ? Vector3.Zero : SettleWorld.Placed(origin, angles, item.Through, item.Prefabs).Origin;
            var typeOk = !types.TryGetValue(valve[i].Vt, out var t) || t == item.Node.Type;
            types.TryAdd(valve[i].Vt, item.Node.Type);
            var near = Vector3.Distance(placed, valve[i].O) < 1e-3f;
            if ((!typeOk || !near) && first is null)
                first = $"entry {i}: ours {item.Node.Type} {item.Node.GetValue<int>("nodeID")} at {placed} (through {item.Through.Count}, prefabs {item.Prefabs.Count}), Valve's vt {valve[i].Vt:x} at {valve[i].O}";
        }
        if (Environment.GetEnvironmentVariable("ENTITYLIST_DUMP") is { Length: > 0 } dump)
            File.WriteAllLines(dump, ours.Select((item, i) =>
            {
                var o = item.Node.Type == "CMapWorld" ? Vector3.Zero : SettleWorld.Placed(item.Node.GetValue<Vector3>("origin") ?? Vector3.Zero, item.Node.GetValue<Vector3>("angles") ?? Vector3.Zero, item.Through, item.Prefabs).Origin;
                return $"{i} {item.Node.Type} {item.Node.GetValue<int>("nodeID")} t{item.Through.Count} p{item.Prefabs.Count} {o.X} {o.Y} {o.Z} {(i < valve.Count ? $"{valve[i].Vt:x} {valve[i].O.X} {valve[i].O.Y} {valve[i].O.Z}" : "")}";
            }));
        output.WriteLine($"{map}: ours {ours.Count} entries, Valve's {valve.Count}; {first ?? "every entry matches"}");
        Assert.Equal(valve.Count, ours.Count);
        // s2c_prefabprobe3 (80 instances in its prefab's map): Valve's up to entry 2463, where the first of them
        // collapses; Baked reports the order unmeasured there.
        if (map == "s2c_prefabprobe3")
        {
            Assert.False(orderMeasured);
            Assert.StartsWith("entry 2463:", first);
            return;
        }
        Assert.True(orderMeasured);
        Assert.Null(first);
    }
}
