using System.Buffers.Binary;
using Source2.Compiler.Maps.NavGen;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// MergePolys (navsystem 180073ba0) call by call against tools/re/capture_navgen.py's m* dumps
/// (<c>NAVGEN_MERGE=&lt;dir&gt;</c>, default %TEMP%/navgen_capture/probe01_merge): each inner pass run on
/// Valve's input and compared with Valve's output.
/// </summary>
public sealed class NavGenMergeTests(ITestOutputHelper output)
{
    private static string? Dir()
    {
        var dir = Environment.GetEnvironmentVariable("NAVGEN_MERGE") ?? Path.Combine(Path.GetTempPath(), "navgen_capture", "probe01_merge");
        return File.Exists(Path.Combine(dir, "m0000_Weld_in.bin")) ? dir : null;
    }

    /// <summary>A dump of 0x168-byte polygons (int32 count, vertex count, record size, records, pool).</summary>
    internal static NavGenPolys LoadWide(string path)
    {
        var b = File.ReadAllBytes(path);
        int n = BinaryPrimitives.ReadInt32LittleEndian(b), nv = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4)),
            size = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(8));
        var g = new NavGenPolys();
        for (var i = 0; i < n; i++)
        {
            var at = 12 + i * size;
            var count = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at));
            g.Polys.Add(new NavPoly
            {
                Indices = [.. Enumerable.Range(0, count).Select(k => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at + 0x68 + k * 4)))],
                Removed = b[at + 8] != 0,
                Kind = b[at + 0x40],
                Flags = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at + 0x44)),
                Nx = BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(at + 0x48)),
                Ny = BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(at + 0x50)),
                Nz = BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(at + 0x58)),
                Field60 = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(at + 0x60)),
                Field62 = b[at + 0x62],
            });
        }
        var vo = 12 + n * size;
        for (var i = 0; i < nv; i++)
            g.Pool.Add((BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(vo + i * 24)), BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(vo + i * 24 + 8)),
                BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(vo + i * 24 + 16))));
        return g;
    }

    internal static List<NavGenPolys.Edge> LoadEdges(string path)
    {
        var b = File.ReadAllBytes(path);
        var n = BinaryPrimitives.ReadInt32LittleEndian(b);
        var list = new List<NavGenPolys.Edge>();
        for (var i = 0; i < n; i++)
        {
            var at = 4 + i * 0x20;
            int I(int o) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at + o));
            list.Add(new NavGenPolys.Edge(BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(at)), I(8), I(0xc), I(0x10), I(0x14), I(0x18), b[at + 0x1c] != 0));
        }
        return list;
    }

    private static string? CompareEdges(List<NavGenPolys.Edge> ours, List<NavGenPolys.Edge> valve)
    {
        if (ours.Count != valve.Count)
            return $"edge count: ours {ours.Count}, Valve {valve.Count}";
        for (var i = 0; i < ours.Count; i++)
        {
            var (a, b) = (ours[i], valve[i]);
            if (BitConverter.DoubleToInt64Bits(a.Len2) != BitConverter.DoubleToInt64Bits(b.Len2) || a.Min != b.Min || a.Max != b.Max
                || a.P0 != b.P0 || a.Count != b.Count || (a.Count == 2 && a.P1 != b.P1))
                return $"edge {i}: ours {a}, Valve {b}";
        }
        return null;
    }

    private static NavGenPolys With(NavGenPolys g, List<NavPoly> polys) => new() { Polys = polys, Pool = g.Pool };

    /// <summary>The round tolerances of 180073ba0: weld from 0 and merge from 1e-5, stepped over six rounds.</summary>
    private static (double Weld, double Merge) Round(int r)
    {
        double merge = 1e-5f, weld = 0f;
        for (var i = 0; i < r; i++)
        {
            merge += ((double)0.1f - (double)1e-5f) / 6;
            weld += ((double)0.01f - (double)0f) / 6;
        }
        return (weld, merge);
    }

    /// <summary>The stages between Recast and MergePolys (006-012) and after LimitMaxPolyEdgeLen (016-018), each from Valve's input.</summary>
    [Fact]
    public void StagesAroundMergeMatchValve()
    {
        var dir = Environment.GetEnvironmentVariable("NAVGEN_INPUT") ?? Path.Combine(Path.GetTempPath(), "navgen_capture", "probe01_in");
        if (!File.Exists(Path.Combine(dir, "006_UnTransformRecastNonZup_in.bin")))
            return;
        var tol = (double)NavGenStages.WeldTolerance;
        foreach (var (stage, run) in new (string, Action<NavGenPolys>)[]
        {
            ("006_UnTransformRecastNonZup", _ => { }),
            ("007_WeldVertices", g => g.Weld(tol, tol)),
            ("008_RemoveDegens", g => g.RemoveDegens(tol)),
            ("009_RemovePolysMarkedNoNav", _ => { }),
            ("010_RemoveStrayVerticalPolys", g => g.RemoveStrayVerticalPolys(NavGenStages.StrayVerticalAngle, false)),
            ("011_WeldVertices", g => g.Weld(tol, tol)),
            ("012_RemoveDegens", g => g.RemoveDegens(tol)),
            ("014_LimitMaxPolyEdges", g => g.Polys = g.SplitToFans(4)),
            ("015_LimitMaxPolyEdgeLen", g => g.LimitMaxPolyEdgeLen(NavGenStages.EdgeLenLimitB, NavGenStages.EdgeLenLimitA)),
            ("016_RemoveStrayVerticalPolys", g => g.RemoveStrayVerticalPolys(NavGenStages.StrayVerticalAngle, false)),
            ("017_WeldVertices", g => g.Weld(tol, tol)),
            ("018_RemoveDegens", g => g.RemoveDegens(tol)),
        })
        {
            var g = NavGenInputTests.LoadStage(Path.Combine(dir, stage + "_in.bin"));
            run(g);
            Assert.True(NavGenInputTests.Compare(g, NavGenInputTests.LoadStage(Path.Combine(dir, stage + "_out.bin"))) is null, stage);
        }
    }

    /// <summary>MergePolys whole from stage 013's input, every inner call against its dump, then the stage output.</summary>
    [Fact]
    public void MergePolysMatchesValve()
    {
        var stages = Environment.GetEnvironmentVariable("NAVGEN_INPUT") ?? Path.Combine(Path.GetTempPath(), "navgen_capture", "probe01_in");
        if (Dir() is not { } dir || !File.Exists(Path.Combine(stages, "013_MergePolys_in.bin")))
            return;
        var calls = Directory.GetFiles(dir, "m*_out.bin").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        var g = NavGenInputTests.LoadStage(Path.Combine(stages, "013_MergePolys_in.bin"));
        var at = 0;
        g.MergePolys(false, (name, now) =>
        {
            Assert.True(at < calls.Count, $"extra call {name}");
            var file = calls[at++]!;
            Assert.EndsWith("_" + name + "_out.bin", file);
            var diff = NavGenInputTests.Compare(now, LoadWide(Path.Combine(dir, file)));
            Assert.True(diff is null, $"{file}: {diff}");
        });
        Assert.Equal(calls.Count, at);
        Assert.Null(NavGenInputTests.Compare(g, NavGenInputTests.LoadStage(Path.Combine(stages, "013_MergePolys_out.bin"))));
        output.WriteLine($"MergePolys: {at} inner calls, {g.Polys.Count} polygons over {g.Pool.Count} vertices, Valve's");
    }

    [Fact]
    public void FirstRoundPassesMatchValve()
    {
        if (Dir() is not { } dir)
            return;
        string P(string name) => Path.Combine(dir, name + ".bin");
        var (weld, tol) = Round(0);
        foreach (var w in new[] { "m0000_Weld", "m0011_Weld", "m0014_Weld", "m0019_Weld" })
        {
            var g = LoadWide(P(w + "_in"));
            g.Weld(weld, weld);
            Assert.Null(NavGenInputTests.Compare(g, LoadWide(P(w + "_out"))));
        }
        foreach (var d in new[] { "m0001_Degens", "m0012_Degens", "m0015_Degens" })
        {
            var g = LoadWide(P(d + "_in"));
            g.RemoveDegens(tol);
            Assert.Null(NavGenInputTests.Compare(g, LoadWide(P(d + "_out"))));
        }
        foreach (var (m, firstEdges) in new[] { ("m0002_MergeEdges", 3), ("m0016_MergeEdges", 17) })
        {
            var g = LoadWide(P(m + "_in"));
            var pass = 0;
            var result = g.MergeEdges(8, 4, tol, edges =>
            {
                var path = P($"m{firstEdges + pass:d4}_edges");
                pass++;
                Assert.Null(CompareEdges(edges, LoadEdges(path)));
            });
            Assert.Null(NavGenInputTests.Compare(With(g, result), LoadWide(P(m + "_out"))));
            output.WriteLine($"{m}: {pass} edge passes, {g.Polys.Count(p => !p.Removed)} polygons, Valve's");
        }
        foreach (var m in new[] { "m0013_MergeVerts", "m0021_MergeVerts", "m0028_MergeVerts" })
        {
            var g = LoadWide(P(m + "_in"));
            var result = g.MergeVerts(8, tol);
            Assert.True(NavGenInputTests.Compare(With(g, result), LoadWide(P(m + "_out"))) is not { } diff, $"{m}: {NavGenInputTests.Compare(With(g, result), LoadWide(P(m + "_out")))}");
        }
    }
}
