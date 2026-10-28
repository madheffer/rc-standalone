using System.Numerics;
using Source2.Compiler.Maps;
using Source2.Compiler.SteamAudio;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The acoustic scene from the .vmap (<see cref="AcousticScene"/>) against the static mesh Valve's bake hands
/// phonon (STEAMAUDIO_REFS/s2c_saprobe_api0/mesh_0_*, tools/steamaudio/capture_phonon_api.py).
/// </summary>
public class SteamAudioSceneTests(ITestOutputHelper output)
{
    private const string Content = @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\content\csgo_addons\s2c_rc_probe\maps\";
    private const string AddonGame = @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo_addons\s2c_rc_probe";

    private static string Dir => Path.Combine(SteamAudioProbeBatchTests.Refs, "s2c_saprobe_api0");

    /// <summary>s2c_saprobe's scene with the given mask, and the material table; null without the map, the capture or the game.</summary>
    private static (AcousticScene Scene, AcousticMaterial[] Materials)? Saprobe(ulong mask)
    {
        var vmap = Path.Combine(Content, "s2c_saprobe.vmap");
        if (!File.Exists(Path.Combine(Dir, "mesh_0_vertices.bin")) || !File.Exists(vmap) || CS2Fixtures.StockPak() is not { } pak
            || MapFixtures.GameSchema() is not { } schema)
            return null;
        var document = DmxBinary.ReadFile(vmap);
        MapPrefabs.Attach(document, MapPrefabs.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(vmap))!));
        using var models = new SettleBuildTests.PakModels(pak, AddonGame);
        var txt = System.Text.Encoding.UTF8.GetString(models.Read("scripts/surfaceproperties_steamaudio.txt")!);
        var (materials, names) = AcousticMaterials.Build(models.SurfaceProperties(), txt);
        return (AcousticScene.Build(document, models, schema, names, mask), materials);
    }

    /// <summary>
    /// The generation collection (mask 0x3400) against mesh_0, the bake's (0x1400) against mesh_1, the two pathing
    /// scenes (mesh_2, mesh_3) against the bake's. s2c_saprobe has no passbullets-only shape, so its four are equal.
    /// </summary>
    [Theory]
    [InlineData(AcousticScene.GenerationMask, 0)]
    [InlineData(AcousticScene.BakeMask, 1)]
    [InlineData(AcousticScene.BakeMask, 2)]
    [InlineData(AcousticScene.BakeMask, 3)]
    public void SaprobeSceneMatchesValve(ulong mask, int mesh)
    {
        if (Saprobe(mask) is not { } built)
            return;
        var (vertices, triangles, materials) = built.Scene.ToSteamAudio();
        var vb = File.ReadAllBytes(Path.Combine(Dir, $"mesh_{mesh}_vertices.bin"));
        var tb = File.ReadAllBytes(Path.Combine(Dir, $"mesh_{mesh}_triangles.bin"));
        var mb = File.ReadAllBytes(Path.Combine(Dir, $"mesh_{mesh}_materialindices.bin"));
        var vv = Enumerable.Range(0, vb.Length / 12).Select(i => new Vector3(BitConverter.ToSingle(vb, i * 12), BitConverter.ToSingle(vb, i * 12 + 4), BitConverter.ToSingle(vb, i * 12 + 8))).ToArray();
        var vt = Enumerable.Range(0, tb.Length / 12).Select(i => (BitConverter.ToInt32(tb, i * 12), BitConverter.ToInt32(tb, i * 12 + 4), BitConverter.ToInt32(tb, i * 12 + 8))).ToArray();
        var vm = Enumerable.Range(0, mb.Length / 4).Select(i => BitConverter.ToInt32(mb, i * 4)).ToArray();
        output.WriteLine($"ours {vertices.Length} vertices, {triangles.Length} triangles; Valve {vv.Length}, {vt.Length}");
        var badV = Enumerable.Range(0, Math.Min(vv.Length, vertices.Length)).Where(i => !Same(vv[i], vertices[i])).ToList();
        foreach (var i in badV.Take(10))
            output.WriteLine($"vertex {i}: ours {vertices[i]}, Valve {vv[i]}");
        var badT = Enumerable.Range(0, Math.Min(vt.Length, triangles.Length)).Where(i => vt[i] != triangles[i]).ToList();
        foreach (var i in badT.Take(10))
            output.WriteLine($"triangle {i}: ours {triangles[i]}, Valve {vt[i]}");
        Assert.Equal(vv.Length, vertices.Length);
        Assert.Equal(vt.Length, triangles.Length);
        Assert.Empty(badV);
        Assert.Empty(badT);
        Assert.Equal(vm, materials);
    }

    /// <summary>
    /// The automatic probes from the .vmap alone: our generation scene's box (180100e40) as phonon's matrix
    /// (18006b2d0, null rotation) against the captured iplProbeArrayGenerateProbes block, then the floor probes on
    /// our scene against the probes Valve's bake used.
    /// </summary>
    [Fact]
    public void SaprobeProbesFromTheMap()
    {
        if (Saprobe(AcousticScene.GenerationMask) is not { } built)
            return;
        var calls = File.ReadLines(Path.Combine(Dir, "api.jsonl")).Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
            .Where(c => c.GetProperty("op").GetString() == "call").ToList();
        var gen = calls.First(c => c.GetProperty("name").GetString() == "iplProbeArrayGenerateProbes" && c.TryGetProperty("struct", out var s) && s.GetString() != "unreadable");
        var block = Convert.FromBase64String(gen.GetProperty("struct").GetString()!);
        var valveMatrix = Enumerable.Range(0, 16).Select(i => BitConverter.ToSingle(block, 12 + 4 * i)).ToArray();
        var (centre, size) = built.Scene.Box();
        var matrix = ProbeGeneration.BoxMatrix(0f, 0f, 0f, 0f, centre.X, centre.Y, centre.Z, size.X, size.Y, size.Z);
        output.WriteLine($"box centre {centre} size {size}");
        for (var i = 0; i < 16; i++)
            Assert.True(BitConverter.SingleToInt32Bits(matrix[i]) == BitConverter.SingleToInt32Bits(valveMatrix[i]), $"matrix [{i}]: ours {matrix[i]:R}, Valve {valveMatrix[i]:R}");
        var (vertices, triangles, materials) = built.Scene.ToSteamAudio();
        var scene = new TriangleScene([.. vertices.SelectMany(v => new[] { v.X, v.Y, v.Z })], [.. triangles.SelectMany(t => new[] { t.Item1, t.Item2, t.Item3 })],
                                      materials, built.Materials);
        var ours = ProbeGeneration.UniformFloor(scene, matrix, BitConverter.ToSingle(block, 4), BitConverter.ToSingle(block, 8)).Select(ProbeGeneration.RoundTrip).ToList();
        var valve = calls.Where(c => c.GetProperty("name").GetString() == "iplProbeArraySetProbe")
            .Select(c => System.Runtime.InteropServices.MemoryMarshal.Read<Sphere>(Convert.FromBase64String(c.GetProperty("struct").GetString()!))).ToList();
        output.WriteLine($"Valve {valve.Count} probes, ours {ours.Count}, ties {scene.Ties}");
        Assert.Equal(valve, ours);
    }

    private static bool Same(Vector3 a, Vector3 b)
        => BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X)
           && BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y)
           && BitConverter.SingleToInt32Bits(a.Z) == BitConverter.SingleToInt32Bits(b.Z);
}
