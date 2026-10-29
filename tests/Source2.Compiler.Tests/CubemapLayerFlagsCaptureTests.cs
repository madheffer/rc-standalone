using System.Text.Json.Nodes;
using Source2.Compiler.Cubemaps;
using Source2.Compiler.Maps;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="CubemapLayerFlags"/>' inputs from the package against the game's (GROUND_TRUTH 157).
/// <c>LAYERCAP=&lt;run dir&gt;</c> holds frida.jsonl from run_probe.py --frida tools/cubehost/layer_members.js and the map
/// package (*.vpk, or <c>LAYERCAP_VPK</c>). Each captured aggregate object is matched to one of the package's world
/// node aggregates by its fragments' index counts; then every fragment's flags, the draw call's +0x1c and +0x14 and
/// the object's +0xe0 computed from the package (m_objectFlags, the draw call's booleans, the material's attributes
/// through its shader) must equal the captured values.
/// </summary>
public sealed class CubemapLayerFlagsCaptureTests(ITestOutputHelper output)
{
    internal static List<CubemapPackageAggregates.Layer> Aggregates(string vpk)
    {
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game");
        using var content = new GameContent(Path.Combine(game, "csgo", "pak01_dir.vpk"), Path.Combine(game, "csgo_addons", "s2c_rc_probe"));
        // The -vulkan game's programs, every render mode's (MaterialAttributes.OfModes).
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"), Path.Combine(game, "core", "shaders_vulkan_dir.vpk")) { Platform = "vulkan_50" };
        using var package = new ValvePak.Package();
        package.Read(vpk);
        return CubemapPackageAggregates.LayerInputs(package, content, shaders);
    }

    /// <summary>Each world node aggregate's layer-selection inputs by its model (m_renderableModel), from the package.</summary>
    internal static Dictionary<string, CubemapFaceDrawList.Membership> Memberships(string vpk) => CubemapPackageAggregates.Memberships(Aggregates(vpk));

    [Fact]
    public void FlagsEqualTheCapture()
    {
        if (Environment.GetEnvironmentVariable("LAYERCAP") is not { Length: > 0 } dir)
            return;
        var vpk = Environment.GetEnvironmentVariable("LAYERCAP_VPK") is { Length: > 0 } given ? given : Directory.GetFiles(dir, "*.vpk").Single();
        var ours = Aggregates(vpk);
        var records = File.ReadLines(Path.Combine(dir, "frida.jsonl")).Select(l => JsonNode.Parse(l)!).ToList();
        var captured = records.Where(r => (string?)r["kind"] == "object").ToList();
        // The game's attribute store of each object's draw-call material (layer_members.js "attributes").
        var stores = records.Where(r => (string?)r["kind"] == "attributes").GroupBy(r => (string)r["object"]!).ToDictionary(g => g.Key, g => g.First()["chain"]![0]!["entries"]!.AsArray());
        var failures = new List<string>();
        var matched = 0;
        foreach (var c in captured)
        {
            var frags = c["fragments"]!.AsArray().Select(f => f!.AsArray()).ToList();
            var counts = frags.Select(f => (int)f[2]!).ToList();
            var candidates = ours.Where(o => o.IndexCounts.SequenceEqual(counts)).ToList();
            if (candidates.Count == 0 || c["material1c14"] is not JsonArray mat)
                continue;
            // Several aggregates may share the index counts; one whose every value is equal is a match.
            string Diff(CubemapPackageAggregates.Layer o)
            {
                var d = new List<string>();
                for (var i = 0; i < frags.Count; i++)
                {
                    var mask = o.VisQuery[i] ? ~0x20 : ~0;
                    if (((int)frags[i][0]! & mask) != (o.Fragments[i] & mask))
                        d.Add($"fragment {i} flags 0x{(int)frags[i][0]!:x} captured, 0x{o.Fragments[i]:x} ours");
                }
                if ((uint)mat[0]! != o.DrawCall1c)
                    d.Add($"draw call +0x1c 0x{(uint)mat[0]!:x} captured, 0x{o.DrawCall1c:x} ours");
                if ((int)mat[1]! != o.DrawCall14)
                    d.Add($"draw call +0x14 0x{(int)mat[1]!:x} captured, 0x{o.DrawCall14:x} ours");
                if (c["f78"] is { } f78 && Convert.ToUInt64((string)f78!, 16) != o.F78)
                    d.Add($"object +0x78 {(string)f78!} captured, 0x{o.F78:x} ours");
                if (c["f80"] is { } f80 && Convert.ToUInt64((string)f80!, 16) != o.F80)
                    d.Add($"object +0x80 {(string)f80!} captured, 0x{o.F80:x} ours");
                if ((uint)c["objectE0"]! != o.ObjectE0)
                    d.Add($"object +0xe0 0x{(uint)c["objectE0"]!:x} captured, 0x{o.ObjectE0:x} ours");
                // Every key of the game's store, as int (vf 0x50 reads bools as 0 / 1).
                if (stores.TryGetValue((string)c["object"]!, out var store) && o.Material != null)
                    foreach (var e in store)
                    {
                        var key = Convert.ToUInt32((string)e![0]!, 16);
                        var theirs = (long)e[1]!;
                        if (o.Material.Int(key) is not { } ours || ours != theirs)
                            d.Add($"attribute 0x{key:x8} {theirs} captured, {(o.Material.Int(key)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "absent")} ours");
                    }
                return string.Join("; ", d);
            }
            var best = candidates.Select(o => (o, Diff(o))).OrderBy(x => x.Item2.Length).First();
            matched++;
            if (best.Item2.Length > 0)
                failures.Add($"{Path.GetFileNameWithoutExtension(best.o.Model)}: {best.Item2}");
        }
        output.WriteLine($"{ours.Count} aggregates in the package, {captured.Count} captured objects, {matched} matched by index counts; "
                         + $"{ours.Sum(o => o.VisQuery.Count(q => q))} fragments' 0x20 not compared (CPVS box query not ported)");
        foreach (var f in failures)
            output.WriteLine("FAIL " + f);
        Assert.True(matched > 0, "no captured object matched");
        Assert.Empty(failures);
    }
}
