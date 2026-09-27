using Source2.Compiler.Maps;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: a map's smart props evaluated, each emitted model with its
/// element path and the prop entity the compile makes of it.
/// <c>SMARTPROP=&lt;addon&gt;|&lt;map&gt;</c>.
/// </summary>
public class SmartPropReplay(ITestOutputHelper output)
{
    [Fact]
    public void Evaluate()
    {
        if (Environment.GetEnvironmentVariable("SMARTPROP") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        var game = Path.Combine(cs2, "game");
        var vmap = Path.Combine(cs2, "content", "csgo_addons", parts[0], "maps", parts[1] + ".vmap");
        using var models = new SettleBuildTests.PakModels(Path.Combine(game, "csgo", "pak01_dir.vpk"), Path.Combine(game, "csgo_addons", parts[0]));
        // SMARTPROP_VPK: the compiled map's world node objects whose model matches SMARTPROP_MODEL.
        if (Environment.GetEnvironmentVariable("SMARTPROP_VPK") is { Length: > 0 } vpk)
        {
            using var package = new ValvePak.Package();
            package.Read(vpk);
            var filter = Environment.GetEnvironmentVariable("SMARTPROP_MODEL") ?? "radiator";
            foreach (var entry in package.Entries.SelectMany(kv => kv.Value).Where(e => e.GetFullPath().EndsWith(".vwnod_c", StringComparison.Ordinal)))
            {
                package.ReadEntry(entry, out var nodeBytes);
                using var nodeResource = new Resource();
                nodeResource.Read(new MemoryStream(nodeBytes));
                var data = ((WorldNode)nodeResource.DataBlock!).Data;
                output.WriteLine($"{entry.GetFullPath()}: {data.GetArray("m_sceneObjects")?.Count} objects, {data.GetArray("m_aggregateSceneObjects")?.Count} aggregates");
                foreach (var o in (data.GetArray("m_aggregateSceneObjects") ?? []).Where(o => o.GetStringProperty("m_renderableModel")?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true))
                {
                    output.WriteLine($"  aggregate {o.GetStringProperty("m_renderableModel")}: {string.Join(", ", o.Keys)}");
                    var ft = o["m_fragmentTransforms"];
                    output.WriteLine($"  fragmentTransforms {ft?.ValueType}: {(ft?.ValueType == ValveKeyValue.KVValueType.BinaryBlob ? string.Join(" ", System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(((byte[])ft)).ToArray().Select(f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) : string.Join(" | ", (ft?.Values ?? []).Select(r => string.Join(" ", r.Values.Select(x => ((float)(double)x).ToString("R", System.Globalization.CultureInfo.InvariantCulture))))))}");
                    foreach (var am in o.GetArray("m_aggregateMeshes") ?? [])
                        output.WriteLine($"  mesh: {string.Join(", ", am.Keys.Select(k => $"{k}={am[k]}"))}");
                    var aggEntry = package.FindEntry(Path.ChangeExtension(o.GetStringProperty("m_renderableModel"), ".vmdl_c"));
                    if (aggEntry == null)
                        continue;
                    package.ReadEntry(aggEntry, out var aggBytes);
                    using var aggResource = new Resource();
                    aggResource.Read(new MemoryStream(aggBytes));
                    var aggModel = (Model)aggResource.DataBlock!;
                    output.WriteLine($"  model data: {string.Join(", ", aggModel.Data.Keys)}");
                    foreach (var part in aggModel.GetEmbeddedPhys()?.Parts ?? [])
                    {
                        output.WriteLine($"  part: hulls {part.Shape.Hulls.Length} meshes {part.Shape.Meshes.Length}");
                        foreach (var mesh in part.Shape.Meshes)
                            output.WriteLine($"    mesh {mesh.Shape.GetVertices().Length} v: {string.Join(" ", mesh.Shape.GetVertices().ToArray().Take(4).Select(v => v.ToString("R", null)))}");
                    }
                }
                foreach (var o in data.GetArray("m_sceneObjects") ?? [])
                {
                    var model = o.GetStringProperty("m_renderableModel");
                    if (model == null || !model.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var m = o.GetArray("m_vTransform").Select(r => r.Values.Select(x => ((float)(double)x).ToString("R", System.Globalization.CultureInfo.InvariantCulture))).Select(r => string.Join(" ", r));
                    output.WriteLine($"{entry.GetFullPath()} {model}: {string.Join(" | ", m)}");
                }
            }
        }
        var doc = DmxBinary.ReadFile(vmap);
        foreach (var node in doc.OfType("CMapSmartProp"))
        {
            var file = node.Get<string>("smartPropFilename")!;
            var bytes = models.Read(Path.ChangeExtension(file, ".vsmart_c"))!;
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            var root = ((BinaryKV3)resource.DataBlock!).Data;
            var (configuration, parameters) = SmartPropEvaluator.NodeData(node);
            output.WriteLine($"CMapSmartProp {node.GetValue<int>("nodeID")} {file}");
            var start = SmartPropEvaluator.NodeTransform(MapMeshes.Local(node));
            foreach (var p in SmartPropEvaluator.Evaluate(root, configuration, parameters, start))
            {
                var (origin, angles, scales) = SmartPropEvaluator.PropPlacement(start, p);
                var t = p.Transform;
                output.WriteLine($"  [{string.Join(",", p.Path)}] {p.Model}");
                output.WriteLine($"    local ({t.Position.X:R},{t.Position.Y:R},{t.Position.Z:R}) s {t.Scale:R} q ({t.Rotation.X:R},{t.Rotation.Y:R},{t.Rotation.Z:R},{t.Rotation.W:R})");
                output.WriteLine($"    origin ({origin.X:R},{origin.Y:R},{origin.Z:R}) angles ({angles.X:R},{angles.Y:R},{angles.Z:R}) scales ({scales.X:R},{scales.Y:R},{scales.Z:R})");
                if (Environment.GetEnvironmentVariable("SMARTPROP_VERTS") == "1" && models.Physics(p.Model) is { } phys)
                {
                    foreach (var part in phys.Parts)
                    {
                        output.WriteLine($"    part: hulls {part.Shape.Hulls.Length} meshes {part.Shape.Meshes.Length} spheres {part.Shape.Spheres.Length} capsules {part.Shape.Capsules.Length}");
                        foreach (var m in part.Shape.Meshes)
                            output.WriteLine($"      mesh {m.Shape.GetVertices().Length} v: {string.Join(" ", m.Shape.GetVertices().ToArray().Take(8).Select(v => v.ToString("R", null)))}");
                    }
                }
            }
        }
    }
}
