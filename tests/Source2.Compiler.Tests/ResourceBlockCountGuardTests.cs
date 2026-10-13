using System.Buffers.Binary;
using ValveResourceFormat;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// Pins <c>vrf-resource-block-count-guard</c>. <c>Resource.Read</c> sized a List from the
/// header's block count before validating it, so 16 bytes declaring a valid header version
/// and ~2^31 blocks reserved about 16 GB. The compiler reads files strangers upload.
/// </summary>
public class ResourceBlockCountGuardTests
{
    /// <summary>A resource header: fileSize, headerVersion 12, version 0, then the block table
    /// offset (relative to its own field) and count, followed by <paramref name="tableBytes"/>.</summary>
    private static byte[] Header(uint blockCount, byte[] tableBytes)
    {
        var data = new byte[16 + tableBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0), (uint)data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), blockCount);
        tableBytes.CopyTo(data, 16);
        return data;
    }

    [Fact]
    public void A_block_count_the_file_cannot_hold_throws_without_allocating_for_it()
    {
        var crafted = Header(int.MaxValue, []);
        var before = GC.GetAllocatedBytesForCurrentThread();

        using var res = new Resource();
        Assert.Throws<InvalidDataException>(() => res.Read(new MemoryStream(crafted)));

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1024 * 1024, $"reading a 16-byte header allocated {allocated:N0} bytes");
    }

    [Fact]
    public void A_block_count_that_exactly_fits_still_reads()
    {
        // One 12-byte entry: a DATA block of size 0. It reads; whether VRF keeps the empty
        // block depends on the VRF pin, so only the count is pinned.
        var entry = new byte[12];
        "DATA"u8.CopyTo(entry);
        using var res = new Resource();
        res.Read(new MemoryStream(Header(1, entry)));
        Assert.True(res.Blocks.Count <= 1);
    }

    [Fact]
    public void One_entry_more_than_the_table_holds_throws()
    {
        using var res = new Resource();
        Assert.Throws<InvalidDataException>(() => res.Read(new MemoryStream(Header(2, new byte[12]))));
    }
}
