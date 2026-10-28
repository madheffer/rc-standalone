using Source2.Compiler.Maps;
using Source2.Compiler.Maps.NavGen;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Nav links (CMapNavLink) on s2c_navlink (tools/coverage/nav_link_specimen.py: probe01 with two raised pads and
/// three links) and s2c_navlink2 (the same with --keys: every other CMapNavLink attribute and three more links),
/// against tools/re/capture_navgen.py's captures of Valve's compiles (%TEMP%/navgen_capture/&lt;map&gt;_in) and the
/// packages those compiles left in the addon.
/// </summary>
public sealed class NavLinkTests(ITestOutputHelper output)
{
    private static string Dir(string map) => Path.Combine(Path.GetTempPath(), "navgen_capture", map + "_in");

    private static string Root => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(CS2Fixtures.StockPak()!)!, "..", ".."));

    private static string Vmap(string map) => Path.Combine(Root, "content", "csgo_addons", "s2c_rc_probe", "maps", map + ".vmap");

    // The .vmap with its prefabs' maps attached, as compile-map loads it (MapSource).
    private static DmxBinary.Document Load(string map)
    {
        var document = DmxBinary.ReadFile(Vmap(map));
        MapPrefabs.Attach(document, MapPrefabs.FromContent(Path.Combine(Root, "content", "csgo_addons", "s2c_rc_probe")));
        return document;
    }

    /// <summary>The collector's records (180f203b0, CNavGenInput +0xc0) bit for bit: count, then start and end per link.</summary>
    [Theory]
    [InlineData("s2c_navlink")]
    [InlineData("s2c_navlink2")]
    [InlineData("s2c_navlink4")]
    public void LinksAreValvesRecords(string map)
    {
        var path = Path.Combine(Dir(map), "navinput_links.bin");
        if (CS2Fixtures.StockPak() is null || !File.Exists(path) || !File.Exists(Vmap(map)))
            return;
        var valve = File.ReadAllBytes(path);
        var links = NavGenMapInput.Links(Load(map));
        Assert.Equal(BitConverter.ToInt32(valve, 0), links.Count);
        var ours = new List<byte>(BitConverter.GetBytes(links.Count));
        foreach (var (a, b) in links)
            foreach (var f in new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z })
                ours.AddRange(BitConverter.GetBytes(f));
        for (var i = 0; i < links.Count; i++)
        {
            var theirs = Enumerable.Range(0, 6).Select(k => BitConverter.ToSingle(valve, 4 + 24 * i + 4 * k)).ToArray();
            var mine = Enumerable.Range(0, 6).Select(k => BitConverter.ToSingle(ours.ToArray(), 4 + 24 * i + 4 * k)).ToArray();
            if (!theirs.SequenceEqual(mine))
                output.WriteLine($"record {i}: Valve's {string.Join(" ", theirs.Select(f => f.ToString("R")))}, ours {string.Join(" ", mine.Select(f => f.ToString("R")))}");
        }
        Assert.Equal(valve, ours.ToArray());
        output.WriteLine(string.Join("; ", links));
    }

    /// <summary>IslandRemoval (029) from Valve's 028 output with our links and seeds: pad A kept through link0, pad B dropped.</summary>
    [Theory]
    [InlineData("s2c_navlink")]
    [InlineData("s2c_navlink2")]
    [InlineData("s2c_navlink4")]
    public void IslandRemovalKeepsTheLinkedPad(string map)
    {
        if (CS2Fixtures.StockPak() is not { } pak || !File.Exists(Path.Combine(Dir(map), "029_IslandRemoval_out_conn.bin")) || !File.Exists(Vmap(map)))
            return;
        var document = Load(map);
        var settings = NavGenInputTests.MapSettings(document, pak);
        var g = Load(map, "029_IslandRemoval_in");
        var valve = Load(map, "029_IslandRemoval_out");
        var before = g.Polys.Count;
        NavGenStages.IslandStage(g, new NavIslandInput(NavGenMapInput.Seeds(document), NavGenMapInput.Links(document), []), settings);
        Assert.Null(NavGenInputTests.Compare(g, valve));
        for (var i = 0; i < g.Polys.Count; i++)
        {
            var diff = NavGenConnectionTests.Compare(g.Polys[i].Links, NavGenConnectionTests.LoadBlocks(Path.Combine(Dir(map), "029_IslandRemoval_out_conn.bin"))[i]);
            Assert.True(diff is null, $"polygon {i}: {diff}");
        }
        output.WriteLine($"029: {before} -> {g.Polys.Count} polygons, Valve's");
    }

    /// <summary>
    /// The links' entities in the lump (CMapNavLink's export, 181065c90) equal Valve's, and every entity keeps its
    /// compile_source_id. s2c_navlink2 sets each attribute: navlink_type by name, in either case and by display name
    /// (which parses as GENERIC), width, locatorangles and the movement strings for each type branch. Authored without
    /// the instance bake: s2c_navlink4's copies are checked whole by EntityLumpAgainstValveTests.
    /// </summary>
    [Theory]
    [InlineData("s2c_navlink")]
    [InlineData("s2c_navlink2")]
    public void LinkEntitiesAreValves(string map)
    {
        var compiled = Path.Combine(Root, "game", "csgo_addons", "s2c_rc_probe", "maps", map + ".vpk");
        if (CS2Fixtures.StockPak() is null || MapFixtures.GameSchema() is not { } schema || !File.Exists(compiled) || !File.Exists(Vmap(map)))
            return;
        using var package = new ValvePak.Package();
        package.Read(compiled);
        var lumpPath = "maps/" + map + "/entities/default_ents.vents_c";
        var theirs = EntityLumpComparison.Read(Io.VpkEntries.Read(package, package.FindEntry(lumpPath)!), lumpPath);
        var document = Load(map);
        var bytes = EntityLumpAuthor.Author(MapEntities.From(document), schema, worldName: map, fixupEntityNames: MapEntities.FixupEntityNames(document));
        var ours = EntityLumpComparison.Read(bytes, lumpPath);
        Assert.Equal(theirs.Count, ours.Count);
        var links = 0;
        for (var i = 0; i < theirs.Count; i++)
        {
            Assert.Equal(theirs[i].Values.GetValueOrDefault("compile_source_id"), ours[i].Values.GetValueOrDefault("compile_source_id"));
            if (theirs[i].ClassName != "ai_nav_link_area")
                continue;
            links++;
            Assert.Equal(theirs[i].KeyOrder, ours[i].KeyOrder);
            foreach (var key in theirs[i].KeyOrder)
                Assert.Equal(theirs[i].Values[key], ours[i].Values[key]);
            output.WriteLine(string.Join(", ", theirs[i].KeyOrder.Where(k => k is "targetname" or "width" or "locatorangles" || k.StartsWith("nav_link")).Select(k => $"{k}={theirs[i].Values[k]}")));
        }
        Assert.Equal(NavGenMapInput.Links(document).Count, links);
        Assert.True(links >= 3);
    }

    /// <summary>s2c_navlink3 also places links through a prefab, whose placement is not ported: refused.</summary>
    [Fact]
    public void PrefabLinksRefuse()
    {
        if (CS2Fixtures.StockPak() is null || !File.Exists(Vmap("s2c_navlink3")))
            return;
        Assert.Throws<NotSupportedException>(() => NavGenMapInput.Links(Load("s2c_navlink3")));
    }

    private static NavGenPolys Load(string map, string stage)
    {
        var g = NavGenInputTests.LoadStage(Path.Combine(Dir(map), stage + ".bin"));
        var blocks = NavGenConnectionTests.LoadBlocks(Path.Combine(Dir(map), stage + "_conn.bin"));
        for (var i = 0; i < blocks.Count; i++)
            g.Polys[i].Links = NavGenConnectionTests.ToLinks(blocks[i]);
        return g;
    }
}
