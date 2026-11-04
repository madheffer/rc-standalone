using Source2.Compiler.Compression;
using Source2.Compiler.Maps;
using ValvePak;
using ValveResourceFormat;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// The particle snapshot a map compile generates (OPEN 5.b10): probe_classes' info_particle_system
/// names node 3, a four-face open box, and Valve's package holds
/// maps/probe_classes/particle_snapshots/node_3.vsnap_c. Ours is compared byte for byte, and the
/// .vsnap source text's CRC32 against the one Valve's RED2 records.
/// </summary>
public class ParticleSnapshotTests
{
    private const string ValvePath = "maps/probe_classes/particle_snapshots/node_3.vsnap_c";

    private static byte[]? ValveSnapshot()
    {
        var cs2 = CS2Fixtures.StockPak();
        if (cs2 is null)
            return null;
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cs2)!, "..", ".."));
        var package = Path.Combine(root, "game", "csgo_addons", "s2c_rc_probe", "maps", "probe_classes.vpk");
        if (!File.Exists(package))
            return null;
        using var pkg = new Package();
        pkg.Read(package);
        return pkg.FindEntry(ValvePath) is { } entry ? Io.VpkEntries.Read(pkg, entry) : null;
    }

    [Fact]
    public void ProbeClasses_Node3_IsValves()
    {
        var source = MapFixtures.VmapSource("s2c_rc_probe", "probe_classes");
        if (source is null) { MapFixtures.Skip("the s2c_rc_probe/probe_classes source"); return; }
        var valve = ValveSnapshot();
        if (valve is null) { MapFixtures.Skip("Valve's probe_classes package with node_3.vsnap_c"); return; }

        var document = DmxBinary.ReadFile(source);
        var files = ParticleSnapshots.Build(document, MapEntities.From(document), MapFixtures.GameSchema(), "s2c_rc_probe", "probe_classes");
        var file = Assert.Single(files);
        Assert.Equal("maps/probe_classes/particle_snapshots/node_3.vsnap", file.SourcePath);
        Assert.Equal(ValvePath, file.CompiledPath);
        // Valve's RED2: m_AdditionalInputDependencies[0].m_nFileCRC of the saved .vsnap.
        Assert.Equal(2605952937u, System.IO.Hashing.Crc32.HashToUInt32(System.Text.Encoding.UTF8.GetBytes(file.Source)));
        Assert.Equal(valve, file.Compiled);
    }

    /// <summary>
    /// s2c_snapprobe (tools/coverage/snapshot_specimen.py), Valve's compile kept at
    /// %TEMP%/fresh_compile/s2c_snapprobe.vpk (compile_fresh.py, rc 1.41.9.5): two CMapPaths (node colours
    /// and radius scales, two spacings), a CMapCable, a closed box mesh and the same box scaled. Valve
    /// writes those five and none for the world and the func_buyzone entity it also names.
    /// </summary>
    [Fact]
    public void SnapProbe_EveryFile_IsValves()
    {
        var source = MapFixtures.VmapSource("s2c_rc_probe", "s2c_snapprobe");
        if (source is null) { MapFixtures.Skip("the s2c_rc_probe/s2c_snapprobe source"); return; }
        var package = Path.Combine(Path.GetTempPath(), "fresh_compile", "s2c_snapprobe.vpk");
        if (!File.Exists(package)) { MapFixtures.Skip("Valve's s2c_snapprobe compile in %TEMP%/fresh_compile"); return; }
        using var pkg = new Package();
        pkg.Read(package);
        var valve = (pkg.Entries.GetValueOrDefault("vsnap_c") ?? []).ToDictionary(e => e.GetFullPath(), e => Io.VpkEntries.Read(pkg, e));

        var document = DmxBinary.ReadFile(source);
        var ours = ParticleSnapshots.Build(document, MapEntities.From(document), MapFixtures.GameSchema(), "s2c_rc_probe", "s2c_snapprobe");
        Assert.Equal(valve.Keys.Order(StringComparer.Ordinal), ours.Select(f => f.CompiledPath).Order(StringComparer.Ordinal));
        if (Environment.GetEnvironmentVariable("SNAPPROBE_DUMP") is { Length: > 0 } dump)
            foreach (var file in ours)
            {
                Directory.CreateDirectory(dump);
                File.WriteAllBytes(Path.Combine(dump, Path.GetFileName(file.CompiledPath)), file.Compiled);
                File.WriteAllText(Path.Combine(dump, Path.GetFileName(file.SourcePath)), file.Source);
            }
        var differ = ours.Where(f => !valve[f.CompiledPath].AsSpan().SequenceEqual(f.Compiled)).Select(f => f.CompiledPath).ToList();
        Assert.True(differ.Count == 0, "differ: " + string.Join(", ", differ));
    }

    [Fact]
    public void FastCompress_RoundTripsValvesSnapBlock()
    {
        var valve = ValveSnapshot();
        if (valve is null) { MapFixtures.Skip("Valve's probe_classes package with node_3.vsnap_c"); return; }
        using var resource = new Resource();
        resource.Read(new MemoryStream(valve));
        var block = resource.Blocks.Single(b => b.Type == BlockType.SNAP);
        var bytes = valve.AsSpan((int)block.Offset, (int)block.Size).ToArray();
        var plain = BlockCompress.FastDecompress(bytes);
        Assert.Equal(16 * 12 * 2, plain.Length);
        Assert.Equal(bytes, BlockCompress.FastCompress(plain));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(4096)]
    [InlineData(70000)]
    public void FastCompress_RoundTrips(int length)
    {
        var random = new Random(length);
        var input = new byte[length];
        for (var i = 0; i < length; i++)
            input[i] = (byte)(i % 7 == 0 ? random.Next(256) : i / 13);
        Assert.Equal(input, BlockCompress.FastDecompress(BlockCompress.FastCompress(input)));
    }
}
