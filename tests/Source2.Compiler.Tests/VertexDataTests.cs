using System.Numerics;
using System.Text;
using Source2.Compiler.Meshes;
using ValvePak;
using Xunit;
using VrfVbib = ValveResourceFormat.Blocks.VBIB;

namespace Source2.Compiler.Tests;

/// <summary>
/// Vertex and index buffers decode to attributes as ValveResourceFormat's VBIB decodes them (every pak01
/// mesh agreed, buffer for buffer and attribute for attribute, when this was written; docs/MESHES.md).
/// </summary>
public class VertexDataTests
{
    // A binary VBIB block: three vertices (POSITION float3 at 0, NORMAL packed uint at 12) and three
    // 16-bit indices, both stored raw (bit 26 of the size word set).
    private static byte[] BinaryBlock(uint[] packedNormals)
    {
        var block = new byte[230];
        var w = new BinaryWriter(new MemoryStream(block));
        w.Write(16u); w.Write(1u); w.Write(32u); w.Write(1u);                     // vertex records at 16, index records at 8 + 32
        w.Write(3u); w.Write(16 | 0x4000000); w.Write(40u); w.Write(2u); w.Write(144u); w.Write(48);   // at 16: attributes at 64, data at 176
        w.Write(3u); w.Write(2 | 0x4000000); w.Write(0u); w.Write(0u); w.Write(168u); w.Write(6);      // at 40: data at 224
        foreach (var (name, format, offset) in new[] { ("POSITION", DXGI_FORMAT.R32G32B32_FLOAT, 0u), ("NORMAL", DXGI_FORMAT.R32_UINT, 12u) })
        {
            var at = w.BaseStream.Position;
            w.Write(Encoding.ASCII.GetBytes(name));
            w.BaseStream.Position = at + 32;
            w.Write(0); w.Write((uint)format); w.Write(offset); w.Write(0); w.Write(0u); w.Write(0);
        }
        for (var v = 0; v < 3; v++)
        {
            w.Write(v + 0.5f); w.Write(-v * 2f); w.Write(3f);
            w.Write(packedNormals[v]);
        }
        w.Write((ushort)0); w.Write((ushort)2); w.Write((ushort)1);
        Assert.Equal(block.Length, w.BaseStream.Position);
        return block;
    }

    [Fact]
    public void BinaryVbib_ReadsAsVrfReadsIt()
    {
        uint[] normals = [0x80000000u | (512u << 12) | 1u, (1023u << 22) | (100u << 1), (300u << 22) | (700u << 12) | (2047u << 1) | 1u];
        var block = BinaryBlock(normals);
        var ours = Vbib.Read(block);
        using var resource = new ValveResourceFormat.Resource();
        var theirs = new VrfVbib { Offset = 0, Size = (uint)block.Length, Resource = resource };
        theirs.Read(new BinaryReader(new MemoryStream(block)));

        var (vb, tvb) = (ours.VertexBuffers.Single(), theirs.VertexBuffers.Single());
        Assert.Equal(tvb.Data, vb.Data);
        Assert.Equal(theirs.IndexBuffers.Single().Data, ours.IndexBuffers.Single().Data);
        Assert.Equal([0u, 2u, 1u], Vbib.GetIndices(ours.IndexBuffers[0]));
        Assert.Equal(["POSITION", "NORMAL"], vb.InputLayoutFields.Select(f => f.SemanticName));

        Assert.Equal(VrfVbib.GetVector3AttributeArray(tvb, tvb.InputLayoutFields[0]), Vbib.GetVector3AttributeArray(vb, vb.InputLayoutFields[0]));
        Assert.Equal(new Vector3(1.5f, -2f, 3f), Vbib.GetVector3AttributeArray(vb, vb.InputLayoutFields[0])[1]);
        var (n, t) = Vbib.GetNormalTangentArray(vb, vb.InputLayoutFields[1]);
        var (tn, tt) = VrfVbib.GetNormalTangentArray(tvb, tvb.InputLayoutFields[1]);
        Assert.Equal(tn, n);
        Assert.Equal(tt, t);
    }

    [Fact]
    public void HostileBuffers_AreInvalidData()
    {
        var block = BinaryBlock([0, 0, 0]);
        BitConverter.GetBytes(1000u).CopyTo(block, 4);           // a thousand vertex records
        Assert.Throws<InvalidDataException>(() => Vbib.Read(block));

        var short_ = new OnDiskBufferData { ElementCount = 4, ElementSizeInBytes = 12, Data = new byte[40], InputLayoutFields = [] };
        var position = new RenderInputLayoutField { SemanticName = "POSITION", Format = DXGI_FORMAT.R32G32B32_FLOAT };
        Assert.Throws<InvalidDataException>(() => Vbib.GetVector3AttributeArray(short_, position));
        var bones = new OnDiskBufferData { ElementCount = 1, ElementSizeInBytes = 4, Data = [5, 0, 0, 0], InputLayoutFields = [] };
        var indices = new RenderInputLayoutField { SemanticName = "BLENDINDICES", Format = DXGI_FORMAT.R8G8B8A8_UINT };
        Assert.Throws<InvalidDataException>(() => Vbib.GetBlendIndicesArray(bones, indices, remapTable: [0, 1]));
    }

    [Fact]
    public void StockModels_DecodeEveryMeshAttribute()
    {
        if (CS2Fixtures.StockPak() is not { } path)
            return;
        using var pak = new Package();
        pak.Read(path);
        var meshes = 0;
        foreach (var entry in Io.VpkEntries.ByExtension(pak)["vmdl_c"].OrderBy(e => e.GetFullPath(), StringComparer.Ordinal).Take(60))
        {
            pak.ReadEntry(entry, out var bytes);
            var model = CompiledModel.Read(bytes);
            foreach (var mesh in model.Meshes)
            {
                var vbib = mesh.Vbib;
                var vertices = vbib.VertexBuffers.Sum(b => (long)b.ElementCount);
                foreach (var ib in vbib.IndexBuffers)
                    Assert.All(Vbib.GetIndices(ib), i => Assert.True(i < vertices, $"{entry.GetFullPath()} index {i} of {vertices}"));
                foreach (var vb in vbib.VertexBuffers)
                {
                    var position = vb.InputLayoutFields.FirstOrDefault(f => f.SemanticName == "POSITION");
                    if (position.SemanticName is null)
                        continue;
                    Assert.All(Vbib.GetVector3AttributeArray(vb, position), p => Assert.True(float.IsFinite(p.X + p.Y + p.Z)));
                    var normal = vb.InputLayoutFields.FirstOrDefault(f => f.SemanticName == "NORMAL");
                    if (normal.SemanticName is not null)
                        Assert.All(Vbib.GetNormalTangentArray(vb, normal).Normals, v => Assert.InRange(v.Length(), 0.99f, 1.01f));
                }
                meshes++;
            }
        }
        Assert.True(meshes > 0);
    }
}
