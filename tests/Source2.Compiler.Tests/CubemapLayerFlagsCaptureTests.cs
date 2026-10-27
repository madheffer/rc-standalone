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
    internal sealed record Ours(string Name, List<ushort> Fragments, List<int> IndexCounts, uint DrawCall1c, ushort DrawCall14, uint ObjectE0, MaterialAttributes? Material, List<bool> VisQuery);

    private static uint ObjectFlags(KVObject mesh)
    {
        var text = mesh.Children.First(c => c.Key == "m_objectFlags").Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (uint.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n))
            return n;
        return text.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Aggregate(0u, (f, name) => f | WorldNodeSceneTrees.ObjectTypes.First(t => t.Name == name).Value);
    }

    private static bool Flag(KVObject o, string key) => o.ContainsKey(key) && o.GetBooleanProperty(key);

    internal static List<Ours> Aggregates(string vpk)
    {
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game");
        using var content = new GameContent(Path.Combine(game, "csgo", "pak01_dir.vpk"), Path.Combine(game, "csgo_addons", "s2c_rc_probe"));
        // The -vulkan game's programs, every render mode's (MaterialAttributes.OfModes).
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"), Path.Combine(game, "core", "shaders_vulkan_dir.vpk")) { Platform = "vulkan_50" };
        var attributes = new Dictionary<string, MaterialAttributes>();
        MaterialAttributes Attributes(string m)
        {
            if (!attributes.TryGetValue(m, out var a))
                attributes[m] = a = content.Material(m) is { } info ? MaterialAttributes.OfModes(info, shaders) : MaterialAttributes.Empty;
            return a;
        }
        using var package = new ValvePak.Package();
        package.Read(vpk);
        var result = new List<Ours>();
        foreach (var entry in package.Entries!.SelectMany(kv => kv.Value).Where(e => e.GetFullPath().EndsWith(".vwnod_c", StringComparison.Ordinal)))
        {
            package.ReadEntry(entry, out var nodeBytes);
            using var nodeResource = new Resource();
            nodeResource.Read(new MemoryStream(nodeBytes));
            foreach (var o in ((WorldNode)nodeResource.DataBlock!).Data.GetArray("m_aggregateSceneObjects") ?? [])
            {
                var name = o.GetStringProperty("m_renderableModel") ?? "";
                package.ReadEntry(package.FindEntry(Path.ChangeExtension(name, ".vmdl_c"))!, out var bytes);
                using var resource = new Resource();
                resource.Read(new MemoryStream(bytes));
                var calls = new List<KVObject>();
                foreach (var (mesh, _, _, _) in ((Model)resource.DataBlock!).GetEmbeddedMeshesAndLoD())
                    foreach (var so in mesh.Data.GetArray("m_sceneObjects"))
                        calls.AddRange(so.GetArray("m_drawCalls"));
                var flags = calls.Select(c => CubemapLayerFlags.MaterialFlags(c.GetStringProperty("m_material") is { Length: > 0 } m ? Attributes(m) : null)).ToList();
                var fragments = new List<ushort>();
                var counts = new List<int>();
                var query = new List<bool>();
                foreach (var m in o.GetArray("m_aggregateMeshes"))
                {
                    var call = m.GetInt32Property("m_nDrawCallIndex");
                    var objectFlags = ObjectFlags(m);
                    // Fragments whose 0x20 needs CPVS's box query (not ported): computed with no clusters, that bit not compared.
                    query.Add(CubemapLayerFlags.NeedsVisQuery(objectFlags));
                    fragments.Add(CubemapLayerFlags.FragmentFlags(objectFlags, flags[call], m.GetInt32Property("m_nVisClusterMemberCount"), visClusters: 0));
                    counts.Add(calls[call].GetInt32Property("m_nIndexCount"));
                }
                var first = calls[0];
                var bools = new CubemapLayerFlags.DrawCallBools(Flag(first, "m_bUseShadowFastPath"), Flag(first, "m_bUseCompressedNormalTangent"),
                    Flag(first, "m_bIsNotMatchedToMaterial"), Flag(first, "m_bHasBakedLightingFromVertexStream"), Flag(first, "m_bHasBakedLightingFromLightMap"),
                    Flag(first, "m_bCanBatchWithDynamicShaderConstants"), Flag(first, "m_bDrawLast"), Flag(first, "m_bBakedTwoSided"),
                    Flag(first, "m_bOnlyShadow"), Flag(first, "m_bNoShadow"));
                var material = first.GetStringProperty("m_material") is { Length: > 0 } fm ? Attributes(fm) : null;
                result.Add(new Ours(name, fragments, counts, flags[0], CubemapLayerFlags.DrawCallFlags(bools, material), CubemapLayerFlags.ObjectFlags(fragments), material, query));
            }
        }
        return result;
    }

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
            string Diff(Ours o)
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
                failures.Add($"{Path.GetFileNameWithoutExtension(best.o.Name)}: {best.Item2}");
        }
        output.WriteLine($"{ours.Count} aggregates in the package, {captured.Count} captured objects, {matched} matched by index counts; "
                         + $"{ours.Sum(o => o.VisQuery.Count(q => q))} fragments' 0x20 not compared (CPVS box query not ported)");
        foreach (var f in failures)
            output.WriteLine("FAIL " + f);
        Assert.True(matched > 0, "no captured object matched");
        Assert.Empty(failures);
    }
}
