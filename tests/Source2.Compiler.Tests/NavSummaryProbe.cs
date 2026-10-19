using ValveResourceFormat.NavMesh;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>NAVSUMMARY=&lt;.vpk&gt;|&lt;path in it&gt;[|&lt;out .nav&gt;]</c>): a packaged
/// .nav read with VRF's NavMeshFile: the generation parameters, the hulls, and each area's
/// corners, connections and attributes. With a third part the raw file is saved too.
/// </summary>
public class NavSummaryProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("NAVSUMMARY") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        using var package = new ValvePak.Package();
        package.Read(p[0]);
        package.ReadEntry(package.FindEntry(p[1])!, out var bytes);
        if (p.Length > 2)
            File.WriteAllBytes(p[2], bytes);
        var nav = new NavMeshFile();
        nav.Read(new MemoryStream(bytes));
        output.WriteLine($"{bytes.Length} bytes, version {nav.Version}.{nav.SubVersion}, {nav.Areas.Count} areas, {nav.Ladders.Length} ladders, analyzed {nav.IsAnalyzed}");
        if (nav.GenerationParams is { } g)
        {
            output.WriteLine($"gen v{g.NavGenVersion} defaults {g.UseProjectDefaults} tile {g.TileSize} cell {g.CellSize}x{g.CellHeight} region {g.MinRegionSize}/{g.MergedRegionSize} sample {g.MeshSampleDistance}/{g.MaxSampleError} edge {g.MaxEdgeLength}/{g.MaxEdgeError} verts/poly {g.VertsPerPoly} small-area {g.SmallAreaOnEdgeRemoval} preset {g.HullPresetName} hulls {g.HullCount}");
            foreach (var h in g.HullParams)
                output.WriteLine($"  hull enabled {h.Enabled} r {h.Radius} h {h.Height} short {h.ShortHeightEnabled}/{h.ShortHeight} crawl {h.AgentCrawlEnabled}/{h.AgentCrawlHeight} climb {h.MaxClimb} slope {h.MaxSlope} jump down {h.MaxJumpDownDist} horiz {h.MaxJumpHorizDistBase} up {h.MaxJumpUpDist} erosion {h.BorderErosion}");
        }
        var limit = int.TryParse(Environment.GetEnvironmentVariable("NAVSUMMARY_N"), out var n) ? n : 12;
        foreach (var (id, a) in nav.Areas.Take(limit))
            output.WriteLine($"area {id} hull {a.HullIndex} attr {a.AttributeFlags} corners [{string.Join(" ", a.Corners.Select(c => $"({c.X},{c.Y},{c.Z})"))}] links [{string.Join(" | ", a.Connections.Select(e => string.Join(",", e.Select(c => $"{c.AreaId}:{c.EdgeId}"))))}]");
        output.WriteLine($"corner counts: {string.Join(", ", nav.Areas.Values.GroupBy(a => a.Corners.Length).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}"))}");
    }
}
