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
        }
        return null;
    }

    /// <summary>
    /// The map alone through the Recast stage: settle bodies, extraction, polygons, the stages before
    /// Recast, then Recast with the captured settings, each against Valve's dumps.
    /// </summary>
    [Fact]
    public void FromTheMapThroughRecastMatchesValve()
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
        var tol = (double)0.001f;
        Assert.True(g.ValidateVertices());
        g.Weld(tol, tol);
        g.RemoveDegens(tol);
        Assert.Null(Compare(g, LoadStage(Path.Combine(dir, "005_Recast_in.bin"))));

        var tiles = NavGenRecastTests.Load(dir, g);
        var (polys, pool) = tiles.Run();
        var valve = LoadStage(Path.Combine(dir, "005_Recast_out.bin"));
        Assert.Equal(valve.Polys.Count, polys.Count);
        Assert.Equal(valve.Pool.Count, pool.Count);
        for (var i = 0; i < polys.Count; i++)
            Assert.Equal(valve.Polys[i].Indices, [polys[i].A, polys[i].B, polys[i].C]);
        for (var i = 0; i < pool.Count; i++)
            Assert.Equal(valve.Pool[i], pool[i]);
        output.WriteLine($"probe01 from the map: {verts.Count / 3} triangles in, {polys.Count} polygons over {pool.Count} vertices out of Recast, all Valve's");
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
