using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Source2.Compiler.Maps;
using Source2.Compiler.Maps.NavGen;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>ClipPolysToHullClearance (022) from Valve's own input against Valve's output.</summary>
public sealed class NavGenClipTests(ITestOutputHelper output)
{
    private const string Content = @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\content\csgo_addons\s2c_rc_probe\maps\";

    /// <summary>
    /// The world as Valve answered it: every box cast navsystem made (navcast_world.bin from
    /// tools/re/capture_navcast.py), keyed by the box, start and delta, with Valve's fraction. A
    /// faithful port asks exactly those; anything else is counted and answered 1.
    /// </summary>
    internal sealed class ReplayWorld : INavBoxWorld
    {
        private readonly Dictionary<(int, int, int, int, int, int, int, int, int, int, int, int), float> _answers = [];
        public int Asked, Unknown, FirstUnknown = -1;
        public string? FirstKey;
        public Stream? Log;

        public ReplayWorld(string path)
        {
            using var f = File.OpenRead(path);
            var r = new byte[0x100];
            int I(int o) => BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(o));
            while (f.Read(r, 0, 0x100) == 0x100)
            {
                var key = (I(8), I(12), I(16), I(20), I(24), I(28), I(0x38), I(0x3c), I(0x40), I(0x44), I(0x48), I(0x4c));
                _answers[key] = BinaryPrimitives.ReadSingleLittleEndian(r.AsSpan(0x88 + 0x34));
            }
        }

        public int Count => _answers.Count;

        public float CastBox((float X, float Y, float Z) mins, (float X, float Y, float Z) maxs, (float X, float Y, float Z) start,
                             (float X, float Y, float Z) delta)
        {
            static int B(float v) => BitConverter.SingleToInt32Bits(v);
            Asked++;
            Log?.Write(MemoryMarshal.AsBytes<int>([B(mins.X), B(mins.Y), B(mins.Z), B(maxs.X), B(maxs.Y), B(maxs.Z), B(start.X), B(start.Y), B(start.Z), B(delta.X), B(delta.Y), B(delta.Z)]));
            var key = (B(mins.X), B(mins.Y), B(mins.Z), B(maxs.X), B(maxs.Y), B(maxs.Z), B(start.X), B(start.Y), B(start.Z), B(delta.X), B(delta.Y), B(delta.Z));
            if (_answers.TryGetValue(key, out var fraction))
                return fraction;
            if (Unknown++ == 0)
            {
                FirstUnknown = Asked;
                FirstKey = $"mins {mins} maxs {maxs} start {start.X:R} {start.Y:R} {start.Z:R} delta {delta.X:R} {delta.Y:R} {delta.Z:R}";
            }
            return 1f;
        }
    }

    private static NavGenPolys Load(string path)
    {
        var g = NavGenInputTests.LoadStage(path);
        var raw = File.ReadAllBytes(path);
        for (var i = 0; i < g.Polys.Count; i++)
        {
            g.Polys[i].Field62 = raw[8 + i * 0x88 + 0x62];
            g.Polys[i].Field64 = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(8 + i * 0x88 + 0x64));
        }
        return g;
    }

    [Theory]
    [InlineData("probe01")]
    [InlineData("cardtest")]
    [InlineData("s2c_prefabprobe3")]
    [InlineData("s2c_prefabprobe4")]
    public void ClipMatchesValveOnReplayedCasts(string map)
    {
        // Valve's polygons vary from run to run, so the stage files and the casts must come from one
        // compile: <map>_same is a capture_navcast.py --with-navgen run; <map>_in holds capture_navgen
        // and capture_navcast output, which match only when the map builds the same each time.
        var same = Path.Combine(Path.GetTempPath(), "navgen_capture", map + "_same");
        var dir = Directory.Exists(same) ? same : Path.Combine(Path.GetTempPath(), "navgen_capture", map + "_in");
        var casts = Environment.GetEnvironmentVariable("NAVCAST_WORLD") ?? Path.Combine(dir, "navcast_world.bin");
        if (!File.Exists(Path.Combine(dir, "022_ClipPolysToHullClearance_in.bin")) || !File.Exists(casts) || CS2Fixtures.StockPak() is not { } pak)
            return;
        var settings = NavGenInputTests.MapSettings(DmxBinary.ReadFile(Path.Combine(Content, map + ".vmap")), pak);
        var g = Load(Path.Combine(dir, "022_ClipPolysToHullClearance_in.bin"));
        var valve = Load(Path.Combine(dir, "022_ClipPolysToHullClearance_out.bin"));
        var world = new ReplayWorld(casts);
        using var log = Environment.GetEnvironmentVariable("NAVCAST_LOG") is { } lp ? File.Create(Path.Combine(lp, map + ".asks")) : null;
        world.Log = log;
        var before = g.Polys.Count;
        g.ClipPolysToHullClearance(settings.Hulls[0], world, orientedMesh: false);
        var diff = NavGenInputTests.Compare(g, valve);
        output.WriteLine($"{map}: {before} -> {g.Polys.Count} polygons (Valve {valve.Polys.Count}), {g.Pool.Count} vertices (Valve {valve.Pool.Count}); " +
                         $"{world.Asked} casts asked, {world.Unknown} not among Valve's {world.Count} (first at ask {world.FirstUnknown}); {diff ?? "Valve's"}");
        if (world.FirstKey != null)
            output.WriteLine("first unknown: " + world.FirstKey);
        Assert.Null(diff);
        Assert.Equal(0, world.Unknown);
    }
}
