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
/// process_all_lightmap_blocks routine and its EXRs (<see cref="LightmapBaker.PostProcess(LightmapImage, PackingGeometry, string, IEnumerable{string}, Func{Oidn}, Action{string})"/>;
/// quality 0 byte for byte on ze_hold_em_p, ledger 84; quality 1 and 2 add the OIDN filters and
/// chart_repacking.dat, ledgers 113 and 114), then the probe volumes with Valve's rtx_vrad3_lpv on the same host
/// (<see cref="RtxBakeHost.BakeLpv"/>) and the CPU post-process.
/// </summary>
public static class OwnLightingBake
{
    /// <summary>Bakes the job in <paramref name="folder"/>, writing the EXRs vrad3 would beside its inputs.</summary>
    public static void Run(string folder, GameContent content, TextWriter log) => Run(folder, content, log, false);

    /// <summary>As above; <paramref name="farfieldOnly"/> skips the lightmap, the probe volumes and the fog (a check of the far field alone).</summary>
    public static void Run(string folder, GameContent content, TextWriter log, bool farfieldOnly)
    {
        // RayScene.Build recurses deeper than Windows' 1 MB default stack on a whole map.
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { RunOnThread(folder, content, log, farfieldOnly); }
            catch (Exception e) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e); }
        }, 256 << 20);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    private static void RunOnThread(string folder, GameContent content, TextWriter log, bool farfieldOnly)
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
        var scriptLines = File.ReadAllLines(Path.Combine(folder, "script-gpu.vrad3"));
        var script = Script.Parse(scriptLines);
        var routine = scriptLines.SkipWhile(l => l.Trim() != "process_all_lightmap_blocks:").Skip(1).ToList();
        if (routine.Count == 0 && !farfieldOnly)
            throw new InvalidDataException("script-gpu.vrad3 has no process_all_lightmap_blocks routine");
        log.WriteLine($"  bake: {scene.Instances.Count} instances, {scene.Meshes.Count} meshes, {scene.Lights.Count} lights, {script.Volumes.Count} probe volumes");

        var rtxShaders = new RtxShaderLibrary(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"), Path.Combine(game, "core", "shaders_vulkan_dir.vpk"));
        var rtx = RtxScene.Build(scene, content.Material, rtxShaders, Read);
        var sky = scene.Global.IblMaterial is { } iblMaterial && content.Material(iblMaterial) is { } skyInfo && skyInfo.Textures.TryGetValue("g_tSkyTexture", out var skyTexture) ? skyTexture : null;
        using (var device = new GpuDevice(rayTracing: true))
        using (var host = new RtxBakeHost(device, scene, rtx, rtxShaders, Read, haltonPermutations: permutations, iblTexture: sky))
        {
            if (farfieldOnly)
            {
                Farfields(host, scene, scriptLines, folder, log);
                return;
            }
            var baker = new LightmapBaker(scene, packing, permutations)
            {
                Rtx = rtx, Device = host, Passes = script.LightmapMaxSpp, MinSpp = script.LightmapMinSpp, Error = script.LightmapError,
            };
            var image = baker.Compute(File.ReadAllBytes(Path.Combine(folder, "lightmap_info.dat")));
            log.WriteLine($"  bake: lightmap on {device.Name} in {watch.Elapsed.TotalSeconds:F1} s");
            // Quality 1 and 2 denoise with the game's own OpenImageDenoise (vrad3's device: CUDA on an NVIDIA card).
            Oidn? oidn = null;
            try
            {
                LightmapBaker.PostProcess(image, packing, folder, routine,
                    () => oidn ??= Oidn.Open(Path.Combine(game, "bin", "win64")) ?? throw new InvalidOperationException("no OpenImageDenoise.dll in the game's bin/win64"),
                    m => log.WriteLine("  bake: " + m));
            }
            finally
            {
                oidn?.Dispose();
            }
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

            // The fog irradiance volume: the probe compute with the fog flag, its median, fiv.exr.
            if (script.Fog is { } f)
            {
                var (fog, solid) = host.BakeFog(new LightProbeVolumeBaker.Volume(f.X, f.Y, f.Z, f.Mins, f.Maxs, [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0]), f.MinSpp, f.MaxSpp, log.WriteLine);
                LightProbeVolumePost.MedianFilter(fog, new LightProbeVolumePost.Grid(f.X, f.Y, f.Z, solid), f.Median, faces: 1);
                File.WriteAllBytes(Path.Combine(folder, f.Output), Rgba(f.X * f.Z, f.Y, fog).Write());
                log.WriteLine($"  bake: fog irradiance volume ({f.X}x{f.Y}x{f.Z}) at {watch.Elapsed.TotalSeconds:F1} s");
            }

            Farfields(host, scene, scriptLines, folder, log);
        }

    }

    /// <summary>
    /// Each compute_farfield_&lt;n&gt; routine's commands in order: farfield_box, farfield_local_to_world,
    /// farfield_cells, farfield_max_memory, farfield_compute_bricks (<see cref="FarfieldBricks"/>; the
    /// metrics on the GPU), farfield_voxelize_bricks (run with the lighting), farfield_compute_lighting
    /// [spp] [bricks per batch] (<see cref="RtxBakeHost.FarfieldLighting"/>, <see cref="FarfieldAtlas"/>),
    /// farfield_write_atlas &lt;path&gt; [-suffix &lt;s&gt;].
    /// </summary>
    private static void Farfields(RtxBakeHost host, BakeScene scene, string[] lines, string folder, TextWriter log)
    {
        float[]? box = null, l2w = null;
        int[]? cells = null;
        var maxMb = FarfieldBricks.DefaultMaxMegabytes;
        FarfieldBricks.Grid? grid = null;
        FarfieldBricks.Result? bricks = null;
        FarfieldAtlas? atlas = null;
        uint[]? solid = null;
        static float[] F(string line) => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                continue;
            switch (words[0])
            {
                case "farfield_box":
                    box = F(line);
                    break;
                case "farfield_local_to_world":
                    l2w = F(line);
                    break;
                case "farfield_cells":
                    cells = [.. F(line).Select(x => (int)x)];
                    break;
                case "farfield_max_memory":
                    maxMb = F(line)[0];
                    break;
                case "farfield_compute_bricks" when box != null && l2w != null && cells != null:
                {
                    grid = new FarfieldBricks.Grid(new(box[0], box[1], box[2]), new(box[3], box[4], box[5]), l2w, cells[0], cells[1], cells[2]);
                    var occupied = FarfieldBricks.Occupancy(grid, Triangles(scene));
                    var samples = host.FarfieldMetrics(grid, occupied);
                    bricks = FarfieldBricks.Compute(grid, samples, maxMb, m => log.WriteLine(m));
                    File.WriteAllText(Path.Combine(folder, words[1]), FarfieldBricks.Kv3(grid, bricks));
                    log.WriteLine($"  bake: far field {words[1]}, {bricks.Bricks.Count - 1} bricks");
                    break;
                }
                case "farfield_compute_lighting" when grid != null && bricks != null:
                {
                    var spp = words.Length > 1 ? int.Parse(words[1], CultureInfo.InvariantCulture) : 0x80;
                    var batch = words.Length > 2 ? int.Parse(words[2], CultureInfo.InvariantCulture) : 0x200;
                    (var luxels, solid) = host.FarfieldLighting(grid, bricks.Bricks, spp, batch, m => log.WriteLine(m));
                    atlas = FarfieldAtlas.FromLuxels(bricks.Bricks.Count, luxels, m => log.WriteLine(m));
                    break;
                }
                case "farfield_median_filter" when atlas != null:
                {
                    var passes = words.Length > 1 ? Math.Max(int.Parse(words[1], CultureInfo.InvariantCulture), 0) : 1;
                    if (passes > 0)
                        FarfieldFilters.Median(atlas, solid, passes);
                    break;
                }
                case "farfield_dilate" when atlas != null && solid != null && grid != null && bricks != null:
                {
                    var passes = words.Length > 1 ? Math.Max(int.Parse(words[1], CultureInfo.InvariantCulture), 0) : 2;
                    if (passes > 0)
                        FarfieldFilters.Dilate(atlas, solid, grid, bricks.Bricks, bricks.Descriptors, passes, m => log.WriteLine(m));
                    break;
                }
                case "farfield_filter_fireflies" when atlas != null:
                {
                    var multiplier = words.Length > 1 ? Math.Max(float.Parse(words[1], CultureInfo.InvariantCulture), 0f) : 4f;
                    if (multiplier > 0f)
                        FarfieldFilters.Fireflies(atlas, multiplier, m => log.WriteLine(m));
                    break;
                }
                case "farfield_resample_borders" when atlas != null && grid != null && bricks != null:
                    FarfieldFilters.ResampleBorders(atlas, grid, bricks.Bricks, bricks.Descriptors);
                    break;
                case "farfield_write_atlas" when atlas != null:
                {
                    var at = Array.IndexOf(words, "-suffix");
                    atlas.Write(Path.Combine(folder, words[1]), at > 0 && at + 1 < words.Length ? words[at + 1] : "");
                    break;
                }
                default:
                    if (words[0].StartsWith("farfield_", StringComparison.Ordinal) && words[0] != "farfield_voxelize_bricks")
                        log.WriteLine($"  bake: {words[0]} is not ported");
                    break;
            }
        }
    }

    // Every instance's triangles in world space, as sceneinfo_gpu's instance transforms place them.
    private static IEnumerable<(System.Numerics.Vector3, System.Numerics.Vector3, System.Numerics.Vector3)> Triangles(BakeScene scene)
    {
        foreach (var inst in scene.Instances)
        {
            var mesh = scene.Meshes[inst.Mesh];
            var m = inst.ObjectToWorld;
            float[] t = [m.M11, m.M21, m.M31, m.M41, m.M12, m.M22, m.M32, m.M42, m.M13, m.M23, m.M33, m.M43];
            var world = mesh.Positions.Select(p => FarfieldBricks.Transform(t, p)).ToArray();
            for (var i = 0; i + 2 < mesh.Indices.Length; i += 3)
                yield return (world[mesh.Indices[i]], world[mesh.Indices[i + 1]], world[mesh.Indices[i + 2]]);
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

    /// <summary>What the bake reads from script-gpu.vrad3: the lightmap's pass counts, each compute_lpv routine and the fog_irradiance_volume routine.</summary>
    private sealed record Script(int LightmapMinSpp, int LightmapMaxSpp, float LightmapError, List<ScriptVolume> Volumes, ScriptFog? Fog)
    {
        public static Script Parse(IEnumerable<string> lines)
        {
            int min = 16, max = 16;
            var error = 0.02f;
            var volumes = new List<ScriptVolume>();
            int? id = null;
            int[]? cells = null;
            float[]? box = null, l2w = null;
            var median = 3;
            (int Min, int Max, float Error)? spp = null;
            var inFog = false;
            int[]? fogCells = null;
            float[]? fogBox = null;
            (int Min, int Max)? fogSpp = null;
            var fogMedian = 0;
            string? fogOutput = null;
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
                    if (a.Length > 2)
                        error = a[2];
                }
                else if (line.StartsWith("compute_lpv_", StringComparison.Ordinal) && line.EndsWith(':'))
                {
                    Close();
                    inFog = false;
                    id = int.Parse(line["compute_lpv_".Length..^1], CultureInfo.InvariantCulture);
                }
                else if (line == "fog_irradiance_volume:")
                {
                    Close();
                    inFog = true;
                }
                else if (inFog && line.StartsWith("lpv_cells ", StringComparison.Ordinal))
                    fogCells = [.. F(line).Select(x => (int)x)];
                else if (inFog && line.StartsWith("lpv_box ", StringComparison.Ordinal))
                    fogBox = F(line);
                else if (line.StartsWith("lpv_compute_block_fog_gpu ", StringComparison.Ordinal))
                {
                    var a = F(line);
                    fogSpp ??= ((int)a[6], (int)a[7]);
                }
                else if (line.StartsWith("lpv_image_median_filter_fog ", StringComparison.Ordinal))
                    fogMedian = (int)F(line)[0];
                else if (line.StartsWith("lpv_image_write_fog ", StringComparison.Ordinal))
                    fogOutput = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
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
            var fog = fogCells != null && fogBox != null && fogSpp is { } fs && fogOutput != null
                ? new ScriptFog(fogCells[0], fogCells[1], fogCells[2], new(fogBox[0], fogBox[1], fogBox[2]), new(fogBox[3], fogBox[4], fogBox[5]), fs.Min, fs.Max, fogMedian, fogOutput)
                : null;
            return new(min, max, error, volumes, fog);
        }
    }

    private sealed record ScriptFog(int X, int Y, int Z, System.Numerics.Vector3 Mins, System.Numerics.Vector3 Maxs, int MinSpp, int MaxSpp, int Median, string Output);

    private sealed record ScriptVolume(int Id, int X, int Y, int Z, System.Numerics.Vector3 Mins, System.Numerics.Vector3 Maxs, float[] LocalToWorld,
                                       int MinSpp, int MaxSpp, float Error, int Median);
}
