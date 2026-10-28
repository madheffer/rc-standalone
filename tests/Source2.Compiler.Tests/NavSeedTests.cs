using Source2.Compiler.Maps;
using Source2.Compiler.Maps.NavGen;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The walkable seed collector (180f21760, CNavGenInput +0x78) against tools/re/capture_navgen.py's records of Valve's
/// compiles (%TEMP%/navgen_capture/&lt;map&gt;_in/navinput_seeds.bin: count, then each seed's three floats), in order and
/// bit for bit. probe_classes and s2c_prefabprobe4 to 6 hold several seeds, through instances and prefabs.
/// </summary>
public sealed class NavSeedTests(ITestOutputHelper output)
{
    private static string Dir(string map) => Path.Combine(Path.GetTempPath(), "navgen_capture", map + "_in");

    private static string Root => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(CS2Fixtures.StockPak()!)!, "..", ".."));

    private static string Vmap(string map) => Path.Combine(Root, "content", "csgo_addons", "s2c_rc_probe", "maps", map + ".vmap");

    [Theory]
    [InlineData("probe01")]
    [InlineData("cardtest")]
    [InlineData("probe_classes")]
    [InlineData("s2c_navangles")]
    [InlineData("s2c_navlink")]
    [InlineData("s2c_navlink2")]
    [InlineData("s2c_navlink3")]
    [InlineData("s2c_navlink4")]
    [InlineData("s2c_navmarkup")]
    [InlineData("s2c_navround")]
    [InlineData("s2c_pinst")]
    [InlineData("s2c_pinst7")]
    [InlineData("s2c_prefabprobe3")]
    [InlineData("s2c_prefabprobe4")]
    [InlineData("s2c_prefabprobe5")]
    [InlineData("s2c_prefabprobe6")]
    public void SeedsAreValvesRecords(string map)
    {
        var path = Path.Combine(Dir(map), "navinput_seeds.bin");
        if (CS2Fixtures.StockPak() is null || !File.Exists(path) || !File.Exists(Vmap(map)))
            return;
        var document = DmxBinary.ReadFile(Vmap(map));
        MapPrefabs.Attach(document, MapPrefabs.FromContent(Path.Combine(Root, "content", "csgo_addons", "s2c_rc_probe")));
        var seeds = NavGenMapInput.Seeds(document);
        var ours = new List<byte>(BitConverter.GetBytes(seeds.Count));
        foreach (var (x, y, z) in seeds)
            foreach (var f in new[] { x, y, z })
                ours.AddRange(BitConverter.GetBytes(f));
        var valve = File.ReadAllBytes(path);
        output.WriteLine($"Valve's: {Describe(valve)}");
        output.WriteLine($"ours:    {Describe([.. ours])}");
        Assert.Equal(valve, ours.ToArray());
    }

    private static string Describe(byte[] records)
        => string.Join("; ", Enumerable.Range(0, BitConverter.ToInt32(records, 0)).Select(i =>
               string.Join(" ", Enumerable.Range(0, 3).Select(k => BitConverter.ToSingle(records, 4 + 12 * i + 4 * k).ToString("R")))));
}
