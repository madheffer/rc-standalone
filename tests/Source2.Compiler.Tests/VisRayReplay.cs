using System.Numerics;
using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The sampler's rays, one by one, against what Valve's tracer returned for the
/// same origin and direction. <c>tools/vis/capture_rays.py</c> records them.
/// Behind <c>RAYS=&lt;map&gt;</c>.
/// </summary>
public class VisRayReplay(ITestOutputHelper output)
{
    [Fact]
    public void EachRayAgainstValvesTracer()
    {
        if (Environment.GetEnvironmentVariable("RAYS") is not { Length: > 0 } map)
            return;
        var addon = map == "ze_hold_em_p" ? "s2c_lighting" : "s2c_rc_probe";
        var rte = RayTraceEnvironment.ReadFile(
            Path.Combine(Path.GetTempPath(), "csgo_addons", addon, "maps", map + ".rte"));
        var lines = File.ReadAllLines(Path.Combine(Path.GetTempPath(), "vis_capture", map + ".rays.jsonl"));

        for (var l = 0; l + 1 < lines.Length; l += 2)
        {
            var recs = Convert.FromHexString(JsonDocument.Parse(lines[l]).RootElement.GetProperty("hex").GetString()!);
            var hits = Convert.FromHexString(JsonDocument.Parse(lines[l + 1]).RootElement.GetProperty("hex").GetString()!);
            var n = recs.Length / 0x20;
            int same = 0, shown = 0;
            for (var i = 0; i < n; i++)
            {
                var o = V(recs, i * 0x20);
                var d = V(recs, i * 0x20 + 12);
                var dist = BitConverter.ToSingle(recs, i * 0x20 + 0x18);
                var flags = recs[i * 0x20 + 0x1e];
                var rawId = BitConverter.ToUInt32(hits, i * 0x38 + 0xc);
                var rawDist = BitConverter.ToSingle(hits, i * 0x38 + 0x10);
                var rawN = V(hits, i * 0x38);

                var end = new Vector3((RayTraceEnvironment.MaxCoord * d.X) + o.X,
                                      (RayTraceEnvironment.MaxCoord * d.Y) + o.Y,
                                      (RayTraceEnvironment.MaxCoord * d.Z) + o.Z);
                var ours = rte.Segment(o, end, VisSeed.Ignored);
                var valveHit = rawId != 0xffffffff;
                var agree = valveHit
                    ? ours is { } h && BitConverter.SingleToInt32Bits(h.Distance) == BitConverter.SingleToInt32Bits(rawDist)
                    : ours is null;
                if (agree)
                {
                    same++;
                    continue;
                }
                if (shown++ < 12)
                    output.WriteLine($"ray {i} dir {d:R}: valve flags {flags:x} dist {dist:R}"
                                   + $" raw id {rawId:x} dist {rawDist:R} n {rawN} | ours "
                                   + (ours is { } x ? $"tri {x.Triangle} dist {x.Distance:R} n {x.Normal}" : "miss")
                                   + (rte.SegmentBruteForce(o, end, VisSeed.Ignored) is { } bf ? $" | brute tri {bf.Triangle} dist {bf.Distance:R}" : " | brute miss"));
            }
            output.WriteLine($"centre {V(recs, 0)}: {same}/{n} rays agree");
            if (n == VisClusterSample.SphereDirections)
            {
                var exact = Enumerable.Range(0, n).Count(i => V(recs, i * 0x20 + 12) == VisClusterSample.Sphere[i]);
                output.WriteLine($"sphere directions bit-exact: {exact}/{n}");
                foreach (var i in Enumerable.Range(0, n).Where(i => V(recs, i * 0x20 + 12) != VisClusterSample.Sphere[i]).Take(3))
                    output.WriteLine($"  dir {i}: valve {V(recs, i * 0x20 + 12):R} ours {VisClusterSample.Sphere[i]:R}");
            }
        }
    }

    private static Vector3 V(byte[] b, int at)
        => new(BitConverter.ToSingle(b, at), BitConverter.ToSingle(b, at + 4), BitConverter.ToSingle(b, at + 8));
}
