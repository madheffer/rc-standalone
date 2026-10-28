using Source2.Compiler.Maps;
using Source2.Compiler.Maps.NavGen;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Nav links (CMapNavLink) on s2c_navlink (tools/coverage/nav_link_specimen.py: probe01 with two raised pads and
/// three links), against tools/re/capture_navgen.py's capture of Valve's compile (%TEMP%/navgen_capture/s2c_navlink_in)
/// and the package that compile left in the addon.
/// </summary>
public sealed class NavLinkTests(ITestOutputHelper output)
{
    private const string Map = "s2c_navlink";

    private static string Dir => Path.Combine(Path.GetTempPath(), "navgen_capture", Map + "_in");

    private static string Root => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(CS2Fixtures.StockPak()!)!, "..", ".."));

    private static string Vmap => Path.Combine(Root, "content", "csgo_addons", "s2c_rc_probe", "maps", Map + ".vmap");

    /// <summary>The collector's records (180f203b0, CNavGenInput +0xc0) bit for bit: count, then start and end per link.</summary>
    [Fact]
    public void LinksAreValvesRecords()
    {
        var path = Path.Combine(Dir, "navinput_links.bin");
        if (CS2Fixtures.StockPak() is null || !File.Exists(path) || !File.Exists(Vmap))
            return;
        var valve = File.ReadAllBytes(path);
        var links = NavGenMapInput.Links(DmxBinary.ReadFile(Vmap));
        Assert.Equal(BitConverter.ToInt32(valve, 0), links.Count);
        var ours = new List<byte>(BitConverter.GetBytes(links.Count));
        foreach (var (a, b) in links)
            foreach (var f in new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z })
                ours.AddRange(BitConverter.GetBytes(f));
        Assert.Equal(valve, ours.ToArray());
        output.WriteLine(string.Join("; ", links));
    }

    /// <summary>IslandRemoval (029) from Valve's 028 output with our links and seeds: pad A kept through link0, pad B dropped.</summary>
    [Fact]
    public void IslandRemovalKeepsTheLinkedPad()
    {
        if (CS2Fixtures.StockPak() is not { } pak || !File.Exists(Path.Combine(Dir, "029_IslandRemoval_out_conn.bin")) || !File.Exists(Vmap))
            return;
        var document = DmxBinary.ReadFile(Vmap);
        var settings = NavGenInputTests.MapSettings(document, pak);
        var g = Load("029_IslandRemoval_in");
        var valve = Load("029_IslandRemoval_out");
        var before = g.Polys.Count;
        NavGenStages.IslandStage(g, new NavIslandInput(NavGenMapInput.Seeds(document), NavGenMapInput.Links(document), []), settings);
        Assert.Null(NavGenInputTests.Compare(g, valve));
        for (var i = 0; i < g.Polys.Count; i++)
        {
            var diff = NavGenConnectionTests.Compare(g.Polys[i].Links, NavGenConnectionTests.LoadBlocks(Path.Combine(Dir, "029_IslandRemoval_out_conn.bin"))[i]);
            Assert.True(diff is null, $"polygon {i}: {diff}");
        }
        output.WriteLine($"029: {before} -> {g.Polys.Count} polygons, Valve's");
    }

    /// <summary>The links' entities in the lump (CMapNavLink's export, 181065c90) equal Valve's, and every entity keeps its compile_source_id.</summary>
    [Fact]
    public void LinkEntitiesAreValves()
    {
        var compiled = Path.Combine(Root, "game", "csgo_addons", "s2c_rc_probe", "maps", Map + ".vpk");
        if (CS2Fixtures.StockPak() is null || MapFixtures.GameSchema() is not { } schema || !File.Exists(compiled) || !File.Exists(Vmap))
            return;
        using var package = new ValvePak.Package();
        package.Read(compiled);
        const string lumpPath = "maps/" + Map + "/entities/default_ents.vents_c";
        var theirs = EntityLumpComparison.Read(Io.VpkEntries.Read(package, package.FindEntry(lumpPath)!), lumpPath);
        var document = DmxBinary.ReadFile(Vmap);
        var bytes = EntityLumpAuthor.Author(MapEntities.From(document), schema, worldName: Map, fixupEntityNames: MapEntities.FixupEntityNames(document));
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
        }
        Assert.Equal(3, links);
    }

    private static NavGenPolys Load(string stage)
    {
        var g = NavGenInputTests.LoadStage(Path.Combine(Dir, stage + ".bin"));
        var blocks = NavGenConnectionTests.LoadBlocks(Path.Combine(Dir, stage + "_conn.bin"));
        for (var i = 0; i < blocks.Count; i++)
            g.Polys[i].Links = NavGenConnectionTests.ToLinks(blocks[i]);
        return g;
    }
}
