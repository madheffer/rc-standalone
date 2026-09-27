using System.Text.Json;
using Source2.Compiler.Gpu;
using Source2.Compiler.Physics;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// physicsbuilder's captured sampler batches (the 40-byte records and the
/// read-back RGB, from <c>capture_physshapes.py --blend</c>) drawn again with
/// <see cref="GpuMaterialSampler"/>, our pixels against Valve's. A compile
/// run with <c>--vulkan</c> ran the same programs; the default one ran the
/// DX11 build of them.
/// <c>GPUREPLAY=&lt;capture.json&gt;|&lt;materials/...vmat&gt;[|&lt;addon&gt;][|exact]</c>.
/// </summary>
public class GpuSamplerReplay(ITestOutputHelper output)
{
    [Fact]
    public void Replay()
    {
        if (Environment.GetEnvironmentVariable("GPUREPLAY") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        var game = Path.Combine(cs2, "game");
        var addon = parts.Skip(2).FirstOrDefault(p => p != "exact");
        using var files = new SettleBuildTests.PakModels(Path.Combine(game, "csgo", "pak01_dir.vpk"), addon == null ? [] : [Path.Combine(game, "csgo_addons", addon)]);
        using var gpu = new GpuMaterialSampler(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"), files.Read);
        output.WriteLine($"device {gpu.DeviceName}");
        var render = gpu.For(parts[1]) ?? throw new InvalidOperationException($"{parts[1]}: no ToolsVis render");

        using var doc = JsonDocument.Parse(File.ReadAllText(parts[0]));
        byte[]? records = null;
        int call = 0, allExact = 0, allSamples = 0;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (!e.TryGetProperty("sampler", out var kind))
                continue;
            if (kind.GetString() == "records")
                records = Convert.FromHexString(e.GetProperty("data").GetString()!);
            else if (kind.GetString() == "pixels" && records != null)
            {
                var want = Convert.FromHexString(e.GetProperty("rgb").GetString()!);
                var count = records.Length / 120;
                var ours = render(records, count, MaterialSampler.TargetSide(count))!;
                int exact = 0, maxDiff = 0, sameLayer = 0, shown = 0;
                for (var i = 0; i < count; i++)
                {
                    var diff = 0;
                    for (var c = 0; c < 3; c++)
                        diff = Math.Max(diff, Math.Abs(ours[(i * 3) + c] - want[(i * 3) + c]));
                    maxDiff = Math.Max(maxDiff, diff);
                    if (diff == 0)
                        exact++;
                    else if (shown++ < 12)
                        output.WriteLine($"  point {i}: ours {ours[i * 3]},{ours[(i * 3) + 1]},{ours[(i * 3) + 2]} valve {want[i * 3]},{want[(i * 3) + 1]},{want[(i * 3) + 2]}");
                    if (MaterialSampler.Vote([MaterialSampler.Weights(ours[i * 3], ours[(i * 3) + 1], ours[(i * 3) + 2])])
                        == MaterialSampler.Vote([MaterialSampler.Weights(want[i * 3], want[(i * 3) + 1], want[(i * 3) + 2])]))
                        sameLayer++;
                }
                output.WriteLine($"call {call++}: {count} points, {exact} exact, max diff {maxDiff}, same point layer {sameLayer}");
                allExact += exact;
                allSamples += count;
                records = null;
            }
        }
        output.WriteLine($"GPUREPLAY {allExact} of {allSamples} points exact");
        if (parts.Contains("exact"))
            Assert.Equal(allSamples, allExact);
    }
}
