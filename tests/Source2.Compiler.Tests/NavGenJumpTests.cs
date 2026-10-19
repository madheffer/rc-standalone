using System.Buffers.Binary;
using System.Numerics;
using Source2.Compiler.Maps;
using Source2.Compiler.Maps.NavGen;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>AddJumps (026) from Valve's own input against Valve's output.</summary>
public sealed class NavGenJumpTests(ITestOutputHelper output)
{
    private const string Content = @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\content\csgo_addons\s2c_rc_probe\maps\";

    internal static NavTriangleWorld InputWorld(string dir)
    {
        var vb = File.ReadAllBytes(Path.Combine(dir, "input_0_verts.bin"));
        var verts = new Vector3[vb.Length / 12];
        for (var i = 0; i < verts.Length; i++)
            verts[i] = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(vb.AsSpan(i * 12)), BinaryPrimitives.ReadSingleLittleEndian(vb.AsSpan(i * 12 + 4)),
                BinaryPrimitives.ReadSingleLittleEndian(vb.AsSpan(i * 12 + 8)));
        var tb = File.ReadAllBytes(Path.Combine(dir, "input_0_tris.bin"));
        var tris = new int[tb.Length / 4];
        for (var i = 0; i < tris.Length; i++)
            tris[i] = BinaryPrimitives.ReadInt32LittleEndian(tb.AsSpan(i * 4));
        return new NavTriangleWorld(verts, tris);
    }

    private static NavGenPolys? Load(string dir, string side)
    {
        var path = Path.Combine(dir, $"026_AddJumps_{side}.bin");
        var conn = Path.Combine(dir, $"026_AddJumps_{side}_conn.bin");
        if (!File.Exists(path) || !File.Exists(conn))
            return null;
        var g = NavGenInputTests.LoadStage(path);
        var blocks = NavGenConnectionTests.LoadBlocks(conn);
        for (var i = 0; i < blocks.Count; i++)
            g.Polys[i].Links = NavGenConnectionTests.ToLinks(blocks[i]);
        return g;
    }

    [Theory]
    [InlineData("probe01")]
    [InlineData("cardtest")]
    [InlineData("probe_classes")]
    [InlineData("s2c_prefabprobe5")]
    [InlineData("s2c_prefabprobe6")]
    public void AddJumpsMatchesValve(string map)
    {
        var dir = Path.Combine(Path.GetTempPath(), "navgen_capture", map + "_in");
        if (Load(dir, "in") is not { } g || Load(dir, "out") is not { } valve || CS2Fixtures.StockPak() is not { } pak)
            return;
        var settings = NavGenInputTests.MapSettings(DmxBinary.ReadFile(Path.Combine(Content, map + ".vmap")), pak);
        var world = InputWorld(dir);
        var before = g.Polys.Sum(p => p.Links?.Count ?? 0);
        g.AddJumps(settings.Hulls[0], world, new NavGenPolys.JumpSettings());
        var blocks = NavGenConnectionTests.LoadBlocks(Path.Combine(dir, "026_AddJumps_out_conn.bin"));
        var diffs = new List<string>();
        for (var i = 0; i < g.Polys.Count; i++)
            if (NavGenConnectionTests.Compare(g.Polys[i].Links, blocks[i], flag61: false) is { } diff)
                diffs.Add($"polygon {i}: {diff}");
        output.WriteLine($"{map}: {before} -> {g.Polys.Sum(p => p.Links?.Count ?? 0)} connections (Valve {valve.Polys.Sum(p => p.Links?.Count ?? 0)}), {world.Casts} casts, {world.Blocked} blocked");
        foreach (var d in diffs.Take(10))
            output.WriteLine(d);
        Assert.Empty(diffs);
    }
}
