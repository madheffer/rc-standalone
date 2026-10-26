using System.Numerics;
using System.Text.Json.Nodes;
using Source2.Compiler.Cubemaps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="LightBinnerItems"/> against the captured inputs of the light binner's cull dispatches.
/// <c>CUBEBIN=&lt;capture dir&gt;[;...]</c>, each holding replay_face0 .. 5 (rdc_replay.py dumps), with
/// <c>CUBEBIN_CUBE</c> the cube (probe01's volumes; s2c_l5probe has the same). For each face: the visible volumes
/// (<see cref="CubemapFaceConstants.VisibleVolumes"/>), their footprints and the packed item records and edge lines
/// must equal the buffers the dispatches read (set 0 binding 30 of the depth-bin pass: the records; binding 30 of the
/// tile pass: the edges), word for word over what we write; past it the buffers hold earlier uploads.
/// </summary>
public sealed class LightBinnerItemsTests(ITestOutputHelper output)
{
    private static readonly CubemapFaceConstants.LightProbeVolume[] Volumes =
    [
        new(new(512, 96, 112), new(-768, -1088, -112), new(768, 896, 176), new(0, 32, 0)),
        new(new(512, 1104, 112), new(-576, -144, -112), new(640, 336, 144), new(0, 32, 0), 1),
    ];

    [Fact]
    public void ItemsEqualTheCapture()
    {
        if (Environment.GetEnvironmentVariable("CUBEBIN") is not { Length: > 0 } dirs)
            return;
        var failures = new List<string>();
        foreach (var spec in dirs.Split(';'))
        {
            var parts = spec.Split('@');
            var dir = parts[0];
            var cube = parts.Length > 1 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
            for (var f = 0; f < 6; f++)
            {
                var faceDir = Path.Combine(dir, $"replay_face{f}");
                var (records, edges) = Captured(faceDir);
                var view = CubemapFaceViews.Face(Volumes[cube].Origin, Vector3.Zero, 1f, 10000f, f);
                var visible = CubemapFaceConstants.VisibleVolumes(AggregateCulling.FaceFrustum(view), Volumes);
                var bin = new LightBinnerItems.View(view.Origin, new Vector3(view.CameraToWorld[0], view.CameraToWorld[4], view.CameraToWorld[8]),
                    AggregateCulling.WorldToProjection(view), 1024, 1024, 8);
                var footprints = visible.Select(v => LightBinnerItems.Bounds(bin, v.BoxMins, v.BoxMaxs, [1f, 0f, 0f, v.Origin.X, 0f, 1f, 0f, v.Origin.Y, 0f, 0f, 1f, v.Origin.Z])).ToList();
                if (footprints.Any(p => p == null))
                {
                    failures.Add($"{dir} face {f}: a visible volume is rejected");
                    continue;
                }
                var (ourRecords, ourEdges) = LightBinnerItems.Pack(bin, footprints!);
                var r = Words(records, ourRecords);
                var e = Words(edges, ourEdges);
                output.WriteLine($"{Path.GetFileName(dir)} face {f}: {visible.Count} volumes, {footprints.Sum(p => p!.Hull.Count)} hull points, {ourEdges.Length / 16} edges; records {r}; edges {e}");
                if (!r.StartsWith("equal", StringComparison.Ordinal) || !e.StartsWith("equal", StringComparison.Ordinal))
                    failures.Add($"{Path.GetFileName(dir)} face {f}: records {r}; edges {e}");
            }
        }
        Assert.Empty(failures);
    }

    private static string Words(byte[] theirs, byte[] ours)
    {
        if (ours.Length > theirs.Length)
            return $"ours longer ({ours.Length} / {theirs.Length})";
        var wrong = Enumerable.Range(0, ours.Length / 4).Where(w => BitConverter.ToUInt32(theirs, w * 4) != BitConverter.ToUInt32(ours, w * 4)).ToList();
        // Past what the view uploads the buffer keeps earlier uploads (other views, other frames): reported only, the
        // face replay shows no shader reads it.
        var tail = Enumerable.Range(ours.Length / 4, (theirs.Length - ours.Length) / 4).Count(w => BitConverter.ToUInt32(theirs, w * 4) != 0);
        return wrong.Count == 0 ? $"equal{(tail > 0 ? $" (then {tail} stale words)" : "")}"
            : $"{wrong.Count} words differ ({string.Join(", ", wrong.Take(12).Select(w => $"+{w * 4} {BitConverter.ToSingle(theirs, w * 4):G9}/{BitConverter.ToSingle(ours, w * 4):G9}"))})";
    }

    /// <summary>The item records and edges as the face's two cull dispatches read them (before the first writes).</summary>
    private static (byte[] Records, byte[] Edges) Captured(string dir)
    {
        var dispatches = File.ReadLines(Path.Combine(dir, "actions.jsonl")).Select(l => JsonNode.Parse(l)!)
            .Where(r => r["stages"]?["cs"] is not null && r["contents"] is not null).ToList();
        byte[] Read(JsonNode r, int binding)
        {
            var x = r["stages"]!["cs"]!["readWrite"]!.AsArray().Concat(r["stages"]!["cs"]!["readOnly"]!.AsArray())
                .First(x => (int)x!["bind"]!["set"]! == 0 && (int)x["bind"]!["binding"]! == binding)!;
            return File.ReadAllBytes(Path.Combine(dir, "blobs", (string)r["contents"]!["before"]![(string)x["descriptor"]!["resource"]!]!["sha1"]! + ".bin"));
        }
        // The depth-bin pass (the first dispatch of the pair) reads the records at binding 30, the tile pass the edges.
        var pair = dispatches.TakeLast(2).ToList();
        return (Read(pair[0], 30), Read(pair[1], 30));
    }
}
