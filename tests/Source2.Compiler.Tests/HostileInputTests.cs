using System.Buffers.Binary;
using Source2.Compiler.Compiling;
using Source2.Compiler.Kv3;
using Source2.Compiler.Meshes;
using Source2.Compiler.Shaders;
using ValveKeyValue;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// Malformed and hostile input fails with the documented exception, quickly, without a huge
/// allocation or a stack overflow (either of which ends a host process).
/// </summary>
public class HostileInputTests
{
    // A legacy VKV3 container (binary, uncompressed) around a body: string count 0, the root, trailer.
    private static byte[] LegacyBlock(byte[] body)
    {
        var o = new MemoryStream();
        o.Write(BitConverter.GetBytes(0x03564B56));
        o.Write(Kv3Formats.Get("binary").Id.ToByteArray());
        o.Write(Kv3Formats.Get("generic").Id.ToByteArray());
        o.Write(BitConverter.GetBytes(0));
        o.Write(body);
        o.Write(BitConverter.GetBytes(0xFFFFFFFF));
        return o.ToArray();
    }

    [Fact]
    public void LegacyKv3_DeepNesting_IsRefusedNotAStackOverflow()
    {
        var body = new MemoryStream();
        for (var i = 0; i < 100_000; i++)
        {
            body.WriteByte((byte)Kv3Type.Array);
            body.Write(BitConverter.GetBytes(1));
        }
        body.WriteByte((byte)Kv3Type.Null);
        Assert.Throws<InvalidDataException>(() => Kv3Binary.Read(LegacyBlock(body.ToArray())));
    }

    [Fact]
    public void LegacyKv3_ZeroWidthArrayOfBillions_IsRefused()
    {
        var body = new MemoryStream();
        body.WriteByte((byte)Kv3Type.ArrayTyped);
        body.Write(BitConverter.GetBytes(int.MaxValue));
        body.WriteByte((byte)Kv3Type.Null);
        Assert.Throws<InvalidDataException>(() => Kv3Binary.Read(LegacyBlock(body.ToArray())));
    }

    // An uncompressed v5 block whose root holds a typed array of 300 nulls, and where its count sits.
    private static (byte[] Block, int CountAt) TypedNullArrayBlock()
    {
        var root = KVObject.Collection();
        var array = KVObject.Array();
        for (var i = 0; i < 300; i++)
            array.Add(KVObject.Null());
        root.Add("a", array);
        var block = Kv3V5Writer.Write(root, Kv3Formats.Get("generic").Id, Kv3Compression.Uncompressed);
        var at = block.AsSpan(120).IndexOf(BitConverter.GetBytes(300));
        Assert.True(at >= 0);
        return (block, 120 + at);
    }

    [Fact]
    public void Kv3V5_ZeroWidthArrayOfBillions_IsRefusedByBothReaders()
    {
        var (block, at) = TypedNullArrayBlock();
        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(at), int.MaxValue);
        Assert.Throws<InvalidDataException>(() => Kv3Tree.Read(block));
        Assert.Throws<InvalidDataException>(() => Kv3Binary.Read(block));
    }

    [Fact]
    public void Kv3V5_EveryTruncation_IsInvalidData()
    {
        var (block, _) = TypedNullArrayBlock();
        for (var length = 0; length < block.Length; length++)
        {
            var cut = block[..length];
            try
            {
                Kv3Tree.Read(cut);
            }
            catch (InvalidDataException)
            {
            }
            try
            {
                Kv3Binary.Read(cut);
            }
            catch (InvalidDataException)
            {
            }
        }
    }

    [Fact]
    public void Kv3ReadLimit_ScopesOnlyLowerAndRestore()
    {
        Assert.Equal(Kv3ReadLimit.DefaultMaxUncompressedBytes, Kv3ReadLimit.MaxUncompressedBytes);
        using (Kv3ReadLimit.Use(64 << 20))
        {
            using (Kv3ReadLimit.Use(1L << 40))
                Assert.Equal(64 << 20, Kv3ReadLimit.MaxUncompressedBytes);
            using (Kv3ReadLimit.Use(1 << 20))
                Assert.Equal(1 << 20, Kv3ReadLimit.MaxUncompressedBytes);
            Assert.Equal(64 << 20, Kv3ReadLimit.MaxUncompressedBytes);
        }
        Assert.Equal(Kv3ReadLimit.DefaultMaxUncompressedBytes, Kv3ReadLimit.MaxUncompressedBytes);

        // An unbounded remaining (a standalone KV3 on a stream that cannot seek) never overflows.
        Kv3Limits.CheckSizes(3 << 20, 1000, long.MaxValue, 3 << 20, 1000);
        var ex = Assert.Throws<InvalidDataException>(() => Kv3Limits.CheckSizes(Kv3ReadLimit.DefaultMaxUncompressedBytes + 1, 1000, long.MaxValue));
        Assert.Contains($"over the {Kv3ReadLimit.DefaultMaxUncompressedBytes}-byte limit", ex.Message);
    }

    // The audit's bomb: a real vdata_c whose DATA is a KV3 v4 zstd block of zeros declaring more
    // than the limit, padded to pass the old 4096x ratio, which let it rent the whole declared size.
    private static byte[] ZstdBombBlock(long declared)
    {
        using var ms = new MemoryStream();
        using (var z = new ZstdSharp.CompressionStream(ms, 1, leaveOpen: true))
        {
            var zeros = new byte[1 << 20];
            for (long w = 0; w < declared; w += zeros.Length)
                z.Write(zeros, 0, (int)Math.Min(zeros.Length, declared - w));
        }
        var frame = ms.ToArray();
        using var o = new MemoryStream();
        using var bw = new BinaryWriter(o);
        bw.Write(0x4B563304); bw.Write(new byte[16]); bw.Write(2u); bw.Write((ushort)0); bw.Write((ushort)0);
        bw.Write((int)(declared - 64)); bw.Write(1); bw.Write(0); bw.Write(1); bw.Write((ushort)0); bw.Write((ushort)0);
        bw.Write((int)declared); bw.Write(frame.Length); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        bw.Write(frame);
        bw.Write(new byte[Math.Max(0, (int)(declared / 4096) - (int)o.Length)]);
        return o.ToArray();
    }

    private const string Kv3TextHeader = "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->\n";

    [Fact]
    public void Kv3ZstdBomb_IsRefusedFromItsHeader()
    {
        var block = ZstdBombBlock(600L << 20);
        var res = CompiledResource.Read(Kv3SourceCompiler.Compile(System.Text.Encoding.UTF8.GetBytes(Kv3TextHeader + "{ a = 1 }"), ".vdata", "x.vdata"));
        res.SetRaw("DATA", block);
        var upload = res.Write();

        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(ResourceDecompiler.DataBlockToKv3(CompiledResource.Read(upload), out var reason));
        var vrf = new ValveResourceFormat.ResourceTypes.BinaryKV3(ValveResourceFormat.BlockType.Undefined) { Resource = null! };
        using (var r = new BinaryReader(new MemoryStream(block)))
            Assert.Contains("-byte limit", Assert.Throws<InvalidDataException>(() => vrf.Read(r)).Message);
        Assert.Contains("-byte limit", reason);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.Elapsed}");
        var used = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.True(used < 32L << 20, $"allocated {used:N0} bytes");
    }

    // A valid file that compresses very well: the old ratio refused this compiler's own output.
    [Fact]
    public void Kv3_OutputThatCompressesWell_RoundTripsUnderARouteLimit()
    {
        var sb = new System.Text.StringBuilder(Kv3TextHeader + "{ a = [");
        for (var i = 0; i < 1_100_000; i++)
            sb.Append("{},");
        sb.Append("{}] }");
        var compiled = Kv3SourceCompiler.Compile(System.Text.Encoding.UTF8.GetBytes(sb.ToString()), ".vdata", "x.vdata");
        Assert.True(compiled.Length < 16 << 10, $"{compiled.Length} bytes");

        using var limit = VrfKv3ReadLimit.Use(64 << 20);
        var text = ResourceDecompiler.DataBlockToKv3(CompiledResource.Read(compiled), out var reason);
        Assert.True(text is not null, reason);
    }

    [Fact]
    public void Kv3V5_HugeDeclaredSize_IsRefusedBeforeAllocating()
    {
        var (block, _) = TypedNullArrayBlock();
        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(18 * 4), int.MaxValue);   // buffer 1, uncompressed
        Assert.Throws<InvalidDataException>(() => Kv3Tree.Read(block));
        Assert.Throws<InvalidDataException>(() => Kv3Binary.Read(block));
    }

    [Fact]
    public async Task Wav_ChunkSizeThatWouldWalkBackwards_EndsTheWalk()
    {
        var wav = new MemoryStream();
        wav.Write("RIFF"u8);
        wav.Write(BitConverter.GetBytes(100));
        wav.Write("WAVE"u8);
        wav.Write("junk"u8);
        wav.Write(BitConverter.GetBytes(0xFFFFFFF0u));
        wav.Write(new byte[16]);
        var bytes = wav.ToArray();
        // A walk that never ends would time out here rather than hang the run.
        var walk = Task.Run(() => ResourceBuilder.BuildSound(bytes));
        await Assert.ThrowsAsync<InvalidDataException>(() => walk.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void DirectoryContentSource_ReadsNothingOutsideItsRoot()
    {
        var root = Directory.CreateTempSubdirectory("s2c-content-");
        var outside = Path.Combine(Path.GetTempPath(), $"s2c-outside-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(outside, "secret");
            File.WriteAllText(Path.Combine(root.FullName, "inside.txt"), "ok");
            var content = new DirectoryContentSource(root.FullName);
            Assert.NotNull(content.Read("inside.txt"));
            Assert.Null(content.Read(outside));
            Assert.Null(content.Read(outside.Replace('\\', '/')));
            Assert.Null(content.Read("../" + Path.GetFileName(outside)));
        }
        finally
        {
            File.Delete(outside);
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void CloneKv_KeepsEvery64BitInteger()
    {
        var big = (1L << 60) + 1;
        Assert.Equal(big, (long)ResourceBuilder.CloneKv(new KVObject(big)));
        Assert.Equal(ulong.MaxValue - 1, (ulong)ResourceBuilder.CloneKv(new KVObject(ulong.MaxValue - 1)));
    }

    [Fact]
    public void ResourceFile_RefusesABlockTypeThatIsNotFourCharacters()
    {
        var file = new ResourceFile();
        file.Blocks.Add(new ResourceBlock("DATAX", [1, 2, 3]));
        Assert.Throws<InvalidOperationException>(() => file.Write());
    }

    [Fact]
    public void MeshoptVertexDecode_RefusesACountTheBufferCannotHold()
    {
        var buffer = MeshoptVertexCodec.Encode(new byte[16 * 4], 4, 16);
        Assert.Throws<InvalidDataException>(() => MeshoptVertexCodec.Decode(buffer, int.MaxValue, 16));
    }

    [Fact]
    public void ShaderExpression_ThatNeverReturns_IsRefused()
    {
        byte[] loop = [0x02, 0x00, 0x00];   // jump to 0
        Assert.Throws<InvalidDataException>(() => ShaderExpression.Evaluate(loop, _ => 0, _ => default));
        byte[] underflow = [0x13];   // add with an empty stack
        Assert.Throws<InvalidDataException>(() => ShaderExpression.Evaluate(underflow, _ => 0, _ => default));
    }

    [Fact]
    public void BuildTexture_RefusesASizeOverTheTextureLimit()
    {
        var def = new ResourceBuilder.TextureDef { RawRgba = new byte[40000 * 4], RawWidth = 40000, RawHeight = 1 };
        Assert.Throws<ArgumentException>(() => ResourceBuilder.BuildTexture(def));
    }
}
