using Source2.Compiler.Maps;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Scratch probe (PROPAGGCHECK=addon|model,model): for each model, every
/// draw call's LOD, material, entry attribute bits and whether
/// <see cref="MeshLists.CanAggregate"/> lets it join, with the bits that stop it.
/// </summary>
public class PropAggregateCheckProbe(ITestOutputHelper output)
{
    [Fact]
    public void Check()
    {
        if (Environment.GetEnvironmentVariable("PROPAGGCHECK") is not { Length: > 0 } spec || CS2Fixtures.StockPak() is not { } pak)
            return;
        var p = spec.Split('|');
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", p[0]));
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_pc_dir.vpk"), Path.Combine(game, "core", "shaders_pc_dir.vpk"));
        foreach (var modelName in p[1].Split(','))
        {
            if (content.LoadedModel(modelName + "_c") is not { } model)
            {
                output.WriteLine($"{modelName}: not found");
                continue;
            }
            foreach (var (mesh, _, _, lod) in model.GetEmbeddedMeshesAndLoD())
                foreach (var so in mesh.Data.GetArray("m_sceneObjects"))
                    foreach (var call in so.GetArray("m_drawCalls"))
                    {
                        var material = call.GetStringProperty("m_material") ?? "";
                        var vb = mesh.VBIB.VertexBuffers[call.GetArray("m_vertexBuffers")[0].GetInt32Property("m_hBuffer")];
                        var attributes = content.Material(material) is { } info ? MaterialAttributes.Of(info, shaders, content.TextureSize) : MaterialAttributes.Empty;
                        var flags = MeshEntryFlags.Compute(attributes, default);
                        var input = new MeshLists.Input(flags.Flags, 8, 0, 0f, false, false);
                        output.WriteLine($"{modelName} lod {lod} {material}: bits {flags.Flags:x} blocking {flags.Flags & 0x800e01:x} instancing {(flags.Flags >> 30) & 1} "
                                         + $"can {MeshLists.CanAggregate(input)} streams {string.Join(",", vb.InputLayoutFields.Select(f => f.SemanticName + f.SemanticIndex))}");
                    }
        }
    }
}
