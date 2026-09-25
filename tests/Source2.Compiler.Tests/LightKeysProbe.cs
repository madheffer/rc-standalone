using Source2.Compiler;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>Scratch probe: dumps the light-class lump differences of a map to a file.</summary>
public sealed class LightKeysProbe(ITestOutputHelper output)
{
    [Theory]
    [InlineData("s2probe", "atixref")]
    [InlineData("s2c_lighting", "ze_hold_em_p")]
    [InlineData("s2probe", "probe01")]
    [InlineData("ze_doom_p2", "cardtest")]
    public void DumpLightDifferences(string addon, string map)
    {
        var dump = Environment.GetEnvironmentVariable("LIGHT_DUMP");
        if (dump is null)
            return;
        var source = MapFixtures.VmapSource(addon, map);
        var valve = source is null ? null : MapFixtures.RcCompiledLumps(source);
        if (valve is null)
            return;
        var document = DmxBinary.ReadFile(source!);
        var ours = EntityLumpSet.Author(MapEntities.From(document), MapFixtures.GameSchema(), map,
                                        MapEntities.FixupEntityNames(document), document, MapFixtures.SmartPropLocators);
        var lines = new List<string>();
        foreach (var lump in ours)
        {
            var report = EntityLumpComparison.Diff(EntityLumpComparison.Read(valve[lump.Path], lump.Path),
                                                   EntityLumpComparison.Read(lump.Bytes, lump.Path));
            lines.AddRange(report.Select(l => $"{Path.GetFileName(lump.Path)}: {l}"));
        }
        File.WriteAllLines(Path.Combine(dump, $"{map}.txt"), lines);
        var keys = new List<string>();
        foreach (var (path, bytes) in valve)
            foreach (var e in EntityLumpComparison.Read(bytes, path))
                if (e.ClassName.StartsWith("light_", StringComparison.Ordinal))
                    keys.Add($"{e.ClassName}#{e.HammerId} " + string.Join(" | ", e.KeyOrder.Select(k => $"{k}={e.Values[k].Value}")));
        File.WriteAllLines(Path.Combine(dump, $"{map}.lights.txt"), keys);
        output.WriteLine($"{lines.Count} lines");
    }
}

/// <summary>
/// Scratch probe: the precomputed keys of every barn and omni2 with an empty
/// scene, against Valve's as dumped by <see cref="LightKeysProbe"/> into
/// LIGHT_DUMP/{map}.lights.txt.
/// </summary>
public sealed class LightPrecomputeProbe(ITestOutputHelper output)
{
    [Theory]
    [InlineData("s2probe", "atixref")]
    [InlineData("s2c_lighting", "ze_hold_em_p")]
    public void CompareWithEmptyScene(string addon, string map)
    {
        var dump = Environment.GetEnvironmentVariable("LIGHT_DUMP");
        if (dump is null || MapFixtures.VmapSource(addon, map) is not { } source
            || Environment.GetEnvironmentVariable("LIGHT_MAP") is { } only && only != map)
            return;
        var valve = new Dictionary<string, Dictionary<string, string>>();
        foreach (var line in File.ReadAllLines(Path.Combine(dump, $"{map}.lights.txt")))
        {
            var head = line.IndexOf(' ');
            var keys = new Dictionary<string, string>();
            foreach (var part in line[(head + 1)..].Split(" | "))
            {
                var eq = part.IndexOf('=');
                if (eq > 0 && part.StartsWith("precomputed", StringComparison.Ordinal))
                    keys[part[..eq]] = part[(eq + 1)..].Replace("String:", "");
            }
            valve[line[..head]] = keys;
        }
        var document = DmxBinary.ReadFile(source);
        var lines = new List<string>();
        int lights = 0, exact = 0, keysSame = 0, keysAll = 0, instanced = 0;
        var perKey = new Dictionary<string, (int Same, int All)>();
        var walked = MapEntities.From(document);
        var (copies, templates) = MapInstances.Expand(document, walked,
            SmartProps.NodesCreatedOnLoad(document, MapFixtures.SmartPropLocators));
        var all = walked.Where((_, i) => !templates.Contains(i))
            .Concat(copies.Select(c => walked[c.Template] with { NodeId = c.NodeId, Origin = c.Origin, Angles = c.Angles, Instanced = true }));
        var list = all.Where(e => !e.Hidden && e.ClassName is "light_barn" or "light_omni2").ToList();
        var computed = new object?[list.Count];
        Parallel.For(0, list.Count, n =>
        {
            var e = list[n];
            try
            {
                computed[n] = LightPrecompute.Keys(e.ClassName,
                    name => e.Keys.FirstOrDefault(k => k.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value,
                    LightPrecompute.World(e.Origin, e.Angles), EmptyLightScene.Instance);
            }
            catch (NotSupportedException ex)
            {
                computed[n] = ex.Message;
            }
        });
        for (var n = 0; n < list.Count; n++)
        {
            var e = list[n];
            if (e.Hidden || e.ClassName is not ("light_barn" or "light_omni2"))
                continue;
            var id = $"{e.ClassName}#{e.NodeId}";
            if (!valve.TryGetValue(id, out var theirs))
            {
                lines.Add($"{id}: not in Valve's lump");
                continue;
            }
            if (e.Instanced)
                instanced++;
            if (computed[n] is string message)
            {
                lines.Add($"{id}: {message}");
                continue;
            }
            var ours = (List<KeyValuePair<string, string>>)computed[n]!;
            lights++;
            var same = ours.Count == theirs.Count;
            foreach (var (k, v) in ours)
            {
                var name = k.TrimEnd('0', '1', '2', '3', '4', '5');
                var tally = perKey.GetValueOrDefault(name);
                var hit = theirs.TryGetValue(k, out var t) && t == v;
                perKey[name] = (tally.Same + (hit ? 1 : 0), tally.All + 1);
                keysAll++;
                if (hit)
                    keysSame++;
                else
                {
                    same = false;
                    lines.Add($"{id}{(e.Instanced ? " (instanced)" : "")} {k}: valve {t ?? "(none)"} ours {v}");
                }
            }
            foreach (var k in theirs.Keys.Where(k => ours.All(o => o.Key != k)))
            {
                same = false;
                lines.Add($"{id} {k}: valve {theirs[k]} ours (none)");
            }
            if (same && theirs.Keys.SequenceEqual(ours.Select(o => o.Key)))
                exact++;
        }
        var summary = $"{map}: {lights} lights, {exact} exact, {instanced} instanced, keys {keysSame}/{keysAll}; "
                      + string.Join(", ", perKey.Select(p => $"{p.Key} {p.Value.Same}/{p.Value.All}"));
        lines.Insert(0, summary);
        File.WriteAllLines(Path.Combine(dump, $"{map}.precompute.txt"), lines);
        output.WriteLine(summary);
    }
}
