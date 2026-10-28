using Source2.Compiler.Maps;
using Source2.Compiler.SteamAudio;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The reverb simulator's pieces against phonon's own state, dumped by tools/steamaudio/capture_tables.py from the
/// seed-0 replay of s2c_saprobe (STEAMAUDIO_REFS/tables_saprobe).
/// </summary>
public class SteamAudioSimulatorTests(ITestOutputHelper output)
{
    private static string Tables => Path.Combine(SteamAudioProbeBatchTests.Refs, "tables_saprobe");

    private static float[][] Rows(string file, int rows)
    {
        var all = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(Path.Combine(Tables, file))).ToArray();
        var n = all.Length / rows;
        return Enumerable.Range(0, rows).Select(r => all.AsSpan(r * n, n).ToArray()).ToArray();
    }

    private int Differences(string what, float[][] valve, float[][] ours)
    {
        var bad = 0;
        for (var r = 0; r < valve.Length; r++)
            for (var i = 0; i < valve[r].Length; i++)
                if (BitConverter.SingleToInt32Bits(valve[r][i]) != BitConverter.SingleToInt32Bits(ours[r][i]) && bad++ < 5)
                    output.WriteLine($"{what}[{r}][{i}]: Valve {valve[r][i]:R}, ours {ours[r][i]:R}");
        output.WriteLine($"{what}: {bad} of {valve.Sum(x => x.Length)} differ");
        return bad;
    }

    [Fact]
    public void SampleTablesMatchPhonon()
    {
        if (!Directory.Exists(Tables))
            return;
        var sphere = Sampling.Sphere(32768);
        var a = Differences("listener samples", Rows("listener_samples.bin", 3), sphere);
        var b = Differences("diffuse samples", Rows("diffuse_samples.bin", 3), Sampling.Hemisphere(2048));
        var c = Differences("listener coeffs", Rows("listener_coeffs.bin", 4), Sampling.Coefficients(Rows("listener_samples.bin", 3), 1));
        Assert.Equal(0, a + b + c);
    }
}

/// <summary>
/// The reverb bake from Valve's own inputs (the capture's mesh, materials and probes, seed 0, one thread): each
/// probe's energy field against the one in Valve's seed-0 .sareverb (STEAMAUDIO_REFS/s2c_saprobe_api0).
/// SAPROBES=n bakes the first n probes (default 4; all 73: 72 exact, probe 65 has one exact edge tie that Embree's
/// BVH traversal order decides, which is not ported, so a probe with ties is reported rather than failed).
/// </summary>
public class SteamAudioReverbBakeTests(ITestOutputHelper output)
{
    [Fact]
    public void EnergyFieldsMatchValve()
    {
        var dir = Path.Combine(SteamAudioProbeBatchTests.Refs, "s2c_saprobe_api0");
        var tables = Path.Combine(SteamAudioProbeBatchTests.Refs, "tables_saprobe");
        if (!Directory.Exists(dir) || !Directory.Exists(tables))
            return;
        static T[] Read<T>(string f) where T : unmanaged => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, T>(File.ReadAllBytes(f)).ToArray();
        var verts = Read<float>(Path.Combine(dir, "mesh_1_vertices.bin"));
        var tris = Read<int>(Path.Combine(dir, "mesh_1_triangles.bin"));
        var mi = Read<int>(Path.Combine(dir, "mesh_1_materialindices.bin"));
        var mf = Read<float>(Path.Combine(dir, "mesh_1_materials.bin"));
        var mats = Enumerable.Range(0, mf.Length / 7).Select(i => new AcousticMaterial(mf[7 * i], mf[7 * i + 1], mf[7 * i + 2], mf[7 * i + 3], mf[7 * i + 4], mf[7 * i + 5], mf[7 * i + 6])).ToArray();
        var batch = ProbeBatch.Read(SteamAudioProbeBatchTests.Batch(SteamAudioProbeBatchTests.Root(Path.Combine(dir, "s2c_saprobe.sareverb"))));
        var l = Read<float>(Path.Combine(tables, "listener.bin"));
        var sim = new ReflectionSimulator(32768, 2048, 1) { Seed = 0 };
        var n = int.TryParse(Environment.GetEnvironmentVariable("SAPROBES"), out var k) ? k : 4;
        var total = 0;
        var fields = batch.Layers[0].Reflections!.EnergyFields!;
        System.Threading.Tasks.Parallel.For(0, Math.Min(n, batch.Probes.Count), p =>
        {
            var c = batch.Probes[p];
            var scene = new TriangleScene(verts, tris, mi, mats);
            var space = new ReflectionSimulator.Space(l[0], l[1], l[2], l[4], l[5], l[6], l[8], l[9], l[10], c.X, c.Y, c.Z);
            var ours = sim.Bake(scene, space, space, 32768, 32, 1.0f, 1, 100);
            var valve = fields[p]!.Data;
            int bad = 0, first = -1;
            var flat = ours.SelectMany(ch => ch.SelectMany(b => b)).ToArray();
            for (var i = 0; i < valve.Length; i++)
                if (BitConverter.SingleToInt32Bits(valve[i]) != BitConverter.SingleToInt32Bits(flat[i]))
                {
                    if (first < 0) first = i;
                    bad++;
                }
            lock (output)
            {
                output.WriteLine($"probe {p} at ({c.X}, {c.Y}, {c.Z}): {valve.Count(v => v != 0)} nonzero, {bad} of {valve.Length} differ, {scene.Ties} ties"
                    + (first >= 0 ? $", first [{first}] Valve {valve[first]:R} ours {flat[first]:R}" : ""));
                foreach (var t in scene.TieLog)
                    output.WriteLine("  tie " + t);
                // A probe may differ only where a ray met an exact tie, which Embree's BVH order decides (not ported).
                if (scene.Ties == 0)
                    total += bad;
            }
        });
        Assert.Equal(0, total);
    }
}

/// <summary>Each probe's three reverb times from Valve's own energy field, against the times in Valve's batch.</summary>
public class SteamAudioReverbTimeTests(ITestOutputHelper output)
{
    [Fact]
    public void ReverbTimesMatchValve()
    {
        if (!Directory.Exists(SteamAudioProbeBatchTests.Refs))
            return;
        var bad = 0;
        var files = Directory.EnumerateFiles(SteamAudioProbeBatchTests.Refs, "*.sareverb", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).StartsWith("ar_")).ToList();
        foreach (var file in files)
        {
            var r = ProbeBatch.Read(SteamAudioProbeBatchTests.Batch(SteamAudioProbeBatchTests.Root(file))).Layers[0].Reflections!;
            var fileBad = 0;
            for (var p = 0; p < r.EnergyFields!.Count; p++)
            {
                var e = r.EnergyFields[p]!;
                var field = Enumerable.Range(0, e.Channels).Select(c => Enumerable.Range(0, 3)
                    .Select(b => e.Data.AsSpan((c * 3 + b) * e.Bins, e.Bins).ToArray()).ToArray()).ToArray();
                var ours = ReverbEstimator.Estimate(field, 0.01f);
                for (var b = 0; b < 3; b++)
                    if (BitConverter.SingleToInt32Bits(ours[b]) != BitConverter.SingleToInt32Bits(r.Reverbs![3 * p + b]) && fileBad++ < 3)
                        output.WriteLine($"{Path.GetFileName(Path.GetDirectoryName(file))} probe {p} band {b}: Valve {r.Reverbs[3 * p + b]:R} ours {ours[b]:R}");
            }
            output.WriteLine($"{Path.GetRelativePath(SteamAudioProbeBatchTests.Refs, file)}: {fileBad} of {r.Reverbs!.Length} differ");
            bad += fileBad;
        }
        Assert.Equal(0, bad);
    }
}

/// <summary>
/// The automatic probes from Valve's generation scene (mesh_0) and box matrix (the captured
/// iplProbeArrayGenerateProbes block), round-tripped through Source inches, against the probes Valve's bake used
/// (the captured iplProbeArraySetProbe spheres).
/// </summary>
public class SteamAudioProbeGenerationTests(ITestOutputHelper output)
{
    [Fact]
    public void FloorProbesMatchValve()
    {
        var dir = Path.Combine(SteamAudioProbeBatchTests.Refs, "s2c_saprobe_api0");
        if (!Directory.Exists(dir))
            return;
        static T[] Read<T>(string f) where T : unmanaged => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, T>(File.ReadAllBytes(f)).ToArray();
        var mf = Read<float>(Path.Combine(dir, "mesh_0_materials.bin"));
        var mats = Enumerable.Range(0, mf.Length / 7).Select(i => new AcousticMaterial(mf[7 * i], mf[7 * i + 1], mf[7 * i + 2], mf[7 * i + 3], mf[7 * i + 4], mf[7 * i + 5], mf[7 * i + 6])).ToArray();
        var scene = new TriangleScene(Read<float>(Path.Combine(dir, "mesh_0_vertices.bin")), Read<int>(Path.Combine(dir, "mesh_0_triangles.bin")),
                                      Read<int>(Path.Combine(dir, "mesh_0_materialindices.bin")), mats);
        var calls = File.ReadLines(Path.Combine(dir, "api.jsonl")).Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
            .Where(c => c.GetProperty("op").GetString() == "call").ToList();
        var gen = calls.First(c => c.GetProperty("name").GetString() == "iplProbeArrayGenerateProbes" && c.TryGetProperty("struct", out var s) && s.GetString() != "unreadable");
        var block = Convert.FromBase64String(gen.GetProperty("struct").GetString()!);
        var spacing = BitConverter.ToSingle(block, 4);
        var height = BitConverter.ToSingle(block, 8);
        var matrix = Enumerable.Range(0, 16).Select(i => BitConverter.ToSingle(block, 12 + 4 * i)).ToArray();
        var valve = calls.Where(c => c.GetProperty("name").GetString() == "iplProbeArraySetProbe")
            .Select(c => System.Runtime.InteropServices.MemoryMarshal.Read<Sphere>(Convert.FromBase64String(c.GetProperty("struct").GetString()!))).ToList();
        var ours = ProbeGeneration.UniformFloor(scene, matrix, spacing, height).Select(ProbeGeneration.RoundTrip).ToList();
        output.WriteLine($"spacing {spacing} height {height}: Valve {valve.Count} probes, ours {ours.Count}, ties {scene.Ties}");
        var bad = 0;
        for (var i = 0; i < Math.Min(ours.Count, valve.Count); i++)
            if (ours[i] != valve[i] && bad++ < 5)
                output.WriteLine($"  probe {i}: Valve {valve[i]} ours {ours[i]}");
        Assert.Equal(valve.Count, ours.Count);
        Assert.Equal(0, bad);
    }
}

/// <summary>The scene's material table from the game's surface properties and Steam Audio txt, against the 164
/// materials Valve's scene got (the capture's mesh_1_materials.bin).</summary>
public class SteamAudioMaterialTests(ITestOutputHelper output)
{
    [Fact]
    public void MaterialTableMatchesValve()
    {
        var file = Path.Combine(SteamAudioProbeBatchTests.Refs, "s2c_saprobe_api0", "mesh_1_materials.bin");
        if (!File.Exists(file) || CS2Fixtures.StockPak() is not { } pak)
            return;
        using var content = new GameContent(pak);
        var txt = System.Text.Encoding.UTF8.GetString(content.Read("scripts/surfaceproperties_steamaudio.txt")!);
        var (ours, names) = AcousticMaterials.Build(content.SurfaceProperties(), txt);
        var mf = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(file)).ToArray();
        output.WriteLine($"Valve {mf.Length / 7} materials, ours {ours.Length}");
        var bad = 0;
        for (var i = 0; i < Math.Min(ours.Length, mf.Length / 7); i++)
        {
            var m = ours[i];
            float[] o = [m.Absorption0, m.Absorption1, m.Absorption2, m.Scattering, m.Transmission0, m.Transmission1, m.Transmission2];
            if (Enumerable.Range(0, 7).Any(k => BitConverter.SingleToInt32Bits(o[k]) != BitConverter.SingleToInt32Bits(mf[7 * i + k])) && bad++ < 6)
                output.WriteLine($"  {i} {names[i]}: Valve ({string.Join(", ", mf.Skip(7 * i).Take(7))}) ours ({string.Join(", ", o)})");
        }
        Assert.Equal(mf.Length / 7, ours.Length);
        Assert.Equal(0, bad);
    }
}
