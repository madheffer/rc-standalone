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
    public void Kv3Limits_AnUnboundedBlock_DoesNotWrapTheExpansionBound()
    {
        Assert.True(Kv3Limits.Expandable(long.MaxValue) > long.MaxValue / 2);
        Kv3Limits.CheckSizes(3 << 20, 1000, long.MaxValue, compressed: true, 3 << 20, 1000);
        Assert.Equal(4096L * 10 + (1 << 20), Kv3Limits.Expandable(10));
        Assert.Throws<InvalidDataException>(() => Kv3Limits.CheckSizes(3 << 20, 1000, 10, compressed: true));
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
