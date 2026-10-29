using System.Buffers.Binary;
using Source2.Compiler.Maps;
using Source2.Compiler.Maps.NavGen;
using ValveKeyValue;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// compile-map's nav and bomb damage (<see cref="NavCompile"/>) from the .vmap alone, against Valve's
/// full compile in %TEMP%/vrad3_refs/&lt;map&gt;.vpk: the .nav (the custom data's heap pointer fields
/// masked) and baked_bomb_damage.vdata_c sampled from our own nav.
/// </summary>
public sealed class NavCompileTests(ITestOutputHelper output)
{
    private static string Csgo => Path.GetDirectoryName(CS2Fixtures.StockPak()!)!;

    private static byte[] Entry(ValvePak.Package package, string path)
    {
        package.ReadEntry(package.FindEntry(path)!, out var bytes);
        return bytes;
    }

    /// <summary>The custom data as path = value lines; spotencounterdata from / to and order ids (heap pointers) masked.</summary>
    private static List<string> Flatten(KVObject node, string path, List<string> lines)
    {
        if (node.IsArray)
        {
            var i = 0;
            foreach (var v in node.Values)
                Flatten(v, $"{path}[{i++}]", lines);
            lines.Add($"{path} count {i}");
        }
        else if (node.ValueType == KVValueType.Collection)
        {
            foreach (var c in node.Children)
            {
                var masked = (path.Contains(".spotencounterdata[") && c.Key is "from" or "to") || (path.Contains(".order[") && c.Key == "id");
                // A pointer's KV3 type follows its value (0 and 1 take the int64 zero / one types), so it is masked too.
                lines.Add(masked ? $"{path}.{c.Key} : pointer" : $"{path}.{c.Key} : {c.Value.ValueType}");
                if (!masked)
                    Flatten(c.Value, $"{path}.{c.Key}", lines);
            }
        }
        else
            lines.Add($"{path} = {node.ValueType} {node}");
        return lines;
    }

    /// <summary>tools/re/capture_navanalysis.py s2c_rc_probe cardtest --out %TEMP%/navgen_capture/cardtest_analysis.</summary>
    private static string CardtestCapture => Environment.GetEnvironmentVariable("NAVAN_CARDTEST") ?? Path.Combine(Path.GetTempPath(), "navgen_capture", "cardtest_analysis");

    private static float F(byte[] b, int p) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(p));

    /// <summary>Each hull sphere cast (kind 6, type 2: 180250b20) of cardtest's analysis, replayed through <see cref="Simulation.HullCast.CastSphere"/>.</summary>
    [Fact]
    public void HullSphereCastsMatchValve()
    {
        var dir = CardtestCapture;
        if (!File.Exists(Path.Combine(dir, "navan_calls.bin")) || !File.Exists(Path.Combine(dir, "navan_shapedata.bin")))
            return;
        var (_, hulls) = NavCastTests.Shapes(dir, "navan_shapedata.bin");
        var b = File.ReadAllBytes(Path.Combine(dir, "navan_calls.bin"));
        int total = 0, same = 0, shown = 0;
        System.Numerics.Vector3 V3(int o) => new(F(b, o), F(b, o + 4), F(b, o + 8));
        for (var p = 0; p < b.Length; p += NavAnalysisTests.RecordSize(b, p))
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p)) != 6 || BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 4)) != 2)
                continue;
            var h = hulls[BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p + 8))];
            Assert.Equal(1f, h.Scale);
            var start = V3(p + 16);
            var delta = V3(p + 28);
            float radius = F(b, p + 40), max = F(b, p + 44);
            var returned = b[p + 48] != 0;
            var want = F(b, p + 52);
            var got = Simulation.HullCast.CastSphere(h.Points, start, delta, radius, max);
            total++;
            if (got.Found == returned && (!returned || BitConverter.SingleToInt32Bits(got.Fraction) == BitConverter.SingleToInt32Bits(want)))
                same++;
            else if (shown++ < 10)
                output.WriteLine($"cast {total}: {start:R} + {delta:R} r {radius} max {max:R}: ours {got.Found} {got.Fraction:R}, Valve {returned} {want:R}");
        }
        output.WriteLine($"{same} of {total} hull sphere casts Valve's");
        Assert.Equal(total, same);
    }

    /// <summary>
    /// Each world sphere cast (kind 5) of cardtest's analysis replayed through <see cref="NavSphereWorld"/> over our
    /// settle bodies in the nav world (<see cref="NavGenInput.NavWorldFilter"/>): the casts up to the last area's
    /// approach step. Three later ones, one ground search at (-0.25, 0.25, 44) after the analysis, meet the
    /// Solidity 1 func_brush the nav world drops; nothing of them reaches the .nav.
    /// </summary>
    [Fact]
    public void CardtestWorldSphereCastsMatchValve()
    {
        var dir = CardtestCapture;
        if (!File.Exists(Path.Combine(dir, "navan_calls.bin")) || CS2Fixtures.StockPak() is not { } pak || MapFixtures.GameSchema() is not { } schema)
            return;
        var root = Path.GetFullPath(Path.Combine(Csgo, "..", ".."));
        var document = DmxBinary.ReadFile(Path.Combine(root, "content", "csgo_addons", "s2c_rc_probe", "maps", "cardtest.vmap"));
        MapPrefabs.Attach(document, MapPrefabs.FromContent(Path.Combine(root, "content", "csgo_addons", "s2c_rc_probe")));
        MapDeformers.Apply(document, MapFixtures.GameSchema());
        using var assets = new GameContent(pak, Path.Combine(root, "game", "csgo_addons", "s2c_rc_probe"));
        var locators = MapCompile.Locators(assets);
        var bodies = SettleWorld.Build(document, assets, schema, SmartProps.NodesCreatedOnLoad(document, locators),
            d => SmartProps.NodesCreatedOnLoad(d, locators), new SettleWorld.CollisionRules());
        var world = new NavSphereWorld(bodies, NavGenInput.NavWorldFilter(document, schema));
        var b = File.ReadAllBytes(Path.Combine(dir, "navan_calls.bin"));
        int total = 0, same = 0, shown = 0;
        System.Numerics.Vector3 V3(int o) => new(F(b, o), F(b, o + 4), F(b, o + 8));
        var lastApproach = 0;
        for (var p = 0; p < b.Length; p += NavAnalysisTests.RecordSize(b, p))
            if (BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p)) == 9)
                lastApproach = p;
        for (var p = 0; p < lastApproach; p += NavAnalysisTests.RecordSize(b, p))
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p)) != 5)
                continue;
            var radius = F(b, p + 8 + 0xc);
            var start = V3(p + 8 + 0x30);
            var delta = V3(p + 8 + 0x30 + 12);
            var r = p + 8 + 0x30 + 24 + 0x38;
            var wantHits = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(r));
            var want = F(b, r + 4);
            var (hits, got) = world.CastSphere(start, delta, radius);
            total++;
            if (hits == wantHits && BitConverter.SingleToInt32Bits(got) == BitConverter.SingleToInt32Bits(want))
                same++;
            else if (shown++ < 10)
                output.WriteLine($"world cast {total}: {start:R} + {delta:R}: ours {hits} {got:R}, Valve {wantHits} {want:R}");
        }
        output.WriteLine($"{same} of {total} world sphere casts Valve's");
        Assert.Equal(total, same);
    }

    /// <summary>
    /// compile-map -nav into a copy of Valve's package with its nav and bomb damage removed: the step
    /// generates the nav and the post step bakes the bomb damage from it, both Valve's (the nav's pointer
    /// fields masked).
    /// </summary>
    [Theory]
    [InlineData("probe01")]
    [InlineData("cardtest")]
    public void CompileMapWritesNavAndBombDamage(string map)
    {
        var reference = Path.Combine(Path.GetTempPath(), "vrad3_refs", map + ".vpk");
        var source = MapFixtures.VmapSource("s2c_rc_probe", map);
        if (source is null || !File.Exists(reference))
        {
            output.WriteLine($"skipped: no {map} source or {reference}");
            return;
        }
        var outRoot = Path.Combine(Path.GetTempPath(), "s2c_compile_nav_test");
        var package = Path.Combine(outRoot, "csgo_addons", "s2c_rc_probe", "maps", map + ".vpk");
        Directory.CreateDirectory(Path.GetDirectoryName(package)!);
        var valve = Io.VpkWriter.ReadAll(reference);
        var start = new Dictionary<string, byte[]>(valve);
        start.Remove($"maps/{map}.nav");
        start.Remove($"maps/{map}/baked_bomb_damage.vdata_c");
        File.WriteAllBytes(package, Io.VpkWriter.Write(start));
        var log = new StringWriter();
        var code = MapCompile.Run(MapCompileArgs.Parse(["-i", source, "-outroot", outRoot, "-nav"]), log);
        output.WriteLine(log.ToString());
        Assert.Equal(0, code);
        var ours = Io.VpkWriter.ReadAll(package);
        var nav = ours[$"maps/{map}.nav"];
        var valveNav = valve[$"maps/{map}.nav"];
        var end = NavWriterTests.CustomDataAt(valveNav);
        Assert.Equal(end, NavWriterTests.CustomDataAt(nav));
        Assert.True(nav.AsSpan(0, end).SequenceEqual(valveNav.AsSpan(0, end)));
        Assert.Equal(Flatten(NavAnalysisTests.CustomData(valveNav), "", []), Flatten(NavAnalysisTests.CustomData(nav), "", []));
        Assert.Equal(valve[$"maps/{map}/baked_bomb_damage.vdata_c"], ours[$"maps/{map}/baked_bomb_damage.vdata_c"]);
    }

    [Theory]
    [InlineData("s2c_rc_probe", "probe01")]
    [InlineData("s2c_rc_probe", "cardtest")]
    [InlineData("s2c_rc_probe", "s2c_navmarkup")]
    public void NavAndBombDamageMatchValve(string addon, string map)
    {
        var reference = Path.Combine(Path.GetTempPath(), "vrad3_refs", map + ".vpk");
        if (CS2Fixtures.StockPak() is not { } pak || MapFixtures.GameSchema() is not { } schema || !File.Exists(reference))
        {
            output.WriteLine($"skipped: no stock pak, schema or {reference}");
            return;
        }
        var root = Path.GetFullPath(Path.Combine(Csgo, "..", ".."));
        var vmap = Path.Combine(root, "content", "csgo_addons", addon, "maps", map + ".vmap");
        if (!File.Exists(vmap))
        {
            output.WriteLine($"skipped: no {vmap}");
            return;
        }
        var document = DmxBinary.ReadFile(vmap);
        MapPrefabs.Attach(document, MapPrefabs.FromContent(Path.Combine(root, "content", "csgo_addons", addon)));
        MapDeformers.Apply(document, MapFixtures.GameSchema());
        using var assets = new GameContent(pak, Path.Combine(root, "game", "csgo_addons", addon));
        using var package = new ValvePak.Package();
        package.Read(reference);
        var entities = MapEntities.From(document);

        var nav = NavCompile.Generate(document, schema, assets, Path.Combine(Csgo, "gameinfo.gi"), entities);
        var valve = Entry(package, $"maps/{map}.nav");
        var end = NavWriterTests.CustomDataAt(valve);
        var first = end == NavWriterTests.CustomDataAt(nav.Bytes) ? Enumerable.Range(0, end).FirstOrDefault(i => nav.Bytes[i] != valve[i], -1) : 0;
        output.WriteLine($"{map}: {nav.Mesh.Areas.Count} areas; .nav {(first < 0 ? $"bytes 0..{end} Valve's" : $"differs at byte {first}")}, ours {nav.Bytes.Length} bytes, Valve's {valve.Length}");
        var ours = Flatten(NavAnalysisTests.CustomData(nav.Bytes), "", []);
        var theirs = Flatten(NavAnalysisTests.CustomData(valve), "", []);
        var lines = ours.Zip(theirs).Select((p, i) => (p, i)).Where(x => x.p.First != x.p.Second).Take(8).ToList();
        foreach (var ((a, b), i) in lines)
            output.WriteLine($"  line {i}: ours {a} | Valve {b}");
        output.WriteLine($"custom data: {ours.Count} lines ours, {theirs.Count} Valve's, {ours.Zip(theirs).Count(p => p.First != p.Second)} differ");

        var sites = NavCompile.Sites(document, schema, entities);
        var bomb = NavCompile.BombDamageFile(NavCompile.Areas(nav.Mesh), sites, NavCompile.World(Entry(package, $"maps/{map}/world_physics.vmdl_c")), addon, map);
        var valveBomb = Entry(package, $"maps/{map}/baked_bomb_damage.vdata_c");
        {
            using var resource = new ValveResourceFormat.Resource();
            resource.Read(new MemoryStream(valveBomb));
            var blob = ((ValveResourceFormat.ResourceTypes.BinaryKV3)resource.DataBlock!).Data.Root["data"]["bombsites"].AsBlob();
            for (var i = 0; i + 28 <= blob.Length; i += 28)
                output.WriteLine($"  Valve site: {string.Join(" ", Enumerable.Range(0, 7).Select(k => BitConverter.ToSingle(blob, i + k * 4).ToString("R")))}");
            foreach (var site in sites)
                output.WriteLine($"  our site:   {site.Mins.X:R} {site.Mins.Y:R} {site.Mins.Z:R} {site.Maxs.X:R} {site.Maxs.Y:R} {site.Maxs.Z:R} {site.Power:R}");
        }
        var bombFirst = bomb is null ? 0 : Enumerable.Range(0, Math.Min(bomb.Length, valveBomb.Length)).FirstOrDefault(i => bomb[i] != valveBomb[i], -1);
        output.WriteLine($"bomb damage: {sites.Count} sites, ours {bomb?.Length} bytes, Valve's {valveBomb.Length}, first difference {bombFirst}");

        Assert.True(first < 0, ".nav before the custom data");
        Assert.Equal(theirs, ours);
        Assert.Equal(valveBomb, bomb);
    }

    /// <summary>
    /// A map with a mapbuilder.ladder mesh (s2c_prefabprobe3's ladder) or nav links (s2c_navlink): the .nav
    /// from the .vmap, with the mesh build's ladders and the areas' ladder lists, or the links' islands,
    /// against the package Valve compiled in the addon. The custom data is compared too, unless <paramref name="analyze"/> is false (the mesh
    /// only, without the hours-long CS analysis). s2c_prefabprobe3's analysis runs over two hours in a Release
    /// suite, so it runs only with NAV_ANALYZE_BIG=1.
    /// </summary>
    [Theory]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe3", false)]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe3", true)]
    [InlineData("s2c_rc_probe", "s2c_navlink", false)]
    [InlineData("s2c_rc_probe", "s2c_navlink", true)]
    [InlineData("s2c_rc_probe", "s2c_navlink2", false)]
    [InlineData("s2c_rc_probe", "s2c_navlink2", true)]
    [InlineData("s2c_rc_probe", "s2c_navlink4", false)]
    [InlineData("s2c_rc_probe", "s2c_navlink4", true)]
    [InlineData("s2c_rc_probe", "s2c_navlink3", false)]
    [InlineData("s2c_rc_probe", "s2c_navlink3", true)]
    public void NavWithLaddersMatchesValve(string addon, string map, bool analyze)
    {
        if (analyze && map == "s2c_prefabprobe3" && Environment.GetEnvironmentVariable("NAV_ANALYZE_BIG") != "1")
        {
            output.WriteLine($"{map}: set NAV_ANALYZE_BIG=1 to run the analysis (hours)");
            return;
        }
        if (CS2Fixtures.StockPak() is not { } pak || MapFixtures.GameSchema() is not { } schema)
        {
            output.WriteLine("skipped: no stock pak or schema");
            return;
        }
        var root = Path.GetFullPath(Path.Combine(Csgo, "..", ".."));
        var vmap = Path.Combine(root, "content", "csgo_addons", addon, "maps", map + ".vmap");
        var reference = Path.Combine(root, "game", "csgo_addons", addon, "maps", map + ".vpk");
        if (!File.Exists(vmap) || !File.Exists(reference))
        {
            output.WriteLine($"skipped: no {vmap} or {reference}");
            return;
        }
        var document = DmxBinary.ReadFile(vmap);
        MapPrefabs.Attach(document, MapPrefabs.FromContent(Path.Combine(root, "content", "csgo_addons", addon)));
        MapDeformers.Apply(document, MapFixtures.GameSchema());
        using var assets = new GameContent(pak, Path.Combine(root, "game", "csgo_addons", addon));
        using var package = new ValvePak.Package();
        package.Read(reference);
        var nav = NavCompile.Generate(document, schema, assets, Path.Combine(Csgo, "gameinfo.gi"), MapEntities.From(document), analyze);
        foreach (var l in nav.Mesh.Ladders)
            output.WriteLine($"ladder {l.Id}: width {l.Width:R} top {l.Top:R} bottom {l.Bottom:R} length {l.Length:R} direction {l.Direction} "
                             + $"areas {string.Join(' ', l.Areas.Select(a => a?.Id ?? 0))}");
        var valve = Entry(package, $"maps/{map}.nav");
        var end = NavWriterTests.CustomDataAt(valve);
        var first = end == NavWriterTests.CustomDataAt(nav.Bytes) ? Enumerable.Range(0, end).FirstOrDefault(i => nav.Bytes[i] != valve[i], -1) : 0;
        output.WriteLine($"{map}: {nav.Mesh.Areas.Count} areas, {nav.Mesh.Ladders.Count} ladders; .nav {(first < 0 ? $"bytes 0..{end} Valve's" : $"differs at byte {first}")}");
        if (!analyze)
        {
            Assert.True(first < 0, ".nav before the custom data");
            return;
        }
        var ours = Flatten(NavAnalysisTests.CustomData(nav.Bytes), "", []);
        var theirs = Flatten(NavAnalysisTests.CustomData(valve), "", []);
        output.WriteLine($"custom data: {ours.Count} lines ours, {theirs.Count} Valve's, {ours.Zip(theirs).Count(p => p.First != p.Second)} differ");
        foreach (var ((a, b), k) in ours.Zip(theirs).Select((p, k) => (p, k)).Where(x => x.p.First != x.p.Second).Take(8))
            output.WriteLine($"  line {k}: ours {a} | Valve {b}");
        Assert.True(first < 0, ".nav before the custom data");
        Assert.Equal(theirs, ours);
    }
}
