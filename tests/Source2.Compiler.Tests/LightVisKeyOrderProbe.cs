using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>LVKORDER=1</c>): where ze_hold_em_p's lump puts
/// precomputed_vis_clusters among a light's values.
/// </summary>
public class LightVisKeyOrderProbe(ITestOutputHelper output)
{
    [Fact]
    public void Order()
    {
        if (Environment.GetEnvironmentVariable("LVKORDER") != "1"
            || MapFixtures.VmapSource("s2c_lighting", "ze_hold_em_p") is not { } source || MapFixtures.RcCompiledLumps(source) is not { } lumps)
            return;
        var tally = new SortedDictionary<string, int>();
        foreach (var (path, bytes) in lumps)
            foreach (var e in EntityLumpComparison.Read(bytes, path).Where(e => e.Values.ContainsKey("precomputed_vis_clusters")))
            {
                var at = e.KeyOrder.ToList().IndexOf("precomputed_vis_clusters");
                var after = string.Join(" ", e.KeyOrder.Skip(at + 1));
                var key = $"{e.ClassName}: {at} of {e.KeyOrder.Count}, before [{string.Join(" ", e.KeyOrder.Skip(Math.Max(0, at - 3)).Take(3))}], after [{after}]";
                tally[key] = tally.GetValueOrDefault(key) + 1;
            }
        foreach (var (k, v) in tally)
            output.WriteLine($"{v,4} {k}");
    }
}
