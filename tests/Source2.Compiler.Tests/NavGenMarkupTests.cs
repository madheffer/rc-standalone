using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Source2.Compiler.Maps;
using Source2.Compiler.Maps.NavGen;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The nav markups (L23) against Valve's: the RC collector (180f209f0) against the markups and tag table
/// navsystem received, and stages 003 / 009 (RemovePolysMarkedNoNav) and 019 (SplitPolysByMarkup) from
/// Valve's own input and markups against Valve's output, from tools/re/capture_navgen.py's dumps under
/// %TEMP%/navgen_capture/&lt;map&gt;_in (s2c_navmarkup from tools/coverage/nav_markup_specimen.py, and
/// probe_classes). The BVH's leaf keys against navsystem's code under unicorn (tools/re/emu_navmorton.py,
/// %TEMP%/navgen_capture/morton.json).
/// </summary>
public sealed class NavGenMarkupTests(ITestOutputHelper output)
{
    private const string Content = @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\content\csgo_addons\s2c_rc_probe\maps\";

    private static string Dir(string map) => Path.Combine(Path.GetTempPath(), "navgen_capture", map + "_in");

    /// <summary>
    /// navinput_markups.bin and navinput_tags.bin as the input navsystem read (the tag table empty when the
    /// capture predates its dump: probe_classes').
    /// </summary>
    internal static NavMarkupInput? Captured(string map)
    {
        var markups = Path.Combine(Dir(map), "navinput_markups.bin");
        var tags = Path.Combine(Dir(map), "navinput_tags.bin");
        if (!File.Exists(markups))
            return null;
        var input = new NavMarkupInput();
        var t = File.Exists(tags) ? File.ReadAllBytes(tags) : new byte[4];
        for (var i = 0; i < BinaryPrimitives.ReadInt32LittleEndian(t); i++)
        {
            string Text(int at) => Encoding.ASCII.GetString(t, at, 0x40).Split('\0')[0];
            input.Tags.Add((Text(4 + i * 0x80), Text(4 + i * 0x80 + 0x40)));
        }
        var m = File.ReadAllBytes(markups);
        for (var i = 0; i < BinaryPrimitives.ReadInt32LittleEndian(m); i++)
        {
            var at = 4 + i * 0x50;
            float F(int k) => BinaryPrimitives.ReadSingleLittleEndian(m.AsSpan(at + k * 4));
            input.Markups.Add(new NavMarkup((F(0), F(1), F(2)), (F(3), F(4), F(5)), [.. Enumerable.Range(6, 12).Select(F)],
                                            BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(at + 0x48)), m[at + 0x4c]));
        }
        return input;
    }

    /// <summary>
    /// s2c_navangles (tools/coverage/nav_angle_specimen.py) holds markups at node angles outside [0, 360) (negative
    /// yaws, tiny angles, pitch and roll below 0, yaws past 360): their matrices are those of the angles CMapPoint's
    /// setter leaves (<see cref="MapNodeAngles"/>), read through <see cref="MapSource.Read"/>.
    /// </summary>
    [Theory]
    [InlineData("s2c_navmarkup")]
    [InlineData("probe_classes")]
    [InlineData("s2c_navangles")]
    public void CollectorMatchesValve(string map)
    {
        if (Captured(map) is not { } valve || MapFixtures.GameSchema() is not { } schema || !File.Exists(Content + map + ".vmap"))
            return;
        var ours = NavMarkupInput.Collect(MapSource.Read(Content + map + ".vmap", schema), schema);
        output.WriteLine($"{map}: ours {ours.Markups.Count} markups {ours.Tags.Count} tags, Valve {valve.Markups.Count} markups {valve.Tags.Count} tags");
        if (File.Exists(Path.Combine(Dir(map), "navinput_tags.bin")))
            Assert.Equal(valve.Tags, ours.Tags);
        Assert.Equal(valve.Markups.Count, ours.Markups.Count);
        static int Bits(float f) => BitConverter.SingleToInt32Bits(f);
        for (var i = 0; i < ours.Markups.Count; i++)
        {
            var (a, b) = (ours.Markups[i], valve.Markups[i]);
            float[] fa = [a.Min.X, a.Min.Y, a.Min.Z, a.Max.X, a.Max.Y, a.Max.Z, .. a.Matrix];
            float[] fb = [b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z, .. b.Matrix];
            Assert.True(fa.Select(Bits).SequenceEqual(fb.Select(Bits)), $"markup {i}: ours [{string.Join(" ", fa.Select(f => f.ToString("R")))}], Valve [{string.Join(" ", fb.Select(f => f.ToString("R")))}]");
            Assert.Equal(b.Tag, a.Tag);
            Assert.Equal(b.Flag, a.Flag);
        }
    }

    private float HullRadius(string map)
    {
        var pak = CS2Fixtures.StockPak()!;
        return NavGenInputTests.MapSettings(DmxBinary.ReadFile(Content + map + ".vmap"), pak).Hulls[0].Radius;
    }

    private void Stage(string map, string stage, Action<NavGenPolys, NavMarkupInput, float> run)
    {
        var dir = Dir(map);
        if (Captured(map) is not { } markups || CS2Fixtures.StockPak() is null || !File.Exists(Path.Combine(dir, stage + "_in.bin")))
            return;
        // Without the tag table (probe_classes' capture), the markups' tags come from the .vmap.
        if (markups.Tags.Count == 0 && markups.Markups.Count > 0 && MapFixtures.GameSchema() is { } schema)
            markups.Tags.AddRange(NavMarkupInput.Collect(DmxBinary.ReadFile(Content + map + ".vmap"), schema).Tags);
        var input = Path.Combine(dir, stage + "_in.bin");
        var g = NavGenInputTests.LoadStage(input);
        var before = g.Polys.Count;
        run(g, markups, HullRadius(map));
        var valve = NavGenInputTests.LoadStage(Path.Combine(dir, stage + "_out.bin"));
        output.WriteLine($"{map} {stage}: {before} -> {g.Polys.Count} polygons {g.Pool.Count} vertices, Valve {valve.Polys.Count} polygons {valve.Pool.Count} vertices");
        Assert.Null(NavGenInputTests.Compare(g, valve));
    }

    [Fact]
    public void RemovePolysMarkedNoNavBeforeRecastMatchesValve()
        => Stage("s2c_navmarkup", "003_RemovePolysMarkedNoNav", (g, m, r) =>
            g.RemovePolysMarkedNoNav(m, NavMarkupSettings.NoNavPad, NavMarkupSettings.NoNavEps, NavMarkupSettings.NoNavEpsPlain, shrink: true, r));

    [Fact]
    public void RemovePolysMarkedNoNavAfterRecastMatchesValve()
        => Stage("s2c_navmarkup", "009_RemovePolysMarkedNoNav", (g, m, r) =>
            g.RemovePolysMarkedNoNav(m, NavMarkupSettings.NoNavPad, NavMarkupSettings.NoNavEps, NavMarkupSettings.NoNavEpsPlain, shrink: false, r));

    [Theory]
    [InlineData("s2c_navmarkup")]
    [InlineData("probe_classes")]
    public void SplitPolysByMarkupMatchesValve(string map)
        => Stage(map, "019_SplitPolysByMarkup", (g, m, r) =>
            g.SplitPolysByMarkup(m, NavMarkupSettings.SplitGrow, NavMarkupSettings.SplitEps, NavMarkupSettings.SplitEpsPlain, r));

    /// <summary>navsystem's 30-bit leaf keys (180047aa0 / 180044fb0) under unicorn against <see cref="NavGenPolys.BvhKey"/>.</summary>
    [Fact]
    public void BvhKeysMatchTheBinary()
    {
        var path = Path.Combine(Path.GetTempPath(), "navgen_capture", "morton.json");
        if (!File.Exists(path))
            return;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var n = 0;
        foreach (var c in doc.RootElement.GetProperty("keys").EnumerateArray())
        {
            var boxes = c.GetProperty("boxes").EnumerateArray().ToList();
            var pairs = c.GetProperty("pairs").EnumerateArray().ToList();
            for (var i = 0; i < boxes.Count; i++)
            {
                var b = boxes[i].EnumerateArray().Select(v => (short)v.GetInt32()).ToArray();
                Assert.Equal(pairs[i][1].GetUInt32(), NavGenPolys.BvhKey(b[0], b[1], b[2], b[3], b[4], b[5]));
                n++;
            }
        }
        output.WriteLine($"{n} keys, the binary's");
    }
}
