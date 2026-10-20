using System.Buffers.Binary;

using Source2.Compiler.Maps.NavGen;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The CS nav analysis (<see cref="NavAnalysis"/>) against tools/re/capture_navanalysis.py's capture
/// (<c>NAVAN_DIR</c>, default %TEMP%/navgen_capture/probe01_analysis): the areas as CSNavMesh::Analyze
/// sees them, and each nearest-area lookup with Valve's answer. The mesh comes from the .nav the same
/// compile wrote (probe01.built.vpk, maps/probe01.nav).
/// </summary>
public sealed class NavAnalysisTests(ITestOutputHelper output)
{
    private static string Dir() => Environment.GetEnvironmentVariable("NAVAN_DIR")
                                   ?? Path.Combine(Path.GetTempPath(), "navgen_capture", "probe01_analysis");

    /// <summary>The .nav's corners, polygons and areas (as tools/re/navfile.py splits it).</summary>
    internal static NavWriter.Mesh ReadNav(byte[] b)
    {
        int p = 12;
        p += 1;
        var nstr = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p));
        p += 2;
        for (var i = 0; i < nstr; i++)
            p += 2 + BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p));
        p += 1;
        p = (p + 7) & ~7;
        p = Kv3End(b, p);
        var mesh = new NavWriter.Mesh();
        var nc = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
        p += 4;
        for (var i = 0; i < nc; i++, p += 12)
            mesh.Corners.Add((F(b, p), F(b, p + 4), F(b, p + 8)));
        var np = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
        p += 4;
        for (var i = 0; i < np; i++)
        {
            int k = b[p++];
            var idx = new int[k];
            for (var j = 0; j < k; j++, p += 4)
                idx[j] = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
            p += 4;
            mesh.Polygons.Add(idx);
        }
        p += 8;
        p = (p + 7) & ~7;
        p = Kv3End(b, p);
        var na = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
        p += 4;
        for (var i = 0; i < na; i++)
        {
            var a = new NavWriter.Area
            {
                Id = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)),
                Attributes = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p + 4)),
                Byte58 = b[p + 12],
                Polygon = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p + 13)),
            };
            p += 21;
            var edges = mesh.Polygons[a.Polygon].Length;
            a.Connections = new List<(uint, int)>[edges];
            for (var e = 0; e < edges; e++)
            {
                var c = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
                p += 4;
                a.Connections[e] = [];
                for (var j = 0; j < c; j++, p += 8)
                    a.Connections[e].Add((BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)), BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p + 4))));
            }
            p += 5;
            p += 4 + 4 * BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
            p += 4 + 4 * BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
            mesh.Areas.Add(a);
        }
        return mesh;
    }

    private static float F(byte[] b, int p) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(p));

    private static int Kv3End(byte[] b, int at) => b.AsSpan(at).IndexOf(new byte[] { 0, 0xdd, 0xee, 0xff }) + at + 4;

    /// <summary>maps/probe01.nav from the package the capture's compile built.</summary>
    internal static byte[]? BuiltNav(string dir, string map)
    {
        var vpk = Path.Combine(dir, map + ".built.vpk");
        if (!File.Exists(vpk))
            return null;
        var pak = new ValvePak.Package();
        pak.Read(vpk);
        var e = pak.FindEntry("maps/" + map + ".nav");
        if (e is null)
            return null;
        pak.ReadEntry(e, out var data);
        return data;
    }

    /// <summary>The captured areas: pointer and the raw 0x490 bytes, in mesh order.</summary>
    internal static List<(ulong Ptr, byte[] Raw)> CapturedAreas(string dir)
    {
        var b = File.ReadAllBytes(Path.Combine(dir, "navan_areas.bin"));
        var n = BinaryPrimitives.ReadInt32LittleEndian(b);
        var p = 4;
        var list = new List<(ulong, byte[])>();
        for (var i = 0; i < n; i++)
        {
            var ptr = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p));
            var raw = b.AsSpan(p + 8, 0x490).ToArray();
            p += 8 + 0x490 + 0x14;
            var edges = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
            p += 4;
            for (var e = 0; e < edges; e++)
                p += 4 + 16 * BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
            list.Add((ptr, raw));
        }
        return list;
    }

    [Fact]
    public void AreasMatchValve()
    {
        var dir = Dir();
        if (!File.Exists(Path.Combine(dir, "navan_areas.bin")) || BuiltNav(dir, "probe01") is not { } nav)
            return;
        var an = new NavAnalysis(ReadNav(nav));
        var caps = CapturedAreas(dir);
        Assert.Equal(caps.Count, an.Areas.Count);
        var bad = 0;
        for (var i = 0; i < caps.Count; i++)
        {
            var raw = caps[i].Raw;
            var a = an.Areas[i];
            var same = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x60)) == a.Id
                       && Same(raw, 0xc, a.Center) && Same(raw, 0x24, a.Min) && Same(raw, 0x30, a.Max);
            if (!same && bad++ < 5)
                output.WriteLine($"area {a.Id}: centre {a.Center} bounds {a.Min} {a.Max}");
        }
        output.WriteLine($"{caps.Count - bad} of {caps.Count} areas Valve's; grid {an.GridX} x {an.GridY} from ({an.OriginX}, {an.OriginY})");
        Assert.Equal(0, bad);
    }

    private static bool Same(byte[] raw, int o, NavAnalysis.V v)
        => BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(o)) == BitConverter.SingleToInt32Bits(v.X)
           && BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(o + 4)) == BitConverter.SingleToInt32Bits(v.Y)
           && BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(o + 8)) == BitConverter.SingleToInt32Bits(v.Z);

    [Fact]
    public void TravelDistancesMatchValve()
    {
        var dir = Dir();
        if (!File.Exists(Path.Combine(dir, "navan_calls.bin")) || BuiltNav(dir, "probe01") is not { } nav)
            return;
        var an = new NavAnalysis(ReadNav(nav));
        var byId = an.Areas.ToDictionary(a => a.Id);
        var heuristic = float.TryParse(Environment.GetEnvironmentVariable("NAV_HEURISTIC"), out var h) ? h : 1f;
        var b = File.ReadAllBytes(Path.Combine(dir, "navan_calls.bin"));
        int p = 0, total = 0, same = 0, shown = 0;
        while (p < b.Length)
        {
            var kind = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
            var n = kind switch { 1 => 8 + 0x80 + 8 + 0x1c + 12, 2 => 8 + 8 + 12 + 24 + 4 + 0x48 + 8 + 4, 4 => 8 + 12 + 12 + 8 + 0xd0, _ => 8 + 8 + 8 + 4 };
            if (kind == 1)
            {
                var req = p + 8;
                var want = F(b, req + 0x84);
                var startId = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(req + 0x80 + 8 + 0x1c));
                var goalId = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(req + 0x80 + 8 + 0x1c + 4));
                if (!float.IsNaN(want) && b[req + 0x34] != 0 && b[req + 0x5c] != 0 && F(b, req + 0x60) < 0f && F(b, req + 0x64) < 0f
                    && byId.TryGetValue(startId, out var s) && byId.TryGetValue(goalId, out var g))
                {
                    var sp = new NavAnalysis.V(F(b, req + 0x28), F(b, req + 0x2c), F(b, req + 0x30));
                    var gp = new NavAnalysis.V(F(b, req + 0x50), F(b, req + 0x54), F(b, req + 0x58));
                    var got = an.TravelDistance(s, g, sp, gp, heuristic);
                    total++;
                    if (BitConverter.SingleToInt32Bits(got) == BitConverter.SingleToInt32Bits(want))
                        same++;
                    else if (shown++ < 10)
                        output.WriteLine($"path {total}: {startId} -> {goalId}, ours {got:R}, Valve {want:R}");
                }
            }
            p += n;
        }
        output.WriteLine($"{same} of {total} travel distances Valve's (heuristic {heuristic})");
        Assert.Equal(total, same);
    }

    private const string Content = @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\content\csgo_addons\s2c_rc_probe\maps\";

    [Fact]
    public void EarliestOccupyTimesMatchValve()
    {
        var dir = Dir();
        if (!File.Exists(Path.Combine(dir, "navan_calls.bin")) || BuiltNav(dir, "probe01") is not { } nav)
            return;
        var an = new NavAnalysis(ReadNav(nav));
        var entities = Source2.Compiler.MapEntities.From(Source2.Compiler.DmxBinary.ReadFile(Path.Combine(Content, "probe01.vmap")));
        List<NavAnalysis.V> Spawns(string cls) => [.. entities.Where(e => e.ClassName == cls).Select(e => new NavAnalysis.V(e.Origin.X, e.Origin.Y, e.Origin.Z))];
        var times = an.EarliestOccupyTimes(Spawns("info_player_terrorist"), Spawns("info_player_counterterrorist"));
        var b = File.ReadAllBytes(Path.Combine(dir, "navan_calls.bin"));
        int p = 0, total = 0, same = 0, shown = 0;
        while (p < b.Length)
        {
            var kind = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
            var n = kind switch { 1 => 8 + 0x80 + 8 + 0x1c + 12, 2 => 8 + 8 + 12 + 24 + 4 + 0x48 + 8 + 4, 4 => 8 + 12 + 12 + 8 + 0xd0, _ => 8 + 8 + 8 + 4 };
            if (kind == 3)
            {
                var id = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 24));
                float wt = F(b, p + 16), wct = F(b, p + 20);
                var i = an.Areas.FindIndex(a => a.Id == id);
                total++;
                if (BitConverter.SingleToInt32Bits(times[i].T) == BitConverter.SingleToInt32Bits(wt) && BitConverter.SingleToInt32Bits(times[i].CT) == BitConverter.SingleToInt32Bits(wct))
                    same++;
                else if (shown++ < 10)
                    output.WriteLine($"area {id}: ours {times[i].T:R} / {times[i].CT:R}, Valve {wt:R} / {wct:R}");
            }
            p += n;
        }
        output.WriteLine($"{same} of {total} areas' occupy times Valve's");
        Assert.Equal(total, same);
    }

    [Fact]
    public void NearestAreaMatchesValve()
    {
        var dir = Dir();
        if (!File.Exists(Path.Combine(dir, "navan_calls.bin")) || BuiltNav(dir, "probe01") is not { } nav)
            return;
        var an = new NavAnalysis(ReadNav(nav));
        var b = File.ReadAllBytes(Path.Combine(dir, "navan_calls.bin"));
        int p = 0, total = 0, same = 0, shown = 0;
        while (p < b.Length)
        {
            var kind = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
            var n = kind switch { 1 => 8 + 0x80 + 8 + 0x1c + 12, 2 => 8 + 8 + 12 + 24 + 4 + 0x48 + 8 + 4, 4 => 8 + 12 + 12 + 8 + 0xd0, _ => 8 + 8 + 8 + 4 };
            if (kind == 2)
            {
                var r = b.AsSpan(p + 8);
                var pos = new NavAnalysis.V(F(b, p + 16), F(b, p + 20), F(b, p + 24));
                var max = BinaryPrimitives.ReadSingleLittleEndian(r[44..]);
                var hull = r.Slice(48, 0x48);
                var wantId = BinaryPrimitives.ReadUInt32LittleEndian(r[(48 + 0x48 + 8)..]);
                var q = new NavAnalysis.NearestQuery(BinaryPrimitives.ReadSingleLittleEndian(hull[0x10..]), BinaryPrimitives.ReadSingleLittleEndian(hull[0x14..]),
                    BinaryPrimitives.ReadSingleLittleEndian(hull[0x18..]), BinaryPrimitives.ReadSingleLittleEndian(hull[0x1c..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(hull[0x20..]), BinaryPrimitives.ReadUInt64LittleEndian(hull[0x28..]), hull[0x40] != 0);
                var got = an.NearestArea(pos, max, q);
                total++;
                if ((got?.Id ?? uint.MaxValue) == wantId)
                    same++;
                else if (shown++ < 10)
                    output.WriteLine($"lookup {total}: {pos} -> ours {got?.Id}, Valve's {wantId}");
            }
            p += n;
        }
        output.WriteLine($"{same} of {total} nearest-area lookups Valve's");
        Assert.Equal(total, same);
    }
}
