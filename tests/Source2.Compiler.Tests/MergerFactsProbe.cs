using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>MERGEFACTS=&lt;merger capture&gt;</c>): the values every
/// <see cref="WrbMeshEntry"/> field takes over the captured merger inputs,
/// per call, to see which a .vmap build must derive and which are constant.
/// </summary>
public class MergerFactsProbe(ITestOutputHelper output)
{
    [Fact]
    public void Distinct()
    {
        if (Environment.GetEnvironmentVariable("MERGEFACTS") is not { } path)
            return;
        var data = File.ReadAllBytes(path);
        var values = new SortedDictionary<string, Dictionary<string, int>>();
        void Add(string field, object? v)
        {
            if (!values.TryGetValue(field, out var d))
                values[field] = d = [];
            var key = v switch { float[] a => string.Join(",", a.Select(x => x.ToString("R"))), System.Collections.IEnumerable e and not string => string.Join(",", e.Cast<object>()), _ => v?.ToString() ?? "null" };
            d[key] = d.GetValueOrDefault(key) + 1;
        }
        for (var at = 0; at < data.Length;)
        {
            var n = BitConverter.ToInt32(data, at);
            var head = JsonDocument.Parse(data.AsMemory(at + 4, n)).RootElement;
            at += 4 + n;
            var m = BitConverter.ToInt32(data, at);
            var blob = data.AsSpan(at + 4, m).ToArray();
            at += 4 + m;
            if (head.GetProperty("ev").GetString() != "in" || !head.TryGetProperty("meshRaw", out var raw))
                continue;
            var call = head.GetProperty("call").GetInt32();
            var mesh = Convert.FromHexString(raw.GetString()!);
            var streams = head.GetProperty("streams").EnumerateArray().Select(x => $"{x.GetProperty("name").GetString()}/{x.GetProperty("index").GetInt32()}/{x.GetProperty("count").GetInt32()}/{x.GetProperty("precise").GetInt32()}/{x.GetProperty("type").GetInt32()}");
            var floats = head.GetProperty("floats").EnumerateArray().Select(x => x.GetSingle()).ToArray();
            var f = WrbMeshEntry.FromBytes(blob.AsSpan(0, 0x238), mesh, "", floats, head.GetProperty("entryName").GetString() ?? "", []);
            var c = $"c{call} ";
            Add(c + "overlayOrder", f.OverlayOrder);
            Add(c + "objectFlags", $"{f.ObjectFlags:x}");
            Add(c + "debugColor", f.DebugColor);
            if (f.DebugColor)
                Add(c + "debugColorValue", f.DebugColorValue);
            Add(c + "streams", string.Join(" ", streams));
            Add(c + "mesh58", f.Mesh58);
            Add(c + "meshFloats", floats);
            Add(c + "1a3", f.Field1a3);
            Add(c + "b8", f.Field_b8);
            Add(c + "1a5", f.Field1a5);
            Add(c + "1a8", f.Field1a8);
            Add(c + "cubemap", f.Cubemap);
            Add(c + "lightProbe", f.LightProbe);
            Add(c + "mesh184", f.Mesh184);
            Add(c + "9c", f.Field9c);
            Add(c + "1a1", f.Field1a1);
            Add(c + "1a0", f.Field1a0);
            Add(c + "b0", f.Field_b0);
            Add(c + "fadeMax", f.FadeMax);
            Add(c + "name", f.Name);
            Add(c + "matrix", f.Matrix);
            Add(c + "mesh152", f.Mesh152);
            Add(c + "field28", f.Field28);
        }
        foreach (var (field, d) in values)
            output.WriteLine($"{field}: {string.Join(" | ", d.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key} x{kv.Value}"))}{(d.Count > 6 ? $" (+{d.Count - 6})" : "")}");
    }
}

/// <summary>Exploration (<c>MATFLAGS=&lt;material&gt;</c>): a material's entry flags and representative texture size under a plain world record.</summary>
public class MaterialFlagsProbe(ITestOutputHelper output)
{
    [Fact]
    public void Flags()
    {
        if (Environment.GetEnvironmentVariable("MATFLAGS") is not { } material || CS2Fixtures.StockPak() is not { } pak)
            return;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", "s2c_rc_probe"));
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_pc_dir.vpk"), Path.Combine(game, "core", "shaders_pc_dir.vpk"));
        var info = content.Material(material);
        output.WriteLine(info is null ? "not found" : $"shader {info.Shader}");
        var attributes = info is null ? MaterialAttributes.Empty : MaterialAttributes.Of(info, shaders, content.TextureSize);
        var flags = MeshEntryFlags.Compute(attributes, new MeshEntryFlags.Record(0, 0, 0, false, null, false));
        output.WriteLine($"flags {flags.Flags:x} size {flags.Width}x{flags.Height}");
    }
}

/// <summary>Exploration (<c>PREFABCOUNT=&lt;vmap&gt;</c>): each prefab's map node count, smart props and the node types it holds.</summary>
public class PrefabCountProbe(ITestOutputHelper output)
{
    [Fact]
    public void Count()
    {
        if (Environment.GetEnvironmentVariable("PREFABCOUNT") is not { } vmap)
            return;
        var doc = MapSource.Read(vmap);
        static int Nodes(DmxBinary.Element g) => g.GetElements("children").Sum(c => 1 + Nodes(c));
        foreach (var prefab in doc.OfType("CMapPrefab"))
        {
            var world = prefab.Get<DmxBinary.Element>(MapPrefabs.WorldKey);
            var loaded = prefab.Attributes.GetValueOrDefault(MapPrefabs.DocumentKey);
            output.WriteLine($"prefab {prefab.GetValue<int>("nodeID")}: tree {(world is null ? -1 : Nodes(world))}, document {loaded?.GetType().Name}");
            if (world is not null && Environment.GetEnvironmentVariable("PREFABSLOTS") is { } range)
            {
                var r = range.Split(',').Select(int.Parse).ToArray();
                var slot = 0;
                void Walk(DmxBinary.Element g, int depth)
                {
                    foreach (var c in g.GetElements("children"))
                    {
                        var at = slot++;
                        if (at >= r[0] && at < r[1])
                            output.WriteLine($"  slot {at} id {2142 + at}: {new string(' ', depth)}{c.Type} {c.GetValue<int>("nodeID")} {c.Get<DmxBinary.Element>("target")?.GetValue<int>("nodeID")} children {c.GetElements("children").Count()}");
                        Walk(c, depth + 1);
                    }
                }
                Walk(world, 0);
            }
            if (loaded is DmxBinary.Document d)
            {
                output.WriteLine($"  smart props {d.OfType("CMapSmartProp").Count()}, max id {d.Elements.Max(e => e.GetValue<int>("nodeID") ?? 0)}");
                foreach (var g in d.Elements.Where(e => e.GetValue<int>("nodeID") is not null).GroupBy(e => e.Type))
                    output.WriteLine($"  {g.Key} {g.Count()}");
            }
        }
    }
}
