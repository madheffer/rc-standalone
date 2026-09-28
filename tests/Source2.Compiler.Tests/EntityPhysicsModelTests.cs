using ValvePak;
using ValveResourceFormat;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Brush entity models built from the map (<see cref="Physics.EntityPhysicsModels"/>)
/// against Valve's: which hold only physics, and for those the whole file
/// (each block's decoded tree and the container facts).
/// <c>ENTBUILD=&lt;addon&gt;|&lt;map&gt;|&lt;compiled .vpk&gt;</c>; <c>ENTBUILD_SHOW</c>
/// caps the listed differences.
/// </summary>
public class EntityPhysicsModelTests(ITestOutputHelper output)
{
    [Fact]
    public void PhysicsOnlyModelsFromTheMap()
    {
        if (Environment.GetEnvironmentVariable("ENTBUILD") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        var show = int.TryParse(Environment.GetEnvironmentVariable("ENTBUILD_SHOW"), out var n) ? n : 12;
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        var game = Path.Combine(cs2, "game");
        using var content = new Maps.GameContent(Path.Combine(game, "csgo", "pak01_dir.vpk"), Path.Combine(game, "csgo_addons", p[0]));
        var doc = DmxBinary.ReadFile(Path.Combine(cs2, "content", "csgo_addons", p[0], "maps", p[1] + ".vmap"));
        Maps.MapPrefabs.Attach(doc, Maps.MapPrefabs.FromContent(Path.Combine(cs2, "content", "csgo_addons", p[0])));
        var notes = new List<string>();
        var models = Physics.EntityPhysicsModels.Build(doc, p[1], content.Material, content.CollisionProperty, MapFixtures.GameSchema(), notes, content.SmartProp);
        if (Environment.GetEnvironmentVariable("ENTBUILD_NODE") is { Length: > 0 } nodeText)
        {
            var src = doc.Elements.First(e => e.Type == "CMapEntity" && e.GetValue<int>("nodeID")?.ToString() == nodeText);
            foreach (var m in src.GetElements("children").Where(c => c.Type == "CMapMesh"))
                foreach (var mat in (m.Get<DmxBinary.Element>("meshData")?.Get<object?[]>("materials") ?? []).Select(x => x as string ?? ""))
                {
                    var info = content.Material(mat);
                    output.WriteLine($"node material {mat} shader {info?.Shader} attr '{Physics.WorldCollision.ReadMaterial(info, content.CollisionProperty).AttributeKey}' strings {string.Join(",", info?.Strings.Select(kv => kv.Key + "=" + kv.Value) ?? [])}");
                }
            return;
        }
        using var package = new Package();
        package.Read(p[2]);
        int exact = 0, differ = 0, missing = 0, kindWrong = 0, shown = 0, renderPhysExact = 0, renderPhysDiffer = 0;
        var tally = new Dictionary<string, int>();
        foreach (var model in models)
        {
            var entry = package.FindEntry(model.Path + "_c");
            if (entry == null)
            {
                missing++;
                if (shown++ < show)
                {
                    var mats = (doc.Elements.FirstOrDefault(e => e.Type == "CMapEntity" && e.GetValue<int>("nodeID") == model.NodeId) ?? doc.Elements.First())
                        .GetElements("children").Where(c => c.Type == "CMapMesh")
                        .SelectMany(m => m.Get<DmxBinary.Element>("meshData")?.Get<object?[]>("materials") ?? []).Select(x => x as string ?? "").Distinct()
                        .Select(m => $"{m}{(content.Material(m) == null ? " (MISSING)" : "")}");
                    output.WriteLine($"not in the package: {model.Path} ({model.ClassName}) materials {string.Join(", ", mats)}");
                }
                continue;
            }
            package.ReadEntry(entry, out var bytes);
            using var res = new Resource();
            res.Read(new MemoryStream(bytes));
            var valvePhysicsOnly = !res.Blocks.Any(b => b.Type == ValveResourceFormat.BlockType.MDAT);
            if (valvePhysicsOnly != model.PhysicsOnly)
            {
                kindWrong++;
                if (shown++ < show)
                {
                    var meshes = (doc.Elements.FirstOrDefault(e => e.Type == "CMapEntity" && e.GetValue<int>("nodeID") == model.NodeId) ?? doc.Elements.First())
                        .GetElements("children").Where(c => c.Type == "CMapMesh")
                        .Select(m => $"{m.Attributes.GetValueOrDefault("physicsType")} [{string.Join(",", m.Get<DmxBinary.Element>("meshData")?.Get<object?[]>("materials") ?? [])}]");
                    output.WriteLine($"{model.Path} ({model.ClassName}): ours {(model.PhysicsOnly ? "physics only" : "with render")} {model.Physics.Hulls.Count} hulls, Valve's {string.Join(" ", res.Blocks.Select(b => b.Type))}; meshes {string.Join("; ", meshes)}");
                }
                continue;
            }
            if (!model.PhysicsOnly)
            {
                // A model with render meshes: its physics block alone, which the
                // same builder makes (the render half is not ported).
                var valvePhys = WorldPhysicsAuthorTests.Trees(bytes).GetValueOrDefault("PHYS");
                var ourPhys = WorldPhysicsAuthorTests.Trees(Physics.EntityPhysicsModels.Author(model, content.SurfaceName)).GetValueOrDefault("PHYS");
                var physReport = valvePhys == null || ourPhys == null
                    ? (valvePhys == null) == (ourPhys == null) ? [] : [$"PHYS: {(valvePhys == null ? "Valve's has none" : "ours has none")}"]
                    : KvTreeDiff.Diff(valvePhys, ourPhys).Select(l => "PHYS" + l).ToList();
                if (physReport.Count == 0)
                    renderPhysExact++;
                else
                {
                    renderPhysDiffer++;
                    foreach (var line in physReport)
                    {
                        var key = System.Text.RegularExpressions.Regex.Replace(line.Split(':')[0], @"\[\d+\]", "[]");
                        tally[key] = tally.GetValueOrDefault(key) + 1;
                    }
                    if (shown++ < show)
                    {
                        output.WriteLine($"{model.Path} ({model.ClassName}, with render): {physReport.Count} PHYS differences: {string.Join(" ; ", physReport.Take(6))}");
                        var src = doc.Elements.FirstOrDefault(e => e.Type == "CMapEntity" && e.GetValue<int>("nodeID") == model.NodeId);
                        foreach (var m in src?.GetElements("children").Where(c => c.Type == "CMapMesh") ?? [])
                            foreach (var mat in (m.Get<DmxBinary.Element>("meshData")?.Get<object?[]>("materials") ?? []).Select(x => x as string ?? ""))
                            {
                                var info = content.Material(mat);
                                output.WriteLine($"    material {mat} shader {info?.Shader} attr '{Physics.WorldCollision.ReadMaterial(info, content.CollisionProperty).AttributeKey}'"
                                    + $" ints {string.Join(",", info?.Ints.Where(kv => kv.Key.StartsWith("mapbuilder", StringComparison.Ordinal) || kv.Key.StartsWith("F_", StringComparison.Ordinal)).Select(kv => $"{kv.Key}={kv.Value}") ?? [])}"
                                    + $" params {string.Join(",", info?.Params.Where(kv => kv.Key.StartsWith("F_", StringComparison.Ordinal)).Select(kv => $"{kv.Key}={kv.Value}") ?? [])}");
                            }
                    }
                }
                continue;
            }
            var mine = Physics.EntityPhysicsModels.Author(model, content.SurfaceName);
            var report = WorldPhysicsAuthorTests.Compare(bytes, mine);
            if (report.Count == 0)
            {
                exact++;
                continue;
            }
            differ++;
            foreach (var line in report)
            {
                var key = System.Text.RegularExpressions.Regex.Replace(line.Split(':')[0], @"\[\d+\]", "[]");
                tally[key] = tally.GetValueOrDefault(key) + 1;
            }
            if (shown++ < show)
                output.WriteLine($"{model.Path} ({model.ClassName}): {report.Count} differences: {string.Join(" ; ", report.Take(8))}");
        }
        var ours = models.Select(m => m.Path + "_c").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var theirsOnly = package.Entries!["vmdl_c"].Select(e => e.GetFullPath()).Where(x => x.Contains("/entities/", StringComparison.Ordinal) && !ours.Contains(x)).ToList();
        foreach (var x in theirsOnly.Take(show))
            output.WriteLine($"only in Valve's: {x}");
        missing += theirsOnly.Count;
        output.WriteLine($"{models.Count} brush entity models: physics only exact {exact}, differ {differ}, kind wrong {kindWrong}, not in package {missing - theirsOnly.Count}, only Valve's {theirsOnly.Count}; with render: PHYS exact {renderPhysExact}, differ {renderPhysDiffer}");
        foreach (var (key, count) in tally.OrderByDescending(x => x.Value).Take(20))
            output.WriteLine($"  {count,4} {key}");
        foreach (var note in notes.Take(10))
            output.WriteLine($"note: {note}");
        Assert.Equal(0, differ + kindWrong + missing + renderPhysDiffer);
    }
}
