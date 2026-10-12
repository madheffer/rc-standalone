using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="ResourceFile"/> reads and writes a compiled resource with no VRF. The stock sweep is
/// <c>s2c container-parity</c> (all 137,469 compiled files of pak01, 26 types, byte-identical,
/// 2026-10-12); these pin it on the committed RC outputs and on hostile headers.
/// </summary>
public class ResourceFileTests
{
    private static string RcReference(string name)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null && !Directory.Exists(Path.Combine(dir, "RcReference")); i++)
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "RcReference", name);
    }

    [Theory]
    [InlineData("rc_probe.vsndevts_c")]
    [InlineData("rc_probe.vdata_c")]
    [InlineData("rc_probe_ints.vdata_c")]
    [InlineData("rc_probe_refs.vpcf_c")]
    public void Reads_and_writes_RCs_output_byte_for_byte(string name)
    {
        var bytes = File.ReadAllBytes(RcReference(name));
        var file = ResourceFile.Read(bytes);
        Assert.Equal(bytes, file.Write());
        if (file["RERL"] is { } rerl)
            foreach (var (id, path) in ResourceRefs.Read(rerl.Data))
                Assert.Equal(Source2ResourceId.ForPath(path), id);
    }

    /// <summary>Every count and offset is checked against the file before anything is sized by it.</summary>
    [Fact]
    public void Hostile_headers_are_refused_before_anything_is_allocated()
    {
        var bytes = File.ReadAllBytes(RcReference("rc_probe_refs.vpcf_c"));
        byte[] With(int at, uint value)
        {
            var b = (byte[])bytes.Clone();
            BitConverter.GetBytes(value).CopyTo(b, at);
            return b;
        }
        Assert.Throws<InvalidDataException>(() => ResourceFile.Read(With(12, 0x7FFFFFFF)));          // block count
        Assert.Throws<InvalidDataException>(() => ResourceFile.Read(With(0, (uint)bytes.Length + 1))); // file size
        Assert.Throws<InvalidDataException>(() => ResourceFile.Read(With(16 + 8, 0x7FFFFFF0)));        // first block's size
        Assert.Throws<InvalidDataException>(() => ResourceFile.Read(bytes.AsSpan(0, 10)));
        var rerl = ResourceFile.Read(bytes)["RERL"]!.Data;
        var huge = (byte[])rerl.Clone();
        BitConverter.GetBytes(int.MaxValue).CopyTo(huge, 4);
        Assert.Throws<InvalidDataException>(() => ResourceRefs.Read(huge));
    }

    /// <summary>A texture's mips follow fileSize; they come back where they were.</summary>
    [Fact]
    public void The_tail_after_the_blocks_is_kept()
    {
        var texture = ResourceBuilder.BuildTexture(new ResourceBuilder.TextureDef { RawRgba = new byte[16 * 16 * 4], RawWidth = 16, RawHeight = 16 });
        var file = ResourceFile.Read(texture);
        Assert.NotEmpty(file.Tail);
        Assert.Equal(texture, file.Write());
    }
}
