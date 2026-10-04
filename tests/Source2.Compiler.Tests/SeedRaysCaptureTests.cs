using System.Numerics;
using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The seed's rays ray by ray against Valve's hits (capture_outside.py
/// --rays): SEEDRAYS=&lt;map&gt; with %TEMP%/vis_capture/&lt;map&gt;.rays.outside.bin
/// and the .rte that compile traced. For each ray, the triangle and distance
/// Valve's tracer gave against ours on the file's kd tree and the rebuilt one.
/// </summary>
public class SeedRaysCaptureTests(ITestOutputHelper output)
{
    [Fact]
    public void RayByRay()
    {
        if (Environment.GetEnvironmentVariable("SEEDRAYS") is not { Length: > 0 } map)
            return;
        var stem = Path.Combine(Path.GetTempPath(), "vis_capture", map + ".rays.outside");
        var file = RayTraceEnvironment.ReadFile(stem + ".rte");
        var rebuilt = file.WithTracerTree();
        using var reader = new BinaryReader(File.OpenRead(stem + ".bin"));
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var head = JsonDocument.Parse(reader.ReadBytes(reader.ReadInt32())).RootElement;
            var blob = reader.ReadBytes(reader.ReadInt32());
            if (head.GetProperty("ev").GetString() != "rays")
                continue;
            var n = head.GetProperty("count").GetInt32();
            Vector3 V(int at) => new(BitConverter.ToSingle(blob, at), BitConverter.ToSingle(blob, at + 4), BitConverter.ToSingle(blob, at + 8));
            int differFile = 0, differRebuilt = 0;
            var lines = new List<string>();
            for (var i = 0; i < n; i++)
            {
                var origin = V(i * 0x20);
                var direction = V(i * 0x20 + 0xc);
                var hitAt = n * 0x20 + i * 56;
                var id = BitConverter.ToUInt32(blob, hitAt + 0xc);
                var t = BitConverter.ToSingle(blob, hitAt + 0x10);
                var normal = V(hitAt);
                var reach = (file.Maxs - file.Mins).Length();
                var a = file.Trace(origin, direction, reach, VisSeed.Ignored);
                var b = rebuilt.Trace(origin, direction, reach, VisSeed.Ignored);
                string Show(RayTraceEnvironment.Hit? h) => h is { } x ? $"{x.Triangle} t {x.Distance:R} n {x.Normal}" : "none";
                var valve = id == uint.MaxValue ? "none" : $"{id} t {t:R} n {normal}";
                var fileSame = (a is null && id == uint.MaxValue) || (a is { } ha && ha.Triangle == id);
                var rebuiltSame = (b is null && id == uint.MaxValue) || (b is { } hb && hb.Triangle == id);
                if (!fileSame)
                    differFile++;
                if (!rebuiltSame)
                    differRebuilt++;
                if (!fileSame || !rebuiltSame)
                    lines.Add($"  ray {i} from {origin} dir {direction}: Valve's {valve}; file {Show(a)}; rebuilt {Show(b)}");
            }
            output.WriteLine($"box {head.GetProperty("box")}: {n} rays, differing on the file tree {differFile}, on the rebuilt tree {differRebuilt}");
            foreach (var l in lines.Take(12))
                output.WriteLine(l);
        }
    }
}
