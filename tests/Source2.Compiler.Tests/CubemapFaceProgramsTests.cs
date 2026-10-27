using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Source2.Compiler.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="CubemapFaceRenderer.Programs"/> against a capture (<c>CUBEPROGS=&lt;capture dir&gt;</c> with the map's
/// package, <c>replay_face0/actions.jsonl</c> and <c>spirv/</c>): every world aggregate draw of face 0 (its first
/// vertex stream is a world node model's buffer, its first index names the draw call and so the material) must draw
/// with our vertex and pixel programs, byte for byte, and with no pixel program where the capture has none. The
/// pass is the draw's target: the D16 atlas a cascade, the face depth with no colour the prepass, with colour the
/// colour pass; a prepass draw of a material with F_ALPHA_TEST is the alpha-tested prepass layer's.
/// </summary>
public sealed class CubemapFaceProgramsTests(ITestOutputHelper output)
{
    [Fact]
    public void ProgramsEqualTheCapture()
    {
        if (Environment.GetEnvironmentVariable("CUBEPROGS") is not { Length: > 0 } dir)
            return;
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game");
        var vpk = Directory.GetFiles(dir, "*.vpk").Single();
        // Each aggregate's draw call flags (the shadow fast path bit) from its layer membership, in the same order.
        var memberships = CubemapLayerFlagsCaptureTests.Memberships(vpk);
        var names = AggregateCullingCaptureTests.Aggregates(vpk).Select(a => a.Name.Split(' ', 2)[1]).ToList();
        var models = CubemapFaceRenderer.AggregateModels(vpk).Select((m, i) => m with { DrawCallFlags = memberships[names[i]].DrawCall14 }).ToList();
        var bySha = models.GroupBy(m => Sha(m.Vertices)).ToDictionary(g => g.Key, g => g.First());
        using var gpu = new GpuDevice(rayTracingShaderFeatures: false);
        using var renderer = new CubemapFaceRenderer(gpu, game, vpk, models, "materials/editor/toolscene_lighting_sky_dust.vmat");
        var resources = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "replay_face0", "resources.json")))!;
        string Module(JsonNode stage) => Sha(File.ReadAllBytes(Path.Combine(dir, "spirv", $"{int.Parse(((string)stage["shader"]!).Split(' ')[^1], System.Globalization.CultureInfo.InvariantCulture):x}.spv")));
        var failures = new List<string>();
        var checkedDraws = 0;
        var byPass = new Dictionary<string, int>();
        foreach (var line in File.ReadLines(Path.Combine(dir, "replay_face0", "actions.jsonl")))
        {
            var r = JsonNode.Parse(line)!;
            if (r["contents"] is null || r["stages"]?["vs"] is not { } vsStage || r["vertexInput"]?["vertexBuffers"] is not JsonArray { Count: > 0 } vbs)
                continue;
            var vbSha = (string?)r["contents"]!["before"]![(string)vbs[0]!["resourceId"]!]?["sha1"];
            if (vbSha == null || !bySha.TryGetValue(vbSha, out var model))
                continue;
            var first = (int)r["indexOffset"]!;
            if (model.DrawCalls.FirstOrDefault(c => c.StartIndex <= first && first < c.StartIndex + c.IndexCount) is not { } call)
            {
                failures.Add($"{r["eid"]}: no draw call of {model.Model} holds index {first}");
                continue;
            }
            var depth = (string?)r["depthOut"];
            var atlas = depth != null && (string)resources["textures"]![depth]!["format"]! == "D16";
            var colour = r["outputs"]!.AsArray().Count > 0;
            var pass = atlas ? CubemapFacePipelines.Pass.SunShadow : colour ? CubemapFacePipelines.Pass.Colour : CubemapFacePipelines.Pass.DepthPrepass;
            var alpha = pass == CubemapFacePipelines.Pass.DepthPrepass && renderer.MaterialInt(call.Material, "F_ALPHA_TEST") == 1;
            var label = $"{pass}{(alpha ? " alpha" : "")}";
            ValveProgram vs;
            ValveProgram? ps;
            try
            {
                (vs, ps) = renderer.Programs(model, call, pass, alpha);
            }
            catch (InvalidDataException e)
            {
                failures.Add($"{r["eid"]} {label} {call.Material}: {e.Message}; the capture's vs {((string)vsStage["shader"]!).Split(' ')[^1]}");
                continue;
            }
            byPass[label] = byPass.GetValueOrDefault(label) + 1;
            checkedDraws++;
            if (Sha(vs.Spirv) != Module(vsStage))
                failures.Add($"{r["eid"]} {label} {call.Material}: vertex {vs.Name} is not the capture's module");
            var capturedPs = r["stages"]!["ps"];
            if ((ps == null) != (capturedPs == null))
                failures.Add($"{r["eid"]} {label} {call.Material}: pixel {(ps?.Name ?? "none")}, the capture {(capturedPs == null ? "none" : "one")}");
            else if (ps != null && Sha(ps.Spirv) != Module(capturedPs!))
                failures.Add($"{r["eid"]} {label} {call.Material}: pixel {ps.Name} is not the capture's module");
        }
        output.WriteLine($"{checkedDraws} world draws checked ({string.Join(", ", byPass.Select(kv => $"{kv.Key} {kv.Value}"))}); {failures.Count} differ");
        foreach (var f in failures.Take(60))
            output.WriteLine("  " + f);
        Assert.True(checkedDraws > 0);
        Assert.Empty(failures);
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA1.HashData(data));
}
