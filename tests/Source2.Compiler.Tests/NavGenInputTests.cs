using System.Buffers.Binary;
using System.Numerics;
using Source2.Compiler.Maps;
using Source2.Compiler.Maps.NavGen;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Nav generation's input (navsystem 180048b20) from the map: the settle world's bodies, their mesh
/// triangles, against tools/re/capture_navgen.py's input_0_* dumps (<c>NAVGEN_INPUT=&lt;dir&gt;</c>, default
/// %TEMP%/navgen_capture/probe01_in).
/// </summary>
public sealed class NavGenInputTests(ITestOutputHelper output)
{
    private const string Content = @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\content\csgo_addons\s2c_rc_probe\maps\";
    private const string AddonGame = @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo_addons\s2c_rc_probe";

    private static string? Dir()
    {
        var dir = Environment.GetEnvironmentVariable("NAVGEN_INPUT") ?? Path.Combine(Path.GetTempPath(), "navgen_capture", "probe01_in");
        return File.Exists(Path.Combine(dir, "input_0_verts.bin")) ? dir : null;
    }

    /// <summary>A stage dump as polygons (corner list, normal, kind, flags) over the double pool.</summary>
    internal static NavGenPolys LoadStage(string path)
    {
        var b = File.ReadAllBytes(path);
        int n = BinaryPrimitives.ReadInt32LittleEndian(b), nv = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4));
        var g = new NavGenPolys();
        for (var i = 0; i < n; i++)
        {
            var at = 8 + i * 0x88;
            var count = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at));
            var p = new NavPoly
            {
                Indices = [.. Enumerable.Range(0, count).Select(k => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at + 0x68 + k * 4)))],
                Kind = b[at + 0x40],
                Flags = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at + 0x44)),
                Nx = BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(at + 0x48)),
                Ny = BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(at + 0x50)),
                Nz = BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(at + 0x58)),
            };
            g.Polys.Add(p);
        }
        var vo = 8 + n * 0x88;
        for (var i = 0; i < nv; i++)
            g.Pool.Add((BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(vo + i * 24)), BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(vo + i * 24 + 8)),
                BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(vo + i * 24 + 16))));
        return g;
    }

    /// <summary>The first difference between two polygon lists, or null.</summary>
    internal static string? Compare(NavGenPolys ours, NavGenPolys valve)
    {
        if (ours.Pool.Count != valve.Pool.Count || ours.Polys.Count != valve.Polys.Count)
            return $"counts: ours {ours.Polys.Count} polys {ours.Pool.Count} vertices, Valve {valve.Polys.Count} polys {valve.Pool.Count} vertices";
        static long Bits(double d) => BitConverter.DoubleToInt64Bits(d);
        for (var i = 0; i < ours.Pool.Count; i++)
        {
            var (a, b) = (ours.Pool[i], valve.Pool[i]);
            if (Bits(a.X) != Bits(b.X) || Bits(a.Y) != Bits(b.Y) || Bits(a.Z) != Bits(b.Z))
                return $"vertex {i}: ours {a}, Valve {b}";
        }
        for (var i = 0; i < ours.Polys.Count; i++)
        {
            var (a, b) = (ours.Polys[i], valve.Polys[i]);
            if (!a.Indices.SequenceEqual(b.Indices))
                return $"poly {i} corners: ours [{string.Join(",", a.Indices)}], Valve [{string.Join(",", b.Indices)}]";
            if (Bits(a.Nx) != Bits(b.Nx) || Bits(a.Ny) != Bits(b.Ny) || Bits(a.Nz) != Bits(b.Nz))
                return $"poly {i} normal: ours ({a.Nx:R}, {a.Ny:R}, {a.Nz:R}), Valve ({b.Nx:R}, {b.Ny:R}, {b.Nz:R})";
            if (a.Kind != b.Kind)
                return $"poly {i} kind: ours {a.Kind}, Valve {b.Kind}";
            if (a.Flags != b.Flags)
                return $"poly {i} flags: ours {a.Flags}, Valve {b.Flags}";
        }
        return null;
    }

    private const string GameInfo = @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\gameinfo.gi";

    /// <summary>gameinfo.gi's NavSystem block as key/value pairs (null when absent).</summary>
    private static Dictionary<string, string>? NavSystemBlock(string path)
    {
        if (!File.Exists(path))
            return null;
        var lines = File.ReadAllLines(path);
        var start = Array.FindIndex(lines, l => l.Trim() == "NavSystem");
        if (start < 0)
            return null;
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var depth = 0;
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
                line = line[..comment];
            line = line.Trim();
            if (line == "{") { depth++; continue; }
            if (line == "}") { if (--depth == 0) break; continue; }
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^""([^""]*)""\s+""([^""]*)""");
            if (depth == 1 && m.Success)
                keys[m.Groups[1].Value] = m.Groups[2].Value;
        }
        return keys;
    }

    /// <summary>scripts/nav_hulls.vdata and scripts/nav_hulls_presets.vdata from the stock pak.</summary>
    internal static NavGenSettings.VData HullVData(string pak)
    {
        using var package = new ValvePak.Package();
        package.Read(pak);
        ValveKeyValue.KVObject Root(string name)
        {
            package.ReadEntry(package.FindEntry(name)!, out var bytes);
            using var resource = new ValveResourceFormat.Resource();
            resource.Read(new MemoryStream(bytes));
            return ((ValveResourceFormat.ResourceTypes.BinaryKV3)resource.DataBlock!).Data.Root;
        }
        static object Scalar(ValveKeyValue.KVObject v) => v.ValueType == ValveKeyValue.KVValueType.Boolean ? (bool)v : (double)v;
        var hulls = new Dictionary<string, IReadOnlyDictionary<string, object>>();
        foreach (var (name, hull) in Root("scripts/nav_hulls.vdata_c").Children)
            if (hull.ValueType == ValveKeyValue.KVValueType.Collection)
                hulls[name] = hull.Children.Where(c => c.Value.ValueType != ValveKeyValue.KVValueType.String)
                    .ToDictionary(c => c.Key, c => Scalar(c.Value));
        var presets = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var (name, preset) in Root("scripts/nav_hulls_presets.vdata_c").Children)
            if (preset.ValueType == ValveKeyValue.KVValueType.Collection && preset["m_vecNavHulls"] is { } list)
                presets[name] = [.. list.Values.Select(v => v.ToString()!)];
        return new NavGenSettings.VData(hulls, presets);
    }

    /// <summary>The settings resourcecompiler hands navsystem for a map: gameinfo, the hull vdata, the CMapNavData.</summary>
    internal static NavGenSettings MapSettings(DmxBinary.Document document, string pak)
    {
        var vdata = HullVData(pak);
        var block = NavSystemBlock(GameInfo);
        var defaults = NavGenSettings.ProjectDefaults(block is null ? null : k => block.TryGetValue(k, out var v) ? v : null, vdata);
        var navData = document.OfType("CMapNavData").Single().Get<DmxBinary.Element>("navData")!;
        return NavGenSettings.FromMap(navData, defaults, vdata);
    }

    /// <summary>
    /// The settings block and hull from the .vmap, gameinfo and the vdata against the ones navsystem received
    /// (recast_params.bin, recast_hulls.bin), every word Recast reads.
    /// </summary>
    [Fact]
    public void SettingsFromTheMapMatchValve()
    {
        if (Dir() is not { } dir || !File.Exists(Path.Combine(dir, "recast_params.bin")) || CS2Fixtures.StockPak() is not { } pak)
            return;
        var settings = MapSettings(DmxBinary.ReadFile(Path.Combine(Content, "probe01.vmap")), pak);
        var ours = settings.Bytes();
        var valve = File.ReadAllBytes(Path.Combine(dir, "recast_params.bin"));
        foreach (var o in new[] { 0, 0x40, 0x58, 0x59 })
            Assert.True(ours[o] == valve[o], $"settings +{o:x}: ours {ours[o]:x2}, Valve {valve[o]:x2}");
        for (var o = 4; o < 0x30; o++)
            Assert.True(ours[o] == valve[o], $"settings +{o:x}: ours {ours[o]:x2}, Valve {valve[o]:x2}");
        var hull = settings.Hulls[0].Bytes();
        var hulls = File.ReadAllBytes(Path.Combine(dir, "recast_hulls.bin"));
        // +0x35..0x37 and +0x3c are padding (uninitialised in Valve's vector).
        for (var o = 0; o < 0x3c; o++)
            if (o is 1 or 2 or 3 or 0x11 or 0x12 or 0x13 or 0x19 or 0x1a or 0x1b or 0x35 or 0x36 or 0x37)
                continue;
            else
                Assert.True(hull[o] == hulls[o], $"hull +{o:x}: ours {hull[o]:x2}, Valve {hulls[o]:x2}");
        output.WriteLine($"probe01: {settings.Hulls.Count} hull ({settings.Hulls[0].Name}), cell {settings.CellSize}, edge error {settings.EdgeMaxError}; block and hull are Valve's");
    }

    /// <summary>
    /// The map alone through stage 018: settle bodies, extraction, polygons, the stages before Recast,
    /// Recast with the settings from the map, gameinfo and the hull vdata, then the stages after it
    /// (MergePolys, the edge limits), each against Valve's dumps.
    /// </summary>
    [Fact]
    public void FromTheMapThroughStage18MatchesValve()
    {
        if (Dir() is not { } dir || !File.Exists(Path.Combine(dir, "005_Recast_out.bin")) || CS2Fixtures.StockPak() is not { } pak
            || MapFixtures.GameSchema() is not { } schema)
            return;
        var vmap = Path.Combine(Content, "probe01.vmap");
        var document = DmxBinary.ReadFile(vmap);
        MapPrefabs.Attach(document, MapPrefabs.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(vmap))!));
        using var models = new SettleBuildTests.PakModels(pak, AddonGame);
        var rules = new SettleWorld.CollisionRules();
        var bodies = SettleWorld.Build(document, models, schema, SmartProps.NodesCreatedOnLoad(document, MapFixtures.SmartPropLocators),
            d => SmartProps.NodesCreatedOnLoad(d, MapFixtures.SmartPropLocators), rules);
        var (verts, indices) = NavGenInput.Extract(bodies, rules);
        var g = NavGenPolys.FromTriangles(verts, indices);
        Assert.Null(Compare(g, LoadStage(Path.Combine(dir, "000_ValidateVertices_out.bin"))));
        Assert.True(NavGenStages.BeforeRecast(g));
        Assert.Null(Compare(g, LoadStage(Path.Combine(dir, "005_Recast_in.bin"))));

        var settings = MapSettings(document, pak);
        var config = RecastConfig.From(settings.Bytes(), settings.Hulls[0].Bytes(), NavGenStages.RadiusPad, 0);
        var tiles = new RecastTiles(config, RecastInput.From([.. g.Pool], [.. g.Polys.Select(p => p.Indices.ToArray())]));
        var (polys, pool) = tiles.Run();
        var recast = NavGenPolys.FromRecast(polys, pool);
        Assert.Null(Compare(recast, LoadStage(Path.Combine(dir, "005_Recast_out.bin"))));

        var oriented = document.Elements.Any(e => e.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname") == "info_nav_oriented_mesh");
        NavGenStages.AfterRecast(recast, oriented);
        Assert.Null(Compare(recast, LoadStage(Path.Combine(dir, "018_RemoveDegens_out.bin"))));
        output.WriteLine($"probe01 from the map: {verts.Count / 3} triangles in, {polys.Count} polygons out of Recast, " +
                         $"{recast.Polys.Count} polygons over {recast.Pool.Count} vertices after stage 018, all Valve's");
    }

    [Fact]
    public void StagesBeforeRecastMatchValve()
    {
        if (Dir() is not { } dir || !File.Exists(Path.Combine(dir, "001_WeldVertices_out.bin")))
            return;
        var tol = (double)0.001f;
        var weld = LoadStage(Path.Combine(dir, "000_ValidateVertices_out.bin"));
        Assert.True(weld.ValidateVertices());
        weld.Weld(tol, tol);
        Assert.Null(Compare(weld, LoadStage(Path.Combine(dir, "001_WeldVertices_out.bin"))));
        var degens = LoadStage(Path.Combine(dir, "001_WeldVertices_out.bin"));
        degens.RemoveDegens(tol);
        Assert.Null(Compare(degens, LoadStage(Path.Combine(dir, "002_RemoveDegens_out.bin"))));
        Assert.Null(Compare(LoadStage(Path.Combine(dir, "002_RemoveDegens_out.bin")), LoadStage(Path.Combine(dir, "005_Recast_in.bin"))));
    }

    [Fact]
    public void ExtractionMatchesValve()
    {
        if (Dir() is not { } dir || CS2Fixtures.StockPak() is not { } pak || MapFixtures.GameSchema() is not { } schema)
            return;
        var vmap = Path.Combine(Content, "probe01.vmap");
        var document = DmxBinary.ReadFile(vmap);
        MapPrefabs.Attach(document, MapPrefabs.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(vmap))!));
        using var models = new SettleBuildTests.PakModels(pak, AddonGame);
        var rules = new SettleWorld.CollisionRules();
        var bodies = SettleWorld.Build(document, models, schema, SmartProps.NodesCreatedOnLoad(document, MapFixtures.SmartPropLocators),
            d => SmartProps.NodesCreatedOnLoad(d, MapFixtures.SmartPropLocators), rules);
        var navclip = rules.TryLayer("navclip", out var bits) ? bits : 0ul;
        var table = Simulation.CollisionGroupTable.Build();
        var vb = File.ReadAllBytes(Path.Combine(dir, "input_0_verts.bin"));
        var want = new Vector3[vb.Length / 12];
        for (var i = 0; i < want.Length; i++)
            want[i] = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(vb.AsSpan(i * 12)), BinaryPrimitives.ReadSingleLittleEndian(vb.AsSpan(i * 12 + 4)),
                BinaryPrimitives.ReadSingleLittleEndian(vb.AsSpan(i * 12 + 8)));
        foreach (var bvhOrder in new[] { false, true })
        {
            var got = new List<Vector3>();
            foreach (var body in bodies)
            {
                var start = got.Count;
                foreach (var shape in body.Shapes)
                {
                    if (shape.Mesh is not { } mesh)
                        continue;
                    if (!NavGenInput.ShapePasses(shape.Attributes, 0, navclip, table))
                    {
                        output.WriteLine($"  filtered: node {body.NodeId} as {shape.Attributes.InteractsAs:x} with {shape.Attributes.InteractsWith:x} group {shape.Attributes.Group} flags {shape.Attributes.Flags:x}");
                        continue;
                    }
                    foreach (var t in NavGenInput.MeshTriangleOrder(mesh, bvhOrder))
                    {
                        var (a, b, c) = mesh.Triangles[t];
                        got.Add(mesh.Vertices[a] * shape.MeshScale);
                        got.Add(mesh.Vertices[b] * shape.MeshScale);
                        got.Add(mesh.Vertices[c] * shape.MeshScale);
                    }
                }
                for (var i = start; i < got.Count; i++)
                    got[i] = NavGenInput.TransformByBody(got[i], body.Position, body.Scale, body.Orientation);
                output.WriteLine($"body node {body.NodeId} at {body.Position} scale {body.Scale} q {body.Orientation}: {(got.Count - start) / 3} triangles, mesh flags {string.Join(",", body.Shapes.Select(s => s.Mesh?.Flags))}");
            }
            var same = 0;
            var first = -1;
            for (var i = 0; i < Math.Min(got.Count, want.Length); i++)
                if (got[i] == want[i] && BitConverter.SingleToInt32Bits(got[i].X) == BitConverter.SingleToInt32Bits(want[i].X))
                    same++;
                else if (first < 0)
                    first = i;
            output.WriteLine($"bvh order {bvhOrder}: {got.Count} vertices against {want.Length}, {same} equal, first difference at {first}" +
                (first >= 0 ? $": ours {got[first]}, Valve {want[first]}" : ""));
        }
    }
}
