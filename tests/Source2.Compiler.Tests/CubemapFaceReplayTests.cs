using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Silk.NET.Vulkan;
using Source2.Compiler.Gpu;
using ValveResourceFormat.CompiledShader;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The cubemap face render, draw by draw, against the game's RenderDoc capture
/// (tools/cubehost/rdc_replay.py with RDC_DUMP=1: each draw's state, the contents
/// it reads and its targets after it). <c>CUBEREPLAY=&lt;dump dir&gt;</c>,
/// <c>CUBEREPLAY_SPIRV=&lt;rdc_spirv.py output&gt;</c>: every draw runs on our own
/// device through <see cref="SceneDrawRunner"/> with Valve's SPIR-V, from the
/// captured "before" targets, and its targets must equal the capture's "after"
/// bit for bit. This checks the GPU state is reproduced (pipelines, bindings,
/// samplers, vertex layouts) before our own inputs replace the captured ones.
/// </summary>
public sealed class CubemapFaceReplayTests(ITestOutputHelper output)
{
    [Fact]
    public void DrawsEqualTheCapture()
    {
        if (Environment.GetEnvironmentVariable("CUBEREPLAY") is not { Length: > 0 } dir
            || Environment.GetEnvironmentVariable("CUBEREPLAY_SPIRV") is not { Length: > 0 } spirvDir)
            return;
        var only = Environment.GetEnvironmentVariable("CUBEREPLAY_EIDS")?.Split(',').Select(int.Parse).ToHashSet();
        var resources = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "resources.json")))!;
        using var gpu = new GpuDevice(rayTracingShaderFeatures: Environment.GetEnvironmentVariable("CUBEREPLAY_BASIC_DEVICE") != "1");
        var runner = new SceneDrawRunner(gpu);
        using var replay = new Replay(dir, spirvDir, resources, gpu, runner);
        var failures = new List<string>();
        foreach (var line in File.ReadLines(Path.Combine(dir, "actions.jsonl")))
        {
            var rec = JsonNode.Parse(line)!;
            if (rec["contents"] is null || (rec["stages"]?["vs"] is null && rec["stages"]?["cs"] is null))
                continue;
            var eid = (int)rec["eid"]!;
            if (only != null && !only.Contains(eid))
                continue;
            var result = rec["stages"]!["cs"] is not null ? replay.RunCompute(rec) : replay.Run(rec);
            output.WriteLine($"{eid} {rec["name"]}: {result}");
            if (!result.StartsWith("equal", StringComparison.Ordinal))
                failures.Add($"{eid}: {result}");
        }
        Assert.Empty(failures);
    }

    /// <summary>
    /// Face 0 of probe01's cube 0 with our constant blocks in place of the captured ones: the per-view blocks
    /// (world, depth range 0 .. 0.95; sky, 1 .. 1), PerViewConstantBufferCsgo_t and
    /// BakedLightingConstantBuffer_t, the transform and instance buffers from <see cref="Cubemaps.CubemapFaceConstants"/>. Every draw that reads them
    /// must still equal the capture.
    /// </summary>
    [Fact]
    public void OurBlocksKeepTheFace()
    {
        if (Environment.GetEnvironmentVariable("CUBEREPLAY") is not { Length: > 0 } dir
            || Environment.GetEnvironmentVariable("CUBEREPLAY_SPIRV") is not { Length: > 0 } spirvDir)
            return;
        var ours = OurBlocks();
        var resources = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "resources.json")))!;
        using var gpu = new GpuDevice(rayTracingShaderFeatures: Environment.GetEnvironmentVariable("CUBEREPLAY_BASIC_DEVICE") != "1");
        var runner = new SceneDrawRunner(gpu);
        using var replay = new Replay(dir, spirvDir, resources, gpu, runner);
        var users = File.ReadLines(Path.Combine(dir, "actions.jsonl"))
            .Where(l => ours.Keys.Any(k => l.Contains(k, StringComparison.Ordinal))).Select(l => JsonNode.Parse(l)!)
            .Where(r => r["contents"] is not null && r["stages"]?["vs"] is not null).ToList();
        if (replay.Baselines != null)
            foreach (var u in users)
                replay.Run(u);   // this GPU's run with the captured blocks is the reference
        foreach (var (prefix, bytes) in ours)
        {
            var sha = Path.GetFileNameWithoutExtension(Directory.GetFiles(Path.Combine(dir, "blobs"), prefix + "*.bin").Single());
            var theirs = File.ReadAllBytes(Path.Combine(dir, "blobs", sha + ".bin"));
            var same = Enumerable.Range(0, Math.Min(theirs.Length, bytes.Length) / 4).Count(i => BitConverter.ToUInt32(theirs, i * 4) == BitConverter.ToUInt32(bytes, i * 4));
            output.WriteLine($"{prefix}: {same} of {theirs.Length / 4} words the capture's ({bytes.Length} bytes ours, {theirs.Length} theirs)");
            replay.Overrides[sha] = bytes;
        }
        var failures = new List<string>();
        foreach (var rec in users)
        {
            var result = replay.Run(rec);
            output.WriteLine($"{rec["eid"]} {rec["name"]}: {result}");
            if (!result.StartsWith("equal", StringComparison.Ordinal))
                failures.Add($"{rec["eid"]}: {result}");
        }
        Assert.Empty(failures);
    }

    /// <summary>
    /// Every constant block and buffer of probe01 cube 0's face 0 from our own data, by the capture's blob hash prefix:
    /// the per-view blocks (world, depth range 0 .. 0.95; sky, 1 .. 1), PerViewConstantBufferCsgo_t,
    /// BakedLightingConstantBuffer_t, transforms and instances, the four cascade per-view blocks and the light block.
    /// </summary>
    private static Dictionary<string, byte[]> OurBlocks()
    {
        var byRole = OurBlocks(0);
        string[] roles = ["world", "sky", "csgo", "baked", "transforms", "instances", "cascade0", "cascade1", "cascade2", "cascade3", "lights"];
        string[] prefixes = ["9cb9e491", "1b1f6cc0", "37c443a8", "d831320f", "40190a9e", "5708ccca", "c84b0bea", "14ee9dcb", "4db015a3", "f0d466f1", "37b7646c"];
        return roles.Zip(prefixes).ToDictionary(p => p.Second, p => byRole[p.First]);
    }

    /// <summary>
    /// probe01's two env_combined_light_probe_volumes (array_index 0 and 1; s2c_l5probe has the same two): each is a
    /// cube record (its origin) and a light probe volume. A face's light block lists the visible volumes in
    /// <see cref="Cubemaps.CubemapFaceConstants.VisibleVolumes"/>' order: both have priority 0, and volume 1's box is
    /// the smaller, so it comes first wherever both are seen.
    /// </summary>
    private static readonly Cubemaps.CubemapFaceConstants.LightProbeVolume[] Probe01Volumes =
    [
        new(new(512, 96, 112), new(-768, -1088, -112), new(768, 896, 176), new(0, 32, 0)),
        new(new(512, 1104, 112), new(-576, -144, -112), new(640, 336, 144), new(0, 32, 0), 1),
    ];

    /// <summary>The same blocks for any face of one of probe01's cubes, by role (<see cref="BlockRoles"/>).</summary>
    private static Dictionary<string, byte[]> OurBlocks(int face, int cube = 0, bool scriptedCamera = true)
    {
        var origin = Probe01Volumes[cube].Origin;
        var view = Cubemaps.CubemapFaceViews.Face(origin, System.Numerics.Vector3.Zero, 1f, 10000f, face);
        var frustum = Cubemaps.AggregateCulling.FaceFrustum(view);
        var volumes = Cubemaps.CubemapFaceConstants.VisibleVolumes(frustum, Probe01Volumes);
        var fog = new Cubemaps.CubemapFaceConstants.CubeFog(400f, 12000f, 0.5f, 1.2f, new System.Numerics.Vector4(0f, 2000f, 1f, 1f),
            System.Numerics.Matrix4x4.CreateTranslation(512, 112, 80));
        var ours = new Dictionary<string, byte[]>
        {
            ["world"] = Cubemaps.CubemapFaceConstants.PerView(view, 1024, 0f, 0.95f),
            ["sky"] = Cubemaps.CubemapFaceConstants.PerView(view, 1024, 1f, 1f),
            ["csgo"] = Cubemaps.CubemapFaceConstants.Csgo(view, fog, 4096f),
            ["baked"] = Cubemaps.CubemapFaceConstants.BakedLighting(new System.Numerics.Vector2(1.1428397f, 1.1428397f), 4096, 4096, false),
            // Our own slots: two identity transforms, every instance pointing at the second.
            ["transforms"] = Cubemaps.CubemapFaceConstants.Transforms([Cubemaps.CubemapFaceConstants.Identity3x4, Cubemaps.CubemapFaceConstants.Identity3x4]),
            ["instances"] = Cubemaps.CubemapFaceConstants.Instances([.. Enumerable.Repeat(1, 63)]),
        };
        // The sun cascades' per-view blocks (three cascades and the viewmodel one).
        var (sun, _, cameraDirection) = Cubemaps.CubemapShadowCascades.SunDirections(new System.Numerics.Vector3(74.99999f, 180.00002f, 0f));
        var cascades = Cubemaps.CubemapShadowCascades.GameCascades(view, sun, 1f, 10000f, scriptedCamera);
        var tiles = CascadeTiles;
        for (var k = 0; k < 4; k++)
            ours[$"cascade{k}"] = Cubemaps.CubemapFaceConstants.CascadePerView(cascades[k], cameraDirection, tiles[k][0], tiles[k][1], Cubemaps.CubemapShadowCascades.Inputs.Resolution, Cubemaps.CubemapShadowCascades.Inputs.AtlasWidth, Cubemaps.CubemapShadowCascades.Inputs.AtlasHeight);
        // The light block: probe01's light_environment and the cube's light probe volume.
        var (_, lightDir, _) = Cubemaps.CubemapShadowCascades.SunDirections(new System.Numerics.Vector3(74.99999f, 180.00002f, 0f));
        var sunLight = new Cubemaps.CubemapFaceConstants.Sun(lightDir, Lighting.SceneLights.SrgbToLinear(248, 249, 232) * 2.25f, 0.1f, 0.05f,
            Lighting.SceneLights.SrgbToLinear(138, 200, 255), 0.875f);
        var stretched = Cubemaps.CubemapShadowCascades.Slice(view, 1f, Cubemaps.CubemapShadowCascades.GameDistances(1f, 10000f, scriptedCamera)[^1]).Diameter * 1.42f;
        // The light binner, from the face's visible items: no lights or environment maps, the volumes above.
        ours["binner"] = Cubemaps.CubemapFaceConstants.LightBinnerCullParams(1024, 1024, 10000f, 0, 0, volumes.Count);
        // The sky: env_sky's angles as the sky object's rotation, its material's $Globals, and the depth downsamples'.
        ours["skyview"] = Cubemaps.CubemapFaceConstants.SkyWsToOs(Cubemaps.CubemapFaceViews.AngleMatrix(System.Numerics.Vector3.Zero, new(512, 112, 48)));
        foreach (var (role, bytes) in ProgramBlocks.Value)
            ours[role] = bytes;
        // The binner's item records and edge lines (LightBinnerItems), for the dispatches' set 0 binding 30 buffers.
        var bin = new Cubemaps.LightBinnerItems.View(view.Origin, new System.Numerics.Vector3(view.CameraToWorld[0], view.CameraToWorld[4], view.CameraToWorld[8]),
            Cubemaps.AggregateCulling.WorldToProjection(view), 1024, 1024, 8);
        var footprints = volumes.Select(v => Cubemaps.LightBinnerItems.Bounds(bin, v.BoxMins, v.BoxMaxs, [1f, 0f, 0f, v.Origin.X, 0f, 1f, 0f, v.Origin.Y, 0f, 0f, 1f, v.Origin.Z])
            ?? throw new InvalidOperationException("a visible volume rejected by the binner")).ToList();
        (ours["binner_records"], ours["binner_edges"]) = Cubemaps.LightBinnerItems.Pack(bin, footprints);
        ours["lights"] = Cubemaps.CubemapFaceConstants.LightBlock(sunLight, volumes, cascades, tiles, Cubemaps.CubemapShadowCascades.Inputs.Resolution, Cubemaps.CubemapShadowCascades.Inputs.AtlasWidth, Cubemaps.CubemapShadowCascades.Inputs.AtlasHeight, origin, stretched);
        return ours;
    }

    /// <summary>
    /// Face 0 chained: every dispatch and draw in order on live targets (each pass from its cleared target), with our
    /// constant blocks, so each draw reads what our earlier draws made (shadow atlas, prepass depth, its downsample)
    /// instead of the capture's. Every action's output must equal the capture's (with CUBEREPLAY_BASELINE, this
    /// device's unchained run with the captured blocks).
    /// </summary>
    [Fact]
    public void ChainedFaceEqualsTheCapture()
    {
        if (Environment.GetEnvironmentVariable("CUBEREPLAY") is not { Length: > 0 } dir
            || Environment.GetEnvironmentVariable("CUBEREPLAY_SPIRV") is not { Length: > 0 } spirvDir)
            return;
        var resources = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "resources.json")))!;
        using var gpu = new GpuDevice(rayTracingShaderFeatures: Environment.GetEnvironmentVariable("CUBEREPLAY_BASIC_DEVICE") != "1");
        var runner = new SceneDrawRunner(gpu);
        using var replay = new Replay(dir, spirvDir, resources, gpu, runner);
        var actions = File.ReadLines(Path.Combine(dir, "actions.jsonl")).Select(l => JsonNode.Parse(l)!)
            .Where(r => r["contents"] is not null && (r["stages"]?["vs"] is not null || r["stages"]?["cs"] is not null)).ToList();
        string Step(JsonNode r) => r["stages"]!["cs"] is not null ? replay.RunCompute(r) : replay.Run(r);
        // With baselines: this device's chained run with the captured blocks is the reference, so only our data differs.
        if (replay.Baselines != null)
        {
            replay.Chain = true;
            foreach (var r in actions)
                Step(r);
            replay.ResetChain();
        }
        foreach (var (prefix, bytes) in OurBlocks())
            replay.Overrides[Path.GetFileNameWithoutExtension(Directory.GetFiles(Path.Combine(dir, "blobs"), prefix + "*.bin").Single())] = bytes;
        replay.Chain = true;
        var failures = new List<string>();
        foreach (var r in actions)
        {
            var result = Step(r);
            output.WriteLine($"{r["eid"]} {r["name"]}: {result}");
            if (!result.StartsWith("equal", StringComparison.Ordinal))
                failures.Add($"{r["eid"]}: {result}");
        }
        foreach (var f in replay.FirstUses)
            output.WriteLine(f);
        Assert.Empty(failures);
    }

    /// <summary>
    /// A whole cube of probe01 (<c>CUBEREPLAY_CUBE</c>, 0 by default): each face's dump (<c>CUBEFACES=&lt;dir&gt;</c> holding replay_face0 .. 5, each
    /// rdc_replay.py with RDC_DUMP=1 over that face's events) chained from cleared targets with our blocks for that
    /// face, found by role (<see cref="BlockRoles"/>). The colour target after the face's last draw must equal the
    /// capture's readback (<c>CUBEREADBACK</c>, rdc_readback.py), and the six through <see cref="Cubemaps.CubemapFaceExr"/>
    /// the game's EXR of the cube (<c>CUBEREADBACK_EXR</c>), byte for byte.
    /// </summary>
    [Fact]
    public void ChainedCubeMakesTheGamesExr()
    {
        if (Environment.GetEnvironmentVariable("CUBEFACES") is not { Length: > 0 } facesDir
            || Environment.GetEnvironmentVariable("CUBEREPLAY_SPIRV") is not { Length: > 0 } spirvDir
            || Environment.GetEnvironmentVariable("CUBEREADBACK") is not { Length: > 0 } readbackDir
            || Environment.GetEnvironmentVariable("CUBEREADBACK_EXR") is not { Length: > 0 } exrPath)
            return;
        var cube = int.Parse(Environment.GetEnvironmentVariable("CUBEREPLAY_CUBE") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        using var gpu = new GpuDevice(rayTracingShaderFeatures: Environment.GetEnvironmentVariable("CUBEREPLAY_BASIC_DEVICE") != "1");
        var runner = new SceneDrawRunner(gpu);
        var faces = new List<Half[]>();
        var failures = new List<string>();
        for (var f = 0; f < 6; f++)
        {
            var dir = Path.Combine(facesDir, $"replay_face{f}");
            var resources = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "resources.json")))!;
            using var replay = new Replay(dir, spirvDir, resources, gpu, runner) { Chain = true, StartFromClear = true };
            var actions = File.ReadLines(Path.Combine(dir, "actions.jsonl")).Select(l => JsonNode.Parse(l)!)
                .Where(r => r["contents"] is not null && (r["stages"]?["vs"] is not null || r["stages"]?["cs"] is not null)).ToList();
            var ours = OurBlocks(f, cube, Environment.GetEnvironmentVariable("CUBEREPLAY_SCRIPTEDCAMERA") != "0");
            var roles = BlockRoles(actions, sha => File.ReadAllBytes(Path.Combine(dir, "blobs", sha + ".bin")));
            // The binner's inputs: the records the depth-bin dispatch reads at set 0 binding 30, the edges the tile
            // dispatch reads there; ours over the start, zeros past it (the capture keeps earlier uploads there).
            var cull = actions.Where(r => r["stages"]?["cs"] is not null).TakeLast(2).ToList();
            foreach (var (dispatch, role) in cull.Zip(new[] { "binner_records", "binner_edges" }))
            {
                var x = dispatch["stages"]!["cs"]!["readWrite"]!.AsArray().Concat(dispatch["stages"]!["cs"]!["readOnly"]!.AsArray())
                    .First(x => (int)x!["bind"]!["set"]! == 0 && (int)x["bind"]!["binding"]! == 30)!;
                var sha = (string)dispatch["contents"]!["before"]![(string)x["descriptor"]!["resource"]!]!["sha1"]!;
                var length = File.ReadAllBytes(Path.Combine(dir, "blobs", sha + ".bin")).Length;
                var data = new byte[length];
                ours[role].CopyTo(data, 0);
                replay.Overrides[sha] = data;
                roles[sha] = role;
            }
            foreach (var (sha, role) in roles)
            {
                // The light block's visible volumes (g_VisibleLPV, g_nNumLPVs) word for word: no draw shows a
                // volume no pixel reaches, so the face's output alone does not check them.
                if (role == "lights")
                {
                    var theirs = File.ReadAllBytes(Path.Combine(dir, "blobs", sha + ".bin"));
                    if (!theirs.AsSpan(18832, 2 * 96).SequenceEqual(ours[role].AsSpan(18832, 2 * 96)) || BitConverter.ToInt32(theirs, 31960) != BitConverter.ToInt32(ours[role], 31960))
                    {
                        var words = Enumerable.Range(18832 / 4, 2 * 96 / 4).Where(w => BitConverter.ToUInt32(theirs, w * 4) != BitConverter.ToUInt32(ours[role], w * 4))
                            .Select(w => $"+{w * 4} {BitConverter.ToUInt32(theirs, w * 4):x8}/{BitConverter.ToUInt32(ours[role], w * 4):x8}");
                        failures.Add($"face {f}: the light block's volumes are not the capture's ({BitConverter.ToInt32(theirs, 31960)} there, {BitConverter.ToInt32(ours[role], 31960)} ours; {string.Join(", ", words)})");
                        output.WriteLine("  " + failures[^1]);
                    }
                }
                if (role is "binner_records" or "binner_edges")
                    continue;
                // A range bound longer than the block keeps the capture's bytes past it (no shader reads them).
                var captured = File.ReadAllBytes(Path.Combine(dir, "blobs", sha + ".bin"));
                // The blocks built whole from the code: every word the capture's.
                if (role is "binner" or "skyview" or "skyglobals" or "downsample_half" or "downsample_quarter"
                    && !captured.AsSpan(0, Math.Min(captured.Length, ours[role].Length)).SequenceEqual(ours[role].AsSpan(0, Math.Min(captured.Length, ours[role].Length))))
                {
                    var words = Enumerable.Range(0, Math.Min(captured.Length, ours[role].Length) / 4).Where(w => BitConverter.ToUInt32(captured, w * 4) != BitConverter.ToUInt32(ours[role], w * 4))
                        .Select(w => $"+{w * 4} {BitConverter.ToUInt32(captured, w * 4):x8}/{BitConverter.ToUInt32(ours[role], w * 4):x8}");
                    failures.Add($"face {f}: {role} is not the capture's ({string.Join(", ", words)})");
                    output.WriteLine("  " + failures[^1]);
                }
                replay.Overrides[sha] = captured.Length > ours[role].Length ? [.. ours[role], .. captured.AsSpan(ours[role].Length)] : ours[role];
                // CUBEREPLAY_WORDS=1: how many words of each block differ from the capture's (diagnosis).
                if (Environment.GetEnvironmentVariable("CUBEREPLAY_WORDS") == "1")
                {
                    var n = Math.Min(captured.Length, ours[role].Length) / 4;
                    var wrong = Enumerable.Range(0, n).Where(w => BitConverter.ToUInt32(captured, w * 4) != BitConverter.ToUInt32(ours[role], w * 4)).ToList();
                    output.WriteLine($"  {role} {sha[..6]}: {wrong.Count} of {n} words differ{(wrong.Count > 0 ? ": " + string.Join(", ", wrong.Take(24).Select(w => $"+{w * 4} {BitConverter.ToSingle(captured, w * 4):G9}/{BitConverter.ToSingle(ours[role], w * 4):G9}")) : "")}");
                }
            }
            // generic's _Globals_ of each world colour draw from its material (the package names it per draw call).
            var programs = new Dictionary<string, (CubemapFacePipelines.Pass, ValveResourceFormat.CompiledShader.VfxRenderStateInfoPixelShader?)>(FacePrograms.Value);
            var drawMaterials = new Dictionary<int, (string, ValveProgram)>();
            foreach (var (sha, (material, globals)) in MaterialGlobals(actions, sha => File.ReadAllBytes(Path.Combine(dir, "blobs", sha + ".bin")), facesDir, spirvDir, failures, programs, drawMaterials))
            {
                var captured = File.ReadAllBytes(Path.Combine(dir, "blobs", sha + ".bin"));
                if (!captured.AsSpan().SequenceEqual(globals))
                {
                    var words = Enumerable.Range(0, Math.Min(captured.Length, globals.Length) / 4).Where(w => BitConverter.ToUInt32(captured, w * 4) != BitConverter.ToUInt32(globals, w * 4))
                        .Select(w => $"+{w * 4} {BitConverter.ToUInt32(captured, w * 4):x8}/{BitConverter.ToUInt32(globals, w * 4):x8}");
                    failures.Add($"face {f}: {material}'s _Globals_ is not the capture's ({string.Join(", ", words)})");
                    output.WriteLine("  " + failures[^1]);
                }
                replay.Overrides[sha] = globals;
                roles[sha] = "material " + material;
            }
            output.WriteLine($"face {f}: {actions.Count} actions; blocks {string.Join(", ", roles.Values.Order())}");
            // Every vertex and index buffer of the world, sky and downsample draws from the package and the engine's meshes.
            var buffers = BufferInputs(actions, sha => File.ReadAllBytes(Path.Combine(dir, "blobs", sha + ".bin")), facesDir, failures);
            foreach (var (sha, data) in buffers)
                replay.Overrides[sha] = data;
            output.WriteLine($"  buffers: {buffers.Count} ours");
            // Every texture of the world colour and sky draws from the package, the materials and the engine's defaults.
            var textureNotes = new List<string>();
            foreach (var (sha, data) in TextureInputs(actions, sha => File.ReadAllBytes(Path.Combine(dir, "blobs", sha + ".bin")), facesDir, drawMaterials, SkyProgram.Value, failures, textureNotes))
                replay.ImageOverrides[sha] = data;
            foreach (var n in textureNotes.Distinct())
                output.WriteLine("  textures: " + n);
            output.WriteLine($"  textures: {replay.ImageOverrides.Count} subresources ours");
            // Every draw's fixed-function state from its programs and pass (CubemapFacePipelines), field by field
            // against the capture's, and drawn with ours; the draws of programs not ported keep the capture's.
            var keptPipelines = 0;
            var oursSamplers = 0;
            var oursLayouts = 0;
            replay.SamplerHook = (r, smp) =>
            {
                if ((int)smp["bind"]!["set"]! != 1)
                    return null;
                Dictionary<int, SamplerCreateInfo>? slots = null;
                if (drawMaterials.TryGetValue((int)r["eid"]!, out var dm))
                    slots = SamplerSlots.GetOrAdd(dm.Item1, path =>
                    {
                        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game");
                        return CubemapFaceSamplers.Samplers(dm.Item2, new ProgramGlobals.MaterialScope(LoadMaterial(game, path), dm.Item2, new Dictionary<string, VfxExpression.Value>()));
                    });
                else if (r["stages"]?["ps"] is { } ps && Sha1(File.ReadAllBytes(Path.Combine(spirvDir, $"{int.Parse(((string)ps["shader"]!).Split(' ')[^1], System.Globalization.CultureInfo.InvariantCulture):x}.spv"))) == Sha1(SkyProgram.Value.Spirv))
                    slots = SamplerSlots.GetOrAdd("sky", _ => CubemapFaceSamplers.Samplers(SkyProgram.Value));
                if (slots == null || !slots.TryGetValue((int)smp["bind"]!["binding"]! - 14, out var ours))
                    return null;
                var theirs = Replay.CapturedSampler(smp["sampler"]!);
                foreach (var difference in SamplerDifferences(theirs, ours))
                    failures.Add($"face {f} {r["eid"]} sampler b{smp["bind"]!["binding"]}: {difference}");
                oursSamplers++;
                return ours;
            };
            replay.StateHook = (r, theirs) =>
            {
                if (OurPipeline(r, theirs, programs) is not { } ours)
                {
                    keptPipelines++;
                    return theirs;
                }
                // The vertex layout from the vertex program's inputs over the mesh.
                if (VertexPrograms.Value.TryGetValue(Sha1(theirs.VertexSpirv), out var vp))
                {
                    var vb0 = (string)r["vertexInput"]!["vertexBuffers"]![0]!["resourceId"]!;
                    var model = vp.Mesh == "model"
                        ? PackageModels.Value.GetOrAdd(Directory.GetFiles(facesDir, "*.vpk").Single(), LoadModels).FirstOrDefault(m => m.VertexSha == (string)r["contents"]!["before"]![vb0]!["sha1"]!)
                        : null;
                    var (fields, stride) = vp.Mesh switch
                    {
                        "model" when model != null => (model.Fields, model.Stride),
                        "sky" => (CubemapFaceVertexLayout.SkyCube, 12u),
                        "quad" => (CubemapFaceVertexLayout.DownsampleQuad, 32u),
                        _ => (null, 0u),
                    };
                    if (fields != null)
                    {
                        var instanced = vp.Mesh != "quad";
                        ours = ours with { Attributes = CubemapFaceVertexLayout.Attributes(vp.Program, fields, instanced), Bindings = CubemapFaceVertexLayout.Bindings(stride, instanced) };
                        var a = string.Join(" ", theirs.Attributes.OrderBy(x => x.Location).Select(x => $"{x.Location}:{x.Binding}:{x.Format}:{x.Offset}"));
                        var b = string.Join(" ", ours.Attributes.OrderBy(x => x.Location).Select(x => $"{x.Location}:{x.Binding}:{x.Format}:{x.Offset}"));
                        if (a != b)
                            failures.Add($"face {f} {r["eid"]}: attributes {a} captured, {b} ours");
                        var bt = string.Join(" ", theirs.Bindings.Select(x => $"{x.Stride}{(x.PerInstance ? "i" : "v")}"));
                        var bo = string.Join(" ", ours.Bindings.Select(x => $"{x.Stride}{(x.PerInstance ? "i" : "v")}"));
                        // The capture lists every bound stream; ours name the ones a layout uses.
                        if (!bt.StartsWith(bo, StringComparison.Ordinal))
                            failures.Add($"face {f} {r["eid"]}: bindings {bt} captured, {bo} ours");
                        oursLayouts++;
                    }
                }
                foreach (var difference in PipelineDifferences(theirs, ours))
                    failures.Add($"face {f} {r["eid"]}: {difference}");
                return ours;
            };
            // A face that sees no world (probe01's +Z) draws the sky alone; any other face uses every block.
            foreach (var role in (roles.ContainsValue("world") ? ours.Keys : (IEnumerable<string>)["sky"]).Except(roles.Values))
                failures.Add($"face {f}: no captured block for {role}");
            foreach (var r in actions)
            {
                var result = r["stages"]!["cs"] is not null ? replay.RunCompute(r) : replay.Run(r);
                if (!result.StartsWith("equal", StringComparison.Ordinal))
                {
                    output.WriteLine($"  {r["eid"]} {r["name"]}: {result}");
                    failures.Add($"face {f} {r["eid"]}: {result}");
                }
            }
            output.WriteLine($"  pipelines: {keptPipelines} draws keep the capture's (programs not ported); samplers: {oursSamplers} bindings ours; vertex layouts: {oursLayouts} draws ours");
            var colorName = actions.Where(r => r["outputs"]!.AsArray().Count > 0).Select(r => (string)r["outputs"]![0]!).Last();
            var face = replay.ReadLive(colorName);
            var readback = File.ReadAllBytes(Path.Combine(readbackDir, $"readback_{f}.bin"));
            var differ = Enumerable.Range(0, Math.Min(face.Length, readback.Length) / 2).Count(i => BitConverter.ToUInt16(face, i * 2) != BitConverter.ToUInt16(readback, i * 2));
            output.WriteLine($"  {colorName} against readback_{f}: {(face.Length == readback.Length && differ == 0 ? "equal" : $"{differ} halves differ")}");
            if (face.Length != readback.Length || differ != 0)
                failures.Add($"face {f}: {differ} halves differ from the readback");
            faces.Add(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(face).ToArray());
        }
        var game = File.ReadAllBytes(exrPath);
        var exr = Cubemaps.CubemapFaceExr.Build(faces, 1024, Lighting.Vrad3Exr.Read(game).Height);
        output.WriteLine($"EXR: {(exr.AsSpan().SequenceEqual(game) ? "the game's, byte for byte" : "differs")} ({exr.Length} / {game.Length} bytes)");
        // CUBEREPLAY_EXR_OUT=<dir>: our EXR under the game's name, for GameCubemapTests to build the array from.
        if (Environment.GetEnvironmentVariable("CUBEREPLAY_EXR_OUT") is { Length: > 0 } exrOut)
        {
            Directory.CreateDirectory(exrOut);
            File.WriteAllBytes(Path.Combine(exrOut, Path.GetFileName(exrPath)), exr);
        }
        Assert.Empty(failures);
        Assert.Equal(game, exr);
    }

    /// <summary>
    /// Our programs of the face's passes, by the SHA-1 of their SPIR-V: csgo_depth_only's vertex program (both
    /// D_COMPRESSED_NORMALS_AND_TANGENTS combos; no pixel program, the pass from its target), the sky's and both
    /// depth downsamples' pixel programs with their render state. generic's colour programs come per material
    /// (<see cref="MaterialGlobals"/>).
    /// </summary>
    private static readonly Lazy<Dictionary<string, (CubemapFacePipelines.Pass, ValveResourceFormat.CompiledShader.VfxRenderStateInfoPixelShader?)>> FacePrograms = new(() =>
    {
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game");
        using var csgo = new ValvePak.Package();
        csgo.Read(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"));
        using var core = new ValvePak.Package();
        core.Read(Path.Combine(game, "core", "shaders_vulkan_dir.vpk"));
        var map = new Dictionary<string, (CubemapFacePipelines.Pass, ValveResourceFormat.CompiledShader.VfxRenderStateInfoPixelShader?)>();
        foreach (var compressed in new[] { 0, 1 })
            map[Sha1(ValveProgram.Load(csgo, "csgo_depth_only", "vulkan_50", "vs", new Dictionary<string, int>(), new Dictionary<string, int> { ["D_COMPRESSED_NORMALS_AND_TANGENTS"] = compressed }).Spirv)]
                = (CubemapFacePipelines.Pass.DepthPrepass, null);
        void Pixel(ValvePak.Package pak, string shader, Dictionary<string, int> dynamics, CubemapFacePipelines.Pass pass)
        {
            var program = ValveProgram.Load(pak, shader, "vulkan_50", "ps", new Dictionary<string, int>(), dynamics);
            map[Sha1(program.Spirv)] = (pass, (ValveResourceFormat.CompiledShader.VfxRenderStateInfoPixelShader)program.RenderState);
        }
        Pixel(csgo, "sky", new() { ["D_FOG_TYPE"] = 0 }, CubemapFacePipelines.Pass.Sky);
        Pixel(core, "downsample_depth", new() { ["D_MINMAX_CHECKER_2X2"] = 1, ["D_NON_MSAA_DEPTH"] = 1 }, CubemapFacePipelines.Pass.DownsampleHalf);
        Pixel(core, "downsample_depth", new() { ["D_MAX"] = 1, ["D_NON_MSAA_DEPTH"] = 1 }, CubemapFacePipelines.Pass.DownsampleQuarter);
        return map;
    });

    /// <summary>
    /// Our vertex programs of the face's draws by the SHA-1 of their SPIR-V, each with the mesh it reads: generic's
    /// colour combo (lightmapped; both D_COMPRESSED_NORMALS_AND_TANGENTS combos; the world model's fields),
    /// csgo_depth_only (both combos), the sky (its cube) and the depth downsample (its quad; not instanced).
    /// </summary>
    private static readonly Lazy<Dictionary<string, (ValveProgram Program, string Mesh)>> VertexPrograms = new(() =>
    {
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game");
        using var csgo = new ValvePak.Package();
        csgo.Read(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"));
        using var core = new ValvePak.Package();
        core.Read(Path.Combine(game, "core", "shaders_vulkan_dir.vpk"));
        var map = new Dictionary<string, (ValveProgram, string)>();
        void Add(ValvePak.Package pak, string shader, Dictionary<string, int> statics, Dictionary<string, int> dynamics, string mesh)
        {
            var p = ValveProgram.Load(pak, shader, "vulkan_50", "vs", statics, dynamics);
            map[Sha1(p.Spirv)] = (p, mesh);
        }
        foreach (var compressed in new[] { 0, 1 })
        {
            Add(csgo, "generic", new() { ["S_MODE_TOOLS_VIS"] = 1 }, new() { ["D_BAKED_LIGHTING_FROM_LIGHTMAP"] = 1, ["D_COMPRESSED_NORMALS_AND_TANGENTS"] = compressed }, "model");
            Add(csgo, "csgo_depth_only", [], new() { ["D_COMPRESSED_NORMALS_AND_TANGENTS"] = compressed }, "model");
        }
        Add(csgo, "sky", [], [], "sky");
        Add(core, "downsample_depth", [], [], "quad");
        return map;
    });

    private static readonly Lazy<ValveProgram> SkyProgram = new(() =>
    {
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game");
        using var csgo = new ValvePak.Package();
        csgo.Read(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"));
        return ValveProgram.Load(csgo, "sky", "vulkan_50", "ps", new Dictionary<string, int>(), new Dictionary<string, int> { ["D_FOG_TYPE"] = 0 });
    });

    /// <summary>
    /// Our fixed-function state for a captured draw, or null when its programs are not ours (the viewmodel, the
    /// pawn's AO box). A depth-only draw is a sun cascade's when it draws into the shadow atlas (a depth target with
    /// no stencil), else the prepass's.
    /// </summary>
    private static SceneDrawState? OurPipeline(JsonNode r, SceneDrawState theirs, Dictionary<string, (CubemapFacePipelines.Pass Pass, ValveResourceFormat.CompiledShader.VfxRenderStateInfoPixelShader? State)> programs)
    {
        if (theirs.PixelSpirv is { } ps)
            return programs.TryGetValue(Sha1(ps), out var p) && p.State != null ? CubemapFacePipelines.Apply(theirs, p.Pass, p.State) : null;
        if (!programs.TryGetValue(Sha1(theirs.VertexSpirv), out var v) || v.State != null)
            return null;
        var atlas = theirs.DepthFormat == Silk.NET.Vulkan.Format.D16Unorm;
        return CubemapFacePipelines.Apply(theirs, atlas ? CubemapFacePipelines.Pass.SunShadow : CubemapFacePipelines.Pass.DepthPrepass, null);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<int, SamplerCreateInfo>> SamplerSlots = new();

    /// <summary>Each sampler field where ours is not the capture's.</summary>
    private static IEnumerable<string> SamplerDifferences(SamplerCreateInfo theirs, SamplerCreateInfo ours)
    {
        (string, object, object)[] fields =
        [
            ("mag", theirs.MagFilter, ours.MagFilter), ("min", theirs.MinFilter, ours.MinFilter), ("mip", theirs.MipmapMode, ours.MipmapMode),
            ("u", theirs.AddressModeU, ours.AddressModeU), ("v", theirs.AddressModeV, ours.AddressModeV), ("w", theirs.AddressModeW, ours.AddressModeW),
            ("bias", theirs.MipLodBias, ours.MipLodBias), ("anisotropy", theirs.AnisotropyEnable, ours.AnisotropyEnable),
            ("max anisotropy", theirs.MaxAnisotropy, ours.MaxAnisotropy), ("compare", theirs.CompareEnable, ours.CompareEnable),
            ("compare op", theirs.CompareEnable ? theirs.CompareOp : "-", ours.CompareEnable ? ours.CompareOp : "-"),
            ("min lod", theirs.MinLod, ours.MinLod), ("max lod", theirs.MaxLod, ours.MaxLod), ("border", theirs.BorderColor, ours.BorderColor),
        ];
        foreach (var (name, a, b) in fields)
            if (!Equals(a, b))
                yield return $"{name} {a} captured, {b} ours";
    }

    /// <summary>Each fixed-function field where our state is not the capture's (the colour write mask only with a colour target).</summary>
    private static IEnumerable<string> PipelineDifferences(SceneDrawState theirs, SceneDrawState ours)
    {
        (string, object, object)[] fields =
        [
            ("topology", theirs.Topology, ours.Topology), ("cull", theirs.Cull, ours.Cull), ("front", theirs.Front, ours.Front),
            ("bias", theirs.DepthBias, ours.DepthBias), ("bias constant", theirs.DepthBiasConstant, ours.DepthBiasConstant),
            ("bias slope", theirs.DepthBiasSlope, ours.DepthBiasSlope), ("bias clamp", theirs.DepthBiasClamp, ours.DepthBiasClamp),
            ("depth clamp", theirs.DepthClamp, ours.DepthClamp), ("depth test", theirs.DepthTest, ours.DepthTest),
            ("depth write", theirs.DepthWrite, ours.DepthWrite), ("depth compare", theirs.DepthCompare, ours.DepthCompare),
            ("stencil", theirs.StencilTest, ours.StencilTest), ("stencil front", theirs.StencilFront, ours.StencilFront),
            ("stencil back", theirs.StencilBack, ours.StencilBack), ("blend", theirs.Blend?.ToString() ?? "none", ours.Blend?.ToString() ?? "none"),
            ("min depth", theirs.Viewport.MinDepth, ours.Viewport.MinDepth), ("max depth", theirs.Viewport.MaxDepth, ours.Viewport.MaxDepth),
            ("write mask", theirs.ColorFormat != null ? theirs.ColorWriteMask : "-", theirs.ColorFormat != null ? ours.ColorWriteMask : "-"),
        ];
        foreach (var (name, a, b) in fields)
            if (!Equals(a, b))
                yield return $"{name} {a} captured, {b} ours";
    }

    /// <summary>
    /// generic's pixel _Globals_ (set 1 binding 0) of every world colour draw, from the captured package: the draw's
    /// first vertex stream is a world node model's vertex buffer (byte for byte), its first index and vertex offset
    /// name the draw call, and the draw call names the material. The program is generic's colour combo for the
    /// cubemap view (S_MODE_TOOLS_VIS, lightmapped) with the material's features; its SPIR-V must be the draw's
    /// module, and the block is <see cref="ProgramGlobals.Fill"/> with g_bRenderingToCubemaps = 1 and gameinfo's
    /// ShaderIDColors (ledger 120). By captured block hash: the material and our block.
    /// </summary>
    private static Dictionary<string, (string Material, byte[] Globals)> MaterialGlobals(IEnumerable<JsonNode> actions, Func<string, byte[]> blob, string facesDir, string spirvDir, List<string> failures,
        Dictionary<string, (CubemapFacePipelines.Pass, ValveResourceFormat.CompiledShader.VfxRenderStateInfoPixelShader?)>? programs = null,
        Dictionary<int, (string Material, ValveProgram Program)>? draws = null)
    {
        var models = PackageModels.Value.GetOrAdd(Directory.GetFiles(facesDir, "*.vpk").Single(), LoadModels);
        var result = new Dictionary<string, (string, byte[])>();
        foreach (var r in actions)
        {
            if (r["stages"]?["ps"] is not { } ps || r["outputs"]!.AsArray().Count == 0
                || ps["constantBlocks"]!.AsArray().FirstOrDefault(c => c!["bytes"] is not null && (int)c["block"]!["fixedBindSetOrSpace"]! == 1 && (int)c["block"]!["fixedBindNumber"]! == 0) is not { } cb
                || !r["stages"]!["vs"]!["readWrite"]!.AsArray().Concat(r["stages"]!["vs"]!["readOnly"]!.AsArray()).Any(x => (int)x!["bind"]!["set"]! == 4 && (int)x["bind"]!["binding"]! == 30))
                continue;
            var vb = (string)r["vertexInput"]!["vertexBuffers"]![0]!["resourceId"]!;
            var vbSha = (string)r["contents"]!["before"]![vb]!["sha1"]!;
            if (models.FirstOrDefault(m => m.VertexSha == vbSha) is not { } model)
                continue;   // not a world node model (the viewmodel's draws)
            var (first, baseVertex) = ((int)r["indexOffset"]!, (int)r["baseVertex"]!);
            if (model.Draws.FirstOrDefault(d => d.Start == first && d.BaseVertex == baseVertex) is not { Material: { } materialPath })
            {
                failures.Add($"{r["eid"]}: no draw call of {model.Name} starts at index {first}");
                continue;
            }
            var sha = (string)cb["bytes"]!["sha1"]!;
            var (program, globals) = GlobalsCache.GetOrAdd((materialPath, blob(sha).Length), k => Globals(k.Item1, k.Item2));
            draws?.TryAdd((int)r["eid"]!, (materialPath, program));
            if (result.TryGetValue(sha, out var had) && had.Item1.Split(" = ").Contains(materialPath))
                continue;
            var module = File.ReadAllBytes(Path.Combine(spirvDir, $"{int.Parse(((string)ps["shader"]!).Split(' ')[^1], System.Globalization.CultureInfo.InvariantCulture):x}.spv"));
            if (!program.Spirv.AsSpan().SequenceEqual(module))
                failures.Add($"{r["eid"]}: {materialPath}'s generic colour program is not the draw's module");
            programs?.TryAdd(Sha1(program.Spirv), (CubemapFacePipelines.Pass.Colour, (ValveResourceFormat.CompiledShader.VfxRenderStateInfoPixelShader)program.RenderState));
            // Materials whose blocks are the same bytes share one captured block (the game uploads by content).
            if (had.Item2 != null && !had.Item2.AsSpan().SequenceEqual(globals))
                failures.Add($"{r["eid"]}: block {sha[..8]} serves {had.Item1} and {materialPath}, whose blocks differ");
            result[sha] = (had.Item1 != null ? had.Item1 + " = " + materialPath : materialPath, globals);
        }
        return result;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int), (ValveProgram, byte[])> GlobalsCache = new();

    /// <summary>
    /// Every vertex and index buffer the world, sky and downsample draws read, from the package and the engine's
    /// meshes (<see cref="Cubemaps.CubemapFaceMeshes"/>), by captured buffer hash: a world draw's first stream is its
    /// model's vertex buffer (the same bytes), the pooled index buffer holds each model's indices whole at the
    /// offset the draws bind it at (the pool's allocation, the capture's), the second stream is the instance ids,
    /// the third a 16-byte zero; the sky draws the engine's cube; a downsample its quad at the offset it binds. Each
    /// range a draw reads must be the capture's.
    /// </summary>
    private static Dictionary<string, byte[]> BufferInputs(IEnumerable<JsonNode> actions, Func<string, byte[]> blob, string facesDir, List<string> failures)
    {
        var models = PackageModels.Value.GetOrAdd(Directory.GetFiles(facesDir, "*.vpk").Single(), LoadModels);
        var result = new Dictionary<string, byte[]>();
        byte[] Ours(string sha) => result.TryGetValue(sha, out var b) ? b : result[sha] = new byte[blob(sha).Length];
        void Check(JsonNode r, string what, byte[] ours, byte[] theirs, int offset, int length)
        {
            if (offset + length > theirs.Length || !ours.AsSpan(offset, length).SequenceEqual(theirs.AsSpan(offset, length)))
                failures.Add($"{r["eid"]}: {what} at {offset} ({length} bytes) is not the capture's");
        }
        foreach (var r in actions)
        {
            if (r["vertexInput"]?["vertexBuffers"] is not JsonArray vbs || vbs.Count == 0 || r["contents"] is null)
                continue;
            var before = r["contents"]!["before"]!;
            string Sha(string name) => (string)before[name]!["sha1"]!;
            var vb0 = (string)vbs[0]!["resourceId"]!;
            var indexed = ((string)r["flags"]!).Contains("Indexed");
            if (models.FirstOrDefault(m => m.VertexSha == Sha(vb0)) is { } model)
            {
                // The model's own vertex buffer, then the pooled indices it draws from.
                result[Sha(vb0)] = model.Vertices;
                if (indexed)
                {
                    var ib = r["ibuffer"]!;
                    var pool = Ours(Sha((string)ib["resourceId"]!));
                    var at = (int)(long)ib["byteOffset"]!;
                    model.Indices.CopyTo(pool, at);
                    var stride = (int)ib["byteStride"]!;
                    Check(r, $"{model.Name}'s indices", pool, blob(Sha((string)ib["resourceId"]!)), at + (int)r["indexOffset"]! * stride, (int)r["numIndices"]! * stride);
                }
                for (var i = 1; i < vbs.Count; i++)
                {
                    var name = (string)vbs[i]!["resourceId"]!;
                    var stride = (int)vbs[i]!["byteStride"]!;
                    var data = Ours(Sha(name));
                    if (stride == 4)
                        Cubemaps.CubemapFaceMeshes.InstanceIds(data.Length).CopyTo(data, 0);
                    Check(r, stride == 4 ? "instance ids" : "the zero stream", data, blob(Sha(name)), 0, data.Length);
                }
                continue;
            }
            var attributes = r["vertexInput"]!["attributes"]!.AsArray();
            if (indexed && (int)vbs[0]!["byteStride"]! == 12 && (int)r["numIndices"]! == 36)
            {
                // The sky's cube.
                var vertices = Ours(Sha(vb0));
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(Cubemaps.CubemapFaceMeshes.SkyCubeVertices.AsSpan()).CopyTo(vertices);
                Check(r, "the sky cube's corners", vertices, blob(Sha(vb0)), 0, vertices.Length);
                var ib = (string)r["ibuffer"]!["resourceId"]!;
                var indices = Ours(Sha(ib));
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(Cubemaps.CubemapFaceMeshes.SkyCubeIndices.AsSpan()).CopyTo(indices);
                Check(r, "the sky cube's indices", indices, blob(Sha(ib)), 0, indices.Length);
                foreach (var extra in vbs.Skip(1))
                {
                    var name = (string)extra!["resourceId"]!;
                    var data = Ours(Sha(name));
                    if ((int)extra["byteStride"]! == 4)
                        Cubemaps.CubemapFaceMeshes.InstanceIds(data.Length).CopyTo(data, 0);
                }
                continue;
            }
            if (!indexed && (int)vbs[0]!["byteStride"]! == 32 && (int)r["numIndices"]! == 4 && attributes.Count == 1)
            {
                // A downsample's quad: only the positions are read (the layout's one attribute).
                var ring = Ours(Sha(vb0));
                var at = (int)(long)vbs[0]!["byteOffset"]!;
                Cubemaps.CubemapFaceMeshes.DownsampleQuad().CopyTo(ring, at);
                var theirs = blob(Sha(vb0));
                for (var v = 0; v < 4; v++)
                    Check(r, "the quad's positions", ring, theirs, at + v * 32, 8);
                foreach (var extra in vbs.Skip(1))
                    Ours(Sha((string)extra!["resourceId"]!));
            }
        }
        return result;
    }

    /// <summary>
    /// Every texture the world colour and sky draws read, from our data, by captured subresource hash: each binding's
    /// variable in the draw's program (set 1, binding 30 + its slot in the combo's write sequence): the lightmap
    /// attributes (direct_light_shadows, irradiance, directional_irradiance, debug_chart_color) are the package's
    /// lightmaps/*.vtex_c, the artist textures the material's .vtex_c (and the sky's g_tSkyTexture its material's),
    /// SSAOOcclusionFactor and ParticleShadowBuffer a 1 x 1 white, CubemapFogTexture a 1 x 1 cube of zeros (no fog
    /// cube in the view), LightCookieTexture and EnvironmentMap zeros: no light or environment map is binned in the
    /// cubemap view, so neither is read (the face's output checks it). Each captured subresource must be ours where
    /// we know the content; the shadow atlas and AO targets are live.
    /// </summary>
    private static Dictionary<string, byte[]> TextureInputs(IEnumerable<JsonNode> actions, Func<string, byte[]> blob, string facesDir,
        Dictionary<int, (string Material, ValveProgram Program)> draws, ValveProgram sky, List<string> failures, List<string> notes)
    {
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game");
        var vpk = Directory.GetFiles(facesDir, "*.vpk").Single();
        var result = new Dictionary<string, byte[]>();
        void Check(JsonNode r, string binding, JsonArray subs, Func<int, int, int, byte[]> ours, string what)
        {
            foreach (var sub in subs)
            {
                var sha = (string)sub!["sha1"]!;
                var data = ours((int)sub["mip"]!, (int)sub["slice"]!, blob(sha).Length);
                if (data.Length != blob(sha).Length || !data.AsSpan().SequenceEqual(blob(sha)))
                {
                    // Zeros stand in for contents no pixel reads; anything else must be the capture's.
                    if (!what.EndsWith("(unread)", StringComparison.Ordinal))
                        failures.Add($"{r["eid"]} {binding} {what} mip {sub["mip"]} slice {sub["slice"]}: not the capture's ({data.Length} / {blob(sha).Length} bytes)");
                }
                result[sha] = data;
            }
        }
        foreach (var r in actions)
        {
            if (r["stages"]?["ps"] is not { } ps)
                continue;
            var eid = (int)r["eid"]!;
            ValveProgram? program = null;
            string? material = null;
            if (draws.TryGetValue(eid, out var d))
                (material, program) = d;
            else if (((string)ps["shader"]!).Split(' ')[^1] is var module && r["stages"]!["ps"] is not null
                     && ps["readOnly"]!.AsArray().Any(x => (int)x!["bind"]!["set"]! == 1 && (int)x["bind"]!["binding"]! == 50) && r["outputs"]!.AsArray().Count > 0
                     && !r["stages"]!["vs"]!["readWrite"]!.AsArray().Concat(r["stages"]!["vs"]!["readOnly"]!.AsArray()).Any(x => (int)x!["bind"]!["set"]! == 4))
                (material, program) = ("materials/editor/toolscene_lighting_sky_dust.vmat", sky);
            if (program == null)
                continue;
            var slots = program.Sequence.RenderState.Select(f => (f.LayoutSet, f.BindingSlot, Variable: program.Program.VariableDescriptions[f.VariableIndex]))
                .Where(f => f.Variable.RegisterType == VfxRegisterType.Texture).ToList();
            foreach (var x in ps["readOnly"]!.AsArray())
            {
                var (set, binding) = ((int)x!["bind"]!["set"]!, (int)x["bind"]!["binding"]!);
                var name = (string)x["descriptor"]!["resource"]!;
                if (set != 1 || r["contents"]!["before"]![name] is not JsonArray subs)
                    continue;
                var v = slots.FirstOrDefault(f => f.LayoutSet == 1 && f.BindingSlot + 30 == binding).Variable;
                if (v == null)
                {
                    notes.Add($"{eid} set 1 binding {binding}: no variable of {program.Name}");
                    continue;
                }
                var label = $"b{binding} {v.Name}";
                if (v.VariableSource == VfxVariableSourceType.__SetByArtist__)
                {
                    var path = LoadMaterial(game, material!).TextureParams.GetValueOrDefault(v.Name);
                    if (path == null)
                    {
                        failures.Add($"{eid} {label}: {material} names no texture");
                        continue;
                    }
                    var tex = TextureCache.GetOrAdd(path, p2 => LoadTexture(game, vpk, p2));
                    Check(r, label, subs, (mip, slice, _) => tex(mip, slice), $"{path}");
                    continue;
                }
                switch (v.SourceString)
                {
                    case "direct_light_shadows" or "irradiance" or "directional_irradiance" or "debug_chart_color":
                        var lightmap = TextureCache.GetOrAdd(vpk + "|" + v.SourceString, _ => LoadLightmap(vpk, v.SourceString));
                        Check(r, label, subs, (mip, slice, _) => lightmap(mip, slice), $"lightmaps/{v.SourceString}");
                        break;
                    case "SSAOOcclusionFactor" or "ParticleShadowBuffer":
                        Check(r, label, subs, (_, _, n) => Enumerable.Repeat((byte)0xff, n).ToArray(), "white");
                        break;
                    case "CubemapFogTexture":
                        Check(r, label, subs, (_, _, n) => new byte[n], "no fog cube");
                        break;
                    case "LightCookieTexture" or "EnvironmentMap":
                        Check(r, label, subs, (_, _, n) => new byte[n], $"{v.SourceString} (unread)");
                        break;
                    case "ShadowDepthBuffer" or "DynamicAmbientOcclusion" or "DynamicAmbientOcclusionDepth":
                        break;   // live targets of the face
                    default:
                        notes.Add($"{eid} {label}: attribute {v.SourceString} not built");
                        break;
                }
            }
        }
        return result;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Func<int, int, byte[]>> TextureCache = new();

    /// <summary>A .vtex_c's subresources (mip, slice) as RenderDoc dumps them: a cube or array mip's layers one after another.</summary>
    private static Func<int, int, byte[]> Subresources(byte[] vtex)
    {
        using var resource = new ValveResourceFormat.Resource();
        resource.Read(new MemoryStream(vtex));
        var tex = (Texture)resource.DataBlock!;
        var layers = tex.Depth > 1 ? tex.Depth : (tex.Flags & ValveResourceFormat.VTexFlags.CUBE_TEXTURE) != 0 ? 6 : 1;
        var mips = new Dictionary<uint, byte[]>();
        foreach (var (level, _, _, _, size) in tex.GetEveryMipLevelMetrics())
        {
            var data = new byte[size];
            tex.ReadTextureMipLevel(data, level);
            mips[level] = data;
        }
        return (mip, slice) => mips[(uint)mip].AsSpan(slice * (mips[(uint)mip].Length / layers), mips[(uint)mip].Length / layers).ToArray();
    }

    private static Func<int, int, byte[]> LoadTexture(string game, string vpk, string path)
    {
        foreach (var pak in new[] { vpk, Path.Combine(game, "csgo", "pak01_dir.vpk"), Path.Combine(game, "core", "pak01_dir.vpk") })
        {
            using var p = new ValvePak.Package();
            p.Read(pak);
            if (p.FindEntry(path + "_c") is { } e)
            {
                p.ReadEntry(e, out var bytes);
                return Subresources(bytes);
            }
        }
        throw new FileNotFoundException(path);
    }

    private static Func<int, int, byte[]> LoadLightmap(string vpk, string name)
    {
        using var p = new ValvePak.Package();
        p.Read(vpk);
        var entry = (p.Entries!.GetValueOrDefault("vtex_c") ?? []).Single(e => e.DirectoryName.EndsWith("lightmaps", StringComparison.Ordinal) && e.FileName == name);
        p.ReadEntry(entry, out var bytes);
        return Subresources(bytes);
    }

    /// <summary>A world node model of a package: its vertex buffer's hash and its draw calls' ranges and materials.</summary>
    private sealed record PackageModel(string Name, string VertexSha, List<(int Start, int Count, int BaseVertex, string? Material)> Draws, byte[] Vertices, byte[] Indices, int IndexSize,
        CubemapFaceVertexLayout.Field[] Fields, uint Stride);

    private static readonly Lazy<System.Collections.Concurrent.ConcurrentDictionary<string, List<PackageModel>>> PackageModels = new(() => new());

    private static List<PackageModel> LoadModels(string vpk)
    {
        using var package = new ValvePak.Package();
        package.Read(vpk);
        var models = new List<PackageModel>();
        foreach (var entry in package.Entries!.GetValueOrDefault("vmdl_c") ?? [])
        {
            if (!entry.DirectoryName.Contains("worldnodes", StringComparison.OrdinalIgnoreCase))
                continue;
            package.ReadEntry(entry, out var bytes);
            using var resource = new ValveResourceFormat.Resource();
            resource.Read(new MemoryStream(bytes));
            foreach (var (mesh, _, _, _) in ((Model)resource.DataBlock!).GetEmbeddedMeshesAndLoD())
            {
                var draws = mesh.Data.GetArray("m_sceneObjects").SelectMany(so => so.GetArray("m_drawCalls"))
                    .Select(dc => (dc.GetInt32Property("m_nStartIndex"), dc.GetInt32Property("m_nIndexCount"), dc.GetInt32Property("m_nBaseVertex"), dc.GetStringProperty("m_material"))).ToList();
                if (mesh.VBIB.VertexBuffers.Count > 0)
                    models.Add(new PackageModel(entry.GetFullPath(), Sha1(mesh.VBIB.VertexBuffers[0].Data), draws,
                        mesh.VBIB.VertexBuffers[0].Data, mesh.VBIB.IndexBuffers[0].Data, (int)mesh.VBIB.IndexBuffers[0].ElementSizeInBytes,
                        [.. mesh.VBIB.VertexBuffers[0].InputLayoutFields.Select(x => new CubemapFaceVertexLayout.Field(x.SemanticName, x.SemanticIndex, CubemapFaceVertexLayout.FromDxgi(x.Format.ToString()), x.Offset))],
                        mesh.VBIB.VertexBuffers[0].ElementSizeInBytes));
            }
        }
        return models;
    }

    private static string Sha1(byte[] data) => Convert.ToHexStringLower(SHA1.HashData(data));

    /// <summary>generic's colour program for a material in the cubemap view and its _Globals_ (<paramref name="size"/> bytes).</summary>
    private static (ValveProgram Program, byte[] Globals) Globals(string materialPath, int size)
    {
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game");
        var material = LoadMaterial(game, materialPath);
        using var shaders = new ValvePak.Package();
        shaders.Read(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"));
        var program = ValveProgram.Load(shaders, "generic", "vulkan_50", "ps", new Dictionary<string, int> { ["S_MODE_TOOLS_VIS"] = 1 },
            new Dictionary<string, int> { ["D_BAKED_LIGHTING_FROM_LIGHTMAP"] = 1 }, material.IntParams.ToDictionary(kv => kv.Key, kv => (int)kv.Value));
        var (r, g, b) = ProgramGlobals.ShaderIdColors(File.ReadAllText(Path.Combine(game, "csgo", "gameinfo.gi"))).GetValueOrDefault(material.ShaderName, (0, 0, 0));
        var attributes = new Dictionary<string, VfxExpression.Value>(StringComparer.OrdinalIgnoreCase) { ["g_bRenderingToCubemaps"] = VfxExpression.Value.Of(1f) };
        var block = new byte[size];
        ProgramGlobals.Fill(program, block, new ProgramGlobals.MaterialScope(material, program, attributes, ProgramGlobals.ShaderIdColor(r, g, b)), (_, _) => -1, _ => -1);
        return (program, block);
    }

    private static Material LoadMaterial(string game, string path)
    {
        foreach (var pak in new[] { Path.Combine(game, "csgo", "pak01_dir.vpk"), Path.Combine(game, "core", "pak01_dir.vpk") })
        {
            using var p = new ValvePak.Package();
            p.Read(pak);
            if (p.FindEntry(path + "_c") is { } e)
            {
                p.ReadEntry(e, out var bytes);
                using var resource = new ValveResourceFormat.Resource();
                resource.Read(new MemoryStream(bytes));
                return (Material)resource.DataBlock!;
            }
        }
        throw new FileNotFoundException(path);
    }

    /// <summary>
    /// Which of our blocks each captured constant block or buffer of a face's actions is, by where it is bound: set 0/1
    /// binding 1 (640 bytes) is a per-view block, the sky's when its depth range (+368) starts at 1, a sun cascade's when
    /// drawn into the D16 shadow atlas (cascade by the viewport's tile); binding 3 PerViewConstantBufferCsgo_t, set 1
    /// binding 4 BakedLightingConstantBuffer_t, set 3 binding 0 the light block, set 4 bindings 30 and 32 the transforms
    /// and instances. A dispatch's set 0 binding 1 (160 bytes) is the binner's CullParams_t. The sky draw's vertex
    /// $Globals (set 0 binding 0, 64 bytes) is g_matSkyWsToOs and its pixel $Globals (set 1 binding 0) the sky
    /// material's; a 16-byte pixel $Globals is a depth downsample's, the half or quarter resolution layer's by its
    /// viewport (the face's half or quarter).
    /// </summary>
    private static Dictionary<string, string> BlockRoles(IEnumerable<JsonNode> actions, Func<string, byte[]> blob)
    {
        var roles = new Dictionary<string, string>();
        var viewmodel = new HashSet<string>();
        void Add(string sha, string role)
        {
            if (roles.TryGetValue(sha, out var had) && had != role)
                throw new InvalidOperationException($"{sha} is both {had} and {role}");
            roles[sha] = role;
        }
        foreach (var r in actions)
        {
            if (r["stages"]?["cs"] is { } cs)
                foreach (var cb in cs["constantBlocks"]!.AsArray())
                    if (cb!["bytes"] is { } bytes && (int)cb["block"]!["fixedBindNumber"]! == 1 && (long)bytes["size"]! == 160)
                        Add((string)bytes["sha1"]!, "binner");
            if (r["stages"]?["vs"] is null)
                continue;
            var vp = r["viewportScissor"]!["viewportScissors"]![0]!["vp"]!;
            var sky = r["stages"]!["vs"]!["constantBlocks"]!.AsArray().Any(c => c!["bytes"] is { } b && (int)c["block"]!["fixedBindSetOrSpace"]! == 0
                && (int)c["block"]!["fixedBindNumber"]! == 0 && (long)b["size"]! == 64);
            var depthName = (string?)r["depthOut"];
            var atlas = depthName != null && r["outputs"]!.AsArray().Count == 0 && depthName.Contains("Depth Attachment", StringComparison.Ordinal) && !depthName.Contains("Stencil", StringComparison.Ordinal);
            // The local player's first-person viewmodel (s2c_l5probe's build had a spawned pawn): its own per-view
            // block, depth range 0 .. 0.1 (+368, +372) where the world's ends at 0.95, and its own scene and light
            // blocks. Not ported: the blocks only its draws read stay the capture's.
            if (!atlas && r["stages"]!["vs"]!["constantBlocks"]!.AsArray().FirstOrDefault(c => c!["bytes"] is { } b && (int)c["block"]!["fixedBindNumber"]! == 1
                    && (long)b["size"]! == 640) is { } view && BitConverter.ToSingle(blob((string)view["bytes"]!["sha1"]!), 372) is var maxDepth && maxDepth != 0.95f && maxDepth != 1f)
            {
                foreach (var stage in new[] { r["stages"]!["vs"], r["stages"]!["ps"] }.Where(s => s != null))
                    foreach (var cb in stage!["constantBlocks"]!.AsArray())
                        if (cb!["bytes"] is { } bytes && (cb["block"]!["bufferBacked"] is not { } backed || (bool)backed))
                            viewmodel.Add((string)bytes["sha1"]!);
                continue;
            }
            foreach (var stage in new[] { r["stages"]!["vs"], r["stages"]!["ps"] }.Where(s => s != null))
            {
                foreach (var cb in stage!["constantBlocks"]!.AsArray())
                {
                    var block = cb!["block"]!;
                    if (cb["bytes"] is not { } bytes || block["bufferBacked"] is { } backed && !(bool)backed)
                        continue;
                    var sha = (string)bytes["sha1"]!;
                    var (set, binding, size) = ((int)block["fixedBindSetOrSpace"]!, (int)block["fixedBindNumber"]!, (long)bytes["size"]!);
                    if (binding == 1 && size == 640)
                    {
                        if (atlas)
                        {
                            var (x, y) = ((int)(float)vp["x"]!, (int)(float)vp["y"]!);
                            Add(sha, $"cascade{Array.FindIndex(CascadeTiles, t => t[0] == x && t[1] == y)}");
                        }
                        else
                            Add(sha, BitConverter.ToSingle(blob(sha), 368) == 1f ? "sky" : "world");
                    }
                    else if (binding == 3 && size >= 656)   // the bound range can be longer (1024 on cube 1's -X)
                        Add(sha, "csgo");
                    else if (set == 1 && binding == 4 && size == 64)
                        Add(sha, "baked");
                    else if (set == 3 && binding == 0 && size == 32256)
                        Add(sha, "lights");
                    else if (sky && set == 0 && binding == 0 && size == 64)
                        Add(sha, "skyview");
                    else if (sky && set == 1 && binding == 0)
                        Add(sha, "skyglobals");
                    else if (set == 1 && binding == 0 && size == 16)
                        Add(sha, (float)vp["width"]! == 512f ? "downsample_half" : (float)vp["width"]! == 256f ? "downsample_quarter" : $"downsample at {vp["width"]}");
                }
                // Storage buffers the shaders only read are still declared read-write.
                foreach (var rw in stage["readWrite"]!.AsArray().Concat(stage["readOnly"]!.AsArray()))
                {
                    var bind = rw!["bind"]!;
                    if ((int)bind["set"]! == 4 && (int)bind["binding"]! is 30 or 32
                        && r["contents"]!["before"]![(string)rw["descriptor"]!["resource"]!] is JsonObject buffer)
                        Add((string)buffer["sha1"]!, (int)bind["binding"]! == 30 ? "transforms" : "instances");
                }
            }
        }
        // A block the viewmodel shares with the world's draws (the prepass's light block) is the world's: the same bytes.
        return roles;
    }

    private static readonly int[][] CascadeTiles = [[0, 0], [1280, 0], [2560, 0], [0, 2048]];

    /// <summary>
    /// The $Globals that Valve's programs compute from a material and render attributes (<see cref="ProgramGlobals.Fill"/>):
    /// the sky's from env_sky's material and SkyTint (white on probe01), and the depth downsamples' g_nDownsampleRatioW/H
    /// from the g_nDownsampleRatio attribute: the half resolution layer sets r_csgo_mixed_resolution_particles_scale (2,
    /// client 1811e7ae0), the quarter one 4 (1811d84d0).
    /// </summary>
    private static readonly Lazy<Dictionary<string, byte[]>> ProgramBlocks = new(() =>
    {
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game");
        Material Load(string path)
        {
            foreach (var pak in new[] { Path.Combine(game, "csgo", "pak01_dir.vpk"), Path.Combine(game, "core", "pak01_dir.vpk") })
            {
                using var p = new ValvePak.Package();
                p.Read(pak);
                if (p.FindEntry(path + "_c") is { } e)
                {
                    p.ReadEntry(e, out var bytes);
                    using var resource = new ValveResourceFormat.Resource();
                    resource.Read(new MemoryStream(bytes));
                    return (Material)resource.DataBlock!;
                }
            }
            throw new FileNotFoundException(path);
        }
        byte[] Globals(string vpk, string shader, Dictionary<string, int> dynamics, Material material, Dictionary<string, VfxExpression.Value> attributes, int size)
        {
            using var shaders = new ValvePak.Package();
            shaders.Read(Path.Combine(game, vpk, "shaders_vulkan_dir.vpk"));
            var program = ValveProgram.Load(shaders, shader, "vulkan_50", "ps", new Dictionary<string, int>(), dynamics, material.IntParams.ToDictionary(kv => kv.Key, kv => (int)kv.Value));
            var block = new byte[size];
            // Render attributes are string tokens of the lowered name: an expression asks for "g_ndownsampleratio".
            var tokens = new Dictionary<string, VfxExpression.Value>(attributes, StringComparer.OrdinalIgnoreCase);
            ProgramGlobals.Fill(program, block, new ProgramGlobals.MaterialScope(material, program, tokens), (_, _) => -1, _ => -1);
            return block;
        }
        var sky = Load("materials/editor/toolscene_lighting_sky_dust.vmat");
        var downsample = Load("materials/dev/downsample_depth.vmat");
        return new()
        {
            ["skyglobals"] = Globals("csgo", "sky", new() { ["D_FOG_TYPE"] = 0 }, sky, new() { ["SkyTint"] = VfxExpression.Value.Of(1f, 1f, 1f, 0f) }, 128),
            ["downsample_half"] = Globals("core", "downsample_depth", new() { ["D_MINMAX_CHECKER_2X2"] = 1, ["D_NON_MSAA_DEPTH"] = 1 }, downsample,
                new() { ["g_nDownsampleRatio"] = VfxExpression.Value.Of(2f) }, 16),
            ["downsample_quarter"] = Globals("core", "downsample_depth", new() { ["D_MAX"] = 1, ["D_NON_MSAA_DEPTH"] = 1 }, downsample,
                new() { ["g_nDownsampleRatio"] = VfxExpression.Value.Of(4f) }, 16),
        };
    });

    /// <summary>Which instance / transform fields the colour draws read: variants of the two buffers, each replayed.</summary>
    [Fact]
    public void InstanceVariants()
    {
        if (Environment.GetEnvironmentVariable("CUBEREPLAY") is not { Length: > 0 } dir
            || Environment.GetEnvironmentVariable("CUBEREPLAY_SPIRV") is not { Length: > 0 } spirvDir
            || Environment.GetEnvironmentVariable("CUBEREPLAY_INSTANCES") != "1")
            return;
        string Sha(string prefix) => Path.GetFileNameWithoutExtension(Directory.GetFiles(Path.Combine(dir, "blobs"), prefix + "*.bin").Single());
        var (instSha, xfSha) = (Sha("5708ccca"), Sha("40190a9e"));
        var inst = File.ReadAllBytes(Path.Combine(dir, "blobs", instSha + ".bin"));
        var xf = File.ReadAllBytes(Path.Combine(dir, "blobs", xfSha + ".bin"));
        var resources = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "resources.json")))!;
        using var gpu = new GpuDevice(rayTracingShaderFeatures: Environment.GetEnvironmentVariable("CUBEREPLAY_BASIC_DEVICE") != "1");
        var runner = new SceneDrawRunner(gpu);
        using var replay = new Replay(dir, spirvDir, resources, gpu, runner);
        var draws = File.ReadLines(Path.Combine(dir, "actions.jsonl")).Select(l => JsonNode.Parse(l)!)
            .Where(r => r["contents"] is not null && r["stages"]?["vs"] is not null && (int)r["eid"]! >= 354 && (int)r["eid"]! <= 485).ToList();
        void Try(string name, byte[]? i, byte[]? x)
        {
            replay.Overrides.Clear();
            if (i != null) replay.Overrides[instSha] = i;
            if (x != null) replay.Overrides[xfSha] = x;
            var bad = draws.Where(d => !replay.Run(d).StartsWith("equal", StringComparison.Ordinal)).Select(d => (int)d["eid"]!).ToList();
            output.WriteLine($"{name}: {(bad.Count == 0 ? "equal" : "differ at " + string.Join(",", bad))}");
        }
        Try("captured", null, null);
        // Each word of the world records (records 1..63) changed in turn to the null record's value or 0.
        uint[] alt = [0, 0, 0, 0xffffffff, 0xffffffff, 0, 0x3f800000, 0];
        for (var w = 0; w < 8; w++)
        {
            var m = (byte[])inst.Clone();
            for (var r = 1; r < 64; r++)
                if (BitConverter.ToUInt32(m, r * 32 + 4) is 4 or 5)
                    BitConverter.TryWriteBytes(m.AsSpan(r * 32 + w * 4), w == 1 ? (BitConverter.ToUInt32(m, r * 32 + 4) == 4 ? 2u : 3u) : alt[w]);
            var x2 = (byte[])xf.Clone();
            if (w == 1) { xf.AsSpan(192, 96).CopyTo(x2.AsSpan(96)); }   // transforms 4, 5 copied to 2, 3
            Try($"word {w}", m, w == 1 ? x2 : null);
        }
    }

    /// <summary>
    /// <c>CUBEREPLAY_SENSE=&lt;sha1 prefix&gt;</c>: which words of a captured block or buffer the face depends on.
    /// Each non-zero word is zeroed and, separately, has its lowest bit flipped; each all-zero 16-byte row is set
    /// to 1.0s; the actions that read the blob run again and any change to their targets marks the word.
    /// </summary>
    [Fact]
    public void Sensitivity()
    {
        if (Environment.GetEnvironmentVariable("CUBEREPLAY") is not { Length: > 0 } dir
            || Environment.GetEnvironmentVariable("CUBEREPLAY_SPIRV") is not { Length: > 0 } spirvDir
            || Environment.GetEnvironmentVariable("CUBEREPLAY_SENSE") is not { Length: > 0 } prefix)
            return;
        var sha = Directory.GetFiles(Path.Combine(dir, "blobs"), prefix + "*.bin").Select(Path.GetFileNameWithoutExtension).Single()!;
        var data = File.ReadAllBytes(Path.Combine(dir, "blobs", sha + ".bin"));
        var users = File.ReadLines(Path.Combine(dir, "actions.jsonl")).Select(l => JsonNode.Parse(l)!)
            .Where(r => r["contents"] is not null && r["stages"]?["vs"] is not null && r.ToJsonString().Contains(sha, StringComparison.Ordinal)).ToList();
        output.WriteLine($"{sha}: {data.Length} bytes, read by {string.Join(", ", users.Select(u => (int)u["eid"]!))}");
        var resources = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "resources.json")))!;
        using var gpu = new GpuDevice(rayTracingShaderFeatures: Environment.GetEnvironmentVariable("CUBEREPLAY_BASIC_DEVICE") != "1");
        var runner = new SceneDrawRunner(gpu);
        using var replay = new Replay(dir, spirvDir, resources, gpu, runner);
        if (replay.Baselines != null)
            foreach (var u in users)
                replay.Run(u);
        bool Changes(byte[] modified)
        {
            replay.Overrides[sha] = modified;
            var any = users.Any(u => !replay.Run(u).StartsWith("equal", StringComparison.Ordinal));
            replay.Overrides.Remove(sha);
            return any;
        }
        if (Environment.GetEnvironmentVariable("CUBEREPLAY_SENSE_WHOLE") == "1")
        {
            // The whole blob zeroed, and the whole blob set to 0xff bytes.
            output.WriteLine($"zeroed: {(Changes(new byte[data.Length]) ? "changes the face" : "no change")}; all ones: {(Changes(Enumerable.Repeat((byte)0xff, data.Length).ToArray()) ? "changes the face" : "no change")}");
            return;
        }
        var sensitive = new List<string>();
        for (var o = 0; o < data.Length; o += 16)
        {
            if (data.AsSpan(o, Math.Min(16, data.Length - o)).IndexOfAnyExcept((byte)0) < 0)
            {
                var m = (byte[])data.Clone();
                for (var w = o; w < Math.Min(o + 16, data.Length); w += 4)
                    BitConverter.TryWriteBytes(m.AsSpan(w), 1f);
                if (Changes(m))
                    sensitive.Add($"+{o} (zero row)");
                continue;
            }
            for (var w = o; w < Math.Min(o + 16, data.Length); w += 4)
            {
                if (BitConverter.ToUInt32(data, w) == 0)
                    continue;
                var zeroed = (byte[])data.Clone();
                BitConverter.TryWriteBytes(zeroed.AsSpan(w), 0);
                var flipped = (byte[])data.Clone();
                flipped[w] ^= 1;
                var (z, f) = (Changes(zeroed), Changes(flipped));
                if (z || f)
                    sensitive.Add($"+{w} ({BitConverter.ToSingle(data, w):G9} / {BitConverter.ToUInt32(data, w):x8}){(f ? " exact" : "")}");
            }
        }
        output.WriteLine("sensitive: " + string.Join(", ", sensitive));
    }

    /// <summary>A RenderDoc dump turned into <see cref="SceneDrawRunner"/> calls, resources shared by content.</summary>
    private sealed unsafe class Replay(string dir, string spirvDir, JsonNode resources, GpuDevice gpu, SceneDrawRunner runner) : IDisposable
    {
        private readonly Dictionary<string, GpuDevice.GpuBuffer> _buffers = [];
        private readonly Dictionary<string, SceneDrawRunner.SceneImage> _images = [];
        private readonly Dictionary<string, Sampler> _samplers = [];

        /// <summary>
        /// CUBEREPLAY_BLANK: resources uploaded as zeros, to see whether a draw's output depends on them
        /// (an input that changes nothing need not be reproduced).
        /// </summary>
        private static readonly HashSet<string> Blank = [.. (Environment.GetEnvironmentVariable("CUBEREPLAY_BLANK") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)];

        private byte[] Blob(string sha1) => File.ReadAllBytes(Path.Combine(dir, "blobs", sha1 + ".bin"));

        /// <summary>
        /// A target's contents before an action, by eid and target name. Dumps made before 10-22 read a single indirect
        /// draw's targets at eventId - 1, RenderDoc's internal "Indirect sub-command" event that already holds the
        /// draw: rdc_targets_before.py writes the real ones (eventId - 2) as targets_before.json; without it the
        /// previous action's "after" stands in, which misses a render pass clear in between. rdc_replay.py now
        /// dumps them right.
        /// </summary>
        private readonly Dictionary<(int Eid, string Name), string> _targetBefore = TargetsBefore(dir);

        private static Dictionary<(int, string), string> TargetsBefore(string dir)
        {
            var map = new Dictionary<(int, string), string>();
            var fixedPath = Path.Combine(dir, "targets_before.json");
            if (File.Exists(fixedPath))
            {
                foreach (var (eid, targets) in JsonNode.Parse(File.ReadAllText(fixedPath))!.AsObject())
                    foreach (var (name, sha) in targets!.AsObject())
                        map[(int.Parse(eid, System.Globalization.CultureInfo.InvariantCulture), name)] = (string)sha!;
                return map;
            }
            var last = new Dictionary<string, string>();
            foreach (var line in File.ReadLines(Path.Combine(dir, "actions.jsonl")))
            {
                var rec = JsonNode.Parse(line)!;
                if (rec["contents"] is not { } c || c["after"] is not JsonObject after)
                    continue;
                var single = ((string)rec["name"]!).Contains("Indirect(1)", StringComparison.Ordinal);
                foreach (var (name, value) in after)
                {
                    if (single && last.TryGetValue(name, out var previous))
                        map[((int)rec["eid"]!, name)] = previous;
                    if (value is JsonArray subs && subs.Count > 0)
                        last[name] = (string)subs[0]!["sha1"]!;
                }
            }
            return map;
        }

        private string BeforeSha(JsonNode rec, JsonNode before, string name) =>
            _targetBefore.TryGetValue(((int)rec["eid"]!, name), out var sha) ? sha : (string)before[name]![0]!["sha1"]!;

        /// <summary>
        /// When set, targets are compared with the first (unperturbed) run of each action on this device instead of the
        /// capture: CUBEREPLAY_BASELINE=1, for sensitivity sweeps on a GPU other than the one that made the capture.
        /// </summary>
        public Dictionary<string, byte[]>? Baselines { get; set; } = Environment.GetEnvironmentVariable("CUBEREPLAY_BASELINE") == "1" ? [] : null;

        /// <summary>Texture subresource contents to upload in place of the captured blob of that hash (ours).</summary>
        public Dictionary<string, byte[]> ImageOverrides { get; } = [];

        /// <summary>Given the draw and its captured fixed-function state, the state to draw with (ours, where we build it).</summary>
        public Func<JsonNode, SceneDrawState, SceneDrawState>? StateHook { get; set; }

        /// <summary>Buffer contents to use in place of the captured blob of that hash (constant blocks we build).</summary>
        public Dictionary<string, byte[]> Overrides { get; } = [];
        private readonly List<GpuDevice.GpuBuffer> _overridden = [];

        /// <summary>
        /// Chained: each target and dispatch output stays alive under its resource name, later actions draw into and
        /// read it instead of the capture's contents; a target's first use starts from the capture's before (the pass's
        /// clear: <see cref="FirstUses"/> says whether it was one uniform value).
        /// </summary>
        public bool Chain { get; set; }
        private readonly Dictionary<string, SceneDrawRunner.SceneImage> _live = [];
        private readonly Dictionary<(string, Format), ImageView> _liveViews = [];
        private readonly Dictionary<string, GpuDevice.GpuBuffer> _liveBuffers = [];
        public List<string> FirstUses { get; } = [];

        /// <summary>
        /// CUBEREPLAY_START=clear: a chained target's first use starts from a clear (colour 0, depth far, stencil 0)
        /// instead of the capture's contents, which hold earlier renders' leftovers.
        /// </summary>
        public static bool StartCleared { get; } = Environment.GetEnvironmentVariable("CUBEREPLAY_START") == "clear";

        /// <summary>This replay's chained targets start from a clear whatever CUBEREPLAY_START says.</summary>
        public bool StartFromClear { get; init; } = StartCleared;

        /// <summary>
        /// The capture's render passes (tools/cubehost/rdc_passes.py: passes.jsonl beside the face dumps, its first line
        /// the image of each view): chained from cleared targets, each pass's CLEAR attachments are cleared to the
        /// pass's value over its render area when the pass begins, as the game's vkCmdBeginRendering does.
        /// </summary>
        private readonly List<(int Eid, Rect2D Area, List<(string Image, char Aspect, float[] Value)> Clears, Dictionary<string, Format> ViewFormats)>? _passes = Passes(Path.Combine(dir, "..", "passes.jsonl"));
        private int _clearedThrough = -1;

        private static List<(int, Rect2D, List<(string, char, float[])>, Dictionary<string, Format>)>? Passes(string path)
        {
            if (!File.Exists(path))
                return null;
            var lines = File.ReadAllLines(path);
            var views = JsonNode.Parse(lines[0])!["views"]!.AsObject();
            var passes = new List<(int, Rect2D, List<(string, char, float[])>, Dictionary<string, Format>)>();
            foreach (var line in lines.Skip(1))
            {
                var p = JsonNode.Parse(line)!;
                var info = p["args"]!["pRenderingInfo"]!;
                var area = info["renderArea"]!;
                var clears = new List<(string, char, float[])>();
                var formats = new Dictionary<string, Format>();
                void Add(JsonNode? a, char aspect)
                {
                    if (a is null)
                        return;
                    var view = views[(string)a["imageView"]!]!;
                    formats[(string)view["image"]!] = (Format)(int)view["format"]!;
                    if ((int)a["loadOp"]! != 1)   // VK_ATTACHMENT_LOAD_OP_CLEAR
                        return;
                    var cv = a["clearValue"]!;
                    float[] value = aspect == 'C' ? [.. cv["color"]!["float32"]!.AsArray().Select(v => (float)v!)]
                        : aspect == 'D' ? [(float)cv["depthStencil"]!["depth"]!] : [(float)(uint)cv["depthStencil"]!["stencil"]!];
                    clears.Add(((string)view["image"]!, aspect, value));
                }
                foreach (var c in info["pColorAttachments"]?.AsArray() ?? [])
                    Add(c, 'C');
                Add(info["pDepthAttachment"], 'D');
                Add(info["pStencilAttachment"], 'S');
                passes.Add(((int)p["eid"]!, new Rect2D(new Offset2D((int)area["offset"]!["x"]!, (int)area["offset"]!["y"]!),
                    new Extent2D((uint)area["extent"]!["width"]!, (uint)area["extent"]!["height"]!)), clears, formats));
            }
            return passes;
        }

        /// <summary>The passes begun since the last action, up to <paramref name="eid"/>: their clears on the live targets.</summary>
        private void ApplyClears(int eid)
        {
            if (_passes == null || !Chain || !StartFromClear)
                return;
            foreach (var (passEid, area, clears, _) in _passes.Where(p => p.Eid > _clearedThrough && p.Eid < eid))
                foreach (var (name, aspect, value) in clears)
                {
                    if (resources["textures"]![name] is not { } t)
                        continue;
                    var format = Format((string)t["format"]!);
                    if (!_live.TryGetValue(name, out var image))
                    {
                        image = runner.CreateImage(format, (uint)(int)t["width"]!, (uint)(int)t["height"]!, 1, 1, 1, ImageViewType.Type2D,
                            SceneDrawRunner.IsDepth(format) ? ImageUsageFlags.DepthStencilAttachmentBit : ImageUsageFlags.ColorAttachmentBit);
                        var depthBytes = SceneDrawRunner.IsDepth(format) ? (int)SceneDrawRunner.DepthTexelBytes(format) : ColorTexelBytes(format);
                        var size = (int)(image.Width * image.Height) * depthBytes + (SceneDrawRunner.HasStencil(format) ? (int)(image.Width * image.Height) : 0);
                        runner.Write(image, 0, 0, Cleared(format, image.Width, image.Height, size));
                        _live[name] = image;
                        FirstUses.Add($"{passEid} {name}: created by the pass clear");
                    }
                    var data = runner.Read(image, SceneDrawRunner.IsDepth(format) ? 0u : (uint)ColorTexelBytes(format));
                    var texel = ClearTexel(format, aspect, value);
                    var plane = aspect == 'S' ? (int)(image.Width * image.Height * SceneDrawRunner.DepthTexelBytes(format)) : 0;
                    // D24's depth is the texel's low 24 bits; its fourth byte is not depth.
                    var write = format == Silk.NET.Vulkan.Format.D24UnormS8Uint && aspect == 'D' ? 3 : texel.Length;
                    var stride = aspect == 'S' ? 1 : SceneDrawRunner.IsDepth(format) ? (int)SceneDrawRunner.DepthTexelBytes(format) : texel.Length;
                    for (var y = area.Offset.Y; y < area.Offset.Y + area.Extent.Height && y < image.Height; y++)
                        for (var x = area.Offset.X; x < area.Offset.X + area.Extent.Width && x < image.Width; x++)
                            texel.AsSpan(0, write).CopyTo(data.AsSpan(plane + (int)(y * image.Width + x) * stride));
                    runner.Write(image, 0, 0, data);
                }
            _clearedThrough = eid - 1;
        }

        private static int ColorTexelBytes(Format f) => f switch
        {
            Silk.NET.Vulkan.Format.R16G16B16A16Sfloat or Silk.NET.Vulkan.Format.R32G32Sfloat => 8,
            Silk.NET.Vulkan.Format.R32G32B32A32Sfloat => 16,
            _ => 4,
        };

        /// <summary>One texel of a clear value in <see cref="SceneDrawRunner.Write"/>'s layout (a stencil clear: the stencil byte).</summary>
        private static byte[] ClearTexel(Format f, char aspect, float[] v)
        {
            if (aspect == 'S')
                return [(byte)v[0]];
            if (aspect == 'D')
                return f switch
                {
                    Silk.NET.Vulkan.Format.D16Unorm => BitConverter.GetBytes((ushort)MathF.Round(v[0] * 65535f)),
                    Silk.NET.Vulkan.Format.D24UnormS8Uint => BitConverter.GetBytes((uint)MathF.Round(v[0] * 16777215f)),
                    Silk.NET.Vulkan.Format.D32Sfloat => BitConverter.GetBytes(v[0]),
                    _ => throw new NotSupportedException($"depth clear of {f}"),
                };
            byte Unorm(float x) => x is 0f or 1f ? (byte)(x * 255f) : throw new NotSupportedException($"clear {x} of {f} (only 0 and 1 are exact)");
            return f switch
            {
                Silk.NET.Vulkan.Format.R8G8B8A8Srgb or Silk.NET.Vulkan.Format.R8G8B8A8Unorm => [Unorm(v[0]), Unorm(v[1]), Unorm(v[2]), Unorm(v[3])],
                Silk.NET.Vulkan.Format.B8G8R8A8Srgb or Silk.NET.Vulkan.Format.B8G8R8A8Unorm => [Unorm(v[2]), Unorm(v[1]), Unorm(v[0]), Unorm(v[3])],
                Silk.NET.Vulkan.Format.R16G16B16A16Sfloat => [.. v.SelectMany(x => BitConverter.GetBytes((Half)x))],
                Silk.NET.Vulkan.Format.R32G32B32A32Sfloat => [.. v.SelectMany(BitConverter.GetBytes)],
                Silk.NET.Vulkan.Format.R32Sfloat => BitConverter.GetBytes(v[0]),
                _ => throw new NotSupportedException($"colour clear of {f}"),
            };
        }

        /// <summary>A chained target's contents as our draws left it.</summary>
        public byte[] ReadLive(string name) => runner.Read(_live[name], SceneDrawRunner.IsDepth(_live[name].Format) ? 0u : (uint)ColorTexelBytes(_live[name].Format));

        private static byte[] Cleared(Format format, uint width, uint height, int size)
        {
            var data = new byte[size];
            var texels = (int)(width * height);
            if (format == Silk.NET.Vulkan.Format.D16Unorm)
                data.AsSpan(0, texels * 2).Fill(0xff);
            else if (format == Silk.NET.Vulkan.Format.D24UnormS8Uint)
                for (var i = 0; i < texels; i++)
                    data[i * 4] = data[i * 4 + 1] = data[i * 4 + 2] = 0xff;
            else if (format == Silk.NET.Vulkan.Format.D32Sfloat)
                for (var i = 0; i < texels; i++)
                    BitConverter.TryWriteBytes(data.AsSpan(i * 4), 1f);
            return data;
        }

        /// <summary>Drops the chained state (live targets and dispatch outputs) for another chained run.</summary>
        public void ResetChain()
        {
            foreach (var v in _liveViews.Values)
                runner.DestroyView(v);
            _liveViews.Clear();
            foreach (var b in _liveBuffers.Values)
                gpu.Destroy(b);
            foreach (var i in _live.Values)
                runner.Destroy(i);
            _liveBuffers.Clear();
            _live.Clear();
            FirstUses.Clear();
        }

        private GpuDevice.GpuBuffer Input(string name, JsonNode before)
        {
            if (Chain && _liveBuffers.TryGetValue(name, out var live))
                return live;
            var sha = (string)before[name]!["sha1"]!;
            if (Blank.Contains(name))
                Overrides[sha] = new byte[Blob(sha).Length];
            return Buffer(sha);
        }

        private GpuDevice.GpuBuffer Buffer(string sha1)
        {
            if (Overrides.TryGetValue(sha1, out var replaced))
            {
                var fresh = gpu.Upload(replaced, BufferUsageFlags.UniformBufferBit | BufferUsageFlags.StorageBufferBit | BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndexBufferBit);
                _overridden.Add(fresh);
                return fresh;
            }
            if (!_buffers.TryGetValue(sha1, out var b))
                _buffers[sha1] = b = gpu.Upload(Blob(sha1), BufferUsageFlags.UniformBufferBit | BufferUsageFlags.StorageBufferBit | BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndexBufferBit);
            return b;
        }

        public static Format Format(string name) => name switch
        {
            "BC1_SRGB" => Silk.NET.Vulkan.Format.BC1RgbaSrgbBlock,
            "BC1_UNORM" => Silk.NET.Vulkan.Format.BC1RgbaUnormBlock,
            "BC3_SRGB" => Silk.NET.Vulkan.Format.BC3SrgbBlock,
            "BC3_UNORM" => Silk.NET.Vulkan.Format.BC3UnormBlock,
            "BC4_UNORM" => Silk.NET.Vulkan.Format.BC4UnormBlock,
            "BC5_UNORM" => Silk.NET.Vulkan.Format.BC5UnormBlock,
            "BC6_UFLOAT" => Silk.NET.Vulkan.Format.BC6HUfloatBlock,
            "BC7_SRGB" => Silk.NET.Vulkan.Format.BC7SrgbBlock,
            "BC7_UNORM" => Silk.NET.Vulkan.Format.BC7UnormBlock,
            "B8G8R8A8_SRGB" => Silk.NET.Vulkan.Format.B8G8R8A8Srgb,
            "B8G8R8A8_UNORM" => Silk.NET.Vulkan.Format.B8G8R8A8Unorm,
            "R8G8B8A8_SRGB" => Silk.NET.Vulkan.Format.R8G8B8A8Srgb,
            "R8G8B8A8_UNORM" => Silk.NET.Vulkan.Format.R8G8B8A8Unorm,
            "R16G16B16A16_FLOAT" => Silk.NET.Vulkan.Format.R16G16B16A16Sfloat,
            "R32G32B32A32_FLOAT" => Silk.NET.Vulkan.Format.R32G32B32A32Sfloat,
            "R32G32B32_FLOAT" => Silk.NET.Vulkan.Format.R32G32B32Sfloat,
            "R32G32_FLOAT" => Silk.NET.Vulkan.Format.R32G32Sfloat,
            "R32_FLOAT" => Silk.NET.Vulkan.Format.R32Sfloat,
            "R32_UINT" => Silk.NET.Vulkan.Format.R32Uint,
            "R32G32B32A32_UINT" => Silk.NET.Vulkan.Format.R32G32B32A32Uint,
            "R16G16_UNORM" => Silk.NET.Vulkan.Format.R16G16Unorm,
            "R16G16_SNORM" => Silk.NET.Vulkan.Format.R16G16SNorm,
            "R8_UNORM" => Silk.NET.Vulkan.Format.R8Unorm,
            "R8G8_UNORM" => Silk.NET.Vulkan.Format.R8G8Unorm,
            "R16_UNORM" => Silk.NET.Vulkan.Format.R16Unorm,
            "R32_SINT" => Silk.NET.Vulkan.Format.R32Sint,
            "R11G11B10_FLOAT" => Silk.NET.Vulkan.Format.B10G11R11UfloatPack32,
            "R8G8B8A8_SINT" => Silk.NET.Vulkan.Format.R8G8B8A8Sint,
            "R16G16B16A16_UINT" => Silk.NET.Vulkan.Format.R16G16B16A16Uint,
            "R16G16B16A16_SNORM" => Silk.NET.Vulkan.Format.R16G16B16A16SNorm,
            "R16G16B16A16_UNORM" => Silk.NET.Vulkan.Format.R16G16B16A16Unorm,
            "R8G8B8A8_SNORM" => Silk.NET.Vulkan.Format.R8G8B8A8SNorm,
            "R10G10B10A2_UNORM" => Silk.NET.Vulkan.Format.A2B10G10R10UnormPack32,
            "R16_FLOAT" => Silk.NET.Vulkan.Format.R16Sfloat,
            "R32G32_UINT" => Silk.NET.Vulkan.Format.R32G32Uint,
            "R16G16_FLOAT" => Silk.NET.Vulkan.Format.R16G16Sfloat,
            "R8G8B8A8_UINT" => Silk.NET.Vulkan.Format.R8G8B8A8Uint,
            "D16" => Silk.NET.Vulkan.Format.D16Unorm,
            "D24S8" => Silk.NET.Vulkan.Format.D24UnormS8Uint,
            "D32" => Silk.NET.Vulkan.Format.D32Sfloat,
            _ => throw new NotSupportedException(name),
        };

        // RenderDoc's CompareFunction: Never, AlwaysTrue, Less, LessEqual, Greater, GreaterEqual, Equal, NotEqual.
        private static CompareOp Compare(int f) => f switch
        {
            0 => CompareOp.Never, 1 => CompareOp.Always, 2 => CompareOp.Less, 3 => CompareOp.LessOrEqual,
            4 => CompareOp.Greater, 5 => CompareOp.GreaterOrEqual, 6 => CompareOp.Equal, 7 => CompareOp.NotEqual,
            _ => throw new NotSupportedException($"compare {f}"),
        };

        // RenderDoc's AddressMode: Wrap, Mirror, MirrorOnce, ClampEdge, ClampBorder.
        private static SamplerAddressMode Address(int a) => a switch
        {
            0 => SamplerAddressMode.Repeat, 1 => SamplerAddressMode.MirroredRepeat, 2 => SamplerAddressMode.MirrorClampToEdge,
            3 => SamplerAddressMode.ClampToEdge, 4 => SamplerAddressMode.ClampToBorder, _ => throw new NotSupportedException($"address {a}"),
        };

        private static StencilOpState Stencil(JsonNode s) => new(
            (StencilOp)(int)s["failOperation"]!, (StencilOp)(int)s["passOperation"]!, (StencilOp)(int)s["depthFailOperation"]!,
            Compare((int)s["function"]!), (uint)s["compareMask"]!, (uint)s["writeMask"]!, (uint)s["reference"]!);

        private Sampler SamplerFor(JsonNode s)
        {
            var key = s.ToJsonString();
            if (_samplers.TryGetValue(key, out var made))
                return made;
            return _samplers[key] = runner.CreateSampler(CapturedSampler(s));
        }

        /// <summary>Our sampler for a state (cached by its fields).</summary>
        private Sampler OurSampler(SamplerCreateInfo info)
        {
            var key = $"ours {info.MagFilter} {info.MinFilter} {info.MipmapMode} {info.AddressModeU} {info.AddressModeV} {info.AddressModeW} {info.MipLodBias} {info.AnisotropyEnable} {info.MaxAnisotropy} {info.CompareEnable} {info.CompareOp} {info.MinLod} {info.MaxLod} {info.BorderColor}";
            if (_samplers.TryGetValue(key, out var made))
                return made;
            return _samplers[key] = runner.CreateSampler(info);
        }

        /// <summary>The draw's sampler entry (stage, bind, sampler) to our sampler's state, or null to keep the capture's.</summary>
        public Func<JsonNode, JsonNode, SamplerCreateInfo?>? SamplerHook { get; set; }

        /// <summary>A captured sampler as Vulkan's create info.</summary>
        public static SamplerCreateInfo CapturedSampler(JsonNode s)
        {
            var f = s["filter"]!;
            // RenderDoc's FilterMode: NoFilter, Point, Linear, Cubic, Anisotropic; FilterFunction: Normal, Comparison, Min, Max.
            Filter Mode(int m) => m == 1 ? Filter.Nearest : Filter.Linear;
            var anisotropic = (int)f["minify"]! == 4 || (int)f["magnify"]! == 4;
            var border = s["borderColorValue"]!["floatValue"]!.AsArray().Select(v => (float)v!).ToArray();
            var info = new SamplerCreateInfo
            {
                MagFilter = Mode((int)f["magnify"]!),
                MinFilter = Mode((int)f["minify"]!),
                MipmapMode = (int)f["mip"]! == 1 ? SamplerMipmapMode.Nearest : SamplerMipmapMode.Linear,
                AddressModeU = Address((int)s["addressU"]!),
                AddressModeV = Address((int)s["addressV"]!),
                AddressModeW = Address((int)s["addressW"]!),
                MipLodBias = (float)s["mipBias"]!,
                AnisotropyEnable = anisotropic,
                MaxAnisotropy = anisotropic ? (float)s["maxAnisotropy"]! : 1f,
                CompareEnable = (int)f["filter"]! == 1,
                CompareOp = Compare((int)s["compareFunction"]!),
                MinLod = (float)s["minLOD"]!,
                MaxLod = (float)s["maxLOD"]!,
                BorderColor = border[3] == 0 ? BorderColor.FloatTransparentBlack : border[0] == 0 ? BorderColor.FloatOpaqueBlack : BorderColor.FloatOpaqueWhite,
            };
            return info;
        }

        private static ImageViewType ViewType(string dim) => dim switch
        {
            "2D" => ImageViewType.Type2D, "2DArray" => ImageViewType.Type2DArray, "3D" => ImageViewType.Type3D,
            "Cube" => ImageViewType.TypeCube, "CubeArray" => ImageViewType.TypeCubeArray, "1D" => ImageViewType.Type1D,
            _ => throw new NotSupportedException(dim),
        };

        /// <summary>A texture's whole contents (every subresource dumped) as an image with the shader's view type.</summary>
        private SceneDrawRunner.SceneImage Image(string name, JsonArray subs, Format format, ImageViewType viewType, ImageUsageFlags usage = 0)
        {
            if (Chain && _live.TryGetValue(name, out var drawn))
            {
                if (viewType != ImageViewType.Type2D)
                    throw new NotSupportedException($"{name} read as {format} {viewType}, drawn as {drawn.Format}");
                if (drawn.Format == format)
                    return drawn;
                // The colour pass reads the sRGB AO target through a UNORM view.
                if (!_liveViews.TryGetValue((name, format), out var other))
                    _liveViews[(name, format)] = other = runner.ViewAs(drawn, format);
                return new SceneDrawRunner.SceneImage
                {
                    Image = drawn.Image, Memory = drawn.Memory, View = other, Format = format, Width = drawn.Width, Height = drawn.Height,
                    Depth = drawn.Depth, Mips = drawn.Mips, Layers = drawn.Layers,
                };
            }
            var key = $"{name}|{format}|{viewType}|{string.Join(",", subs.Select(s => (string)s!["sha1"]!))}";
            if (_images.TryGetValue(key, out var image))
                return image;
            var t = resources["textures"]![name]!;
            var mips = (uint)(int)t["mips"]!;
            var layers = (uint)Math.Max(1, (int)t["arraysize"]!);
            // CUBEREPLAY_WHITE=<name>: that texture as opaque white RGBA8 of the same size, to test a format path.
            if (Environment.GetEnvironmentVariable("CUBEREPLAY_WHITE") == name)
            {
                var white = runner.CreateImage(Silk.NET.Vulkan.Format.R8G8B8A8Unorm, (uint)(int)t["width"]!, (uint)(int)t["height"]!, 1, mips, layers, viewType, usage);
                for (var m = 0u; m < mips; m++)
                    for (var l = 0u; l < layers; l++)
                    {
                        var data = new byte[Math.Max(1, (int)t["width"]! >> (int)m) * Math.Max(1, (int)t["height"]! >> (int)m) * 4];
                        Array.Fill(data, (byte)0xff);
                        runner.Write(white, m, l, data);
                    }
                return _images[key] = white;
            }
            image = runner.CreateImage(format, (uint)(int)t["width"]!, (uint)(int)t["height"]!, (uint)Math.Max(1, (int)t["depth"]!), mips, layers, viewType, usage);
            foreach (var s in subs)
            {
                var data = ImageOverrides.TryGetValue((string)s!["sha1"]!, out var ours) ? ours : Blob((string)s!["sha1"]!);
                if (Blank.Contains(name))
                    data = new byte[data.Length];
                runner.Write(image, (uint)(int)s["mip"]!, (uint)(int)s["slice"]!, data);
            }
            return _images[key] = image;
        }

        private static string Sha1(byte[] data) => Convert.ToHexStringLower(SHA1.HashData(data));

        /// <summary>The declaration at set/binding that RenderDoc's res&lt;id&gt; names (our reflection names it _&lt;id&gt;), else the first there.</summary>
        private static SpirvReflection.Resource Declaration(IEnumerable<SpirvReflection.Resource> resources, int set, int binding, string? renderDocName)
        {
            var id = renderDocName is { Length: > 3 } n && n.StartsWith("res", StringComparison.Ordinal) ? "_" + n[3..] : renderDocName;
            return resources.FirstOrDefault(r => r.Set == set && r.Binding == binding && r.Name == id)
                ?? resources.First(r => r.Set == set && r.Binding == binding);
        }

        public string Run(JsonNode rec)
        {
            ApplyClears((int)rec["eid"]!);
            var before = rec["contents"]!["before"]!;
            var after = rec["contents"]!["after"]!;
            var vs = rec["stages"]!["vs"]!;
            var ps = rec["stages"]!["ps"];
            byte[] Spirv(JsonNode stage) => File.ReadAllBytes(Path.Combine(spirvDir, $"{int.Parse(((string)stage["shader"]!).Split(' ')[^1]):x}.spv"));
            var vsCode = Spirv(vs);
            var psCode = ps != null ? Spirv(ps) : null;
            var vsReflection = SpirvReflection.Parse(vsCode);
            var psReflection = psCode != null ? SpirvReflection.Parse(psCode) : null;
            // One binding can be declared under several names of different image types (atixref's bindless set 4
            // binding 46: 2D, 3D, 2D array, cube), in each stage's own module; RenderDoc names the declaration an
            // element is read through as res<SPIR-V id> of that stage's module.
            SpirvReflection.Resource Reflected(JsonNode stage, int set, int binding, string? name) =>
                Declaration((ReferenceEquals(stage, vs) ? vsReflection : psReflection!).Resources, set, binding, name);

            var bindings = new SceneDrawRunner.Bindings();
            foreach (var stage in new[] { vs, ps }.Where(s => s != null))
            {
                foreach (var cb in stage!["constantBlocks"]!.AsArray())
                {
                    var block = cb!["block"]!;
                    var bytes = cb["bytes"]!;
                    // A block not backed by a buffer is the push constants (RenderDoc reads them from the command buffer).
                    if (block["bufferBacked"] is { } backed && !(bool)backed)
                    {
                        bindings.PushConstants = Blob((string)bytes["sha1"]!);
                        continue;
                    }
                    // CUBEREPLAY_PATCHCB=<eid>:<set>:<binding>:<byte offset>:<value>[;...]: floats (or i<int>) of constant
                    // blocks (diagnosis).
                    var cbSha = (string)bytes["sha1"]!;
                    byte[]? patched = null;
                    foreach (var patch in (Environment.GetEnvironmentVariable("CUBEREPLAY_PATCHCB") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (patch.Split(':') is not [var pe, var ps2, var pb, var po, var pv]
                            || pe != ((int)rec["eid"]!).ToString(System.Globalization.CultureInfo.InvariantCulture)
                            || int.Parse(ps2, System.Globalization.CultureInfo.InvariantCulture) != (int)block["fixedBindSetOrSpace"]!
                            || int.Parse(pb, System.Globalization.CultureInfo.InvariantCulture) != (int)block["fixedBindNumber"]!)
                            continue;
                        patched ??= Blob(cbSha);
                        var at = patched.AsSpan(int.Parse(po, System.Globalization.CultureInfo.InvariantCulture));
                        if (pv.StartsWith('i'))   // i<int>: an integer member
                            BitConverter.TryWriteBytes(at, int.Parse(pv[1..], System.Globalization.CultureInfo.InvariantCulture));
                        else
                            BitConverter.TryWriteBytes(at, float.Parse(pv, System.Globalization.CultureInfo.InvariantCulture));
                    }
                    if (patched != null)
                    {
                        cbSha += "_patched";
                        Overrides[cbSha] = patched;
                    }
                    bindings.Buffer((int)block["fixedBindSetOrSpace"]!, (int)block["fixedBindNumber"]!, (uint)(int)cb["access"]!["arrayElement"]!,
                        Buffer(cbSha), 0, (ulong)(long)bytes["size"]!);
                }
                foreach (var rw in stage["readWrite"]!.AsArray().Concat(stage["readOnly"]!.AsArray()))
                {
                    var bind = rw!["bind"]!;
                    var d = rw["descriptor"]!;
                    var name = (string)d["resource"]!;
                    var (set, binding, element) = ((int)bind["set"]!, (int)bind["binding"]!, (uint)(int)rw["access"]!["arrayElement"]!);
                    if (before[name] is JsonArray subs)
                    {
                        var image = Image(name, subs, Format((string)d["format"]!), ViewType(Reflected(stage, set, binding, (string?)bind["name"]).Dim));
                        bindings.Image(set, binding, element, image.View);
                        // CUBEREPLAY_ALSO=<element>: every image also bound at that slot (diagnosis of high slots).
                        if (Environment.GetEnvironmentVariable("CUBEREPLAY_ALSO") is { } also)
                            bindings.Image(set, binding, uint.Parse(also, System.Globalization.CultureInfo.InvariantCulture), image.View);
                    }
                    else
                    {
                        var size = (ulong)d["byteSize"]!;
                        bindings.Buffer(set, binding, element, Input(name, before), (ulong)d["byteOffset"]!, size >= (1UL << 40) ? Vk.WholeSize : size);
                    }
                }
                foreach (var smp in stage["samplers"]!.AsArray())
                    bindings.Sampler((int)smp!["bind"]!["set"]!, (int)smp["bind"]!["binding"]!, (uint)(int)smp["access"]!["arrayElement"]!,
                        SamplerHook?.Invoke(rec, smp) is { } ours ? OurSampler(ours) : SamplerFor(smp["sampler"]!));
            }

            var vi = rec["vertexInput"]!;
            var vbs = vi["vertexBuffers"]!.AsArray();
            var perInstance = vi["bindings"]!.AsArray().ToDictionary(b => (int)b!["vertexBufferBinding"]!, b => (bool)b!["perInstance"]!);
            var rast = rec["rasterizer"]!;
            var ds = rec["depthStencil"]!;
            var vp = rec["viewportScissor"]!["viewportScissors"]![0]!;
            var blends = rec["colorBlend"]!["blends"]!.AsArray();
            var flags = (string)rec["flags"]!;
            var colorName = rec["outputs"]!.AsArray().Select(o => (string)o!).FirstOrDefault();
            var depthName = (string?)rec["depthOut"];
            var state = new SceneDrawState
            {
                VertexSpirv = vsCode,
                PixelSpirv = psCode,
                Bindings = vbs.Select((b, i) => ((uint)(int)b!["byteStride"]!, perInstance.GetValueOrDefault(i))).ToArray(),
                Attributes = vi["attributes"]!.AsArray().Select(a => new ProgramRunner.Attribute((uint)(int)a!["location"]!, (uint)(int)a["binding"]!, Format((string)a["format"]!), (uint)(int)a["byteOffset"]!)).ToArray(),
                Topology = (int)rec["inputAssembly"]!["topology"]! switch { 1 => PrimitiveTopology.PointList, 2 => PrimitiveTopology.LineList, 3 => PrimitiveTopology.LineStrip, 5 => PrimitiveTopology.TriangleList, 6 => PrimitiveTopology.TriangleStrip, 7 => PrimitiveTopology.TriangleFan, var t => throw new NotSupportedException($"topology {t}") },
                Cull = (CullModeFlags)(int)rast["cullMode"]!,
                Front = (bool)rast["frontCCW"]! ? FrontFace.CounterClockwise : FrontFace.Clockwise,
                DepthBias = (bool)rast["depthBiasEnable"]!,
                DepthBiasConstant = (float)rast["depthBias"]!,
                DepthBiasSlope = (float)rast["slopeScaledDepthBias"]!,
                DepthBiasClamp = (float)rast["depthBiasClamp"]!,
                DepthClamp = (bool)rast["depthClampEnable"]!,
                DepthTest = (bool)ds["depthTestEnable"]!,
                DepthWrite = (bool)ds["depthWriteEnable"]!,
                DepthCompare = Compare((int)ds["depthFunction"]!),
                StencilTest = (bool)ds["stencilTestEnable"]!,
                StencilFront = Stencil(ds["frontFace"]!),
                StencilBack = Stencil(ds["backFace"]!),
                Viewport = new Viewport((float)vp["vp"]!["x"]!, (float)vp["vp"]!["y"]!, (float)vp["vp"]!["width"]!, (float)vp["vp"]!["height"]!, (float)vp["vp"]!["minDepth"]!, (float)vp["vp"]!["maxDepth"]!),
                Scissor = new Rect2D(new Offset2D((int)vp["scissor"]!["x"]!, (int)vp["scissor"]!["y"]!), new Extent2D((uint)(int)vp["scissor"]!["width"]!, (uint)(int)vp["scissor"]!["height"]!)),
                ColorFormat = colorName != null ? Format((string)resources["textures"]![colorName]!["format"]!) : null,
                ColorWriteMask = blends.Count > 0 ? (ColorComponentFlags)(int)blends[0]!["writeMask"]! : ColorComponentFlags.None,
                DepthFormat = depthName != null ? Format((string)resources["textures"]![depthName]!["format"]!) : null,
            };
            // RenderDoc's BlendMultiplier and BlendOperation enumerate as Vulkan's BlendFactor and BlendOp do; the
            // constant-colour factors (10 to 13) would need the blend constants, which no draw here sets.
            if (blends.Count > 0 && (bool)blends[0]!["enabled"]!)
            {
                var (cb, ab) = (blends[0]!["colorBlend"]!, blends[0]!["alphaBlend"]!);
                int[] factors = [(int)cb["source"]!, (int)cb["destination"]!, (int)ab["source"]!, (int)ab["destination"]!];
                if (factors.Any(f => f is >= 10 and <= 13))
                    return "blend constants not modelled";
                state = state with { Blend = ((BlendFactor)factors[0], (BlendFactor)factors[1], (BlendOp)(int)cb["operation"]!, (BlendFactor)factors[2], (BlendFactor)factors[3], (BlendOp)(int)ab["operation"]!) };
            }
            if (blends.Skip(1).Any(b => (bool)b!["enabled"]!))
                return "blending on a second target not modelled";
            if (StateHook != null)
                state = StateHook(rec, state);
            // CUBEREPLAY_LOOSE=<eid>: that draw with no depth or stencil test, no culling and the whole target as
            // viewport and scissor, to see where its triangles land at all.
            if (Environment.GetEnvironmentVariable("CUBEREPLAY_LOOSE") is { } loose && loose == ((int)rec["eid"]!).ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                var whole = resources["textures"]![colorName ?? depthName!]!;
                var (tw, th) = ((uint)(int)whole["width"]!, (uint)(int)whole["height"]!);
                state = state with
                {
                    DepthTest = false, StencilTest = false, Cull = CullModeFlags.None, DepthWrite = true, DepthClamp = true,
                    Viewport = state.Viewport with { X = 0, Y = 0, Width = tw, Height = th },
                    Scissor = new Rect2D(new Offset2D(0, 0), new Extent2D(tw, th)),
                };
            }

            var vertexBuffers = vbs.Select(b => (Input((string)b!["resourceId"]!, before), (ulong)b["byteOffset"]!)).ToArray();
            var ib = rec["ibuffer"]!;
            (GpuDevice.GpuBuffer, ulong, IndexType)? index = flags.Contains("Indexed")
                ? (Input((string)ib["resourceId"]!, before), (ulong)ib["byteOffset"]!, (int)ib["byteStride"]! == 4 ? IndexType.Uint32 : IndexType.Uint16)
                : null;
            var args = flags.Contains("Indexed")
                ? new SceneDrawArgs(true, (uint)rec["numIndices"]!, (uint)rec["numInstances"]!, (uint)rec["indexOffset"]!, (int)rec["baseVertex"]!, (uint)rec["instanceOffset"]!)
                : new SceneDrawArgs(false, (uint)rec["numIndices"]!, (uint)rec["numInstances"]!, (uint)rec["vertexOffset"]!, 0, (uint)rec["instanceOffset"]!);

            // CUBEREPLAY_ARGS=<eid>:<first index>:<count>: that indexed draw over another index range (diagnosis).
            if (Environment.GetEnvironmentVariable("CUBEREPLAY_ARGS") is { } over && over.Split(':') is [var oe, var of, var oc]
                && oe == ((int)rec["eid"]!).ToString(System.Globalization.CultureInfo.InvariantCulture) && args.Indexed)
                args = args with { First = uint.Parse(of, System.Globalization.CultureInfo.InvariantCulture), Count = uint.Parse(oc, System.Globalization.CultureInfo.InvariantCulture) };

            // Fresh targets from the capture's contents before the draw.
            SceneDrawRunner.SceneImage? Target(string? name, ImageUsageFlags usage)
            {
                if (name == null)
                    return null;
                if (Chain && _live.TryGetValue(name, out var drawn))
                    return drawn;
                var t = resources["textures"]![name]!;
                var image = runner.CreateImage(Format((string)t["format"]!), (uint)(int)t["width"]!, (uint)(int)t["height"]!, 1, 1, 1, ImageViewType.Type2D, usage);
                var start = Blob(BeforeSha(rec, before, name));
                if (Chain && StartFromClear)
                    start = Cleared(image.Format, image.Width, image.Height, start.Length);
                runner.Write(image, 0, 0, start);
                if (Chain)
                {
                    _live[name] = image;
                    var texel = SceneDrawRunner.IsDepth(image.Format) ? (int)SceneDrawRunner.DepthTexelBytes(image.Format) : ColorTexelBytes(image.Format);
                    var uniform = Enumerable.Range(0, start.Length / texel).All(i => start.AsSpan(i * texel, texel).SequenceEqual(start.AsSpan(0, texel)));
                    FirstUses.Add($"{rec["eid"]} {name}: starts {(uniform ? "uniform 0x" + Convert.ToHexString(start, 0, texel) : "NOT uniform")}");
                }
                return image;
            }
            var color = Target(colorName, ImageUsageFlags.ColorAttachmentBit);
            // The pass may render the target through a view of another format of its class (the AO target's UNORM
            // view of an sRGB image): blending and the store then work in that format.
            if (color != null && _passes?.LastOrDefault(p => p.Eid < (int)rec["eid"]!).ViewFormats is { } viewFormats
                && viewFormats.TryGetValue(colorName!, out var passFormat) && passFormat != color.Format)
            {
                // Unchained, the target is this draw's own: its view goes with it (the image's own view is dropped here).
                if (!_liveViews.TryGetValue((colorName!, passFormat), out var other))
                {
                    other = runner.ViewAs(color, passFormat);
                    if (Chain)
                        _liveViews[(colorName!, passFormat)] = other;
                    else
                        runner.DestroyView(color.View);
                }
                color = new SceneDrawRunner.SceneImage
                {
                    Image = color.Image, Memory = color.Memory, View = other, Format = passFormat, Width = color.Width, Height = color.Height,
                    Depth = color.Depth, Mips = color.Mips, Layers = color.Layers,
                };
                state = state with { ColorFormat = passFormat };
            }
            var depth = Target(depthName, ImageUsageFlags.DepthStencilAttachmentBit);
            // CUBEREPLAY_RENDERDOC=<eid>: our device's draw captured by RenderDoc (ENABLE_VULKAN_RENDERDOC_CAPTURE=1).
            var capture = Environment.GetEnvironmentVariable("CUBEREPLAY_RENDERDOC") == ((int)rec["eid"]!).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (capture)
                RenderDocInApp.Start();
            runner.Draw(state, bindings, vertexBuffers, index, args, color, depth);
            if (capture)
                RenderDocInApp.End();

            var report = new List<string>();
            var equal = true;
            foreach (var (name, image, texel) in new[] { (colorName, color, color != null ? (uint)ColorTexelBytes(color.Format) : 8u), (depthName, depth, 0u) })
            {
                if (name == null || image == null)
                    continue;
                var ours = runner.Read(image, texel);
                var theirs = Blob((string)after[name]![0]!["sha1"]!);
                // CUBEREPLAY_SAVE=<dir>: our target after the draw, as <eid>_<target>.bin, beside the capture's sha.
                if (Environment.GetEnvironmentVariable("CUBEREPLAY_SAVE") is { Length: > 0 } save)
                {
                    Directory.CreateDirectory(save);
                    File.WriteAllBytes(Path.Combine(save, $"{rec["eid"]}_{name.Split(' ')[^1]}.bin"), ours);
                }
                if (Baselines != null)
                {
                    // Against this device's own unperturbed run (another GPU need not reproduce the capture's bits).
                    var key = $"{rec["eid"]}|{name}";
                    if (!Baselines.TryGetValue(key, out var baseline))
                        Baselines[key] = baseline = ours;
                    theirs = baseline;
                }
                var beforeBytes = Blob(BeforeSha(rec, before, name));
                var diff = 0;
                var kept = 0;
                for (var i = 0; i < Math.Min(ours.Length, theirs.Length); i++)
                    if (ours[i] != theirs[i])
                    {
                        // From a cleared start, bytes no draw wrote keep our clear where the capture kept leftovers.
                        if (Chain && StartFromClear && Baselines == null && theirs[i] == beforeBytes[i])
                            kept++;
                        else
                            diff++;
                    }
                var changed = 0;
                for (var i = 0; i < theirs.Length; i++)
                    if (theirs[i] != beforeBytes[i])
                        changed++;
                equal &= diff == 0 && ours.Length == theirs.Length;
                report.Add($"{name}: {(diff == 0 ? "same" : $"{diff} bytes differ")}{(kept > 0 ? $", {kept} kept from the clear" : "")} ({changed} bytes changed by the draw{(ours.Length != theirs.Length ? $", sizes {ours.Length} / {theirs.Length}" : "")})");
                if (!Chain)
                    runner.Destroy(image);
            }
            return (equal ? "equal; " : "DIFFERENT; ") + string.Join("; ", report);
        }

        /// <summary>A dispatch from fresh copies of its read-write buffers; each must equal the capture's after it.</summary>
        public string RunCompute(JsonNode rec)
        {
            ApplyClears((int)rec["eid"]!);
            var before = rec["contents"]!["before"]!;
            var after = rec["contents"]!["after"]!;
            var cs = rec["stages"]!["cs"]!;
            var code = File.ReadAllBytes(Path.Combine(spirvDir, $"{int.Parse(((string)cs["shader"]!).Split(' ')[^1]):x}.spv"));
            var reflection = SpirvReflection.Parse(code);
            var bindings = new SceneDrawRunner.Bindings();
            foreach (var cb in cs["constantBlocks"]!.AsArray())
            {
                var block = cb!["block"]!;
                if (block["bufferBacked"] is { } backed && !(bool)backed)
                {
                    bindings.PushConstants = Blob((string)cb["bytes"]!["sha1"]!);
                    continue;
                }
                bindings.Buffer((int)block["fixedBindSetOrSpace"]!, (int)block["fixedBindNumber"]!, (uint)(int)cb["access"]!["arrayElement"]!,
                    Buffer((string)cb["bytes"]!["sha1"]!), 0, (ulong)(long)cb["bytes"]!["size"]!);
            }
            var written = new List<(string Name, GpuDevice.GpuBuffer Buffer)>();
            foreach (var r in cs["readWrite"]!.AsArray().Concat(cs["readOnly"]!.AsArray()))
            {
                var bind = r!["bind"]!;
                var d = r["descriptor"]!;
                var name = (string)d["resource"]!;
                var (set, binding, element) = ((int)bind["set"]!, (int)bind["binding"]!, (uint)(int)r["access"]!["arrayElement"]!);
                if (before[name] is JsonArray subs)
                {
                    bindings.Image(set, binding, element, Image(name, subs, Format((string)d["format"]!), ViewType(Declaration(reflection.Resources, set, binding, (string?)bind["name"]).Dim)).View);
                    continue;
                }
                var buffer = written.FirstOrDefault(w => w.Name == name).Buffer;
                if (buffer == null && after[name] != null)
                {
                    if (!(Chain && _liveBuffers.TryGetValue(name, out buffer)))
                    {
                        var start = (string)before[name]!["sha1"]!;
                        buffer = gpu.Upload(Overrides.TryGetValue(start, out var ours) ? ours : Blob(start), BufferUsageFlags.StorageBufferBit | BufferUsageFlags.UniformBufferBit | BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndexBufferBit);
                        if (Chain)
                            _liveBuffers[name] = buffer;
                    }
                    written.Add((name, buffer));
                }
                else if (buffer == null && Chain && _liveBuffers.TryGetValue(name, out var live))
                    buffer = live;
                var size = (ulong)d["byteSize"]!;
                bindings.Buffer(set, binding, element, buffer ?? Buffer((string)before[name]!["sha1"]!), (ulong)d["byteOffset"]!, size >= (1UL << 40) ? Vk.WholeSize : size);
            }
            foreach (var smp in cs["samplers"]!.AsArray())
                bindings.Sampler((int)smp!["bind"]!["set"]!, (int)smp["bind"]!["binding"]!, (uint)(int)smp["access"]!["arrayElement"]!, SamplerFor(smp["sampler"]!));
            var dim = rec["dispatchDimension"]!.AsArray().Select(v => (uint)v!).ToArray();
            runner.Dispatch(code, bindings, dim[0], dim[1], dim[2]);
            var report = new List<string>();
            var equal = true;
            foreach (var (name, buffer) in written)
            {
                var theirs = Blob((string)after[name]!["sha1"]!);
                var beforeBytes = Blob((string)before[name]!["sha1"]!);
                var changed = Enumerable.Range(0, theirs.Length).Count(i => beforeBytes[i] != theirs[i]);
                // A buffer the dispatch leaves as it was (an input bound read-write) whose contents are ours: it must
                // still hold our upload.
                if (changed == 0 && Overrides.TryGetValue((string)before[name]!["sha1"]!, out var uploaded))
                    theirs = uploaded;
                var ours = SceneDrawRunner.Read(buffer, 0, (ulong)theirs.Length);
                var diff = Enumerable.Range(0, theirs.Length).Count(i => ours[i] != theirs[i]);
                equal &= diff == 0;
                report.Add($"{name}: {(diff == 0 ? "same" : $"{diff} bytes differ")} ({changed} bytes changed by the dispatch)");
                if (!Chain)
                    gpu.Destroy(buffer);
            }
            return (equal ? "equal; " : "DIFFERENT; ") + string.Join("; ", report);
        }

        public void Dispose()
        {
            foreach (var v in _liveViews.Values)
                runner.DestroyView(v);
            foreach (var b in _overridden)
                gpu.Destroy(b);
            foreach (var b in _liveBuffers.Values)
                gpu.Destroy(b);
            foreach (var i in _live.Values)
                runner.Destroy(i);
            foreach (var b in _buffers.Values)
                gpu.Destroy(b);
            foreach (var i in _images.Values)
                runner.Destroy(i);
            foreach (var s in _samplers.Values)
                gpu.Vk.DestroySampler(gpu.Device, s, null);
        }
    }
}
