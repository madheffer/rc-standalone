using System.Numerics;
using SharpGLTF.Schema2;
using Source2.Compiler.Gltf;
using Source2.Compiler.Materials;
using Source2.Compiler.Meshes;
using ValvePak;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// glTF export without ValveResourceFormat or SharpGLTF (SharpGLTF only reads the result back here):
/// sampled pak01 models exported identical to VRF's exporter, node for node, accessor for accessor,
/// image byte for image byte, when this was written (docs/GLTF.md).
/// </summary>
public class GltfExportTests
{
    [Fact]
    public void Writer_ProducesAValidGlb()
    {
        var gltf = new GltfModel();
        var scene = gltf.CreateScene("test");
        var node = scene.CreateNode("triangle");
        var mesh = gltf.CreateMesh("triangle");
        var primitive = mesh.CreatePrimitive();
        primitive.SetAttribute("POSITION", gltf.CreateFloatAccessor(new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }, "VEC3", bounds: true));
        primitive.Indices = gltf.CreateIndexAccessor([0, 1, 2]);
        var material = gltf.CreateMaterial("red");
        material.BaseColorFactor = new Vector4(1, 0, 0, 1);
        primitive.Material = material;
        node.Mesh = mesh;
        node.Translation = new Vector3(1, 2, 3);

        var model = ModelRoot.ParseGLB(gltf.ToGlb());
        var read = model.LogicalMeshes.Single().Primitives.Single();
        Assert.Equal([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], read.GetVertexAccessor("POSITION").AsVector3Array());
        Assert.Equal([0u, 1u, 2u], read.GetIndices());
        Assert.Equal(new Vector3(1, 2, 3), model.LogicalNodes.Single().LocalTransform.Translation);
        Assert.Equal("red", read.Material.Name);
    }

    [Fact]
    public void Skin_BindsTheJointsWhereTheyStand()
    {
        var gltf = new GltfModel();
        var scene = gltf.CreateScene(null);
        var root = scene.CreateNode("root");
        var bone = root.CreateNode("bone");
        bone.Translation = new Vector3(0, 1, 0);
        var skin = gltf.CreateSkin([root, bone], Matrix4x4.Identity);
        var model = ModelRoot.ParseGLB(gltf.ToGlb());
        var (joint, inverseBind) = model.LogicalSkins.Single().GetJoint(1);
        Assert.Equal("bone", joint.Name);
        Assert.Equal(Matrix4x4.CreateTranslation(0, -1, 0), inverseBind);
        Assert.Equal(2, skin.Joints.Count);
    }

    private static ModelAnimationsLoader Loader(Package pak) => new(pak);

    private sealed class ModelAnimationsLoader(Package pak)
    {
        public byte[]? Load(string path)
        {
            var entry = pak.FindEntry(path + "_c");
            if (entry is null)
                return null;
            pak.ReadEntry(entry, out var bytes);
            return bytes;
        }
    }

    [Theory]
    [InlineData("models/props/de_dust/hr_dust/dust_crates/dust_crate_style_01_32x32x32.vmdl_c", false)]
    [InlineData("weapons/models/ak47/weapon_rif_ak47.vmdl_c", true)]
    public void StockModels_ExportToValidGlb(string path, bool skinned)
    {
        if (CS2Fixtures.StockPak() is not { } pakPath)
            return;
        using var pak = new Package();
        pak.Read(pakPath);
        if (pak.FindEntry(path) is not { } entry)
            return;
        pak.ReadEntry(entry, out var bytes);
        var exporter = new GltfModelExporter(Loader(pak).Load) { ExportSkeleton = true };
        var model = ModelRoot.ParseGLB(exporter.Export(CompiledModel.Read(bytes), path));
        Assert.NotEmpty(model.LogicalMeshes);
        Assert.All(model.LogicalMeshes.SelectMany(m => m.Primitives), p => Assert.NotNull(p.GetVertexAccessor("POSITION")));
        Assert.Equal(skinned, model.LogicalSkins.Count > 0);
        Assert.NotEmpty(model.LogicalMaterials);
    }

    [Fact]
    public void StockMaterial_DecompilesItsDynamicExpressions()
    {
        if (CS2Fixtures.StockPak() is not { } pakPath)
            return;
        using var pak = new Package();
        pak.Read(pakPath);
        foreach (var entry in Io.VpkEntries.ByExtension(pak)["vmat_c"].OrderBy(e => e.GetFullPath(), StringComparer.Ordinal))
        {
            pak.ReadEntry(entry, out var bytes);
            var material = CompiledMaterial.Read(bytes);
            if (material.DynamicExpressions.Count == 0)
                continue;
            Assert.All(material.DynamicExpressions.Values, e => Assert.False(string.IsNullOrWhiteSpace(e)));
            Assert.False(string.IsNullOrEmpty(material.ShaderName));
            return;
        }
    }

    [Fact]
    public void StringToken_NamesKnownKeysAndStoredNames()
    {
        Assert.Equal("targetname", StringToken.Lookup(StringToken.Get("targetname")));
        var token = StringToken.Store("s2c_test_attribute");
        Assert.Equal("s2c_test_attribute", StringToken.Lookup(token));
    }

    [Fact]
    public void ResourceBlocks_RecordWhereTheyWereRead()
    {
        var file = new ResourceFile { Version = 1 };
        file.Blocks.Add(new ResourceBlock("RED2", [1, 2, 3]));
        file.Blocks.Add(new ResourceBlock("DATA", [4, 5, 6, 7]));
        var bytes = file.Write();
        var read = ResourceFile.Read(bytes);
        foreach (var block in read.Blocks)
        {
            Assert.NotNull(block.SourceOffset);
            Assert.True(bytes.AsSpan((int)block.SourceOffset!.Value, block.Data.Length).SequenceEqual(block.Data));
        }
        var resource = CompiledResource.Read(bytes);
        resource.SetRaw("DATA", [9]);
        Assert.Null(resource.Block("DATA")!.SourceOffset);
        Assert.NotNull(resource.Block("RED2")!.SourceOffset);
    }
}
