using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="VisBuild.Run"/> against the VXVS the compile shipped, byte for
/// byte: on Valve's own trace scene, on the same triangles under the vis
/// loader's rebuilt tree, and on a scene built from the .vmap.
/// </summary>
public class VisBuildTests(ITestOutputHelper output)
{
    public static TheoryData<string, string> Specimens => new()
    {
        { "s2c_rc_probe", "probe01" },
        { "s2c_rc_probe", "cardtest" },
        { "s2c_lighting", "ze_hold_em_p" },
    };

    [Theory]
    [MemberData(nameof(Specimens))]
    public void FromValvesScene(string addon, string map) => Check(addon, map, rte => rte);

    [Theory]
    [MemberData(nameof(Specimens))]
    public void UnderTheRebuiltTree(string addon, string map) => Check(addon, map, rte => rte.WithTracerTree());

    /// <summary>The .viscfg built from the map: pvstype, the sun and the visibility hints, bit for bit.</summary>
    [Theory]
    [MemberData(nameof(Sources))]
    public void SettingsFromTheMap(string addon, string map, string compiledIn)
    {
        var source = MapFixtures.VmapSource(addon, map);
        var path = Path.Combine(Path.GetTempPath(), "csgo_addons", compiledIn, "maps", map + ".viscfg");
        if (source is null || !File.Exists(path) || MapFixtures.GameSchema() is not { } schema)
        {
            MapFixtures.Skip($"{map}'s source and .viscfg");
            return;
        }
        var valve = VisConfig.Read(path);
        var ours = VisConfig.FromMap(MapEntities.From(DmxBinary.ReadFile(source)), schema);
        output.WriteLine($"{map}: pvstype {ours.PvsType}/{valve.PvsType}, sun {ours.DirToSun}/{valve.DirToSun}, hints {ours.Hints.Count}/{valve.Hints.Count}");
        Assert.Equal(valve.PvsType, ours.PvsType);
        Assert.Equal(valve.Hints.Count, ours.Hints.Count);
        for (var i = 0; i < valve.Hints.Count; i++)
        {
            var (vh, oh) = (valve.Hints[i], ours.Hints[i]);
            Assert.True(vh.Type == oh.Type && Bits(vh.Origin) == Bits(oh.Origin) && Bits(vh.BoxMins) == Bits(oh.BoxMins) && Bits(vh.BoxMaxs) == Bits(oh.BoxMaxs),
                        $"hint {i}: {oh} against {vh}");
        }
        Assert.Equal(valve.DirToSun.HasValue, ours.DirToSun.HasValue);
        if (valve.DirToSun is { } v && ours.DirToSun is { } o)
            Assert.True(Bits(v) == Bits(o), $"sun {o} against {v}");

        static (int, int, int) Bits(System.Numerics.Vector3 x)
            => (BitConverter.SingleToInt32Bits(x.X), BitConverter.SingleToInt32Bits(x.Y), BitConverter.SingleToInt32Bits(x.Z));
    }

    /// <summary>Everything from the .vmap: our trace scene and our settings.</summary>
    [Theory]
    [MemberData(nameof(Sources))]
    public void FromTheMap(string addon, string map, string compiledIn)
    {
        var source = MapFixtures.VmapSource(addon, map);
        if (source is null || VisFixtures.RayTraceScene(compiledIn, map) is not var (_, shipped)
            || CS2Fixtures.StockPak() is not { } pak || MapFixtures.GameSchema() is not { } schema)
        {
            MapFixtures.Skip($"{map}'s source and compile");
            return;
        }
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        var packages = new[] { "csgo", "core" }.Select(d => { var p = new ValvePak.Package(); p.Read(Path.Combine(game, d, "pak01_dir.vpk")); return p; }).ToList();
        var visFlags = new MaterialVisFlags.Source([Path.Combine(game, "csgo_addons", addon)], packages);
        bool RendersAsWorld(string c) => schema.IsSolidClass(c) && schema.HasFlag(c, "render_as_world_but_physics_as_entity");
        var document = DmxBinary.ReadFile(source);
        var scene = TraceScene.Environment(TraceScene.Triangles(MapMeshes.Read(document), content.Material, m => visFlags[m], RendersAsWorld));
        var ours = VisBuild.Run(scene, VisConfig.FromMap(MapEntities.From(document), schema)).WriteVxvs();
        var theirs = shipped.WriteVxvs();
        var differing = Enumerable.Range(0, Math.Min(ours.Length, theirs.Length)).Count(i => ours[i] != theirs[i])
                        + Math.Abs(ours.Length - theirs.Length);
        output.WriteLine($"{map} from the .vmap: VXVS ours {ours.Length:n0} valve {theirs.Length:n0}, differing {differing:n0}");
        Assert.Equal(0, differing);
    }

    public static TheoryData<string, string, string> Sources => new()
    {
        { "s2probe", "probe01", "s2c_rc_probe" },
        { "ze_doom_p2", "cardtest", "s2c_rc_probe" },
        { "s2c_lighting", "ze_hold_em_p", "s2c_lighting" },
        { "s2probe", "atixref", "s2probe" },
        { "s2c_big", "ze_ffvii_mako_reactor_v6_p", "s2c_big" },
    };

    private void Check(string addon, string map, Func<RayTraceEnvironment, RayTraceEnvironment> scene)
    {
        if (VisFixtures.RayTraceScene(addon, map) is not var (rte, shipped))
        {
            MapFixtures.Skip($"{map}'s .rte and compile");
            return;
        }
        var config = VisConfig.Read(Path.Combine(Path.GetTempPath(), "csgo_addons", addon, "maps", map + ".viscfg"));
        var ours = VisBuild.Run(scene(rte), config).WriteVxvs();
        var theirs = shipped.WriteVxvs();
        var differing = Enumerable.Range(0, Math.Min(ours.Length, theirs.Length)).Count(i => ours[i] != theirs[i])
                        + Math.Abs(ours.Length - theirs.Length);
        output.WriteLine($"{map}: VXVS ours {ours.Length:n0} valve {theirs.Length:n0}, differing {differing:n0}");
        Assert.Equal(0, differing);
    }
}

public class TracerTreeProbe(ITestOutputHelper output)
{
    [Fact]
    public void SameHits()
    {
        if (Environment.GetEnvironmentVariable("TREEPROBE") != "1" || VisFixtures.RayTraceScene("s2c_rc_probe", "cardtest") is not var (rte, _))
            return;
        var rebuilt = rte.WithTracerTree();
        output.WriteLine($"box {rte.Mins} {rte.Maxs} -> {rebuilt.Mins} {rebuilt.Maxs}; triangles {rte.TriangleCount} {rebuilt.TriangleCount}");
        var random = new Random(1);
        int same = 0, differ = 0, lost = 0;
        for (var i = 0; i < 20000; i++)
        {
            var o = new System.Numerics.Vector3(random.NextSingle() * 1500 - 250, random.NextSingle() * 2300 - 970, random.NextSingle() * 280);
            var d = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f));
            var a = rte.Trace(o, d, 16384f);
            var b = rebuilt.Trace(o, d, 16384f);
            if (a?.Triangle == b?.Triangle && a?.Distance == b?.Distance) same++;
            else if (a is not null && b is null)
            {
                if (lost++ < 5)
                    output.WriteLine($"lost: o {o} d {d} hit tri {a.Value.Triangle} at {a.Value.Distance} flags 0x{rte.RawFlags(a.Value.Triangle):x4} in order {rte.TracerOrderContains(a.Value.Triangle)}");
            }
            else differ++;
        }
        output.WriteLine($"same {same} differ {differ} lost {lost}");
        var ov1 = rte.Overlapping(new(0, 0, 0), new(200, 200, 200));
        var ov2 = rebuilt.Overlapping(new(0, 0, 0), new(200, 200, 200));
        output.WriteLine($"overlapping {ov1.Length} vs {ov2.Length}");
    }
}
