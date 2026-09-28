using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// What visibility takes from outside its .rte, set beside what the map gives:
/// the file's own box, the bounds of its triangles and of the traced ones, and
/// the shipped VXVS's bounds and grid. Behind <c>VISOWN=1</c>.
/// </summary>
public class VisOwnProbe(ITestOutputHelper output)
{
    [Theory]
    [InlineData("s2c_rc_probe", "probe01")]
    [InlineData("s2c_rc_probe", "cardtest")]
    [InlineData("s2c_lighting", "ze_hold_em_p")]
    [InlineData("s2c_big", "ze_ffvii_mako_reactor_v6_p")]
    public void Bounds(string addon, string map)
    {
        if (Environment.GetEnvironmentVariable("VISOWN") != "1" || VisFixtures.RayTraceScene(addon, map) is not var (rte, shipped))
            return;
        Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
        for (var i = 0; i < rte.TriangleCount; i++)
            if (rte.Vertices(i) is { } v)
                foreach (var p in v)
                {
                    lo = Vector3.Min(lo, p);
                    hi = Vector3.Max(hi, p);
                }
        var (tlo, thi) = rte.TracedBounds;
        output.WriteLine($"{map}: header {rte.Mins} {rte.Maxs}");
        output.WriteLine($"  all triangles {lo} {hi}");
        output.WriteLine($"  traced {tlo} {thi}");
        output.WriteLine($"  shipped {shipped.MinBounds} {shipped.MaxBounds} grid {shipped.GridSize}");
        var (min, max) = VisVoxelizer.RootCube(tlo, thi, shipped.GridSize);
        output.WriteLine($"  root cube {min} {max}");
        Assert.Equal(shipped.MinBounds, min);
        Assert.Equal(shipped.MaxBounds, max);
    }
}

public class VisConfigDump(ITestOutputHelper output)
{
    [Theory]
    [InlineData("s2c_rc_probe", "probe01")]
    [InlineData("s2c_rc_probe", "cardtest")]
    [InlineData("s2c_lighting", "ze_hold_em_p")]
    [InlineData("s2c_big", "ze_ffvii_mako_reactor_v6_p")]
    public void Keys(string addon, string map)
    {
        var path = Path.Combine(Path.GetTempPath(), "csgo_addons", addon, "maps", map + ".viscfg");
        if (Environment.GetEnvironmentVariable("VISOWN") != "1" || !File.Exists(path))
            return;
        using var reader = new BinaryReader(File.OpenRead(path));
        using var owner = new ValveResourceFormat.Resource();
        var kv = new ValveResourceFormat.ResourceTypes.BinaryKV3 { Resource = owner };
        kv.Read(reader);
        output.WriteLine($"{map}:\n{ValveResourceFormat.Serialization.KeyValues.KVDocumentExtensions.ToKV3String(kv.Data)}");
    }
}
