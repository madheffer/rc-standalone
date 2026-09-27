using System.Buffers.Binary;
using Source2.Compiler.Kv3;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="Kv3Tree"/> is lossless: a block read and re-encoded carries Valve's own uncompressed
/// sections and header counters, and whatever it writes both readers decode to the same tree.
/// The stock sweep behind the counter rules (46,265 blocks, 10 types) is <c>kv3-parity</c>;
/// these pin it on committed RC outputs, a hand-built tree, and a stock sample when CS2 is present.
/// </summary>
public class Kv3TreeTests
{
    private static readonly int[] ComputedFields = [7, 8, 9, 10, 11, 14, 15, 16, 18, 22, 23, 24, 25, 26, 27, 28, 29];

    private static IEnumerable<byte[]> Kv3Blocks(byte[] resource)
    {
        using var res = new Resource();
        res.Read(new MemoryStream(resource), verifyFileSize: false);
        foreach (var b in res.Blocks)
            if (b.Size >= 120 && BinaryPrimitives.ReadUInt32LittleEndian(resource.AsSpan((int)b.Offset)) == 0x4B563305)
                yield return resource.AsSpan((int)b.Offset, (int)b.Size).ToArray();
    }

    private static string VrfText(byte[] block)
    {
        var kv3 = new BinaryKV3(BlockType.DATA) { Resource = null!, Size = (uint)block.Length };
        using var r = new BinaryReader(new MemoryStream(block));
        kv3.Read(r);
        return kv3.ToString();
    }

    private static void AssertLossless(byte[] block, string label)
    {
        var t = Kv3Tree.Read(block);
        var (b1, b2, blobs, h) = t.Encode();
        Assert.True(b1.AsSpan().SequenceEqual(t.ReadSections.Buffer1), $"{label}: buffer 1");
        Assert.True(b2.AsSpan().SequenceEqual(t.ReadSections.Buffer2), $"{label}: buffer 2");
        Assert.True(blobs.AsSpan().SequenceEqual(t.ReadSections.Blobs), $"{label}: blobs");
        foreach (var i in ComputedFields) Assert.True(h[i] == t.ReadHeader[i], $"{label}: header[{i}] {h[i]} vs {t.ReadHeader[i]}");

        var written = t.Write();
        var back = Kv3Tree.Read(written);
        Assert.True(back.Encode().Buffer2.AsSpan().SequenceEqual(t.ReadSections.Buffer2), $"{label}: written buffer 2");
        if (t.Compression == 0) Assert.Equal(t.ReadHeader, back.ReadHeader);
        Assert.Equal(VrfText(block), VrfText(written));
    }

    [Fact]
    public void Rc_reference_outputs_are_lossless()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null && !Directory.Exists(Path.Combine(dir, "RcReference")); i++) dir = Path.GetDirectoryName(dir);
        var refs = Directory.EnumerateFiles(Path.Combine(dir!, "RcReference"), "*_c").ToList();
        Assert.NotEmpty(refs);
        var blocks = 0;
        foreach (var f in refs)
            foreach (var block in Kv3Blocks(File.ReadAllBytes(f)))
            {
                AssertLossless(block, Path.GetFileName(f));
                blocks++;
            }
        Assert.True(blocks > 0, "no KV3 v5 block in RcReference");
    }

    private static Kv3Node Scalar(Kv3Type type, ulong bits = 0) => new() { Type = type, Bits = bits };

    private static Kv3Node Typed(Kv3Type kind, Kv3Type elem, IEnumerable<Kv3Node> items) =>
        new() { Type = kind, ElementType = elem, Items = items.ToList() };

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void Every_array_kind_is_written_so_both_readers_agree(uint compression)
    {
        var t = new Kv3Tree { Format = Guid.NewGuid(), Compression = compression };
        var obj = new Kv3Node { Type = Kv3Type.Object, Items = [], KeyIds = [] };
        void Put(string key, Kv3Node v) { obj.KeyIds!.Add(t.StringId(key)); obj.Items!.Add(v); }
        Put("name", new Kv3Node { Type = Kv3Type.String, StringId = t.StringId("draw_m4a4") });
        Put("frames", Scalar(Kv3Type.Int32, 300));
        Put("offsets", Typed(Kv3Type.ArrayTyped, Kv3Type.UInt32, Enumerable.Range(0, 300).Select(i => Scalar(Kv3Type.UInt32, (ulong)(i * 7)))));
        Put("weights", Typed(Kv3Type.ArrayTypeByteLength, Kv3Type.Float, Enumerable.Range(0, 9).Select(i => Scalar(Kv3Type.Float, BitConverter.SingleToUInt32Bits(i * 0.5f)))));
        Put("longAux", Typed(Kv3Type.ArrayTypeAuxiliaryBuffer, Kv3Type.Double, Enumerable.Range(0, 40).Select(i => Scalar(Kv3Type.Double, BitConverter.DoubleToUInt64Bits(i / 3.0)))));
        Put("shortAux", Typed(Kv3Type.ArrayTypeAuxiliaryBuffer, Kv3Type.Double, Enumerable.Range(0, 4).Select(i => Scalar(Kv3Type.Double, BitConverter.DoubleToUInt64Bits(i)))));
        Put("nested", new Kv3Node
        {
            Type = Kv3Type.ArrayTypeByteLength, ElementType = Kv3Type.ArrayTypeAuxiliaryBuffer,
            Items = [Typed(Kv3Type.ArrayTypeAuxiliaryBuffer, Kv3Type.Double, Enumerable.Range(0, 8).Select(i => Scalar(Kv3Type.Double, BitConverter.DoubleToUInt64Bits(-i))))],
        });
        Put("generic", new Kv3Node { Type = Kv3Type.Array, Items = [Scalar(Kv3Type.BooleanTrue), Scalar(Kv3Type.Int64One)] });
        Put("empty", new Kv3Node { Type = Kv3Type.Array, Items = [] });
        Put("pose", new Kv3Node { Type = Kv3Type.BinaryBlob, Blob = Enumerable.Range(0, 40000).Select(i => (byte)(i * 31 % 251)).ToArray() });
        t.Root = obj;

        var written = t.Write();
        var back = Kv3Tree.Read(written);
        Assert.True(back.Encode().Buffer1.AsSpan().SequenceEqual(t.Encode().Buffer1));
        Assert.True(back.Encode().Buffer2.AsSpan().SequenceEqual(t.Encode().Buffer2));
        Assert.Equal(compression, back.Compression);

        var kv3 = new BinaryKV3(BlockType.DATA) { Resource = null!, Size = (uint)written.Length };
        using (var r = new BinaryReader(new MemoryStream(written))) kv3.Read(r);
        ValveKeyValue.KVObject d = kv3.Data;
        Assert.Equal("draw_m4a4", d.GetStringProperty("name"));
        Assert.Equal(300, d.GetIntegerArray("offsets").Length);
        Assert.Equal(299 * 7, d.GetIntegerArray("offsets")[299]);
        Assert.Equal(40, d.GetArray("longAux").Count);
        Assert.Equal(-7.0, (double)d.GetArray("nested")[0][7]);
        Assert.Equal(40000, d.GetArray<byte>("pose").Length);
    }

    /// <summary>
    /// tier0 decodes LZ4 blobs one blob at a time and copies each whole frame into that blob,
    /// so no frame may span two blobs. Shapes are draw_famas (7812+744) and reload_famas
    /// (25200+3000), whose single spanning frame corrupted the heap in game.
    /// </summary>
    [Theory]
    [InlineData(new[] { 7812, 744 }, new[] { 1, 1 })]
    [InlineData(new[] { 25200, 3000 }, new[] { 2, 1 })]
    [InlineData(new[] { 0, 16384, 1 }, new[] { 0, 1, 1 })]
    public void Lz4_frames_never_span_two_blobs(int[] lengths, int[] framesPerBlob)
    {
        var t = new Kv3Tree { Format = Guid.NewGuid(), Compression = 1 };
        var obj = new Kv3Node { Type = Kv3Type.Object, Items = [], KeyIds = [] };
        for (var b = 0; b < lengths.Length; b++)
        {
            obj.KeyIds!.Add(t.StringId($"blob{b}"));
            obj.Items!.Add(new Kv3Node { Type = Kv3Type.BinaryBlob, Blob = Enumerable.Range(0, lengths[b]).Select(i => (byte)((i * 7 + b) % 253)).ToArray() });
        }
        t.Root = obj;

        var written = t.Write();
        var header = new int[30];
        for (var i = 0; i < 30; i++) header[i] = BinaryPrimitives.ReadInt32LittleEndian(written.AsSpan(i * 4));
        Assert.Equal(framesPerBlob.Sum() * 2, header[17]);

        var back = Kv3Tree.Read(written);
        Assert.True(back.ReadSections.Blobs.AsSpan().SequenceEqual(t.Encode().Blobs));
    }

    [Fact]
    public void Stock_animation_blocks_are_lossless()
    {
        if (CS2Fixtures.StockPak() is not { } path) { CS2Fixtures.Skip("pak01_dir.vpk"); return; }
        using var pak = new Package();
        pak.Read(path);
        var checkedBlocks = 0;
        foreach (var ext in new[] { "vnmclip_c", "vnmgraph_c", "vnmskel_c" })
            foreach (var e in pak.Entries[ext].Take(150))
            {
                pak.ReadEntry(e, out var bytes);
                foreach (var block in Kv3Blocks(bytes)) { AssertLossless(block, e.GetFullPath()); checkedBlocks++; }
            }
        Assert.True(checkedBlocks >= 600, $"only {checkedBlocks} blocks sampled");
    }
}
