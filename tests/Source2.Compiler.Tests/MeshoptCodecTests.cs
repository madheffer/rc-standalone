using Source2.Compiler.Meshes;
using ValvePak;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// The meshopt vertex and index codecs: round trips on synthetic data, and resourcecompiler's own
/// buffers from pak01 decoded and re-encoded to the same bytes (docs/MESHES.md).
/// </summary>
public class MeshoptCodecTests
{
    [Theory]
    [InlineData(1, 4)]
    [InlineData(17, 12)]
    [InlineData(300, 20)]
    [InlineData(1000, 32)]
    public void Vertex_RoundTrips(int count, int size)
    {
        var rng = new Random(count * 31 + size);
        var data = new byte[count * size];
        // Smooth-ish data, like positions, with noise in some channels.
        for (int v = 0; v < count; v++)
            for (int b = 0; b < size; b++)
                data[v * size + b] = (byte)(b % 4 == 0 ? v * 3 + b : b % 4 == 1 ? rng.Next(4) : rng.Next(256));
        var encoded = MeshoptVertexCodec.Encode(data, count, size);
        Assert.Equal(0xA1, encoded[0]);
        Assert.Equal(data, MeshoptVertexCodec.Decode(encoded, count, size));
    }

    [Fact]
    public void Index_RoundTrips()
    {
        var rng = new Random(3);
        var indices = new List<uint>();
        // A strip-like grid, then some scattered triangles and a reset to 0 1 2.
        for (uint y = 0; y < 20; y++)
            for (uint x = 0; x < 20; x++)
            {
                uint a = y * 21 + x;
                indices.AddRange([a, a + 1, a + 21, a + 1, a + 22, a + 21]);
            }
        for (int i = 0; i < 50; i++)
            indices.AddRange([(uint)rng.Next(440), (uint)rng.Next(440), (uint)rng.Next(440)]);
        indices.AddRange([0, 1, 2]);
        var bytes = indices.SelectMany(i => BitConverter.GetBytes(i)).ToArray();
        var encoded = MeshoptIndexCodec.Encode(bytes, 4);
        Assert.Equal(0xE1, encoded[0]);
        // The codec keeps each triangle and its winding, not which vertex comes first.
        var decoded = MeshoptIndexCodec.Decode(encoded, indices.Count, 4);
        for (int t = 0; t < indices.Count; t += 3)
        {
            uint a = BitConverter.ToUInt32(decoded, t * 4), b = BitConverter.ToUInt32(decoded, t * 4 + 4), c = BitConverter.ToUInt32(decoded, t * 4 + 8);
            var want = (indices[t], indices[t + 1], indices[t + 2]);
            Assert.True(want == (a, b, c) || want == (b, c, a) || want == (c, a, b), $"triangle {t / 3}: {want} decoded as {(a, b, c)}");
        }
    }

    [Fact]
    public void ResourceCompilersBuffers_ReencodeToTheSameBytes()
    {
        if (CS2Fixtures.StockPak() is not { } path) { CS2Fixtures.Skip("pak01_dir.vpk"); return; }
        using var pak = new Package();
        pak.Read(path);
        int vertex = 0, index = 0;
        foreach (var entry in pak.Entries!["vmdl_c"].OrderBy(e => e.GetFullPath(), StringComparer.Ordinal).Take(1500))
        {
            pak.ReadEntry(entry, out var bytes);
            var model = CompiledResource.Read(bytes);
            if (!model.File.Blocks.Any(b => b.Type == "CTRL") || model.Kv3("CTRL") is not { } ctrl || !ctrl.ContainsKey("embedded_meshes"))
                continue;
            var blocks = model.File.Blocks.ToList();
            foreach (var mesh in ctrl["embedded_meshes"]!.Values)
                foreach (var (kind, isVertex) in new[] { ("m_vertexBuffers", true), ("m_indexBuffers", false) })
                {
                    if (!mesh.ContainsKey(kind))
                        continue;
                    foreach (var buffer in mesh[kind]!.Values)
                    {
                        if (!(bool)buffer["m_bMeshoptCompressed"]! || (bool)buffer["m_bCompressedZSTD"]!)
                            continue;
                        var data = blocks[(int)buffer["m_nBlockIndex"]!].Data;
                        int count = (int)buffer["m_nElementCount"]!, size = (int)buffer["m_nElementSizeInBytes"]!;
                        if (isVertex)
                        {
                            var raw = MeshoptVertexCodec.Decode(data, count, size);
                            if (data[0] != 0xA1)
                                continue; // v0: older compiles; decoded, not re-encoded
                            Assert.True(MeshoptVertexCodec.Encode(raw, count, size).AsSpan().SequenceEqual(data), $"{entry.GetFullPath()} vertex buffer");
                            vertex++;
                        }
                        else
                        {
                            var raw = MeshoptIndexCodec.Decode(data, count, size);
                            Assert.True(MeshoptIndexCodec.Encode(raw, size).AsSpan().SequenceEqual(data), $"{entry.GetFullPath()} index buffer");
                            index++;
                        }
                    }
                }
        }
        Console.WriteLine($"re-encoded identically: {vertex} vertex, {index} index buffers");
        Assert.True(vertex > 0 && index > 0);
    }
}
