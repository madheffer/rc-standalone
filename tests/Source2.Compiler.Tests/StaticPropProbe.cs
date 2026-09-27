using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: a .vmap's prop_static entities, every attribute of their
/// elements and entity properties, with a tally of the keys across all of
/// them. <c>PROPPROBE=&lt;.vmap&gt;</c>, <c>PROPPROBE_SHOW</c> (default 3).
/// </summary>
public class StaticPropProbe(ITestOutputHelper output)
{
    [Fact]
    public void Props()
    {
        if (Environment.GetEnvironmentVariable("PROPPROBE") is not { Length: > 0 } path)
            return;
        var show = int.TryParse(Environment.GetEnvironmentVariable("PROPPROBE_SHOW"), out var s) ? s : 3;
        var doc = DmxBinary.ReadFile(path);
        var keys = new Dictionary<string, int>();
        var values = new Dictionary<string, Dictionary<string, int>>();
        var count = 0;
        foreach (var e in doc.Elements.Where(e => e.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname") == "prop_static"))
        {
            count++;
            var props = e.Get<DmxBinary.Element>("entity_properties")!;
            foreach (var (k, v) in e.Attributes.Concat(props.Attributes.Select(kv => new KeyValuePair<string, object?>("kv." + kv.Key, kv.Value))))
            {
                keys[k] = keys.GetValueOrDefault(k) + 1;
                if (k is "kv.solid" or "kv.scales" or "kv.modelscale" or "scales")
                {
                    values.TryAdd(k, []);
                    var text = v?.ToString() ?? "null";
                    values[k][text] = values[k].GetValueOrDefault(text) + 1;
                }
            }
            if (count <= show)
            {
                output.WriteLine($"prop {e.Type} {e.Name}:");
                foreach (var (k, v) in e.Attributes)
                    output.WriteLine($"  {k} = {Describe(v)}");
                foreach (var (k, v) in props.Attributes)
                    output.WriteLine($"  kv {k} = {Describe(v)}");
            }
        }
        output.WriteLine($"{count} prop_static");
        foreach (var (k, n) in keys.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            output.WriteLine($"key {k}: {n}");
        foreach (var (k, vs) in values)
            output.WriteLine($"values {k}: {string.Join(", ", vs.OrderByDescending(kv => kv.Value).Take(12).Select(kv => $"{kv.Key} x{kv.Value}"))}");
    }

    private static string Describe(object? v) => v switch
    {
        null => "null",
        DmxBinary.Element el => $"<{el.Type} {el.Name}>",
        object?[] arr => $"[{arr.Length}] {string.Join(" ", arr.Take(4))}",
        _ => v.ToString() ?? "",
    };
}

/// <summary>Exploration: element type counts of a .vmap, and the attributes of its reference-like elements. <c>VMAPTYPES=&lt;.vmap&gt;</c>.</summary>
public class VmapTypesProbe(ITestOutputHelper output)
{
    [Fact]
    public void Types()
    {
        if (Environment.GetEnvironmentVariable("VMAPTYPES") is not { Length: > 0 } path)
            return;
        var doc = DmxBinary.ReadFile(path);
        foreach (var g in doc.Elements.GroupBy(e => e.Type).OrderByDescending(g => g.Count()))
            output.WriteLine($"{g.Key}: {g.Count()}");
        foreach (var e in doc.Elements.Where(e => e.Type is "CMapPrefab" or "CMapInstance").Take(3))
            foreach (var (k, v) in e.Attributes)
                output.WriteLine($"  {e.Type} {k} = {v}");
    }
}
