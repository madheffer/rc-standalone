using Source2.Compiler.Meshes;
using ValvePak;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="CompiledModel"/> on CS2's own models: an unedited model writes back byte for byte,
/// every buffer read and stored again gives resourcecompiler's bytes, and an edit survives.
/// </summary>
public class CompiledModelTests
{
    private static IEnumerable<(string Path, byte[] Bytes)> Models(int take)
    {
        if (CS2Fixtures.StockPak() is not { } path)
            yield break;
        using var pak = new Package();
        pak.Read(path);
        foreach (var entry in pak.Entries!["vmdl_c"].OrderBy(e => e.GetFullPath(), StringComparer.Ordinal).Take(take))
        {
            pak.ReadEntry(entry, out var bytes);
            yield return (entry.GetFullPath(), bytes);
        }
    }

    [Fact]
    public void BuffersStoredAgain_GiveResourceCompilersBytes()
    {
        int models = 0, buffers = 0;
        foreach (var (path, bytes) in Models(1500))
        {
            var model = CompiledModel.Read(bytes);
            var meshes = model.Meshes;
            // Current compiles only: older ones hold v0 vertex buffers, which RC no longer writes.
            if (meshes.Count == 0 || meshes.SelectMany(m => m.VertexBuffers).Any(b => !b.Descriptor.ContainsKey("m_nMeshoptMeshletEncodeVersion")))
                continue;
            foreach (var buffer in meshes.SelectMany(m => m.VertexBuffers.Concat(m.IndexBuffers)))
            {
                buffer.Write(buffer.Read(), buffer.ElementCount);
                buffers++;
            }
            Assert.True(bytes.AsSpan().SequenceEqual(model.Write()), path);
            models++;
        }
        if (models == 0) { CS2Fixtures.Skip("pak01_dir.vpk"); return; }
        Console.WriteLine($"{models} models, {buffers} buffers stored again byte-identically");
    }

    [Fact]
    public void UneditedModel_WritesBackByteForByte()
    {
        int models = 0;
        foreach (var (path, bytes) in Models(300))
        {
            Assert.True(bytes.AsSpan().SequenceEqual(CompiledModel.Read(bytes).Write()), path);
            models++;
        }
        if (models == 0) CS2Fixtures.Skip("pak01_dir.vpk");
    }

    [Fact]
    public void EditedVertexBuffer_ReadsBackEdited()
    {
        foreach (var (path, bytes) in Models(1500))
        {
            var model = CompiledModel.Read(bytes);
            if (model.Meshes.SelectMany(m => m.VertexBuffers).FirstOrDefault(b => b.IsCompressed && b.ElementCount > 100) is not { } buffer)
                continue;
            var vertices = buffer.Read();
            // Move every vertex: the first float of each (POSITION.x on CS2's layouts) plus one.
            for (int v = 0; v < buffer.ElementCount; v++)
                BitConverter.TryWriteBytes(vertices.AsSpan(v * buffer.ElementSize), BitConverter.ToSingle(vertices, v * buffer.ElementSize) + 1f);
            buffer.Write(vertices, buffer.ElementCount);

            var reread = CompiledModel.Read(model.Write());
            var again = reread.Meshes.SelectMany(m => m.VertexBuffers).First(b => b.Descriptor["m_nBlockIndex"]!.ToString() == buffer.Descriptor["m_nBlockIndex"]!.ToString());
            Assert.True(again.IsCompressed, path);
            Assert.Equal(vertices, again.Read());
            return;
        }
        CS2Fixtures.Skip("pak01_dir.vpk");
    }
}
