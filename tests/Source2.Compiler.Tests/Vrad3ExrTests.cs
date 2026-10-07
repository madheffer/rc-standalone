using Source2.Compiler.Lighting;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// vrad3's EXR layout: every EXR in a map's _vrad3 folder reads and writes back
/// byte for byte (header, offset table and lines).
/// </summary>
public class Vrad3ExrTests
{
    private static string? Vrad3Folder() => CS2Fixtures.StockPak() is { } pak
        ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, "..", "csgo_addons", "s2c_rc_probe", "_vrad3"))
        : null;

    [Fact]
    public void PackingGeometryRoundTrips()
    {
        var file = Vrad3Folder() is { } dir ? Path.Combine(dir, "lightmap_packing_geometry.dat") : null;
        if (file is null || !File.Exists(file))
        {
            MapFixtures.Skip("s2c_rc_probe's lightmap_packing_geometry.dat");
            return;
        }
        var bytes = File.ReadAllBytes(file);
        var geometry = PackingGeometry.Read(bytes);
        Assert.Equal(40, geometry.VertexStride);
        Assert.Equal(new uint[] { 0, 0, 12, 24, 0, 0 }, geometry.Offsets);
        Assert.True(bytes.AsSpan().SequenceEqual(geometry.Write()));
    }

    [Fact]
    public void EveryVrad3OutputRoundTrips()
    {
        var dir = Vrad3Folder();
        if (dir is null || !Directory.Exists(dir))
        {
            MapFixtures.Skip("s2c_rc_probe's _vrad3 folder");
            return;
        }
        var files = Directory.GetFiles(dir, "*.exr").OrderBy(f => new FileInfo(f).Length).Take(6).ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            var exr = Vrad3Exr.Read(bytes);
            Assert.True(bytes.AsSpan().SequenceEqual(exr.Write()), $"{Path.GetFileName(file)} does not write back as it was");
        }
    }
}
