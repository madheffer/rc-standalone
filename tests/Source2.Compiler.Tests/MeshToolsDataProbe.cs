using Source2.Compiler.Maps;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// What a compiled model's meshes carry for the tools (MESHTOOLS=&lt;model path&gt;,
/// e.g. models/props/de_dust/hr_dust/dust_crates/dust_crate_style_01_32x32x32.vmdl_c):
/// each embedded mesh's top-level keys, its blocks, and its draw calls' vertex
/// buffer indices, to see whether the tools vertex buffer the light scene's
/// trace data comes from (m_toolsBuffers, m_nToolsVBBlock) is there.
/// </summary>
public class MeshToolsDataProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("MESHTOOLS") is not { Length: > 0 } path || CS2Fixtures.StockPak() is not { } pak)
            return;
        using var content = new GameContent(pak);
        var bytes = content.Read(path) ?? throw new FileNotFoundException(path);
        using var resource = new Resource { FileName = path };
        resource.Read(new MemoryStream(bytes));
        output.WriteLine("blocks: " + string.Join(" ", resource.Blocks.Select(b => b.Type)));
        var model = (Model)resource.DataBlock!;
        foreach (var (mesh, index, name, lod) in model.GetEmbeddedMeshesAndLoD())
        {
            output.WriteLine($"mesh {index} {name} lod 0x{lod:x}: keys {string.Join(",", mesh.Data.Keys)}");
            foreach (var key in mesh.Data.Keys.Where(k => k.Contains("tools", StringComparison.OrdinalIgnoreCase)))
                output.WriteLine($"  {key} = {mesh.Data[key]}");
            var objects = mesh.Data.GetArray("m_sceneObjects");
            foreach (var so in objects)
                foreach (var call in so.GetArray("m_drawCalls"))
                    output.WriteLine($"  draw: material {call.GetStringProperty("m_material")} vbs {string.Join(",", call.GetArray("m_vertexBuffers").Select(v => v.GetInt32Property("m_hBuffer")))} indexStart {call.GetInt32Property("m_nStartIndex")} count {call.GetInt32Property("m_nIndexCount")} base {call.GetInt32Property("m_nBaseVertex")}");
        }
    }
}
