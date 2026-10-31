using System.Numerics;
using System.Text.Json.Nodes;
using Source2.Compiler.Cubemaps;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="BarnLightBounds"/> against the boxes 1800ce950 stores, captured in cubehost
/// (<c>CUBELIGHTBOX=&lt;frida.jsonl of tools/cubehost/light_radius.js&gt;</c>, <c>CUBELIGHTBOX_VPK</c> the map's
/// package): each captured object is found by its transform (a light_barn's AngleMatrix), and its stored min and max
/// (after the snap) must be ours bit for bit. The captures' objects had +0x251 clear when their bounds were made,
/// so the illumination clip is not exercised here.
/// </summary>
public sealed class BarnLightBoundsTests(ITestOutputHelper output)
{
    [Fact]
    public void BoundsEqualTheCapture()
    {
        if (Environment.GetEnvironmentVariable("CUBELIGHTBOX") is not { Length: > 0 } path
            || Environment.GetEnvironmentVariable("CUBELIGHTBOX_VPK") is not { Length: > 0 } vpk)
            return;
        var lights = new List<(LightPrecompute.KeyReader Key, float[] Transform)>();
        using (var package = new ValvePak.Package())
        {
            package.Read(vpk);
            foreach (var entry in (package.Entries!.GetValueOrDefault("vents_c") ?? []).OrderBy(e => e.GetFullPath(), StringComparer.Ordinal))
            {
                package.ReadEntry(entry, out var bytes);
                foreach (var (_, key) in Lighting.Vrad3Job.LumpEntities(bytes, entry.GetFullPath()).Where(e => e.Class == "light_barn"))
                    lights.Add((key, BarnLightDescriptions.Transform(key)));
            }
        }
        var boxes = File.ReadLines(path).Select(l => JsonNode.Parse(l)!).Where(n => (string?)n["kind"] == "box" && n["transform"] is not null).ToList();
        var checkedLights = new HashSet<int>();
        var failures = new List<string>();
        foreach (var b in boxes)
        {
            var xf = b["transform"]!.AsArray().Select(v => (float)v!).ToArray();
            var light = lights.FindIndex(l => l.Transform.SequenceEqual(xf));
            if (light < 0 || !checkedLights.Add(light))
                continue;
            Assert.Equal(0, (int)b["illumination"]!);
            var box = b["box"]!.AsArray().Select(v => (float)v!).ToArray();
            var (min, max) = BarnLightBounds.Frustum(BarnLightDescriptions.Description(lights[light].Key), lights[light].Transform);
            var (sMin, sMax) = BarnLightBounds.Snap(min, max);
            float[] ours = [sMin.X, sMin.Y, sMin.Z, sMax.X, sMax.Y, sMax.Z];
            float[] theirs = [box[0], box[1], box[2], box[4], box[5], box[6]];
            if (!ours.Select(BitConverter.SingleToInt32Bits).SequenceEqual(theirs.Select(BitConverter.SingleToInt32Bits)))
            {
                failures.Add($"light {light}: ours ({string.Join(", ", ours)}) from ({min}, {max}), game ({string.Join(", ", theirs)})");
                output.WriteLine(failures[^1]);
            }
        }
        output.WriteLine($"{checkedLights.Count} light_barn boxes checked, {failures.Count} differ");
        Assert.NotEmpty(checkedLights);
        Assert.Empty(failures);
    }
}
