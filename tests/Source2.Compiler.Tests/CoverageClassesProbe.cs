using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Coverage: which entity classes each local .vmap holds (instances and
/// prefabs as the file stores them, every CMapEntity and brush entity).
/// <c>COVERAGE_CLASSES=&lt;out.json&gt;|&lt;vmap&gt;[|&lt;vmap&gt;...]</c> writes
/// {class: [map, ...]}.
/// </summary>
public class CoverageClassesProbe(ITestOutputHelper output)
{
    [Fact]
    public void ListsClassesPerMap()
    {
        if (Environment.GetEnvironmentVariable("COVERAGE_CLASSES") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var found = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var path in parts.Skip(1))
        {
            var map = Path.GetFileNameWithoutExtension(path);
            DmxBinary.Document doc;
            try
            {
                doc = DmxBinary.ReadFile(path);
            }
            catch (InvalidDataException ex)
            {
                output.WriteLine($"skipped {map}: {ex.Message}");
                continue;
            }
            foreach (var e in doc.Elements)
            {
                var name = e.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname");
                if (name == null)
                    continue;
                if (!found.TryGetValue(name, out var maps))
                    found[name] = maps = new SortedSet<string>(StringComparer.Ordinal);
                maps.Add(map);
            }
            // worldspawn is the CMapWorld's own properties.
            if (doc.Elements.Any(e => e.Type == "CMapWorld"))
            {
                if (!found.TryGetValue("worldspawn", out var w))
                    found["worldspawn"] = w = new SortedSet<string>(StringComparer.Ordinal);
                w.Add(map);
            }
        }
        File.WriteAllText(parts[0], JsonSerializer.Serialize(found.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray())));
        output.WriteLine($"{found.Count} classes over {parts.Length - 1} maps");
    }
}

/// <summary>
/// Coverage: every class's finalized keys as the schema holds them, for the
/// probe map generator (tools/coverage/probe_map.py).
/// <c>COVERAGE_KEYS=&lt;out.json&gt;</c> writes {class: {solid, keys: [{name,
/// type, default, flags}]}}.
/// </summary>
public class CoverageKeysProbe(ITestOutputHelper output)
{
    [Fact]
    public void ListsKeysPerClass()
    {
        if (Environment.GetEnvironmentVariable("COVERAGE_KEYS") is not { Length: > 0 } path)
            return;
        var schema = MapFixtures.GameSchema() ?? throw new InvalidOperationException("no game schema");
        var classes = schema.ClassNames.Order(StringComparer.Ordinal).ToDictionary(c => c, c => new
        {
            solid = schema.IsSolidClass(c),
            keys = schema.KeysOf(c).Select(k => new
            {
                name = k.Name,
                type = k.Type.ToString(),
                @default = k.Default,
                flags = k.Flags.Select(f => new { bit = f.Bit, name = f.Name }).ToArray(),
            }).ToArray(),
        });
        File.WriteAllText(path, JsonSerializer.Serialize(classes, new JsonSerializerOptions { WriteIndented = true }));
        output.WriteLine($"{classes.Count} classes");
    }
}
