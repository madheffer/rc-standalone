using System.Numerics;
using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="MapEntityList"/> against Valve's list at the nav link collector, from tools/re/capture_entitylist.py
/// (%TEMP%/navgen_capture/&lt;map&gt;_entitylist.json, the first "links" snapshot: each entry's vtable and origin +0xa0).
/// Entry by entry: the same count, each entry's DMX type on one vtable throughout, and a node of the map's own tree by
/// its id, any other by the origin it is left at (<see cref="SettleWorld.Placed"/>; within 1e-3, since the order is
/// what is checked here).
/// </summary>
public sealed class MapEntityListTests(ITestOutputHelper output)
{
    // Valve's list: capture_entitylist.py's snapshot at the link collector, or capture_entitylist_trace.py's at the
    // bake's exit (the same list: nothing edits it in between; s2c_prefabprobe3 has both, equal id for id).
    private static List<(int Vt, Vector3 O, int? Id)>? Valve(string map)
    {
        var dir = Path.Combine(Path.GetTempPath(), "navgen_capture");
        var capture = Path.Combine(dir, map + "_entitylist.json");
        if (File.Exists(capture))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(capture));
            var snap = json.RootElement.EnumerateArray().First(s => s.GetProperty("tag").GetString() == "links").GetProperty("list");
            if (snap.EnumerateArray().First().TryGetProperty("o", out _))
                return [.. snap.EnumerateArray().Select(e => (e.GetProperty("vt").GetInt32(),
                    new Vector3(e.GetProperty("o")[0].GetSingle(), e.GetProperty("o")[1].GetSingle(), e.GetProperty("o")[2].GetSingle()),
                    (int?)e.GetProperty("id").GetInt32()))];
        }
        var trace = Path.Combine(dir, map + "_eltrace");
        if (!File.Exists(Path.Combine(trace, "bake_leave.json")))
            return null;
        using var leave = JsonDocument.Parse(File.ReadAllText(Path.Combine(trace, "bake_leave.json")));
        var bytes = File.ReadAllBytes(Path.Combine(trace, $"snap_{leave.RootElement.GetProperty("snap").GetInt32()}.bin"));
        using var nodes = JsonDocument.Parse(File.ReadAllText(Path.Combine(trace, "nodes.json")));
        var list = new List<(int, Vector3, int?)>();
        for (var i = 0; i < BitConverter.ToInt32(bytes, 0); i++)
        {
            var node = nodes.RootElement.GetProperty($"0x{BitConverter.ToUInt64(bytes, 4 + 8 * i):x}");
            var o = node.GetProperty("o");
            list.Add((node.GetProperty("vt").GetInt32(), new Vector3(o[0].GetSingle(), o[1].GetSingle(), o[2].GetSingle()), node.GetProperty("id").GetInt32()));
        }
        return list;
    }

    private static string Root => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(CS2Fixtures.StockPak()!)!, "..", ".."));

    [Theory]
    [InlineData("s2c_prefabprobe5")]
    [InlineData("s2c_prefabprobe3")]
    [InlineData("s2c_prefabprobe4")]
    [InlineData("s2c_prefabprobe7")]
    [InlineData("s2c_prefabprobe9")]
    [InlineData("s2c_deformpath3")]
    [InlineData("s2c_deformpath4")]
    [InlineData("ze_ffvii_mako_reactor_v6_p")]
    [InlineData("c2m3_coaster_d_d")]
    public void ListIsValves(string map)
    {
        if (CS2Fixtures.StockPak() is null || Valve(map) is not { } valve)
            return;

        var addon = Path.Combine(Root, "content", "csgo_addons", map.StartsWith("ze_", StringComparison.Ordinal) ? "s2c_big" : map.StartsWith("c2m", StringComparison.Ordinal) ? "c2m2" : "s2c_rc_probe");
        var document = DmxBinary.ReadFile(Path.Combine(addon, "maps", map + ".vmap"));
        MapPrefabs.Attach(document, MapPrefabs.FromContent(addon));
        var ours = MapEntityList.Baked(document);

        var parents = new Dictionary<DmxBinary.Element, DmxBinary.Element>(ReferenceEqualityComparer.Instance);
        foreach (var e in SettleWorld.AllDocuments(document).SelectMany(d => d.Elements))
            foreach (var child in e.GetElements("children"))
                parents.TryAdd(child, e);
        // Under a CMapDeformer: the node, or an instance or prefab it came through, has one above it in its own map.
        bool Deformed(MapEntityList.Item item)
            => new[] { item.Node }.Concat(item.Through).Concat(item.Prefabs).Any(start =>
               {
                   for (var at = parents.GetValueOrDefault(start); at != null; at = parents.GetValueOrDefault(at))
                       if (at.Type.StartsWith("CMapDeformer", StringComparison.Ordinal))
                           return true;
                   return false;
               });
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
            // A node of the map's own tree keeps its id (a deformer may move it in the bake); a copy or a prefab map's
            // node takes a fresh one, so those are checked by where they stand.
            var near = item.Through.Count == 0 && item.Prefabs.Count == 0 && valve[i].Id is { } id
                ? id == item.Node.GetValue<int>("nodeID")
                : Deformed(item)
                    // A deformer bends what it reaches (PathDeformer, ledger 133): checked by type only here.
                    || Vector3.Distance(placed, valve[i].O) < 1e-3f + 1e-6f * Math.Max(placed.Length(), valve[i].O.Length());
            if ((!typeOk || !near) && Environment.GetEnvironmentVariable("ENTITYLIST_ALL") == "1")
                output.WriteLine($"  differs {i}: {item.Node.Type} {item.Node.GetValue<int>("nodeID")} {item.Node.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname")} through {item.Through.Count} prefabs {item.Prefabs.Count} off {Vector3.Distance(placed, valve[i].O)} type {typeOk}");
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
        Assert.Null(first);
        Assert.Equal(valve.Count, ours.Count);
    }
}
