using System.Numerics;
using System.Text.Json.Nodes;
using Source2.Compiler.Cubemaps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="LightProbeVolumeRecords"/> against a capture's PerViewLightProbeVolumeConstantBuffer_t:
/// <c>CUBELPV=&lt;a face's dump&gt;</c> and <c>CUBELPV_VPK</c> the captured map's package. The volumes are the
/// package's env_combined_light_probe_volume entities in lump order; every word of all 400 records must be the
/// capture's except the bindless indices (+0x4c, +0x6c, +0x7c, +0x88, +0x8c), which are ours.
/// </summary>
public sealed class LightProbeVolumeRecordsTests(ITestOutputHelper output)
{
    private static readonly int[] IndexWords = [0x4c, 0x6c, 0x7c, 0x88, 0x8c];

    [Fact]
    public void BufferEqualsTheCapture()
    {
        if (Environment.GetEnvironmentVariable("CUBELPV") is not { Length: > 0 } dir
            || Environment.GetEnvironmentVariable("CUBELPV_VPK") is not { Length: > 0 } vpk)
            return;
        var block = File.ReadLines(Path.Combine(dir, "actions.jsonl")).Select(l => JsonNode.Parse(l)!)
            .SelectMany(a => new[] { a["stages"]?["vs"], a["stages"]?["ps"] }.Where(s => s != null))
            .SelectMany(s => s!["constantBlocks"]!.AsArray())
            .First(cb => (int)cb!["block"]!["fixedBindSetOrSpace"]! == 1 && (int)cb["block"]!["fixedBindNumber"]! == 2
                         && (long?)cb["bytes"]?["size"] == LightProbeVolumeRecords.Count * LightProbeVolumeRecords.Size)!;
        var captured = File.ReadAllBytes(Path.Combine(dir, "blobs", (string)block["bytes"]!["sha1"]! + ".bin"));

        var volumes = new List<LightProbeVolumeRecords.Volume>();
        using (var package = new ValvePak.Package())
        {
            package.Read(vpk);
            foreach (var entry in (package.Entries!.GetValueOrDefault("vents_c") ?? []).OrderBy(e => e.GetFullPath(), StringComparer.Ordinal))
            {
                package.ReadEntry(entry, out var bytes);
                foreach (var (cls, key) in Lighting.Vrad3Job.LumpEntities(bytes, entry.GetFullPath()).Where(e => e.Class == "env_combined_light_probe_volume"))
                {
                    static Vector3 V(string? t) => t!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray() is var a ? new(a[0], a[1], a[2]) : default;
                    static int N(string? t) => int.Parse(t ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                    volumes.Add(new(V(key("origin")), V(key("angles")), V(key("box_mins")), V(key("box_maxs")),
                        N(key("light_probe_size_x")), N(key("light_probe_size_y")), N(key("light_probe_size_z"))));
                }
            }
        }
        var ours = LightProbeVolumeRecords.Buffer(volumes, 1, 2);
        var failures = new List<string>();
        for (var i = 0; i < LightProbeVolumeRecords.Count; i++)
        {
            var differ = new List<string>();
            for (var w = 0; w < LightProbeVolumeRecords.Size; w += 4)
            {
                if (IndexWords.Contains(w))
                    continue;
                var at = i * LightProbeVolumeRecords.Size + w;
                if (BitConverter.ToUInt32(ours, at) != BitConverter.ToUInt32(captured, at))
                    differ.Add($"+0x{w:x2} ours {BitConverter.ToUInt32(ours, at):x8} theirs {BitConverter.ToUInt32(captured, at):x8}");
            }
            if (differ.Count > 0)
                failures.Add($"record {i}: {string.Join(", ", differ.Take(6))}");
        }
        output.WriteLine($"{volumes.Count} volumes; {LightProbeVolumeRecords.Count - failures.Count} of {LightProbeVolumeRecords.Count} records the capture's");
        foreach (var f in failures.Take(12))
            output.WriteLine("  " + f);
        Assert.Empty(failures);
    }
}
