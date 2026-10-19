using System.Globalization;
using Source2.Compiler.Lighting;
using Source2.Compiler.Lighting.Bake;
using Source2.Compiler.Maps;
using ValveKeyValue;
using ValveResourceFormat.ResourceTypes;

namespace Source2.Compiler.Gpu;

/// <summary>
/// Our own bake of a vrad3 job folder (<see cref="Vrad3Job"/>), in place of vrad3: the lightmap
/// on the GPU with Valve's own vrad3 SPIR-V (<see cref="RtxBakeHost"/>), the script's
/// post-process and its EXRs (<see cref="LightmapBaker.PostProcess"/>; byte for byte on
/// ze_hold_em_p, ledger 84), then the probe volumes with Valve's rtx_vrad3_lpv on the same host
/// (<see cref="RtxBakeHost.BakeLpv"/>) and the CPU post-process.
/// </summary>
public static class OwnLightingBake
{
    /// <summary>Bakes the job in <paramref name="folder"/>, writing the EXRs vrad3 would beside its inputs.</summary>
    public static void Run(string folder, GameContent content, TextWriter log)
    {
        // RayScene.Build recurses deeper than Windows' 1 MB default stack on a whole map.
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { RunOnThread(folder, content, log); }
            catch (Exception e) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e); }
        }, 256 << 20);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    private static void RunOnThread(string folder, GameContent content, TextWriter log)
    {
        var game = Path.GetDirectoryName(Path.GetDirectoryName(content.PakPath)!)!;
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_pc_dir.vpk"), Path.Combine(game, "core", "shaders_pc_dir.vpk"));
        using var core = new ValvePak.Package();
        core.Read(Path.Combine(game, "core", "pak01_dir.vpk"));
        byte[]? Core(string path)
        {
            if (core.FindEntry(path) is not { } e)
                return null;
            core.ReadEntry(e, out var b);
            return b;
        }
        byte[]? Read(string path) => content.Read(path) ?? Core(path);
        var textures = new Dictionary<string, BakeTexture?>(StringComparer.OrdinalIgnoreCase);
        BakeTexture? Texture(string path)
        {
            if (!textures.TryGetValue(path, out var t))
                textures[path] = t = BakeTextures.Load(Read(path + "_c"), 0);
            return t;
        }
        var materials = new Dictionary<string, BakeMaterial>(StringComparer.OrdinalIgnoreCase);
        BakeMaterial Material(string path)
        {
            if (materials.TryGetValue(path, out var m))
                return m;
            var info = content.Material(path);
            MeshEntryFlags.IAttributes attrs = info is null ? MaterialAttributes.Empty : MaterialAttributes.Of(info, shaders, content.TextureSize);
            return materials[path] = BakeMaterial.From(path, attrs, info, Texture);
        }
        (BakeCubemap, float)? Cubemap(string path)
        {
            if (content.Material(path) is not { } info || !info.Textures.TryGetValue("g_tSkyTexture", out var tex))
                return null;
            var bytes = Read(tex + "_c");
            var faces = new BakeTexture[6];
            for (var f = 0; f < 6; f++)
                faces[f] = BakeTextures.Load(bytes, f)!;
            return (new BakeCubemap(faces), info.Floats.TryGetValue("g_flBrightnessExposureBias", out var b) ? b : 0f);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        KVObject sceneInfo;
        using (var s = File.OpenRead(Path.Combine(folder, "scene_info.kv3")))
            sceneInfo = KVSerializer.Create(KVSerializationFormat.KeyValues3Text).Deserialize(s);
        KVObject sceneMeshes;
        using (var reader = new BinaryReader(File.OpenRead(Path.Combine(folder, "scene_meshes.kv3"))))
        using (var owner = new ValveResourceFormat.Resource())
        {
            var kv = new BinaryKV3 { Resource = owner };
            kv.Read(reader);
            sceneMeshes = kv.Data.Root;
        }
        var scene = BakeScene.Load(sceneInfo, sceneMeshes, Material, Cubemap);
        var packing = PackingGeometry.Read(File.ReadAllBytes(Path.Combine(folder, "lightmap_packing_geometry.dat")));
        using var halton = new ValveResourceFormat.Resource();
        halton.Read(new MemoryStream(Core("textures/dev/scrambled_halton.vdata_c") ?? throw new InvalidOperationException("core's scrambled_halton.vdata_c is missing")));
        var permutations = HaltonSamples.Permutations(((BinaryKV3)halton.DataBlock!).Data.Root["permutations"].AsBlob());
        var script = Script.Parse(File.ReadAllLines(Path.Combine(folder, "script-gpu.vrad3")));
        log.WriteLine($"  bake: {scene.Instances.Count} instances, {scene.Meshes.Count} meshes, {scene.Lights.Count} lights, {script.Volumes.Count} probe volumes");

        var rtxShaders = new RtxShaderLibrary(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"), Path.Combine(game, "core", "shaders_vulkan_dir.vpk"));
        var rtx = RtxScene.Build(scene, content.Material, rtxShaders, Read);
        var sky = scene.Global.IblMaterial is { } iblMaterial && content.Material(iblMaterial) is { } skyInfo && skyInfo.Textures.TryGetValue("g_tSkyTexture", out var skyTexture) ? skyTexture : null;
        using (var device = new GpuDevice(rayTracing: true))
        using (var host = new RtxBakeHost(device, scene, rtx, rtxShaders, Read, haltonPermutations: permutations, iblTexture: sky))
        {
            var baker = new LightmapBaker(scene, packing, permutations) { Rtx = rtx, Device = host, Passes = script.LightmapMaxSpp };
            if (script.LightmapMinSpp != script.LightmapMaxSpp)
                throw new NotSupportedException($"lightmap_compute_block_gpu {script.LightmapMinSpp} {script.LightmapMaxSpp}: the adaptive pass count is not ported");
            var image = baker.Compute(File.ReadAllBytes(Path.Combine(folder, "lightmap_info.dat")));
            log.WriteLine($"  bake: lightmap on {device.Name} in {watch.Elapsed.TotalSeconds:F1} s");
            var (irradiance, directional, shadows, chart) = LightmapBaker.PostProcess(image, packing, File.ReadAllBytes(Path.Combine(folder, "lightmap_seams.dat")));
            File.WriteAllBytes(Path.Combine(folder, "irradiance.exr"), irradiance.Write());
            File.WriteAllBytes(Path.Combine(folder, "directional_irradiance.exr"), directional.Write());
            File.WriteAllBytes(Path.Combine(folder, "direct_light_shadows.exr"), shadows.Write());
            File.WriteAllBytes(Path.Combine(folder, "lightmap_chart_color.exr"), chart.Write());
            log.WriteLine($"  bake: lightmap post-process done at {watch.Elapsed.TotalSeconds:F1} s");

            foreach (var v in script.Volumes)
            {
                var r = host.BakeLpv(new LightProbeVolumeBaker.Volume(v.X, v.Y, v.Z, v.Mins, v.Maxs, v.LocalToWorld), v.MinSpp, v.MaxSpp, v.Error, log.WriteLine);
                var grid = new LightProbeVolumePost.Grid(v.X, v.Y, v.Z, r.SolidMask);
                LightProbeVolumePost.AmbientOcclusion(r.AmbientCube, grid);
                LightProbeVolumePost.RemoveFireflies(r.AmbientCube, grid, 3f);
                LightProbeVolumePost.DilateShadows(r.ShadowIds, r.Shadow, v.X, v.Y, v.Z);
                var dlshd = LightProbeVolumePost.DirectLightShadows(r.ShadowIds, r.Shadow, v.X, v.Y, v.Z);
                LightProbeVolumePost.Dilate(r.AmbientCube, grid);
                LightProbeVolumePost.WriteOpenBand(r.AmbientCube, grid);
                LightProbeVolumePost.MedianFilter(r.AmbientCube, grid, v.Median);
                File.WriteAllBytes(Path.Combine(folder, $"lpv_{v.Id}_ambientcube.exr"), Rgba(r.Width, r.Height, r.AmbientCube).Write());
                File.WriteAllBytes(Path.Combine(folder, $"lpv_{v.Id}_dlshd.exr"), Rgba(v.X * v.Z, v.Y, dlshd).Write());
                log.WriteLine($"  bake: probe volume {v.Id} ({v.X}x{v.Y}x{v.Z}) at {watch.Elapsed.TotalSeconds:F1} s");
            }
        }

    }

    private static Vrad3Exr Rgba(int width, int height, float[] rgba)
    {
        var exr = new Vrad3Exr(width, height, ["R", "G", "B", "A"]);
        foreach (var (c, k) in new[] { ("R", 0), ("G", 1), ("B", 2), ("A", 3) })
        {
            var plane = exr.Plane(c);
            for (var i = 0; i < width * height; i++)
                plane[i] = LightmapImage.ToHalfAway(rgba[i * 4 + k]);
        }
        return exr;
    }

    /// <summary>What the bake reads from script-gpu.vrad3: the lightmap's pass counts and each compute_lpv routine.</summary>
    private sealed record Script(int LightmapMinSpp, int LightmapMaxSpp, List<ScriptVolume> Volumes)
    {
        public static Script Parse(IEnumerable<string> lines)
        {
            int min = 16, max = 16;
            var volumes = new List<ScriptVolume>();
            int? id = null;
            int[]? cells = null;
            float[]? box = null, l2w = null;
            var median = 3;
            (int Min, int Max, float Error)? spp = null;
            static float[] F(string line) => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            void Close()
            {
                if (id is { } i && cells != null && box != null && l2w != null && spp is { } p)
                    volumes.Add(new(i, cells[0], cells[1], cells[2], new(box[0], box[1], box[2]), new(box[3], box[4], box[5]), l2w, p.Min, p.Max, p.Error, median));
                (id, cells, box, l2w, spp, median) = (null, null, null, null, null, 3);
            }
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.StartsWith("lightmap_compute_block_gpu ", StringComparison.Ordinal))
                {
                    var a = F(line);
                    (min, max) = ((int)a[0], (int)a[1]);
                }
                else if (line.StartsWith("compute_lpv_", StringComparison.Ordinal) && line.EndsWith(':'))
                {
                    Close();
                    id = int.Parse(line["compute_lpv_".Length..^1], CultureInfo.InvariantCulture);
                }
                else if (line.StartsWith("lpv_cells ", StringComparison.Ordinal))
                    cells = [.. F(line).Select(x => (int)x)];
                else if (line.StartsWith("lpv_box ", StringComparison.Ordinal))
                    box = F(line);
                else if (line.StartsWith("lpv_local_to_world ", StringComparison.Ordinal))
                    l2w = F(line);
                else if (line.StartsWith("lpv_compute_block_gpu ", StringComparison.Ordinal))
                {
                    var a = F(line);
                    if ((int)a[0] != 0 || (int)a[1] != 0 || (int)a[2] != 0)
                        continue;
                    spp = ((int)a[6], (int)a[7], a[8]);
                }
                else if (line.StartsWith("lpv_image_median_filter ", StringComparison.Ordinal))
                    median = (int)F(line)[0];
            }
            Close();
            return new(min, max, volumes);
        }
    }

    private sealed record ScriptVolume(int Id, int X, int Y, int Z, System.Numerics.Vector3 Mins, System.Numerics.Vector3 Maxs, float[] LocalToWorld,
                                       int MinSpp, int MaxSpp, float Error, int Median);
}
