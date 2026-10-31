using System.Numerics;
using Source2.Compiler.Cubemaps;
using Source2.Compiler.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// A whole cube rendered by <see cref="CubemapFaceRenderer"/> from our data alone: the map's package (with the scene's
/// inputs from its lumps and world, <see cref="CubemapFaceBlocks.FromPackage"/>), the game's shader and content
/// packages and gameinfo. No file of a RenderDoc dump is read to render. The six faces' colour must equal the game's readbacks (<c>CUBEREADBACK</c>, rdc_readback.py) and
/// through <see cref="CubemapFaceExr"/> the game's EXR (<c>CUBEREADBACK_EXR</c>) byte for byte. <c>CUBENODUMP_VPK</c>
/// is the captured map's package, <c>CUBENODUMP_CUBE</c> the cube (0 by default), <c>CUBEREPLAY_SCRIPTEDCAMERA=0</c> a build without a scripted camera; <c>CUBEREPLAY_EXR_OUT</c> writes our
/// EXR for GameCubemapTests to build the array from, <c>CUBENODUMP_FACES_OUT</c> our raw faces.
/// </summary>
public sealed class CubemapFaceRendererTests(ITestOutputHelper output)
{
    /// <summary>
    /// probe01's inputs (the same as CubemapFaceReplayTests' blocks): its two env_combined_light_probe_volumes (the
    /// cubes' origins), env_cubemap_fog, light_environment (angles, colour 248 249 232 at brightness 2.25, sky colour
    /// 138 200 255 at 0.875), the world's lightmap uv scale and 4096 lightmaps, env_sky at (512, 112, 48) with no
    /// rotation. Its builds saw a scripted camera (ledger 149).
    /// </summary>
    public static CubemapFaceBlocks.Scene Probe01(int cube)
    {
        CubemapFaceConstants.LightProbeVolume[] volumes =
        [
            new(new(512, 96, 112), new(-768, -1088, -112), new(768, 896, 176), new(0, 32, 0)),
            new(new(512, 1104, 112), new(-576, -144, -112), new(640, 336, 144), new(0, 32, 0), 1),
        ];
        return new CubemapFaceBlocks.Scene(
            volumes[cube].Origin,
            volumes,
            new CubemapFaceConstants.CubeFog(400f, 12000f, 0.5f, 1.2f, new Vector4(0f, 2000f, 1f, 1f), Matrix4x4.CreateTranslation(512, 112, 80)),
            new Vector3(74.99999f, 180.00002f, 0f),
            Lighting.SceneLights.SrgbToLinear(248, 249, 232) * 2.25f,
            0.1f,
            0.05f,
            Lighting.SceneLights.SrgbToLinear(138, 200, 255),
            0.875f,
            new Vector2(1.1428397f, 1.1428397f),
            4096,
            4096,
            Vector3.Zero,
            new Vector3(512, 112, 48),
            ScriptedCamera: true);
    }

    [Fact]
    public void CubeWithNoDumpMakesTheGamesExr()
    {
        if (Environment.GetEnvironmentVariable("CUBENODUMP_VPK") is not { Length: > 0 } vpk
            || Environment.GetEnvironmentVariable("CUBEREADBACK") is not { Length: > 0 } readbackDir
            || Environment.GetEnvironmentVariable("CUBEREADBACK_EXR") is not { Length: > 0 } exrPath)
            return;
        var cube = int.Parse(Environment.GetEnvironmentVariable("CUBENODUMP_CUBE") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game");
        // CUBEREPLAY_SCRIPTEDCAMERA=0: a build with no scripted camera (s2c_l5probe's, the same volumes and lights).
        var scripted = Environment.GetEnvironmentVariable("CUBEREPLAY_SCRIPTEDCAMERA") != "0";
        // The scene from the package's lumps and world, and gameinfo (CubemapFaceBlocks.FromPackage).
        List<CubemapFaceBlocks.Cube> cubes;
        string? skyMaterial;
        using (var package = new ValvePak.Package())
        {
            package.Read(vpk);
            cubes = CubemapFaceBlocks.FromPackage(package, File.ReadAllText(Path.Combine(game, "csgo", "gameinfo.gi")), scripted, out skyMaterial);
        }
        var scene = cubes[cube].Scene;
        output.WriteLine($"cube {cube}: {cubes[cube].Class} at {scene.Origin}, handshake {cubes[cube].Handshake}; {cubes.Count} cubes, {scene.Volumes.Count} volumes, sky {skyMaterial}");
        Assert.Equal($"env_cubemap_{cubes[cube].Handshake}.exr", Path.GetFileName(exrPath));
        // The values typed from probe01's entities, which matched the captures word for word, are what the reader gives.
        if (Path.GetFileName(vpk).Contains("probe01", StringComparison.Ordinal) || Path.GetFileName(vpk).Contains("l5probe", StringComparison.Ordinal))
        {
            var typed = Probe01(cube) with { ScriptedCamera = scripted };
            Assert.Equal(typed with { Volumes = scene.Volumes }, scene);
            Assert.Equal(typed.Volumes, scene.Volumes);
        }
        var memberships = CubemapLayerFlagsCaptureTests.Memberships(vpk);
        var models = CubemapFaceRenderer.AggregateModels(vpk);
        // The draw list's aggregates (fragments, LODs) and their layer-selection inputs, in the same order.
        var aggregates = AggregateCullingCaptureTests.Aggregates(vpk);
        Assert.Equal(aggregates.Select(a => Path.ChangeExtension(a.Name.Split(' ', 2)[1], ".vmdl_c")), models.Select(m => m.Model));
        models = [.. models.Select((m, i) => m with { DrawCallFlags = memberships[aggregates[i].Name.Split(' ', 2)[1]].DrawCall14 })];
        var listAggregates = aggregates.Select(a => new CubemapFaceDrawList.Aggregate(a.Fragments, a.OwnTransforms, a.Lods, memberships[a.Name.Split(' ', 2)[1]])).ToList();
        var order = CubemapFaceDrawList.MaterialOrder(CubemapFaceRenderer.AggregateMaterials(models));
        Vector3? toSun = scene.HasSun ? CubemapShadowCascades.SunDirections(scene.SunAngles).ToSun : null;

        using var gpu = new GpuDevice(rayTracingShaderFeatures: Environment.GetEnvironmentVariable("CUBEREPLAY_BASIC_DEVICE") != "1");
        using var renderer = new CubemapFaceRenderer(gpu, game, vpk, models, skyMaterial, [.. aggregates.Select(a => (IReadOnlyList<AggregateCulling.Fragment>)a.Fragments)]);
        var faces = new List<Half[]>();
        var failures = new List<string>();
        for (var f = 0; f < 6; f++)
        {
            var blocks = CubemapFaceBlocks.Build(scene, f);
            var (draws, instances) = CubemapFaceDrawList.Face(blocks.View, toSun, listAggregates, scene.ScriptedCamera, materialOrder: order);
            var actions = 0;
            // CUBENODUMP_TRACE=1: after each action, how much of the face's colour and depth it has touched.
            var trace = Environment.GetEnvironmentVariable("CUBENODUMP_TRACE") == "1";
            var face = renderer.RenderFace(blocks, draws, instances, a =>
            {
                actions++;
                // With CUBENODUMP_FACES_OUT, the face depth once the prepass is done (depth plane then stencil plane).
                if (a.Label.StartsWith("DownsampleHalf", StringComparison.Ordinal) && Environment.GetEnvironmentVariable("CUBENODUMP_FACES_OUT") is { Length: > 0 } depthOut)
                {
                    Directory.CreateDirectory(depthOut);
                    File.WriteAllBytes(Path.Combine(depthOut, $"prepass_depth_{f}.bin"), renderer.ReadTarget(CubemapFaceRenderer.FaceDepth));
                }
                if (!trace || a.Colour is null && a.Depth is null)
                    return;
                var colour = a.Colour is { } c ? renderer.ReadTarget(c) : [];
                var lit = Enumerable.Range(0, colour.Length / 2).Count(i => i % 4 != 3 && BitConverter.ToUInt16(colour, i * 2) != 0);
                var depth = a.Depth is { } d ? renderer.ReadTarget(d) : [];
                var near = a.Depth == CubemapFaceRenderer.FaceDepth ? Enumerable.Range(0, Math.Min(depth.Length / 4, CubemapFaceDrawList.FaceSize * CubemapFaceDrawList.FaceSize)).Count(i => (BitConverter.ToUInt32(depth, i * 4) & 0xffffff) != 0xffffff) : -1;
                output.WriteLine($"  face {f} {a.Label}: colour halves set {lit}, face depth texels below 1: {near}");
            });
            var readback = File.ReadAllBytes(Path.Combine(readbackDir, $"readback_{f}.bin"));
            var differ = Enumerable.Range(0, Math.Min(face.Length, readback.Length) / 2).Count(i => BitConverter.ToUInt16(face, i * 2) != BitConverter.ToUInt16(readback, i * 2));
            // How far off: the largest difference of a colour half, and the pixels with any channel off by more than 1 / 256.
            var halves = Math.Min(face.Length, readback.Length) / 2;
            var worst = Enumerable.Range(0, halves).Where(i => i % 4 != 3).Select(i => Math.Abs((float)BitConverter.ToHalf(face, i * 2) - (float)BitConverter.ToHalf(readback, i * 2))).DefaultIfEmpty(0f).Max();
            var far = Enumerable.Range(0, halves / 4).Count(p => Enumerable.Range(0, 3).Any(c => Math.Abs((float)BitConverter.ToHalf(face, (p * 4 + c) * 2) - (float)BitConverter.ToHalf(readback, (p * 4 + c) * 2)) > 1f / 256));
            output.WriteLine($"face {f}: {actions} actions ({draws.Sum(d => d.Commands.Count)} world draws), colour against readback_{f}: {(face.Length == readback.Length && differ == 0 ? "equal" : $"{differ} halves differ, largest {worst}, {far} pixels off by more than 1/256")}");
            // CUBENODUMP_FACES_OUT=<dir>: our face as raw RGBA16F (face_<f>.bin), to diff against readback_<f>.bin, and
            // the instance and transform buffers its draws read.
            if (Environment.GetEnvironmentVariable("CUBENODUMP_FACES_OUT") is { Length: > 0 } facesOut)
            {
                Directory.CreateDirectory(facesOut);
                File.WriteAllBytes(Path.Combine(facesOut, $"face_{f}.bin"), face);
                var (transformBuffer, instanceBuffer) = CubemapFaceRenderer.InstanceBuffers(draws, instances, [.. aggregates.Select(a => (IReadOnlyList<AggregateCulling.Fragment>)a.Fragments)]);
                File.WriteAllBytes(Path.Combine(facesOut, $"instances_{f}.bin"), instanceBuffer);
                File.WriteAllBytes(Path.Combine(facesOut, $"transforms_{f}.bin"), transformBuffer);
                File.WriteAllLines(Path.Combine(facesOut, $"draws_{f}.txt"), draws.SelectMany(d => d.Commands.Select(c => $"{d.Layer} view {d.View} aggregate {d.Aggregate} {models[d.Aggregate].Model} {c}")));
                if (f == 0)
                    File.WriteAllLines(Path.Combine(facesOut, "models.txt"), models.Select((m, i) => $"{i} {m.Model}: streams "
                        + string.Join(" | ", m.Streams.Select(st => $"stride {st.Stride} [{string.Join(", ", st.Fields.Select(x => x.ToString()))}]"))
                        + "; calls " + string.Join(", ", m.DrawCalls.Select(c => $"{c.Material} depth stream {c.DepthStream} compressed {c.CompressedNormals}"))));
            }
            if (face.Length != readback.Length || differ != 0)
                failures.Add($"face {f}: {differ} halves differ from the readback");
            faces.Add(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(face).ToArray());
        }
        // CUBEREPLAY_SPIRV=<the capture's modules> (rdc_spirv.py): every program we drew with is one of them, byte for byte.
        if (Environment.GetEnvironmentVariable("CUBEREPLAY_SPIRV") is { Length: > 0 } spirvDir)
        {
            var captured = Directory.GetFiles(spirvDir, "*.spv").Select(p => Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(p)))).ToHashSet();
            foreach (var p in renderer.LoadedPrograms)
            {
                var found = captured.Contains(Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(p.Spirv)));
                output.WriteLine($"program {p.Name}: {(found ? "a module of the capture" : "NOT in the capture")}");
                if (!found)
                    failures.Add($"{p.Name} is no module of the capture");
            }
        }
        var gameExr = File.ReadAllBytes(exrPath);
        var exr = CubemapFaceExr.Build(faces, CubemapFaceDrawList.FaceSize, Lighting.Vrad3Exr.Read(gameExr).Height);
        output.WriteLine($"EXR: {(exr.AsSpan().SequenceEqual(gameExr) ? "the game's, byte for byte" : "differs")} ({exr.Length} / {gameExr.Length} bytes)");
        if (Environment.GetEnvironmentVariable("CUBEREPLAY_EXR_OUT") is { Length: > 0 } exrOut)
        {
            Directory.CreateDirectory(exrOut);
            File.WriteAllBytes(Path.Combine(exrOut, Path.GetFileName(exrPath)), exr);
        }
        Assert.Empty(failures);
        Assert.Equal(gameExr, exr);
    }
}
