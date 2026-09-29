using Source2.Compiler.Meshopt;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The meshopt encoders against Valve's world node models: every MVTX and
/// MIDX block, decoded, encodes back to the same bytes.
/// </summary>
public class MeshoptEncoderTests(ITestOutputHelper output)
{
    private static string Package(string map) => Path.Combine(
        @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo_addons\s2c_rc_probe\maps", map + ".vpk");

    /// <summary>The resource's blocks of one type, raw, in file order.</summary>
    internal static List<byte[]> RawBlocks(byte[] file, string type)
    {
        var blockOffset = BitConverter.ToUInt32(file, 8);
        var count = BitConverter.ToUInt32(file, 12);
        var at = 8 + (int)blockOffset;
        var list = new List<byte[]>();
        for (var i = 0; i < count; i++, at += 12)
        {
            var t = System.Text.Encoding.ASCII.GetString(file, at, 4);
            var offset = BitConverter.ToUInt32(file, at + 4);
            var size = BitConverter.ToUInt32(file, at + 8);
            if (t == type)
                list.Add(file.AsSpan(at + 4 + (int)offset, (int)size).ToArray());
        }
        return list;
    }

    [Theory]
    [InlineData("probe01")]
    [InlineData("cardtest")]
    [InlineData("atixref")]
    public void EveryNodeBufferRoundTrips(string map)
    {
        if (!File.Exists(Package(map)))
            return;
        using var package = new Package();
        package.Read(Package(map));
        int vertexOk = 0, indexOk = 0, other = 0;
        var failures = new List<string>();
        foreach (var entry in package.Entries.GetValueOrDefault("vmdl_c") ?? [])
        {
            var path = entry.GetFullPath();
            if (!path.Contains("/worldnodes/", StringComparison.Ordinal))
                continue;
            package.ReadEntry(entry, out var bytes);
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            var model = (Model)resource.DataBlock!;
            var mesh = model.GetEmbeddedMeshesAndLoD().First().Mesh;
            var vertexBlocks = RawBlocks(bytes, "MVTX");
            var indexBlocks = RawBlocks(bytes, "MIDX");
            for (var b = 0; b < vertexBlocks.Count; b++)
            {
                var raw = vertexBlocks[b];
                if (raw[0] >> 4 != 0xa)
                {
                    other++;
                    continue;
                }
                var vb = mesh.VBIB.VertexBuffers[b];
                var ours = MeshoptEncoder.EncodeVertexBuffer(vb.Data, (int)vb.ElementCount, (int)vb.ElementSizeInBytes);
                if (ours.AsSpan().SequenceEqual(raw))
                    vertexOk++;
                else
                    failures.Add($"{path} MVTX {b}");
            }
            for (var b = 0; b < indexBlocks.Count; b++)
            {
                var raw = indexBlocks[b];
                var ib = mesh.VBIB.IndexBuffers[b];
                var indices = new uint[ib.ElementCount];
                for (var i = 0; i < indices.Length; i++)
                    indices[i] = ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, i * 2) : BitConverter.ToUInt32(ib.Data, i * 4);
                byte[] ours;
                if (raw[0] >> 4 == 0xe)
                    ours = MeshoptEncoder.EncodeIndexBuffer(indices);
                else if (raw[0] >> 4 == 0xd)
                    ours = MeshoptEncoder.EncodeIndexSequence(indices);
                else
                {
                    other++;
                    continue;
                }
                if (ours.AsSpan().SequenceEqual(raw))
                    indexOk++;
                else
                    failures.Add($"{path} MIDX {b} header {raw[0]:x2}");
            }
        }
        output.WriteLine($"{map}: {vertexOk} vertex and {indexOk} index buffers exact, {other} not meshopt, {failures.Count} differ");
        foreach (var f in failures.Take(20))
            output.WriteLine(f);
        Assert.Empty(failures);
    }
}
