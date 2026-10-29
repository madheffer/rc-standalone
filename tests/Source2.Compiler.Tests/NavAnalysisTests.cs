using System.Buffers.Binary;

using Source2.Compiler.Maps;
using Source2.Compiler.Maps.NavGen;
using ValveKeyValue;
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

    /// <summary>The size of the navan_calls.bin record at <paramref name="p"/>.</summary>
    internal static int RecordSize(byte[] b, int p) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p)) switch
    {
        1 => 8 + 0x80 + 8 + 0x1c + 12,
        2 => 8 + 8 + 12 + 24 + 4 + 0x48 + 8 + 4,
        3 => 8 + 8 + 4 + 4 + 4,
        4 => 8 + 12 + 12 + 8 + 0xd0,
        5 => 8 + 0x30 + 12 + 12 + 0x38 + 4 + 4 + 4,
        6 => 4 + 4 + 8 + 12 + 12 + 4 + 4 + 4 + 4 + 4, // no thread id
        7 => 16 + BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p + 12)) * (8 + 0x30),
        9 => 8 + 16 * 0x38 + 4,
        var kind => throw new InvalidDataException($"record kind {kind}"),
    };

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

    /// <summary>
    /// An edge shorter than 1e-17 takes 18146bed0 (VectorNormalize_Slow) in 1814c5a00 rather than
    /// refusing. A corner 1e-20 along the bottom edge of a square leaves a collinear edge whose
    /// slow-path normal is the bottom edge's, so every point answers as it does for the plain square.
    /// </summary>
    [Fact]
    public void ContainsXYTakesTheSlowNormaliseOnATinyEdge()
    {
        static NavAnalysis.Area Square(params (float X, float Y)[] corners) => new()
        {
            Corners = [.. corners.Select(c => new NavAnalysis.V(c.X, c.Y, 0f))],
            Min = new NavAnalysis.V(0f, 0f, 0f),
            Max = new NavAnalysis.V(10f, 10f, 0f),
        };
        var plain = Square((0f, 0f), (10f, 0f), (10f, 10f), (0f, 10f));
        var tiny = Square((0f, 0f), (1e-20f, 0f), (10f, 0f), (10f, 10f), (0f, 10f));
        var n = VectorNormalizeSlow.Normalise(new System.Numerics.Vector3(0f, -1e-20f, 0f), out var len);
        Assert.Equal(new System.Numerics.Vector3(0f, -1f, 0f), n);
        Assert.Equal(1e-20f, len);
        for (var x = -2f; x <= 12f; x += 0.5f)
            for (var y = -2f; y <= 12f; y += 0.5f)
                foreach (var tol in new[] { 0f, 1f })
                {
                    var p = new NavAnalysis.V(x, y, 0f);
                    Assert.Equal(NavAnalysis.ContainsXY(plain, p, tol), NavAnalysis.ContainsXY(tiny, p, tol));
                }
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
            var n = RecordSize(b, p);
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
            var n = RecordSize(b, p);
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
            var n = RecordSize(b, p);
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

    /// <summary>Each mesh sphere cast (kind 6, type 3) the analysis made, replayed through <see cref="Simulation.SphereCast.Mesh"/>.</summary>
    [Fact]
    public void MeshSphereCastsMatchValve()
    {
        var dir = Dir();
        if (!File.Exists(Path.Combine(dir, "navan_calls.bin")) || !File.Exists(Path.Combine(dir, "navan_shapedata.bin")))
            return;
        var (meshes, _) = NavCastTests.Shapes(dir, "navan_shapedata.bin");
        var b = File.ReadAllBytes(Path.Combine(dir, "navan_calls.bin"));
        int p = 0, total = 0, same = 0, shown = 0;
        System.Numerics.Vector3 V3(int o) => new(F(b, o), F(b, o + 4), F(b, o + 8));
        while (p < b.Length)
        {
            var kind = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
            if (kind == 6 && BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 4)) == 3
                && meshes.TryGetValue(BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p + 8)), out var m))
            {
                var start = V3(p + 16);
                var delta = V3(p + 28);
                float radius = F(b, p + 40), max = F(b, p + 44);
                var returned = b[p + 48] != 0;
                var want = F(b, p + 52);
                var got = Simulation.SphereCast.Mesh(m.Mesh, m.Scale, start, delta, radius, max);
                total++;
                if (got.Found == returned && (!returned || BitConverter.SingleToInt32Bits(got.Fraction) == BitConverter.SingleToInt32Bits(want)))
                    same++;
                else if (shown++ < 10)
                    output.WriteLine($"cast {total}: {start} + {delta} r {radius} max {max:R}: ours {got.Found} {got.Fraction:R}, Valve {returned} {want:R}");
            }
            p += RecordSize(b, p);
        }
        output.WriteLine($"{same} of {total} mesh sphere casts Valve's");
        Assert.Equal(total, same);
    }

    /// <summary>Each world sphere cast (kind 5) replayed through <see cref="NavSphereWorld"/> over our settle bodies of probe01.</summary>
    [Fact]
    public void WorldSphereCastsMatchValve()
    {
        var dir = Dir();
        if (!File.Exists(Path.Combine(dir, "navan_calls.bin")) || CS2Fixtures.StockPak() is not { } pak || MapFixtures.GameSchema() is not { } schema)
            return;
        var document = DmxBinary.ReadFile(Path.Combine(Content, "probe01.vmap"));
        MapPrefabs.Attach(document, MapPrefabs.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(Path.Combine(Content, "probe01.vmap")))!));
        using var models = new SettleBuildTests.PakModels(pak, @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo_addons\s2c_rc_probe");
        var bodies = SettleWorld.Build(document, models, schema, SmartProps.NodesCreatedOnLoad(document, MapFixtures.SmartPropLocators),
            d => SmartProps.NodesCreatedOnLoad(d, MapFixtures.SmartPropLocators), new SettleWorld.CollisionRules());
        var world = new NavSphereWorld(bodies);
        var b = File.ReadAllBytes(Path.Combine(dir, "navan_calls.bin"));
        int p = 0, total = 0, same = 0, shown = 0;
        System.Numerics.Vector3 V3(int o) => new(F(b, o), F(b, o + 4), F(b, o + 8));
        while (p < b.Length)
        {
            var kind = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
            if (kind == 5)
            {
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
                    output.WriteLine($"world cast {total}: {start} + {delta}: ours {hits} {got:R}, Valve {wantHits} {want:R}");
            }
            p += RecordSize(b, p);
        }
        output.WriteLine($"{same} of {total} world sphere casts Valve's");
        Assert.Equal(total, same);
    }

    /// <summary>The .nav's last KV3 block (the CS custom data), read back.</summary>
    internal static ValveKeyValue.KVObject CustomData(byte[] nav)
    {
        var start = nav.AsSpan().LastIndexOf(new byte[] { 0x05, 0x33, 0x56, 0x4b });
        using var holder = new ValveResourceFormat.Resource();
        var kv3 = new ValveResourceFormat.ResourceTypes.BinaryKV3 { Resource = holder, Offset = (uint)start, Size = (uint)(nav.Length - start) };
        using var reader = new BinaryReader(new MemoryStream(nav));
        reader.BaseStream.Position = start;
        kv3.Read(reader);
        return kv3.Data.Root;
    }

    [Fact]
    public void DumpCustomData()
    {
        if (Environment.GetEnvironmentVariable("NAV_DUMP") is not { Length: > 0 } path)
            return;
        File.WriteAllText(path, Source2.Compiler.Kv3TextWriter.Write(CustomData(BuiltNav(Dir(), "probe01")!), Guid.Empty));
    }

    /// <summary>The A* record snapshots (kind 7): stage, area id, and per area its id and the 0x30 record bytes.</summary>
    internal static List<(int Stage, uint Area, List<(uint Id, byte[] Raw)> Records)> Snapshots(byte[] b)
    {
        var list = new List<(int, uint, List<(uint, byte[])>)>();
        for (var p = 0; p < b.Length; p += RecordSize(b, p))
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p)) != 7)
                continue;
            var n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p + 12));
            var recs = new List<(uint, byte[])>();
            for (var i = 0; i < n; i++)
            {
                var o = p + 16 + i * 0x38;
                recs.Add((BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o)), b.AsSpan(o + 8, 0x30).ToArray()));
            }
            list.Add((BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p + 4)), BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 8)), recs));
        }
        return list;
    }

    /// <summary>Area pointer to id; the first Analyze call's dump (the mesh the custom data comes from) wins where a later one reused an address.</summary>
    internal static Dictionary<ulong, uint> AreaIdsByPointer(string dir)
    {
        var ids = new Dictionary<ulong, uint>();
        foreach (var f in Directory.GetFiles(dir, "navan_areas*.bin").OrderByDescending(f => f.Length).ThenByDescending(f => f, StringComparer.Ordinal))
        {
            var b = File.ReadAllBytes(f);
            var n = BinaryPrimitives.ReadInt32LittleEndian(b);
            var p = 4;
            for (var i = 0; i < n; i++)
            {
                var ptr = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p));
                ids[ptr] = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 8 + 0x60));
                p += 8 + 0x490 + 0x14;
                var edges = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
                p += 4;
                for (var e = 0; e < edges; e++)
                {
                    var c = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
                    p += 4 + c * 0x10;
                }
            }
        }
        return ids;
    }

    /// <summary>
    /// Our records against a snapshot: position bits, parent, how and back edge per area; the number
    /// that differ, the first few written to the output.
    /// </summary>
    private int CompareRecords(NavAnalysis an, List<(uint Id, byte[] Raw)> valve, Dictionary<ulong, uint> idOf, string label)
    {
        var byId = an.Areas.ToDictionary(a => a.Id);
        var bad = 0;
        foreach (var (id, raw) in valve)
        {
            var area = byId[id];
            var parentPtr = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(0x18));
            uint? parent = parentPtr == 0 ? null : idOf.TryGetValue(parentPtr, out var pid) ? pid : 0xdeadu;
            var how = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(0x28));
            var back = raw[0x2c];
            var want = (F(raw, 0), F(raw, 4), F(raw, 8));
            var ours = an.Records.GetValueOrDefault(area);
            var same = ours != null && BitConverter.SingleToInt32Bits(ours.Pos.X) == BitConverter.SingleToInt32Bits(want.Item1)
                       && BitConverter.SingleToInt32Bits(ours.Pos.Y) == BitConverter.SingleToInt32Bits(want.Item2)
                       && BitConverter.SingleToInt32Bits(ours.Pos.Z) == BitConverter.SingleToInt32Bits(want.Item3)
                       && ours.Parent?.Id == parent && ours.How == how && ours.BackEdge == back;
            if (same)
                continue;
            if (bad++ < 8)
                output.WriteLine($"{label} area {id}: ours {(ours == null ? "none" : $"{ours.Pos} parent {ours.Parent?.Id} how {ours.How} back {ours.BackEdge}")}, "
                                 + $"Valve ({want.Item1:R}, {want.Item2:R}, {want.Item3:R}) parent {parent} how {how} back {back}");
        }
        return bad;
    }

    /// <summary>The records the occupy step's searches leave, against Valve's as the hiding-spot step starts.</summary>
    [Fact]
    public void RecordsAfterOccupyMatchValve()
    {
        var dir = Dir();
        if (!File.Exists(Path.Combine(dir, "navan_calls.bin")) || BuiltNav(dir, "probe01") is not { } nav)
            return;
        var b = File.ReadAllBytes(Path.Combine(dir, "navan_calls.bin"));
        var snap = Snapshots(b).FirstOrDefault(s => s.Stage == 2);
        if (snap.Records is null)
            return;
        var an = new NavAnalysis(ReadNav(nav));
        var entities = Source2.Compiler.MapEntities.From(Source2.Compiler.DmxBinary.ReadFile(Path.Combine(Content, "probe01.vmap")));
        List<NavAnalysis.V> Spawns(string cls) => [.. entities.Where(e => e.ClassName == cls).Select(e => new NavAnalysis.V(e.Origin.X, e.Origin.Y, e.Origin.Z))];
        an.EarliestOccupyTimes(Spawns("info_player_terrorist"), Spawns("info_player_counterterrorist"));
        var bad = CompareRecords(an, snap.Records, AreaIdsByPointer(dir), "after occupy");
        output.WriteLine($"{snap.Records.Count - bad} of {snap.Records.Count} records Valve's after the occupy step");
        Assert.Equal(0, bad);
    }

    /// <summary>probe01's world for the analysis traces: our settle bodies through <see cref="NavSphereWorld"/>.</summary>
    private static Func<NavAnalysis.V, NavAnalysis.V, float>? Probe01Trace(out IDisposable? keep)
    {
        keep = null;
        if (CS2Fixtures.StockPak() is not { } pak || MapFixtures.GameSchema() is not { } schema)
            return null;
        var document = DmxBinary.ReadFile(Path.Combine(Content, "probe01.vmap"));
        MapPrefabs.Attach(document, MapPrefabs.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(Path.Combine(Content, "probe01.vmap")))!));
        var models = new SettleBuildTests.PakModels(pak, @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo_addons\s2c_rc_probe");
        keep = models;
        var bodies = SettleWorld.Build(document, models, schema, SmartProps.NodesCreatedOnLoad(document, MapFixtures.SmartPropLocators),
            d => SmartProps.NodesCreatedOnLoad(d, MapFixtures.SmartPropLocators), new SettleWorld.CollisionRules());
        var world = new NavSphereWorld(bodies);
        return (s, e) => world.CastSphere(new System.Numerics.Vector3(s.X, s.Y, s.Z), new System.Numerics.Vector3(e.X - s.X, e.Y - s.Y, e.Z - s.Z)).Fraction;
    }

    /// <summary>
    /// Each area's approach entries (kind 9) from the records Valve's analysis held as that area's
    /// approach step began (the kind 7 snapshot just before it); chained, only the first area starts
    /// from Valve's records and each later one from ours, which must equal Valve's snapshot.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApproachAreasMatchValveFromValvesRecords(bool chained)
    {
        var dir = Dir();
        if (!File.Exists(Path.Combine(dir, "navan_calls.bin")) || BuiltNav(dir, "probe01") is not { } nav)
            return;
        var trace = Probe01Trace(out var keep);
        using var _ = keep;
        if (trace is null)
            return;
        var an = new NavAnalysis(ReadNav(nav)) { Trace = trace };
        var byId = an.Areas.ToDictionary(a => a.Id);
        var idOf = AreaIdsByPointer(dir);
        var b = File.ReadAllBytes(Path.Combine(dir, "navan_calls.bin"));
        List<(uint Id, byte[] Raw)>? state = null;
        int total = 0, same = 0, shown = 0, recordsSame = 0, recordsTotal = 0;
        string Name(ulong ptr) => ptr == 0 ? "-" : idOf.TryGetValue(ptr, out var id) ? id.ToString() : $"?{ptr:x}";
        for (var p = 0; p < b.Length; p += RecordSize(b, p))
        {
            var kind = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
            if (kind == 7 && BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p + 4)) == 4)
                state = Snapshots(b[p..(p + RecordSize(b, p))]).Single().Records;
            if (kind != 9 || state is null)
                continue;
            if (chained && total > 0)
            {
                var badRecords = CompareRecords(an, state, idOf, $"before area {BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 4))}");
                recordsSame += badRecords == 0 ? 1 : 0;
                recordsTotal++;
                state = null;
            }
            if (state is not null)
                an.Records.Clear();
            foreach (var (id, raw) in state ?? [])
            {
                var parent = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(0x18));
                an.Records[byId[id]] = new NavAnalysis.Record
                {
                    Pos = new NavAnalysis.V(F(raw, 0), F(raw, 4), F(raw, 8)),
                    Parent = parent != 0 && idOf.TryGetValue(parent, out var pid) ? byId[pid] : null,
                    How = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(0x28)),
                    BackEdge = raw[0x2c],
                };
            }
            var area = byId[BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 4))];
            var count = b[p + 8 + 16 * 0x38];
            var want = new List<string>();
            for (var k = 0; k < count; k++)
            {
                var e = b.AsSpan(p + 8 + k * 0x38, 0x38);
                want.Add($"{Name(BinaryPrimitives.ReadUInt64LittleEndian(e[0x10..]))} {Name(BinaryPrimitives.ReadUInt64LittleEndian(e))}/{e[8]} "
                         + $"{Name(BinaryPrimitives.ReadUInt64LittleEndian(e[0x20..]))}/{e[0x28]} {BinaryPrimitives.ReadUInt16LittleEndian(e[0x30..])}/{BinaryPrimitives.ReadUInt16LittleEndian(e[0x32..])}");
            }
            var got = an.ApproachAreas(area).Select(e => $"{(e.Prev is null ? "-" : e.Prev.Id.ToString())} {e.Here.Id}/{e.HereBackEdge} {e.Next.Id}/{e.NextBackEdge} "
                                                         + $"{e.PrevToHereHow}/{e.HereToNextHow}").ToList();
            total++;
            if (got.SequenceEqual(want))
                same++;
            else if (shown++ < 6)
                output.WriteLine($"area {area.Id}: ours [{string.Join(", ", got)}], Valve [{string.Join(", ", want)}]");
        }
        output.WriteLine($"{same} of {total} areas' approach entries Valve's" + (chained ? $"; records Valve's before {recordsSame} of {recordsTotal} areas" : ""));
        Assert.Equal(total, same);
        Assert.Equal(recordsTotal, recordsSame);
    }

    /// <summary>The hiding spots the .nav's custom data lists, area by area: id, position, flags.</summary>
    internal static List<(uint Id, NavAnalysis.V Pos, uint Flags)> NavHidingSpots(byte[] nav)
    {
        var list = new List<(uint, NavAnalysis.V, uint)>();
        foreach (var area in CustomData(nav)["arealist"]!.Values)
            foreach (var spot in area["hidingspotdata"]!.Values)
            {
                var pos = spot["pos"]!.Values.Select(v => (float)v).ToArray();
                list.Add(((uint)spot["id"]!, new NavAnalysis.V(pos[0], pos[1], pos[2]), (uint)spot["flags"]!));
            }
        return list;
    }

    /// <summary>
    /// The records after the hiding-spot step from ours after the occupy step: each spot's sniper search
    /// (from the area GetNavArea finds under it, at that area's centre, range 2000) in turn, against
    /// Valve's as the encounter step starts.
    /// </summary>
    [Fact]
    public void RecordsAfterSniperSearchesMatchValve()
    {
        var dir = Dir();
        if (!File.Exists(Path.Combine(dir, "navan_calls.bin")) || BuiltNav(dir, "probe01") is not { } nav)
            return;
        var b = File.ReadAllBytes(Path.Combine(dir, "navan_calls.bin"));
        var snap = Snapshots(b).FirstOrDefault(s => s.Stage == 3);
        if (snap.Records is null)
            return;
        var an = new NavAnalysis(ReadNav(nav));
        var entities = Source2.Compiler.MapEntities.From(Source2.Compiler.DmxBinary.ReadFile(Path.Combine(Content, "probe01.vmap")));
        List<NavAnalysis.V> Spawns(string cls) => [.. entities.Where(e => e.ClassName == cls).Select(e => new NavAnalysis.V(e.Origin.X, e.Origin.Y, e.Origin.Z))];
        an.EarliestOccupyTimes(Spawns("info_player_terrorist"), Spawns("info_player_counterterrorist"));
        foreach (var (_, pos, _) in NavHidingSpots(nav))
        {
            var area = an.GetNavArea(pos);
            Assert.NotNull(area);
            an.SurroundingSearch(area, area.Center, 2000f);
        }
        var bad = CompareRecords(an, snap.Records, AreaIdsByPointer(dir), "after sniper searches");
        output.WriteLine($"{snap.Records.Count - bad} of {snap.Records.Count} records Valve's after the hiding-spot step");
        Assert.Equal(0, bad);
    }

    /// <summary>
    /// The hiding-spot step after our occupy step: the spots against the .nav's (id, position, flags),
    /// the trace count against Valve's for the step, and the records against Valve's as the encounter
    /// step starts.
    /// </summary>
    [Fact]
    public void HidingSpotsMatchValve()
    {
        var dir = Dir();
        if (!File.Exists(Path.Combine(dir, "navan_calls.bin")) || BuiltNav(dir, "probe01") is not { } nav)
            return;
        var trace = Probe01Trace(out var keep);
        using var _ = keep;
        if (trace is null)
            return;
        var b = File.ReadAllBytes(Path.Combine(dir, "navan_calls.bin"));
        int? valveTraces = null;
        var inStep = false;
        for (var p = 0; p < b.Length; p += RecordSize(b, p))
        {
            var kind = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
            if (kind == 7)
            {
                var stage = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p + 4));
                if (stage == 2)
                {
                    inStep = true;
                    valveTraces = 0;
                }
                else if (inStep)
                    inStep = false;
            }
            else if (kind == 4 && inStep)
                valveTraces++;
        }
        var traces = 0;
        var an = new NavAnalysis(ReadNav(nav)) { Trace = (s, e) => { traces++; return trace(s, e); } };
        var entities = Source2.Compiler.MapEntities.From(Source2.Compiler.DmxBinary.ReadFile(Path.Combine(Content, "probe01.vmap")));
        List<NavAnalysis.V> Spawns(string cls) => [.. entities.Where(e => e.ClassName == cls).Select(e => new NavAnalysis.V(e.Origin.X, e.Origin.Y, e.Origin.Z))];
        an.EarliestOccupyTimes(Spawns("info_player_terrorist"), Spawns("info_player_counterterrorist"));
        traces = 0;
        an.ComputeHidingSpots();
        var ours = an.Areas.SelectMany(a => an.Spots[a]).Select(s => $"{s.Id} {s.Pos} {s.Flags}").ToList();
        var valve = NavHidingSpots(nav).Select(s => $"{s.Id} {s.Pos} {s.Flags}").ToList();
        output.WriteLine($"ours [{string.Join(", ", ours)}]");
        output.WriteLine($"Valve [{string.Join(", ", valve)}]");
        output.WriteLine($"traces: ours {traces}, Valve {valveTraces}");
        Assert.Equal(valve, ours);
        Assert.Equal(valveTraces, traces);
        var snap = Snapshots(b).First(s => s.Stage == 3);
        Assert.Equal(0, CompareRecords(an, snap.Records, AreaIdsByPointer(dir), "after hiding spots"));
    }

    /// <summary>A KV3 integer's low 32 bits, whatever its stored type.</summary>
    private static uint Dword(KVObject o) => unchecked((uint)(long)o);

    /// <summary>Our analysis of probe01 through the given step (1 occupy, 2 hiding spots, 3 encounter spots).</summary>
    private NavAnalysis Probe01Analysis(byte[] nav, Func<NavAnalysis.V, NavAnalysis.V, float> trace, int through)
    {
        var an = new NavAnalysis(ReadNav(nav)) { Trace = trace };
        var entities = Source2.Compiler.MapEntities.From(Source2.Compiler.DmxBinary.ReadFile(Path.Combine(Content, "probe01.vmap")));
        List<NavAnalysis.V> Spawns(string cls) => [.. entities.Where(e => e.ClassName == cls).Select(e => new NavAnalysis.V(e.Origin.X, e.Origin.Y, e.Origin.Z))];
        an.EarliestOccupyTimes(Spawns("info_player_terrorist"), Spawns("info_player_counterterrorist"));
        if (through >= 2)
            an.ComputeHidingSpots();
        if (through >= 3)
            an.ComputeEncounterSpots();
        return an;
    }

    /// <summary>
    /// The encounter step after ours: per area, each encounter's from / fromdir / to / todir (Valve's area
    /// pointers' low dwords resolved through the area dump) and the area's order (the last encounter's
    /// spots: t, the spot pointers masked).
    /// </summary>
    [Fact]
    public void EncounterSpotsMatchValve()
    {
        var dir = Dir();
        if (!File.Exists(Path.Combine(dir, "navan_calls.bin")) || BuiltNav(dir, "probe01") is not { } nav)
            return;
        var trace = Probe01Trace(out var keep);
        using var _ = keep;
        if (trace is null)
            return;
        var an = Probe01Analysis(nav, trace, 3);
        var low = AreaIdsByPointer(dir).GroupBy(kv => (uint)kv.Key).ToDictionary(g => g.Key, g => g.First().Value);
        var areas = CustomData(nav)["arealist"]!.Values.ToList();
        int same = 0, shown = 0;
        for (var i = 0; i < an.Areas.Count; i++)
        {
            var area = an.Areas[i];
            var want = areas[i]["spotencounterdata"]!.Values.Select(e =>
                $"{(low.TryGetValue(Dword(e["from"]!), out var f) ? f : 0)}/{(int)e["fromdir"]!}>{(low.TryGetValue(Dword(e["to"]!), out var t) ? t : 0)}/{(int)e["todir"]!}").ToList();
            var wantOrder = areas[i]["order"]!.Values.Select(o => ((float)o["t"]!).ToString("R")).ToList();
            var encs = an.Encounters[area];
            var got = encs.Select(e => $"{e.From.Id}/{e.FromDir}>{e.To.Id}/{e.ToDir}").ToList();
            var gotOrder = encs.Count == 0 ? [] : encs[^1].Spots.Select(s => s.T.ToString("R")).ToList();
            if (got.SequenceEqual(want) && gotOrder.SequenceEqual(wantOrder))
                same++;
            else if (shown++ < 6)
                output.WriteLine($"area {area.Id}: ours [{string.Join(" ", got)}] order [{string.Join(" ", gotOrder)}]; Valve [{string.Join(" ", want)}] order [{string.Join(" ", wantOrder)}]");
        }
        output.WriteLine($"{same} of {an.Areas.Count} areas' encounters Valve's");
        Assert.Equal(an.Areas.Count, same);
    }

    /// <summary>Walks two KV3 trees, collecting differences in keys, value types and values (order ids skipped).</summary>
    private static void Diff(KVObject ours, KVObject valve, string path, List<string> diffs)
    {
        if (diffs.Count > 20)
            return;
        if (ours.ValueType != valve.ValueType || ours.IsArray != valve.IsArray)
        {
            diffs.Add($"{path}: ours {ours.ValueType}{(ours.IsArray ? "[]" : "")}, Valve {valve.ValueType}{(valve.IsArray ? "[]" : "")}");
            return;
        }
        if (ours.IsArray)
        {
            var a = ours.Values.ToList();
            var b = valve.Values.ToList();
            if (a.Count != b.Count)
                diffs.Add($"{path}: ours {a.Count} items, Valve {b.Count}");
            for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
                Diff(a[i], b[i], $"{path}[{i}]", diffs);
            return;
        }
        if (ours.ValueType == KVValueType.Collection)
        {
            var ak = ours.Children.Select(c => c.Key).ToList();
            var bk = valve.Children.Select(c => c.Key).ToList();
            if (!ak.SequenceEqual(bk))
                diffs.Add($"{path}: keys ours [{string.Join(",", ak)}], Valve [{string.Join(",", bk)}]");
            foreach (var c in ours.Children)
                if (valve[c.Key] is { } v && !(path.Contains(".order[") && c.Key == "id"))
                    Diff(c.Value, v, $"{path}.{c.Key}", diffs);
            return;
        }
        if (ours.ToString() != valve.ToString())
            diffs.Add($"{path}: ours {ours}, Valve {valve}");
    }

    /// <summary>
    /// Our probe01 analysis end to end into the custom data, against the .nav's: every key, value type
    /// and value, the area pointers in from / to taken from the capture's area dump, the order ids
    /// (spot pointers) skipped.
    /// </summary>
    [Fact]
    public void CustomDataMatchesValve()
    {
        var dir = Dir();
        if (!File.Exists(Path.Combine(dir, "navan_areas.bin")) || BuiltNav(dir, "probe01") is not { } nav)
            return;
        var trace = Probe01Trace(out var keep);
        using var _ = keep;
        if (trace is null)
            return;
        var an = new NavAnalysis(ReadNav(nav)) { Trace = trace };
        var entities = Source2.Compiler.MapEntities.From(Source2.Compiler.DmxBinary.ReadFile(Path.Combine(Content, "probe01.vmap")));
        List<NavAnalysis.V> Spawns(string cls) => [.. entities.Where(e => e.ClassName == cls).Select(e => new NavAnalysis.V(e.Origin.X, e.Origin.Y, e.Origin.Z))];
        an.Analyze(Spawns("info_player_terrorist"), Spawns("info_player_counterterrorist"));
        var pointerOf = CapturedAreas(dir).ToDictionary(c => BinaryPrimitives.ReadUInt32LittleEndian(c.Raw.AsSpan(0x60)), c => unchecked((int)(uint)c.Ptr));
        var ours = an.CustomData(a => pointerOf[a.Id]);
        var diffs = new List<string>();
        Diff(ours, CustomData(nav), "", diffs);
        foreach (var d in diffs)
            output.WriteLine(d);
        Assert.Empty(diffs);
    }

    /// <summary>
    /// The custom data block's bytes: ours (with Valve's area and spot pointer dwords put in, the only
    /// values a compile cannot reproduce) through Kv3V5Writer, against the last KV3 block of Valve's .nav.
    /// </summary>
    [Fact]
    public void CustomDataBytesMatchValve()
    {
        var dir = Dir();
        if (!File.Exists(Path.Combine(dir, "navan_areas.bin")) || BuiltNav(dir, "probe01") is not { } nav)
            return;
        var trace = Probe01Trace(out var keep);
        using var _ = keep;
        if (trace is null)
            return;
        var entities = Source2.Compiler.MapEntities.From(Source2.Compiler.DmxBinary.ReadFile(Path.Combine(Content, "probe01.vmap")));
        List<NavAnalysis.V> Spawns(string cls) => [.. entities.Where(e => e.ClassName == cls).Select(e => new NavAnalysis.V(e.Origin.X, e.Origin.Y, e.Origin.Z))];
        var fresh = new NavAnalysis(ReadNav(nav)) { Trace = trace };
        fresh.Analyze(Spawns("info_player_terrorist"), Spawns("info_player_counterterrorist"));
        var pointerOf = CapturedAreas(dir).ToDictionary(c => BinaryPrimitives.ReadUInt32LittleEndian(c.Raw.AsSpan(0x60)), c => unchecked((int)(uint)c.Ptr));
        var valveTree = CustomData(nav);
        var valveAreas = valveTree["arealist"]!.Values.ToList();
        var spotPointer = new Dictionary<NavAnalysis.HidingSpot, int>();
        for (var i = 0; i < fresh.Areas.Count; i++)
        {
            var encs = fresh.Encounters[fresh.Areas[i]];
            if (encs.Count == 0)
                continue;
            var ids = valveAreas[i]["order"]!.Values.Select(o => unchecked((int)Dword(o["id"]!))).ToList();
            for (var k = 0; k < ids.Count; k++)
                spotPointer[encs[^1].Spots[k].Spot] = ids[k];
        }
        var ours = Kv3V5Writer.Write(fresh.CustomData(a => pointerOf[a.Id], s => spotPointer.GetValueOrDefault(s)), NavWriter.CustomDataFormat);
        var start = nav.AsSpan().LastIndexOf(new byte[] { 0x05, 0x33, 0x56, 0x4b });
        var valve = nav.AsSpan(start).ToArray();
        output.WriteLine($"ours {ours.Length} bytes, Valve {valve.Length}");
        var first = Enumerable.Range(0, Math.Min(ours.Length, valve.Length)).FirstOrDefault(i => ours[i] != valve[i], -1);
        Assert.True(first < 0 && ours.Length == valve.Length, $"first difference at byte {first}");
    }
}
