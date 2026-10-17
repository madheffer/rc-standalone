using Source2.Compiler.Lighting.Bake;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// scenesystem's ray trace vertex layout (<see cref="SceneVertexLayout"/>) over a vrad3 job's
/// scene_meshes.kv3 (<c>SCENELAYOUT=&lt;folder&gt;</c>): every mesh builds, the stride is the
/// field sizes' sum, and each distinct layout is printed. On 2026-10-18 the port, written
/// from the disassembly, gave the same fields, vertex bytes and GeometryInfo_t as an
/// independent second port on all 90 meshes of probe01 and ze_hold_em_p.
/// </summary>
public class SceneVertexLayoutTests(ITestOutputHelper output)
{
    [Fact]
    public void Scene()
    {
        if (Environment.GetEnvironmentVariable("SCENELAYOUT") is not { } folder || CS2Fixtures.StockPak() is not { } pak)
            return;
        using var content = new GameContent(pak);
        var meshes = SceneInfoTests.ReadBinary(Path.Combine(folder, "scene_meshes.kv3"))["meshes"].Values.ToList();
        var read = typeof(BakeScene).GetMethod("ReadMesh", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var seen = new HashSet<string>();
        for (var m = 0; m < meshes.Count; m++)
        {
            var mesh = (BakeScene.Mesh)read.Invoke(null, [meshes[m]])!;
            var signature = MaterialAuthor.ExtractInputSignature(content.Read(mesh.Material + "_c")!);
            var r = SceneVertexLayout.Build(mesh, m, signature);
            Assert.Equal(r.Stride * mesh.Positions.Length, r.Words.Length * 4);
            var text = string.Join(" ", r.Fields.Select(f => $"{f.Name}{f.SemanticIndex}:{f.Format}@{f.Offset}"));
            if (seen.Add(text + mesh.Material))
                output.WriteLine($"{mesh.Material}: {text}, stride {r.Stride}");
            // csgo_simple_2way_blend's weights: VertexPaintBlendParams as TEXCOORD 4, R8G8B8A8_UNORM.
            if (signature.Any(e => e.Semantic == "VertexPaintBlendParams") && mesh.Fields.Any(f => f.Name == "VertexPaintBlendParams"))
                Assert.Equal(0x1c, r.Geometry.m_vTexCoordFieldInfo.data[4].y);
        }
    }
}
