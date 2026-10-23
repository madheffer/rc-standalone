using Source2.Compiler.Physics;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The CMeshes <see cref="WorldCollision"/> hands the simplifier, in order,
/// against the pieces physicsbuilder welded and simplified in Valve's compile
/// (<c>tools/physics/capture_simplify.py</c>):
/// <c>SIMPLIFY_INPUTS=&lt;addon&gt;|&lt;map&gt;|&lt;capture&gt;</c>.
/// </summary>
public class SimplifyInputs(ITestOutputHelper output)
{
    [Fact]
    public void PiecesMatchTheCapture()
    {
        if (Environment.GetEnvironmentVariable("SIMPLIFY_INPUTS") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        var game = Path.Combine(cs2, "game");
        var vmap = Path.Combine(cs2, "content", "csgo_addons", parts[0], "maps", parts[1] + ".vmap");
        using var models = new SettleBuildTests.PakModels(Path.Combine(game, "csgo", "pak01_dir.vpk"), Path.Combine(game, "csgo_addons", parts[0]));
        var ours = new List<(float[] V, int Stride, int[] I, float Error)>();
        WorldCollision.SimplifyTrace = (v, stride, i, e) => ours.Add((v, stride, i, e));
        try
        {
            WorldCollision.Pieces(DmxBinary.ReadFile(vmap), n => WorldCollision.ReadMaterial(n, models.Material, models.CollisionProperty), null, null, models.Physics, models.SmartProp, models.CollisionProperty);
        }
        finally
        {
            WorldCollision.SimplifyTrace = null;
        }
        var valve = SimplifyReplay.Records(parts[2]).Where(r => r.Head.GetProperty("ev").GetString() == "weld").ToList();
        output.WriteLine($"{ours.Count} ours, {valve.Count} Valve's");
        var exact = 0;
        for (var k = 0; k < Math.Max(ours.Count, valve.Count); k++)
        {
            if (k >= ours.Count || k >= valve.Count)
            {
                output.WriteLine($"piece {k}: only {(k < ours.Count ? "ours" : "Valve's")}");
                continue;
            }
            var (vh, vb) = valve[k];
            var (vv, vi) = SimplifyReplay.Mesh(vh, vb);
            var stride = vh.GetProperty("stride").GetInt32();
            var o = ours[k];
            var same = o.Stride == stride || vv.Length == 0;
            same &= o.I.AsSpan().SequenceEqual(vi) && o.V.Length == vv.Length;
            var firstDiff = -1;
            for (var x = 0; same && x < vv.Length; x++)
                if (BitConverter.SingleToInt32Bits(o.V[x]) != BitConverter.SingleToInt32Bits(vv[x]))
                {
                    firstDiff = x;
                    same = false;
                }
            // Positions alone, whatever the streams.
            var vn = vv.Length / Math.Max(stride, 1);
            var on = o.V.Length / Math.Max(o.Stride, 1);
            var positionsSame = vn == on && Enumerable.Range(0, vn).All(x => Enumerable.Range(0, 3).All(c =>
                BitConverter.SingleToInt32Bits(vv[(x * stride) + c]) == BitConverter.SingleToInt32Bits(o.V[(x * o.Stride) + c])));
            output.WriteLine($"piece {k}: error {o.Error} vs {vh.GetProperty("id")}, positions {(positionsSame ? "same" : "differ")}");
            if (same)
                exact++;
            else
                output.WriteLine($"piece {k}: ours {o.V.Length / Math.Max(o.Stride, 1)} vertices stride {o.Stride} {o.I.Length} indices, Valve {vv.Length / Math.Max(stride, 1)} stride {stride} {vi.Length}"
                                 + (firstDiff >= 0 ? $"; first float {firstDiff} (vertex {firstDiff / stride} stream float {firstDiff % stride}) {o.V[firstDiff]:R} vs {vv[firstDiff]:R}" : $"; indices same {o.I.AsSpan().SequenceEqual(vi)}"));
        }
        output.WriteLine($"{exact} of {valve.Count} exact");
        Assert.Equal(valve.Count, exact);
        Assert.Equal(valve.Count, ours.Count);
    }
}
