using Source2.Compiler.SteamAudio;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Valve's .sareverb and .sapaths (STEAMAUDIO_REFS, default %TEMP%/steamaudio_refs, from
/// tools/steamaudio/capture_sareverb.py and capture_phonon_api.py): the probe batch in each, read and written back
/// by <see cref="ProbeBatch"/>, comes out as Valve's bytes.
/// </summary>
public class SteamAudioProbeBatchTests(ITestOutputHelper output)
{
    internal static string Refs => Environment.GetEnvironmentVariable("STEAMAUDIO_REFS") ?? Path.Combine(Path.GetTempPath(), "steamaudio_refs");

    /// <summary>A Valve .sareverb / .sapaths: its KV3 root.</summary>
    internal static ValveKeyValue.KVObject Root(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var holder = new Resource();
        var kv3 = new BinaryKV3 { Resource = holder, Offset = 0, Size = (uint)bytes.Length };
        using var reader = new BinaryReader(new MemoryStream(bytes));
        kv3.Read(reader);
        return kv3.Data.Root;
    }

    internal static byte[] Batch(ValveKeyValue.KVObject root)
        => (byte[])(root.ContainsKey("m_pProbeBatch") ? root["m_pProbeBatch"] : root["m_probes"]["m_pProbeBatch"]);

    [Fact]
    public void ValveBatchesRoundTrip()
    {
        if (!Directory.Exists(Refs))
            return;
        var files = Directory.EnumerateFiles(Refs, "*.sa*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".sareverb") || f.EndsWith(".sapaths")) && !Path.GetFileName(f).StartsWith("ar_")).ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var valve = Batch(Root(file));
            var batch = ProbeBatch.Read(valve);
            var ours = batch.Write();
            var layers = string.Join(", ", batch.Layers.Select(l => $"type {l.Type} variation {l.Variation}"
                + (l.Reflections is { } r ? $", {r.EnergyFields?.Count ?? 0} fields, reverbs {r.Reverbs?.Length ?? -1}" : "")
                + (l.Pathing is { } p ? $", {p.VisGraph.Count} nodes, {p.UniquePaths.Count} paths, {p.Paths.Length} refs" : "")));
            var first = Enumerable.Range(0, Math.Min(ours.Length, valve.Length)).FirstOrDefault(i => ours[i] != valve[i], -1);
            output.WriteLine($"{Path.GetRelativePath(Refs, file)}: {valve.Length} bytes, {batch.Probes.Count} probes, {layers}; ours {ours.Length}, first difference {first}");
            Assert.Equal(valve.Length, ours.Length);
            Assert.Equal(-1, first);
        }
    }

    /// <summary>The whole file around Valve's probe batch: s2c_saprobe's worldspawn takes the FGD defaults
    /// (rays 32768, bounces 32, IR 1 s, order 1, clustering off with 16 / 10, compression off at 0.95).</summary>
    [Fact]
    public void ValveFilesFromTheirBatch()
    {
        if (!Directory.Exists(Refs))
            return;
        var settings = new ReverbSettings();
        foreach (var file in Directory.EnumerateFiles(Refs, "s2c_saprobe.sa*", SearchOption.AllDirectories))
        {
            var valve = File.ReadAllBytes(file);
            var batch = Batch(Root(file));
            var ours = file.EndsWith(".sareverb") ? SteamAudioFiles.Reverb(batch, settings) : SteamAudioFiles.Paths(batch);
            var first = Enumerable.Range(0, Math.Min(ours.Length, valve.Length)).FirstOrDefault(i => ours[i] != valve[i], -1);
            output.WriteLine($"{Path.GetRelativePath(Refs, file)}: Valve {valve.Length}, ours {ours.Length}, first difference {first}");
            Assert.Equal(valve.Length, ours.Length);
            Assert.Equal(-1, first);
        }
    }
}
