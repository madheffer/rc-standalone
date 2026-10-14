using ValvePak;
using ValveKeyValue;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// KV3 blocks are written back in the binary version they were read in: version 4 (most stock
/// materials and models) as Valve writes it, so an unedited v4 block re-encodes to its own bytes.
/// </summary>
public class Kv3VersionTests
{
    private static IEnumerable<(string Path, ResourceBlock Block)> StockV4Blocks(string extension, int take)
    {
        if (CS2Fixtures.StockPak() is not { } path)
            yield break;
        using var pak = new Package();
        pak.Read(path);
        var found = 0;
        foreach (var entry in Io.VpkEntries.ByExtension(pak)[extension].OrderBy(e => e.GetFullPath(), StringComparer.Ordinal))
        {
            pak.ReadEntry(entry, out var bytes);
            foreach (var block in ResourceFile.Read(bytes).Blocks)
            {
                if (Kv3Binary.Version(block.Data) != 4)
                    continue;
                yield return (entry.GetFullPath(), block);
                if (++found == take)
                    yield break;
            }
        }
    }

    [Theory]
    [InlineData("vmat_c")]
    [InlineData("vmdl_c")]
    [InlineData("vpcf_c")]
    [InlineData("vsmart_c")]
    public void StockV4Blocks_ReencodeToTheirOwnBytes(string extension)
    {
        foreach (var (path, block) in StockV4Blocks(extension, 150))
        {
            var doc = Kv3Binary.Read(block.Data);
            // Large zstd blocks were written by an older zstd; LZ4 and raw ones are tier0's own.
            if (BitConverter.ToUInt32(block.Data, 20) == 2)
                continue;
            var written = Kv3Binary.Write(doc.Root, doc.Header!.Format, version: 4);
            Assert.True(written.AsSpan().SequenceEqual(block.Data), $"{path} {block.Type}");
        }
    }

    [Fact]
    public void EditedV4Block_StaysV4()
    {
        var (path, _) = StockV4Blocks("vmat_c", 1).FirstOrDefault();
        if (path is null)
            return;
        using var pak = new Package();
        pak.Read(CS2Fixtures.StockPak()!);
        pak.ReadEntry(pak.FindEntry(path)!, out var bytes);

        var resource = CompiledResource.Read(bytes);
        var data = resource.Kv3("DATA")!;
        data.Add("s2c_edit", new KVObject(5));
        var written = CompiledResource.Read(resource.Write());

        Assert.Equal(4, Kv3Binary.Version(written.Block("DATA")!.Data));
        Assert.Equal(5, (int)written.Kv3("DATA")!["s2c_edit"]!);
    }
}
