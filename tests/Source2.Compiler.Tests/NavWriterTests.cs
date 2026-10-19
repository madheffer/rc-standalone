using Source2.Compiler.Maps.NavGen;
using ValveResourceFormat.NavMesh;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>The .nav writer (SaveNavMeshToBuffer, rc 1818d1320) against Valve's files.</summary>
public class NavWriterTests(ITestOutputHelper output)
{
    private const string Content = @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\content\csgo_addons\s2c_rc_probe\maps\";
    private const string AddonMaps = @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo_addons\s2c_rc_probe\maps\";

    /// <summary>Valve's polygons after IslandRemoval (029) with their connection blocks and +0x62 bytes.</summary>
    internal static NavGenPolys Stage029(string dir)
    {
        var path = Path.Combine(dir, "029_IslandRemoval_out.bin");
        var g = NavGenInputTests.LoadStage(path);
        var raw = File.ReadAllBytes(path);
        for (var i = 0; i < g.Polys.Count; i++)
            g.Polys[i].Field62 = raw[8 + i * 0x88 + 0x62];
        var blocks = NavGenConnectionTests.LoadBlocks(Path.Combine(dir, "029_IslandRemoval_out_conn.bin"));
        for (var i = 0; i < blocks.Count; i++)
            g.Polys[i].Links = NavGenConnectionTests.ToLinks(blocks[i]);
        return g;
    }

    private static byte[] ValveNav(string map)
    {
        using var package = new ValvePak.Package();
        package.Read(Path.Combine(AddonMaps, map + ".vpk"));
        package.ReadEntry(package.FindEntry($"maps/{map}.nav")!, out var bytes);
        return bytes;
    }

    // navsystem's movable mesh list as captured at 180022200 (navinput_movable.bin: count, 0x70-byte entries).
    private static IEnumerable<NavWriter.MovableMesh> Movable(string dir)
    {
        var path = Path.Combine(dir, "navinput_movable.bin");
        if (!File.Exists(path))
            yield break;
        var b = File.ReadAllBytes(path);
        for (var i = 0; i < BitConverter.ToInt32(b, 0); i++)
            yield return NavWriter.MovableMesh.FromNavGenEntry(b.AsSpan(4 + i * 0x70, 0x70));
    }

    // The custom data block's offset: the last KV3 (magic 05 33 56 4b) in the file.
    private static int CustomDataAt(byte[] nav)
    {
        var at = -1;
        for (var i = 0; i + 4 <= nav.Length; i += 8)
            if (nav[i] == 0x05 && nav[i + 1] == 0x33 && nav[i + 2] == 0x56 && nav[i + 3] == 0x4b)
                at = i;
        return at;
    }

    /// <summary>
    /// Valve's stage 029 output through the mesh build and the save: every byte before the CS custom
    /// data (whose spot encounter ids are heap addresses) is Valve's, on probe01 and cardtest.
    /// </summary>
    [Theory]
    [InlineData("probe01")]
    [InlineData("cardtest")]
    [InlineData("probe_classes")]
    [InlineData("s2c_prefabprobe4")]
    [InlineData("s2c_prefabprobe5")]
    [InlineData("s2c_prefabprobe6")]
    public void FileBeforeCustomDataMatchesValve(string map)
    {
        var dir = Path.Combine(Path.GetTempPath(), "navgen_capture", map + "_in");
        if (!File.Exists(Path.Combine(dir, "029_IslandRemoval_out_conn.bin")) || CS2Fixtures.StockPak() is not { } pak)
            return;
        // Ladders (the mesh build's 10-unit casts, 1818dc620) are not ported.
        var ladders = Path.Combine(dir, "navinput_ladders.bin");
        if (File.Exists(ladders) && BitConverter.ToInt32(File.ReadAllBytes(ladders), 0) > 0)
        {
            output.WriteLine($"{map}: has ladders, not ported");
            return;
        }
        var valve = ValveNav(map);
        var settings = NavGenInputTests.MapSettings(DmxBinary.ReadFile(Path.Combine(Content, map + ".vmap")), pak);
        var mesh = NavWriter.Build(Stage029(dir));
        mesh.Movable.AddRange(Movable(dir));
        var ours = NavWriter.Write(mesh, settings, ValveKeyValue.KVObject.Null());
        if (Environment.GetEnvironmentVariable("NAVWRITER_OUT") is { Length: > 0 } outDir)
            File.WriteAllBytes(Path.Combine(outDir, map + ".ours.nav"), ours);
        var end = CustomDataAt(valve);
        Assert.Equal(end, CustomDataAt(ours));
        var first = Enumerable.Range(0, end).FirstOrDefault(i => ours[i] != valve[i], -1);
        Assert.True(first < 0, $"{map}: first difference at byte {first} of {end}");
        output.WriteLine($"{map}: {mesh.Areas.Count} areas, {mesh.Corners.Count} corners; bytes 0..{end} of {valve.Length} are Valve's");
    }

    /// <summary>
    /// Exploration (<c>NAVKV3=&lt;.nav&gt;</c>): the file's KV3 blocks as text, read with VRF.
    /// </summary>
    [Fact]
    public void DumpBlocks()
    {
        if (Environment.GetEnvironmentVariable("NAVKV3") is not { Length: > 0 } path)
            return;
        var nav = new NavMeshFile();
        nav.Read(path);
        void Dump(string name, ValveKeyValue.KVDocument? kv)
        {
            output.WriteLine($"== {name}");
            if (kv is null)
                return;
            var m = new MemoryStream();
            ValveKeyValue.KVSerializer.Create(ValveKeyValue.KVSerializationFormat.KeyValues3Text).Serialize(m, kv);
            output.WriteLine(System.Text.Encoding.UTF8.GetString(m.ToArray()));
        }
        Dump("KV3Unknown1", nav.KV3Unknown1);
        Dump("KV3Unknown2", nav.KV3Unknown2);
        Dump("KV3Unknown3", nav.KV3Unknown3);
        Dump("CustomData", nav.CustomData);
    }
}
