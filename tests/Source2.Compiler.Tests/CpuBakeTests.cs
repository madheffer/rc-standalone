using AtiFormat = Source2.Compiler.RcTexture.AtiCompress.Format;
using System.Numerics;
using Source2.Compiler.Lighting;
using Source2.Compiler.Lighting.Bake;
using Source2.Compiler.Maps;
using ValveKeyValue;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

public class CpuBakeTests(ITestOutputHelper output)
{
    /// <summary>
    /// The CPU lightmap bake (<c>CPUBAKE=&lt;vrad3 folder&gt;</c>, <c>CPUBAKE_ADDON</c> the addon for
    /// its materials): our irradiance.exr, directional_irradiance.exr and
    /// direct_light_shadows.exr into tools/re/out/cpubake, compared with Valve's in the folder.
    /// </summary>
    [Fact]
    public void Bake()
    {
        if (Environment.GetEnvironmentVariable("CPUBAKE") is not { } folder || CS2Fixtures.StockPak() is not { } pak)
            return;
        // RayScene.Build recurses deeper than Windows' 1 MB default stack on ze_hold_em_p.
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { BakeOnThread(folder, pak); }
            catch (Exception e) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e); }
        }, 256 << 20);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    private void BakeOnThread(string folder, string pak)
    {
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var loose = Environment.GetEnvironmentVariable("CPUBAKE_ADDON") is { } addon ? new[] { Path.Combine(game, "csgo_addons", addon) } : [];
        using var content = new GameContent(pak, loose);
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
                textures[path] = t = LoadTexture(Read(path + "_c"), 0);
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
                faces[f] = LoadTexture(bytes, f)!;
            var bias = info.Floats.TryGetValue("g_flBrightnessExposureBias", out var b) ? b : 0f;
            output.WriteLine($"sky {tex}: {faces[0].Width}x{faces[0].Height}, bias {bias}");
            return (new BakeCubemap(faces), bias);
        }
        KVObject sceneInfo;
        // CPUBAKE_SCENE: another scene file in the folder (a vrad3_variant.py experiment's).
        using (var s = File.OpenRead(Path.Combine(folder, Environment.GetEnvironmentVariable("CPUBAKE_SCENE") ?? "scene_info.kv3")))
            sceneInfo = KVSerializer.Create(KVSerializationFormat.KeyValues3Text).Deserialize(s);
        var sceneMeshes = SceneInfoTests.ReadBinary(Path.Combine(folder, "scene_meshes.kv3"));
        var scene = BakeScene.Load(sceneInfo, sceneMeshes, Material, Cubemap);
        output.WriteLine($"{scene.Instances.Count} instances, {scene.Meshes.Count} meshes, {scene.Lights.Count} lights");
        // CPUBAKE_INSTANCES=1 lists each instance's mesh fields (! no_compression, d delta); =only stops there.
        if (Environment.GetEnvironmentVariable("CPUBAKE_INSTANCES") is "1" or "only")
            foreach (var inst in scene.Instances)
                output.WriteLine($"  mesh {inst.Mesh} {inst.Material.Name}: mask {inst.Mask:x2} material flags {inst.MaterialFlags:x} instance flags {inst.InstanceFlags:x} and {inst.Material.MaskAnd:x} or {inst.Material.MaskOr:x}, colour {(inst.Material.Color is { } c ? $"{c.Width}x{c.Height}" : "none")} tint {inst.Material.ColorTint}, fields {string.Join(" ", scene.Meshes[inst.Mesh].Fields.Select(f => $"{f.Name}{f.SemanticIndex}@{f.Offset}x{f.Size}{(f.NoCompression ? "!" : "")}{(f.Delta ? "d" : "")}"))}");
        if (Environment.GetEnvironmentVariable("CPUBAKE_INSTANCES") == "only")
            return;
        var packing = PackingGeometry.Read(File.ReadAllBytes(Path.Combine(folder, "lightmap_packing_geometry.dat")));
        using var halton = LoadResource(Core("textures/dev/scrambled_halton.vdata_c")!);
        var permutations = HaltonSamples.Permutations(((BinaryKV3)halton.DataBlock!).Data.Root["permutations"].AsBlob());
        RtxScene? rtx = null;
        if (Environment.GetEnvironmentVariable("CPUBAKE_RTX") != "0")
        {
            using var rtxShaders = new RtxShaderLibrary(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"), Path.Combine(game, "core", "shaders_vulkan_dir.vpk"));
            rtx = RtxScene.Build(scene, content.Material, rtxShaders, Read);
            output.WriteLine($"material hit groups: {Enumerable.Range(0, scene.Instances.Count).Count(rtx.Has)} of {scene.Instances.Count} instances");
            foreach (var note in rtx.Notes)
                output.WriteLine($"  {note}");
        }
        if (Environment.GetEnvironmentVariable("CPUBAKE_ALBEDO") == "1" && rtx != null)
        {
            // The same hits through the default hit group and through the material's own: albedo, normal, emission.
            var hits = new BakeHits(scene);
            var rtxHits = new BakeHits(scene) { Rtx = rtx };
            var random = new Random(5);
            var b = scene.Rays;
            var shown = new Dictionary<string, int>();
            for (var i = 0; i < 200000 && shown.Values.Sum() < 40; i++)
            {
                var inst0 = scene.Instances[random.Next(scene.Instances.Count)];
                var m = scene.Meshes[inst0.Mesh];
                var o = Vector3.Transform(m.Positions[random.Next(m.Positions.Length)], inst0.ObjectToWorld) + new Vector3(0, 0, 8);
                var d = Vector3.Normalize(new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f));
                var hit = b.Trace(o, d, 0f, 1e5f, 0, 255);
                if (!hit.Found)
                    continue;
                var inst = b.TriangleAt(hit.Triangle).Instance;
                if (!rtx.Has(inst))
                    continue;
                var name = scene.Instances[inst].Material.Name;
                shown.TryGetValue(name, out var count);
                if (count >= 5)
                    continue;
                shown[name] = count + 1;
                var p0 = Payload.Initial(0.5f);
                var p1 = Payload.Initial(0.5f);
                hits.Shade(ref p0, hit);
                rtxHits.Shade(ref p1, hit);
                output.WriteLine($"{Path.GetFileName(name)}: default albedo {p0.Albedo} normal {p0.Normal}; rtx albedo {p1.Albedo} normal {p1.Normal} emissive {p1.Emissive} rough {p1.Roughness} metal {p1.Metalness} face {p1.FaceNormal} flags {p1.MaterialTypeAndFlags:x}");
            }
            return;
        }
        if (Environment.GetEnvironmentVariable("CPUBAKE_LPV") is { Length: > 0 } lpv)
        {
            BakeLpv(folder, scene, rtx, permutations, lpv);
            return;
        }
        var baker = new LightmapBaker(scene, packing, permutations)
        {
            Rtx = rtx,
            Emission = float.TryParse(Environment.GetEnvironmentVariable("CPUBAKE_EMISSION"), System.Globalization.CultureInfo.InvariantCulture, out var em) ? em : 1f,
            Threads = int.TryParse(Environment.GetEnvironmentVariable("CPUBAKE_THREADS"), out var th) ? th : 4,
            CropBlock = int.TryParse(Environment.GetEnvironmentVariable("CPUBAKE_BLOCK"), out var bl) ? bl : null,
            Stride = int.TryParse(Environment.GetEnvironmentVariable("CPUBAKE_STRIDE"), out var st) ? st : 1,
        };
        if (Environment.GetEnvironmentVariable("CPUBAKE_SUNPROBE") == "1" && baker.CropBlock is { } probeBlock)
        {
            // Luxels Valve gives full sun: the sun ray from the front offset, as traced with front faces culled and with none.
            var (samples, ids) = baker.DebugSamples(File.ReadAllBytes(Path.Combine(folder, "lightmap_info.dat")), probeBlock);
            var valveShadow = Vrad3Exr.Read(File.ReadAllBytes(Path.Combine(folder, "direct_light_shadows.exr"))).Plane("R");
            var sun = Vector3.Normalize(new Vector3(scene.Lights[0].Position.X, scene.Lights[0].Position.Y, scene.Lights[0].Position.Z));
            var px = probeBlock % 4 * 1024;
            var py = probeBlock / 4 * 1024;
            var shown = 0;
            for (var i = 0; i < samples.Length && shown < 12; i += 997)
            {
                var sm = samples[i];
                if (sm.Position.W == 0f || (float)valveShadow[(py + i / 1024) * 4096 + px + i % 1024] < 0.99f)
                    continue;
                var pos = LightEval.V3(sm.Position);
                var fn = LightEval.V3(sm.FaceNormal);
                var o = LightEval.Offset(pos, fn);
                string Probe(uint flags)
                {
                    var hit = scene.Rays.Trace(o, sun, 0f, 1e6f, flags, 2);
                    if (!hit.Found)
                        return "clear";
                    var inst = scene.Rays.TriangleAt(hit.Triangle).Instance;
                    var c = scene.Rays.Corners(hit.Triangle);
                    var n = Vector3.Normalize(Vector3.Cross(c.B - c.A, c.C - c.A));
                    return $"hit instance {inst} t {hit.T:F2} front {hit.FrontFacing} n {n} (n.dir {Vector3.Dot(n, sun):F2})";
                }
                output.WriteLine($"luxel {i % 1024},{i / 1024} pos {pos} face {fn}: cull-front {Probe(RayScene.CullFrontFacing)}; none {Probe(0)}");
                shown++;
            }
            return;
        }
        if (Environment.GetEnvironmentVariable("CPUBAKE_RGDEBUG") is { } at && at.Split(',') is [var dx, var dy])
        {
            Vrad3Raygen.DebugAt = (int.Parse(dx, System.Globalization.CultureInfo.InvariantCulture), int.Parse(dy, System.Globalization.CultureInfo.InvariantCulture));
            Vrad3Raygen.Debug = s => { lock (output) output.WriteLine(s); };
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var image = baker.Compute(File.ReadAllBytes(Path.Combine(folder, "lightmap_info.dat")), output.WriteLine);
        output.WriteLine($"compute {watch.Elapsed.TotalSeconds:F1} s, NaN luxel runs {Vrad3Raygen.NanLuxels}");
        if (baker.CropBlock is { } block)
        {
            // The block alone: AHD-encoded (no filtering) against the same texels of Valve's, where we traced.
            var traced = new bool[image.Width * image.Height];
            for (var i = 0; i < traced.Length; i++)
                traced[i] = image.SampleCount(i) > 0;
            var bx = baker.Width / baker.BlockSize;
            var (ox, oy) = (block % bx * baker.BlockSize, block / bx * baker.BlockSize);
            if (Environment.GetEnvironmentVariable("CPUBAKE_PREPOST") is { Length: > 0 } prePost)
            {
                // Before any post-process: our block's channels against vrad3's lightmap_image_save
                // of the same compute (post_oracle.py bake, vrad3_variant.py), channel for channel.
                foreach (var c in new[] { LightmapImage.Ch.Irradiance, LightmapImage.Ch.ShR, LightmapImage.Ch.AmbientOcclusion, LightmapImage.Ch.Normal })
                {
                    var path = Path.Combine(prePost, $"channel{c:00}.exr");
                    if (image.Channels[c] is not { } channel || !File.Exists(path))
                        continue;
                    var names = new[] { "R", "G", "B", "A" }.Where((_, k) => (channel.Mask >> k & 1) != 0).ToArray();
                    var ours = new Vrad3Exr(image.Width, image.Height, names);
                    foreach (var (n, k) in names.Select(n => (n, "RGBA".IndexOf(n[0]))))
                    {
                        var plane = ours.Plane(n);
                        for (var i = 0; i < plane.Length; i++)
                            plane[i] = (Half)channel.Data[i * 4 + k];
                    }
                    // CPUBAKE_DUMP=<dir>: our block's raw channel images, for per-luxel comparisons.
                    if (Environment.GetEnvironmentVariable("CPUBAKE_DUMP") is { Length: > 0 } dump)
                        File.WriteAllBytes(Path.Combine(dump, $"channel{c:00}.exr"), ours.Write());
                    Compare($"channel{c:00}", ours, Vrad3Exr.Read(File.ReadAllBytes(path)), traced, ox, oy);
                }
                return;
            }
            if (Environment.GetEnvironmentVariable("CPUBAKE_FILTER") == "1")
            {
                // The script's fireflies (4) and median (7) on the block, the charts moved into it and clipped.
                var charts = packing.Charts.Select(c =>
                {
                    int x0 = Math.Clamp(c.X - ox, 0, image.Width), y0 = Math.Clamp(c.Y - oy, 0, image.Height);
                    int x1 = Math.Clamp(c.X + c.Width - ox, 0, image.Width), y1 = Math.Clamp(c.Y + c.Height - oy, 0, image.Height);
                    return c with { X = x0, Y = y0, Width = x1 - x0, Height = y1 - y0 };
                }).ToList();
                output.WriteLine($"fireflies {LightmapPost.RemoveFireflies(image, charts, 4f)}");
                LightmapPost.FilterMedian(image, charts, 7);
            }
            LightmapAhd.Encode(image);
            foreach (var (name, ours) in new[] { ("irradiance.exr", LightmapBaker.ColourExr(image)), ("directional_irradiance.exr", LightmapPost.AhdDirectionExr(image)), ("direct_light_shadows.exr", LightmapPost.DirectLightShadowsExr(image)) })
                Compare(name, ours, Vrad3Exr.Read(File.ReadAllBytes(Path.Combine(folder, name))), traced, ox, oy);
            return;
        }
        var (irr, dir, shadows) = LightmapBaker.PostProcess(image, packing, File.ReadAllBytes(Path.Combine(folder, "lightmap_seams.dat")));
        output.WriteLine($"post {watch.Elapsed.TotalSeconds:F1} s");
        var outDir = Path.Combine(RepoRoot(), "tools", "re", "out", "cpubake", Path.GetFileName(folder.TrimEnd('/', '\\')));
        Directory.CreateDirectory(outDir);
        foreach (var (name, ours) in new[] { ("irradiance.exr", irr), ("directional_irradiance.exr", dir), ("direct_light_shadows.exr", shadows) })
        {
            File.WriteAllBytes(Path.Combine(outDir, name), ours.Write());
            Compare(name, ours, Vrad3Exr.Read(File.ReadAllBytes(Path.Combine(folder, name))));
        }
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "tools", "re")))
            d = d.Parent;
        return d!.FullName;
    }

    // Per channel over the texels either image covers: the means, their ratio and the mean absolute difference.
    private void Compare(string name, Vrad3Exr ours, Vrad3Exr valve, bool[] traced, int ox, int oy)
    {
        output.WriteLine($"{name} block at {ox},{oy}:");
        foreach (var c in valve.Channels)
        {
            if (!ours.Channels.Contains(c))
                continue;
            var a = ours.Plane(c);
            var b = valve.Plane(c);
            double sa = 0, sb = 0, err = 0, saa = 0, sbb = 0, sab = 0;
            var n = 0;
            for (var y = 0; y < ours.Height; y++)
                for (var x = 0; x < ours.Width; x++)
                {
                    if (!traced[y * ours.Width + x])
                        continue;
                    var u = (double)a[y * ours.Width + x];
                    var v = (double)b[(oy + y) * valve.Width + ox + x];
                    sa += u;
                    sb += v;
                    saa += u * u;
                    sbb += v * v;
                    sab += u * v;
                    err += Math.Abs(u - v);
                    n++;
                }
            var ma = sa / Math.Max(1, n);
            var mb = sb / Math.Max(1, n);
            var corr = (sab / n - ma * mb) / Math.Sqrt(Math.Max(1e-30, (saa / n - ma * ma) * (sbb / n - mb * mb)));
            output.WriteLine($"  {c}: {n} luxels, mean ours {ma:F4} valve {mb:F4} ratio {sa / Math.Max(1e-9, sb):F3}, mean |diff| {err / Math.Max(1, n):F4}, correlation {corr:F3}");
            // 16 x 16 bins: the noise averaged out, the structure compared.
            const int bin = 16;
            var bw = (ours.Width + bin - 1) / bin;
            var bh = (ours.Height + bin - 1) / bin;
            var ba = new double[bw * bh];
            var bb = new double[bw * bh];
            var bn = new int[bw * bh];
            for (var y = 0; y < ours.Height; y++)
                for (var x = 0; x < ours.Width; x++)
                {
                    if (!traced[y * ours.Width + x])
                        continue;
                    var k = y / bin * bw + x / bin;
                    ba[k] += (double)a[y * ours.Width + x];
                    bb[k] += (double)b[(oy + y) * valve.Width + ox + x];
                    bn[k]++;
                }
            var ratios = new List<double>();
            double xa = 0, xb = 0, xaa = 0, xbb = 0, xab = 0;
            var m = 0;
            for (var k = 0; k < bn.Length; k++)
            {
                if (bn[k] < 8)
                    continue;
                var u = ba[k] / bn[k];
                var v = bb[k] / bn[k];
                xa += u;
                xb += v;
                xaa += u * u;
                xbb += v * v;
                xab += u * v;
                m++;
                if (Math.Abs(v) > 1e-4)
                    ratios.Add(u / v);
            }
            if (m < 2)
                continue;
            if (Environment.GetEnvironmentVariable("CPUBAKE_RATIOGRID") == "1" && c == "G" && name == "irradiance.exr")
            {
                // The bin ratio map: one character per 16 x 16 bin, '0' to '9' for ratios 1.0 to 1.9+ (below 1 '-', empty '.').
                for (var y = 0; y < bh; y++)
                {
                    var line = new System.Text.StringBuilder();
                    for (var x = 0; x < bw; x++)
                    {
                        var k = y * bw + x;
                        if (bn[k] < 8 || Math.Abs(bb[k]) < 1e-6)
                        {
                            line.Append('.');
                            continue;
                        }
                        var r = ba[k] / bb[k];
                        line.Append(r < 1 ? '-' : (char)('0' + Math.Min(9, (int)((r - 1) * 10))));
                    }
                    output.WriteLine("     " + line);
                }
            }
            var am = xa / m;
            var bm = xb / m;
            var bc = (xab / m - am * bm) / Math.Sqrt(Math.Max(1e-30, (xaa / m - am * am) * (xbb / m - bm * bm)));
            ratios.Sort();
            string Q(double q) => ratios.Count == 0 ? "-" : ratios[(int)(q * (ratios.Count - 1))].ToString("F3");
            output.WriteLine($"     bins {m}: correlation {bc:F3}, ratio p10 {Q(0.1)} p50 {Q(0.5)} p90 {Q(0.9)}");
        }
    }

    /// <summary>
    /// <c>CPUBAKE_LPV=&lt;volume id | all&gt;</c>: each compute_lpv routine of the script baked by
    /// <see cref="LightProbeVolumeBaker"/> (<c>CPUBAKE_LPVSPP</c> caps the passes) against vrad3's
    /// lpv_&lt;id&gt;_ambientcube_noisy.exr, face band by band, and the solid cells against Valve's empty ones.
    /// </summary>
    private void BakeLpv(string folder, BakeScene scene, RtxScene? rtx, ushort[] permutations, string which)
    {
        var script = File.ReadAllLines(Path.Combine(folder, "script-gpu.vrad3"));
        string? id = null;
        int[]? cells = null;
        float[]? box = null, l2w = null;
        static float[] F(string line) => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        foreach (var raw in script)
        {
            var line = raw.Trim();
            if (line.StartsWith("compute_lpv_", StringComparison.Ordinal) && line.EndsWith(':'))
                id = line["compute_lpv_".Length..^1];
            else if (line.StartsWith("lpv_cells ", StringComparison.Ordinal))
                cells = F(line).Select(x => (int)x).ToArray();
            else if (line.StartsWith("lpv_box ", StringComparison.Ordinal))
                box = F(line);
            else if (line.StartsWith("lpv_local_to_world ", StringComparison.Ordinal))
                l2w = F(line);
            else if (line.StartsWith("lpv_compute_block_gpu ", StringComparison.Ordinal) && id != null && cells != null && box != null && l2w != null)
            {
                if (which != "all" && which != id)
                    continue;
                var args = F(line);
                var maxSpp = (int)args[7];
                if (int.TryParse(Environment.GetEnvironmentVariable("CPUBAKE_LPVSPP"), out var cap))
                    maxSpp = Math.Min(maxSpp, cap);
                var baker = new LightProbeVolumeBaker(scene, new BakeHits(scene) { Rtx = rtx }, permutations)
                {
                    Threads = int.TryParse(Environment.GetEnvironmentVariable("CPUBAKE_THREADS"), out var th) ? th : 4,
                    Log = output.WriteLine,
                };
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var v = new LightProbeVolumeBaker.Volume(cells[0], cells[1], cells[2], new Vector3(box[0], box[1], box[2]), new Vector3(box[3], box[4], box[5]), l2w);
                var r = baker.Bake(v, (int)args[6], maxSpp, args[8]);
                output.WriteLine($"lpv {id}: {cells[0]}x{cells[1]}x{cells[2]}, {maxSpp} passes in {watch.Elapsed.TotalSeconds:F1} s; solid after voxelize {r.SolidAfterVoxelize}, after flood fill {r.SolidMask.Count(m => m != 0)} of {r.SolidMask.Length}");
                var grid = new LightProbeVolumePost.Grid(cells[0], cells[1], cells[2], r.SolidMask);
                LightProbeVolumePost.AmbientOcclusion(r.AmbientCube, grid);
                CompareLpv(folder, $"lpv_{id}_ambientcube_noisy.exr", r, cells);
                var fireflies = LightProbeVolumePost.RemoveFireflies(r.AmbientCube, grid, 3f);
                LightProbeVolumePost.DilateShadows(r.ShadowIds, r.Shadow, cells[0], cells[1], cells[2]);
                var dlshd = LightProbeVolumePost.DirectLightShadows(r.ShadowIds, r.Shadow, cells[0], cells[1], cells[2]);
                {
                    var valveShadow = Vrad3Exr.Read(File.ReadAllBytes(Path.Combine(folder, $"lpv_{id}_dlshd.exr")));
                    foreach (var (c, k) in new[] { ("R", 0), ("G", 1), ("B", 2), ("A", 3) })
                    {
                        if (!valveShadow.Channels.Contains(c))
                            continue;
                        var vp = valveShadow.Plane(c);
                        double sa = 0, sb = 0, sab = 0, saa = 0, sbb = 0;
                        var same = 0;
                        for (var i = 0; i < vp.Length; i++)
                        {
                            double xa = (float)(Half)dlshd[i * 4 + k], xb = (float)vp[i];
                            sa += xa; sb += xb; sab += xa * xb; saa += xa * xa; sbb += xb * xb;
                            if ((Half)dlshd[i * 4 + k] == vp[i])
                                same++;
                        }
                        var m = vp.Length;
                        var corr = (sab / m - sa / m * (sb / m)) / Math.Sqrt(Math.Max(1e-30, (saa / m - sa / m * (sa / m)) * (sbb / m - sb / m * (sb / m))));
                        output.WriteLine($"  dlshd {c}: mean ours {sa / m:F4} valve {sb / m:F4}, correlation {corr:F3}, identical {same} of {m}");
                    }
                }
                LightProbeVolumePost.Dilate(r.AmbientCube, grid);
                LightProbeVolumePost.WriteOpenBand(r.AmbientCube, grid);
                LightProbeVolumePost.MedianFilter(r.AmbientCube, grid, 3);
                output.WriteLine($"  fireflies {fireflies}");
                CompareLpv(folder, $"lpv_{id}_ambientcube.exr", r, cells);
            }
        }
    }

    private void CompareLpv(string folder, string file, LightProbeVolumeBaker.Result r, int[] cells)
    {
        var valve = Vrad3Exr.Read(File.ReadAllBytes(Path.Combine(folder, file)));
        var ours = new Vrad3Exr(r.Width, r.Height, ["R", "G", "B", "A"]);
        foreach (var (c, k) in new[] { ("R", 0), ("G", 1), ("B", 2), ("A", 3) })
        {
            var plane = ours.Plane(c);
            for (var i = 0; i < r.Width * r.Height; i++)
                plane[i] = (Half)r.AmbientCube[i * 4 + k];
        }
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "ours_" + file), ours.Write());
        int nx = cells[0], ny = cells[1], nz = cells[2];
        output.WriteLine($"  {file}:");
        int bothSolid = 0, oursOnly = 0, valveOnly = 0;
        var vr = valve.Plane("R");
        var vg = valve.Plane("G");
        var vb = valve.Plane("B");
        for (var z = 0; z < nz; z++)
            for (var y = 0; y < ny; y++)
                for (var x = 0; x < nx; x++)
                {
                    var empty = true;
                    for (var f = 0; f < 6 && empty; f++)
                    {
                        var at = (f * ny + y) * r.Width + z * nx + x;
                        empty = (float)vr[at] == 0f && (float)vg[at] == 0f && (float)vb[at] == 0f;
                    }
                    var solid = r.SolidMask[((z + 1) * (ny + 2) + y + 1) * (nx + 2) + x + 1] != 0;
                    if (solid && empty) bothSolid++;
                    else if (solid) oursOnly++;
                    else if (empty) valveOnly++;
                }
        output.WriteLine($"    solid: both {bothSolid}, ours only {oursOnly}, valve only {valveOnly}");
        for (var f = 0; f < 7; f++)
            foreach (var c in new[] { "R", "G", "B", "A" })
            {
                var a = ours.Plane(c);
                var b = valve.Plane(c);
                double sa = 0, sb = 0, sab = 0, saa = 0, sbb = 0;
                int n = 0, same = 0;
                for (var y = 0; y < ny; y++)
                    for (var x = 0; x < r.Width; x++)
                    {
                        var at = (f * ny + y) * r.Width + x;
                        var xa = (double)a[at];
                        var xb = (double)b[at];
                        if (a[at] == b[at])
                            same++;
                        if (xa == 0 && xb == 0)
                            continue;
                        sa += xa; sb += xb; sab += xa * xb; saa += xa * xa; sbb += xb * xb;
                        n++;
                    }
                if (n == 0)
                {
                    output.WriteLine($"    band {f} {c}: all zero in both");
                    continue;
                }
                var ma = sa / n;
                var mb = sb / n;
                var corr = (sab / n - ma * mb) / Math.Sqrt(Math.Max(1e-30, (saa / n - ma * ma) * (sbb / n - mb * mb)));
                output.WriteLine($"    band {f} {c}: {n} cells, mean ours {ma:F4} valve {mb:F4} ratio {sa / Math.Max(1e-12, sb):F3}, correlation {corr:F3}, identical texels {same} of {ny * r.Width}");
            }
    }

    private void Compare(string name, Vrad3Exr ours, Vrad3Exr valve)
    {
        output.WriteLine($"{name}: ours {ours.Width}x{ours.Height} [{string.Join(",", ours.Channels)}], valve {valve.Width}x{valve.Height} [{string.Join(",", valve.Channels)}]");
        foreach (var c in valve.Channels)
        {
            if (!ours.Channels.Contains(c))
                continue;
            var a = ours.Plane(c);
            var b = valve.Plane(c);
            double sa = 0, sb = 0, err = 0;
            var n = 0;
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                var x = (double)a[i];
                var y = (double)b[i];
                if (x == 0 && y == 0)
                    continue;
                sa += x;
                sb += y;
                err += Math.Abs(x - y);
                n++;
            }
            output.WriteLine($"  {c}: {n} texels, mean ours {sa / Math.Max(1, n):F4} valve {sb / Math.Max(1, n):F4} ratio {sa / Math.Max(1e-9, sb):F3}, mean |diff| {err / Math.Max(1, n):F4}");
        }
    }

    private static ValveResourceFormat.Resource LoadResource(byte[] bytes)
    {
        var res = new ValveResourceFormat.Resource();
        res.Read(new MemoryStream(bytes));
        return res;
    }

    /// <summary>A texture's mip 0 (face <paramref name="face"/> of a cubemap) as linear floats; 8-bit textures are taken as sRGB.</summary>
    internal static BakeTexture? LoadTexture(byte[]? bytes, int face)
    {
        if (bytes is null)
            return null;
        using var res = LoadResource(bytes);
        if (res.DataBlock is not Texture tex)
            return null;
        using var bmp = tex.GenerateBitmap(0, (Texture.CubemapFace)face, 0, default);
        var w = bmp.Width;
        var h = bmp.Height;
        var texels = new Vector4[w * h];
        if (bmp.ColorType is SkiaSharp.SKColorType.RgbaF32)
        {
            var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bmp.GetPixelSpan());
            for (var i = 0; i < texels.Length; i++)
                texels[i] = new Vector4(span[i * 4], span[i * 4 + 1], span[i * 4 + 2], span[i * 4 + 3]);
        }
        else if (bmp.ColorType is SkiaSharp.SKColorType.RgbaF16)
        {
            var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(bmp.GetPixelSpan());
            for (var i = 0; i < texels.Length; i++)
                texels[i] = new Vector4((float)span[i * 4], (float)span[i * 4 + 1], (float)span[i * 4 + 2], (float)span[i * 4 + 3]);
        }
        else
        {
            // 8-bit: the bitmap's own byte order (CopyTo between colour types can fail and leave zeros).
            using var rgba = bmp.ColorType is SkiaSharp.SKColorType.Bgra8888 or SkiaSharp.SKColorType.Rgba8888 ? null : bmp.Copy(SkiaSharp.SKColorType.Rgba8888);
            var src = rgba ?? bmp;
            var bytes8 = src.GetPixelSpan().ToArray();
            if (src.ColorType == SkiaSharp.SKColorType.Bgra8888)
                for (var i = 0; i < bytes8.Length; i += 4)
                    (bytes8[i], bytes8[i + 2]) = (bytes8[i + 2], bytes8[i]);
            return BakeTexture.FromRgba8(w, h, bytes8, true);
        }
        return new BakeTexture(w, h, texels);
    }
    /// <summary>Exploration (<c>CPUBAKE_MATERIAL=&lt;vmat&gt;</c>): a material's attributes and parameters.</summary>
    [Fact]
    public void DumpMaterial()
    {
        if (Environment.GetEnvironmentVariable("CPUBAKE_MATERIAL") is not { } name || CS2Fixtures.StockPak() is not { } pak)
            return;
        using var content = new GameContent(pak);
        var bytes = content.Read(name + "_c");
        Assert.NotNull(bytes);
        using var res = new ValveResourceFormat.Resource();
        res.Read(new MemoryStream(bytes!));
        var mat = (Material)res.DataBlock!;
        output.WriteLine($"shader {mat.ShaderName}");
        foreach (var (k, v) in mat.IntAttributes) output.WriteLine($"int attr {k} = {v}");
        foreach (var (k, v) in mat.FloatAttributes) output.WriteLine($"float attr {k} = {v}");
        foreach (var (k, v) in mat.StringAttributes) output.WriteLine($"string attr {k} = {v}");
        foreach (var (k, v) in mat.VectorAttributes) output.WriteLine($"vector attr {k} = {v}");
        foreach (var (k, v) in mat.TextureParams) output.WriteLine($"texture {k} = {v}");
        foreach (var (k, v) in mat.FloatParams) output.WriteLine($"float {k} = {v}");
        foreach (var (k, v) in mat.IntParams) output.WriteLine($"int {k} = {v}");
        foreach (var (k, v) in mat.VectorParams) output.WriteLine($"vector {k} = {v}");
    }
}

public class CpuBakeProbe(ITestOutputHelper output)
{
    /// <summary>
    /// <c>RTXCOMBO=&lt;scene_info.kv3&gt;</c>: each material of the scene, its shader, F_ parameters and the
    /// static combo of the shader's _vulkan_60_rtx program (the hit group vrad3 runs).
    /// </summary>
    [Fact]
    public void RtxCombos()
    {
        if (Environment.GetEnvironmentVariable("RTXCOMBO") is not { Length: > 0 } sceneInfo || CS2Fixtures.StockPak() is not { } pak)
            return;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        using var content = new GameContent(pak);
        using var shaders = new ValvePak.Package();
        shaders.Read(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"));
        VfxProgram? Load(string name)
        {
            if (shaders.FindEntry($"shaders/vfx/{name}.vcs") is not { } e)
                return null;
            shaders.ReadEntry(e, out var bytes);
            var p = new ValveResourceFormat.CompiledShader.VfxProgramData();
            p.Read(name + ".vcs", new MemoryStream(bytes));
            return new VfxProgram(p);
        }
        var text = File.ReadAllText(sceneInfo);
        foreach (var m in System.Text.RegularExpressions.Regex.Matches(text, "material = resource_name:\"([^\"]+)\"").Select(m => m.Groups[1].Value).Distinct())
        {
            var info = content.Material(m);
            if (info is null)
            {
                output.WriteLine($"{m}: missing");
                continue;
            }
            var shader = Path.GetFileNameWithoutExtension(info.Shader);
            var features = Load($"{shader}_vulkan_60_features");
            var rtx = Load($"{shader}_vulkan_60_rtx");
            var fparams = info.Params.Where(p => p.Key.StartsWith("F_", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => (byte)p.Value);
            var id = features is null || rtx is null ? -1
                : ValveResourceFormat.IO.ShaderDataProvider.GetStaticConfiguration_ForFeatureState(features.Data, rtx.Data, fparams).StaticComboId;
            var exists = rtx?.Data.StaticComboEntries.ContainsKey(id) == true;
            output.WriteLine($"{m}: {shader} [{string.Join(" ", fparams.Select(p => $"{p.Key}={p.Value}"))}] rtx combo {id}{(exists ? "" : " (not present)")}");
            features?.Data.Dispose();
            rtx?.Data.Dispose();
        }
    }

    private sealed record VfxProgram(ValveResourceFormat.CompiledShader.VfxProgramData Data);

    /// <summary>
    /// <c>LMTEX=&lt;vrad3 folder&gt;|&lt;map vpk&gt;|&lt;map name&gt;</c>: irradiance.exr through the game's
    /// ispc_texcomp.dll with each BC6H profile, against the shipped lightmaps/irradiance.vtex_c (the first
    /// 16 block rows), and the shipped texture's header.
    /// </summary>
    [Fact]
    public void LightmapEncode()
    {
        if (Environment.GetEnvironmentVariable("LMTEX") is not { Length: > 0 } spec || spec.Split('|') is not [var folder, var vpk, var map] || CS2Fixtures.StockPak() is not { } pak)
            return;
        var bin = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, "..", "bin", "win64"));
        Assert.True(Source2.Compiler.IspcTexcomp.Load(bin), bin);
        using var pkg = new ValvePak.Package();
        pkg.Read(vpk);
        pkg.ReadEntry(pkg.FindEntry($"maps/{map}/lightmaps/irradiance.vtex_c")!, out var vtex);
        using var res = new ValveResourceFormat.Resource();
        res.Read(new MemoryStream(vtex));
        var t = (Texture)res.DataBlock!;
        output.WriteLine($"shipped: {t.Width}x{t.Height}x{t.Depth} {t.Format} flags {t.Flags} mips {t.NumMipLevels}");
        var shipped = new byte[t.CalculateBufferSizeForMipLevel(0)];
        t.ReadTextureMipLevel(shipped, 0);
        var exr = Vrad3Exr.Read(File.ReadAllBytes(Path.Combine(folder, "irradiance.exr")));
        const int rows = 16 * 4;
        var w = exr.Width;
        var r = exr.Plane("R");
        var g = exr.Plane("G");
        var b = exr.Plane("B");
        foreach (var alpha in new[] { (Half)1f, (Half)0f })
        {
            var rgba = new Half[w * rows * 4];
            for (var i = 0; i < w * rows; i++)
                (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = (r[i], g[i], b[i], alpha);
            for (var profile = 0; profile < 5; profile++)
            {
                var ours = Source2.Compiler.IspcTexcomp.CompressBc6h(rgba, w, rows, Source2.Compiler.IspcTexcomp.Bc6hProfile(profile));
                var same = 0;
                for (var k = 0; k < ours.Length / 16; k++)
                    if (ours.AsSpan(k * 16, 16).SequenceEqual(shipped.AsSpan(k * 16, 16)))
                        same++;
                output.WriteLine($"alpha {(float)alpha} profile {Source2.Compiler.IspcTexcomp.Bc6hProfiles[profile]}: {same} of {ours.Length / 16} blocks identical");
            }
        }
    }

    /// <summary>
    /// <c>LMTEX=...</c> as above: directional_irradiance.exr and lightmap_chart_color.exr to 8 bits (several
    /// candidate conversions) through ispc's BC1, against directional_irradiance.vtex_c and debug_chart_color.vtex_c.
    /// </summary>
    [Fact]
    public void LightmapEncodeBc1()
    {
        if (Environment.GetEnvironmentVariable("LMTEX") is not { Length: > 0 } spec || spec.Split('|') is not [var folder, var vpk, var map])
            return;
        using var pkg = new ValvePak.Package();
        pkg.Read(vpk);
        foreach (var (exrName, vtexName, format) in new[] { ("directional_irradiance.exr", "directional_irradiance", AtiFormat.Dxt1), ("lightmap_chart_color.exr", "debug_chart_color", AtiFormat.Dxt1), ("direct_light_shadows.exr", "direct_light_shadows", AtiFormat.Ati1n) })
        {
            pkg.ReadEntry(pkg.FindEntry($"maps/{map}/lightmaps/{vtexName}.vtex_c")!, out var vtex);
            using var res = new ValveResourceFormat.Resource();
            res.Read(new MemoryStream(vtex));
            var t = (Texture)res.DataBlock!;
            var shipped = new byte[t.CalculateBufferSizeForMipLevel(0)];
            t.ReadTextureMipLevel(shipped, 0);
            var exr = Vrad3Exr.Read(File.ReadAllBytes(Path.Combine(folder, exrName)));
            output.WriteLine($"{vtexName}: {t.Width}x{t.Height}x{t.Depth} {t.Format}; exr {exr.Width}x{exr.Height} [{string.Join(",", exr.Channels)}]");
            var w = exr.Width;
            var rows = 64;
            var planes = new[] { "R", "G", "B", "A" }.Select(c => exr.Channels.Contains(c) ? exr.Plane(c) : null).ToArray();
            var rgba = new float[w * rows * 4];
            for (var i = 0; i < w * rows; i++)
                for (var c = 0; c < 4; c++)
                    rgba[i * 4 + c] = planes[c] is { } p ? (float)p[i] : c == 3 ? 1f : 0f;
            var bytes = Source2.Compiler.RcTexture.RcMipChain.WriteRgba8(new Source2.Compiler.RcTexture.RcMipChain.Level(w, rows, rgba), srgb: false);
            for (var i = 0; i < bytes.Length; i += 4)
                (bytes[i], bytes[i + 2]) = (bytes[i + 2], bytes[i]);
            var blockBytes = Source2.Compiler.RcTexture.AtiCompress.BlockSize(format);
            foreach (var lum in new[] { false, true })
            {
                var ours = Source2.Compiler.RcTexture.AtiCompress.Compress(bytes, w, rows, format, lum);
                var same = 0;
                for (var k = 0; k < ours.Length / blockBytes; k++)
                    if (ours.AsSpan(k * blockBytes, blockBytes).SequenceEqual(shipped.AsSpan(k * blockBytes, blockBytes)))
                        same++;
                output.WriteLine($"  perceptual {lum}: {same} of {ours.Length / blockBytes} blocks identical");
            }
        }
    }

    /// <summary>
    /// <c>LMTEX=...</c>: the six lightmap textures rebuilt by <see cref="LightmapTextures"/> from vrad3's
    /// EXRs against the map's files, byte for byte, RED2's hashes derived.
    /// </summary>
    [Fact]
    public void LightmapVtexFiles()
    {
        if (Environment.GetEnvironmentVariable("LMTEX") is not { Length: > 0 } spec || spec.Split('|') is not [var folder, var vpk, var map] || CS2Fixtures.StockPak() is not { } pak)
            return;
        var bin = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, "..", "bin", "win64"));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vpk))!);
        using var pkg = new ValvePak.Package();
        pkg.Read(vpk);
        var different = new List<string>();
        foreach (var (exrName, channel) in new[] { ("irradiance.exr", "irradiance"), ("directional_irradiance.exr", "directional_irradiance"), ("lightmap_chart_color.exr", "debug_chart_color"), ("direct_light_shadows.exr", "direct_light_shadows"), ("lpv_atlas_ambientcube.exr", "env_light_probe_volume_atlas"), ("lpv_atlas_dlshd.exr", "env_light_probe_volume_atlas_dlshd") })
        {
            pkg.ReadEntry(pkg.FindEntry($"maps/{map}/lightmaps/{channel}.vtex_c")!, out var shipped);
            using var res = new ValveResourceFormat.Resource();
            res.Read(new MemoryStream(shipped));
            var args = res.EditInfo!.ArgumentDependencies;
            var image = LightmapTextures.Bitmap(Vrad3Exr.Read(File.ReadAllBytes(Path.Combine(folder, exrName))));
            var t = (Texture)res.DataBlock!;
            var ours = channel.StartsWith("env_light_probe", StringComparison.Ordinal)
                ? LightmapTextures.AtlasVtex(image, t.Width, t.Height, t.Depth, channel.EndsWith("dlshd", StringComparison.Ordinal) ? LightmapTextures.ImageFormat.Bc7 : LightmapTextures.ImageFormat.Bc6h, $"csgo_addons/{addon}", null, bin, ambientCube: !channel.EndsWith("dlshd", StringComparison.Ordinal))
                : LightmapTextures.Vtex(channel, image, LightmapTextures.Cs2Channels[channel], $"csgo_addons/{addon}", nativeFolder: bin);
            var first = Enumerable.Range(0, Math.Min(ours.Length, shipped.Length)).FirstOrDefault(i => ours[i] != shipped[i], -1);
            output.WriteLine($"{channel}: ours {ours.Length} shipped {shipped.Length} {(first < 0 && ours.Length == shipped.Length ? "identical" : $"first difference at {first}")}");
            if (first >= 0)
                output.WriteLine($"  ours {Convert.ToHexString(ours.AsSpan(Math.Max(0, first - 8), 32))}" + Environment.NewLine + $"  vrad {Convert.ToHexString(shipped.AsSpan(Math.Max(0, first - 8), 32))}");
            if (first >= 0 || ours.Length != shipped.Length)
                different.Add(channel);
        }
        Assert.Empty(different);
    }

    /// <summary>
    /// <c>LMTEX=...</c>: the LPV atlas EXRs cut into 36x44 slices (tiles in row-major order) and encoded per slice,
    /// BC6H for the ambient cube and BC7 for dlshd, against env_light_probe_volume_atlas(_dlshd).vtex_c; reflectivity candidates.
    /// </summary>
    [Fact]
    public void LpvAtlasEncode()
    {
        if (Environment.GetEnvironmentVariable("LMTEX") is not { Length: > 0 } spec || spec.Split('|') is not [var folder, var vpk, var map] || CS2Fixtures.StockPak() is not { } pak)
            return;
        var bin = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, "..", "bin", "win64"));
        Assert.True(Source2.Compiler.IspcTexcomp.Load(bin), bin);
        using var pkg = new ValvePak.Package();
        pkg.Read(vpk);
        foreach (var (exrName, vtexName) in new[] { ("lpv_atlas_ambientcube.exr", "env_light_probe_volume_atlas"), ("lpv_atlas_dlshd.exr", "env_light_probe_volume_atlas_dlshd") })
        {
            pkg.ReadEntry(pkg.FindEntry($"maps/{map}/lightmaps/{vtexName}.vtex_c")!, out var vtex);
            using var res = new ValveResourceFormat.Resource();
            res.Read(new MemoryStream(vtex));
            var t = (Texture)res.DataBlock!;
            var shipped = new byte[t.CalculateBufferSizeForMipLevel(0)];
            t.ReadTextureMipLevel(shipped, 0);
            var image = LightmapTextures.Bitmap(Vrad3Exr.Read(File.ReadAllBytes(Path.Combine(folder, exrName))));
            int sw = t.Width, sh = t.Height, cols = image.Width / sw;
            var bc7 = t.Format.ToString() == "BC7";
            var sliceBytes = ((sw + 3) / 4) * ((sh + 3) / 4) * 16;
            output.WriteLine($"{vtexName}: {t.Width}x{t.Height}x{t.Depth} {t.Format}, image {image.Width}x{image.Height}, shipped {shipped.Length} bytes, {sliceBytes} per slice");
            var sliceSame = 0;
            var all = new List<float>();
            var slices = new List<Source2.Compiler.RcTexture.RcMipChain.Level>();
            for (var z = 0; z < t.Depth; z++)
            {
                var tx = z % cols;
                var ty = z / cols;
                var px = new float[sw * sh * 4];
                for (var y = 0; y < sh; y++)
                    Array.Copy(image.Rgba, ((ty * sh + y) * image.Width + tx * sw) * 4, px, y * sw * 4, sw * 4);
                var level = new Source2.Compiler.RcTexture.RcMipChain.Level(sw, sh, px);
                slices.Add(level);
                byte[] ours;
                if (bc7)
                {
                    var blocks = new byte[sliceBytes / 16 * 64];
                    var k = 0;
                    for (var by = 0; by < sh / 4; by++)
                        for (var bx = 0; bx < sw / 4; bx++)
                            for (var y = 0; y < 4; y++)
                                for (var x = 0; x < 4; x++)
                                    for (var c = 0; c < 4; c++)
                                        blocks[k++] = (byte)MathF.Round(MathF.Min(1f, MathF.Max(0f, px[((by * 4 + y) * sw + bx * 4 + x) * 4 + c])) * 255f, MidpointRounding.ToEven);
                    ours = Source2.Compiler.RcTexture.Bc7e.EncodeBlocks(blocks, perceptual: false, Source2.Compiler.RcTexture.Bc7Vendor.Amd);
                }
                else
                {
                    var half = px.Select(v => (Half)v).ToArray();
                    ours = Source2.Compiler.IspcTexcomp.CompressBc6h(half, sw, sh, Source2.Compiler.IspcTexcomp.Bc6hProfile(3));
                }
                if (ours.AsSpan().SequenceEqual(shipped.AsSpan(z * sliceBytes, sliceBytes)))
                    sliceSame++;
            }
            output.WriteLine($"  {sliceSame} of {t.Depth} slices identical");
            var volume = new Source2.Compiler.RcTexture.RcMipChain.Level(sw, sh * t.Depth, slices.SelectMany(l => l.Rgba).ToArray());
            string F(float v) => $"{v:R}({BitConverter.SingleToUInt32Bits(v):x8})";
            output.WriteLine("  shipped " + string.Join(" ", new[] { t.Reflectivity.X, t.Reflectivity.Y, t.Reflectivity.Z, t.Reflectivity.W }.Select(F)));
            output.WriteLine("  volume  " + string.Join(" ", LightmapTextures.Reflectivity(volume).Select(F)));
            output.WriteLine("  image   " + string.Join(" ", LightmapTextures.Reflectivity(image).Select(F)));
        }
    }

    /// <summary><c>LMTEX=...</c>: each lightmap EXR's reflectivity candidates (the float chain's 1x1, a four-lane mean) against the shipped header.</summary>
    [Fact]
    public void LightmapReflectivity()
    {
        if (Environment.GetEnvironmentVariable("LMTEX") is not { Length: > 0 } spec || spec.Split('|') is not [var folder, var vpk, var map])
            return;
        using var pkg = new ValvePak.Package();
        pkg.Read(vpk);
        foreach (var (exrName, vtexName) in new[] { ("irradiance.exr", "irradiance"), ("directional_irradiance.exr", "directional_irradiance"), ("lightmap_chart_color.exr", "debug_chart_color"), ("direct_light_shadows.exr", "direct_light_shadows") })
        {
            pkg.ReadEntry(pkg.FindEntry($"maps/{map}/lightmaps/{vtexName}.vtex_c")!, out var vtex);
            using var res = new ValveResourceFormat.Resource();
            res.Read(new MemoryStream(vtex));
            var t = (Texture)res.DataBlock!;
            var exr = Vrad3Exr.Read(File.ReadAllBytes(Path.Combine(folder, exrName)));
            var n = exr.Width * exr.Height;
            var rgba = new float[n * 4];
            var planes = new[] { "R", "G", "B", "A" }.Select(c => exr.Channels.Contains(c) ? exr.Plane(c) : null).ToArray();
            for (var i = 0; i < n; i++)
                for (var c = 0; c < 4; c++)
                    rgba[i * 4 + c] = planes[c] is { } p ? (float)p[i] : 1f;
            var chain = Source2.Compiler.RcTexture.RcMipChain.Chain(new Source2.Compiler.RcTexture.RcMipChain.Level(exr.Width, exr.Height, rgba));
            var one = chain[^1].Rgba;
            var lanes = new float[4];
            var mean = new float[4];
            for (var c = 0; c < 4; c++)
            {
                Array.Clear(lanes);
                for (var i = 0; i < n; i++)
                    lanes[i & 3] += rgba[i * 4 + c];
                mean[c] = ((lanes[0] + lanes[1]) + (lanes[2] + lanes[3])) / n;
            }
            string F(float v) => $"{v:R}({BitConverter.SingleToUInt32Bits(v):x8})";
            var shipped = new[] { t.Reflectivity.X, t.Reflectivity.Y, t.Reflectivity.Z, t.Reflectivity.W };
            output.WriteLine($"{vtexName} [{string.Join(",", exr.Channels)}]");
            output.WriteLine("  shipped " + string.Join(" ", shipped.Select(F)));
            output.WriteLine("  chain   " + string.Join(" ", one.Select(F)));
            output.WriteLine("  mean    " + string.Join(" ", mean.Select(F)));
        }
    }

    /// <summary><c>TEXPIX=&lt;vtex&gt;</c>: format, codec from the edit info, and a few texels decoded raw and with Auto.</summary>
    [Fact]
    public void TexturePixels()
    {
        if (Environment.GetEnvironmentVariable("TEXPIX") is not { Length: > 0 } name || CS2Fixtures.StockPak() is not { } pak)
            return;
        using var content = new GameContent(pak);
        using var res = new ValveResourceFormat.Resource();
        res.Read(new MemoryStream(content.Read(name + "_c")!));
        var t = (Texture)res.DataBlock!;
        output.WriteLine($"{t.Width}x{t.Height} {t.Format} flags {t.Flags} codec {t.RetrieveCodecFromResourceEditInfo()} reflectivity {t.Reflectivity}");
        foreach (var codec in new[] { ValveResourceFormat.TextureDecoders.TextureCodec.None, ValveResourceFormat.TextureDecoders.TextureCodec.Auto })
        {
            using var bmp = t.GenerateBitmap(0, 0, 0, codec);
            var px = new[] { bmp.GetPixel(bmp.Width / 2, bmp.Height / 2), bmp.GetPixel(bmp.Width / 4, bmp.Height / 4), bmp.GetPixel(bmp.Width / 3, bmp.Height * 2 / 3) };
            output.WriteLine($"{codec}: {bmp.ColorType} {string.Join(" ", px.Select(p => $"({p.Red},{p.Green},{p.Blue},{p.Alpha})"))}");
        }
    }

    /// <summary><c>RTXWRITES=&lt;shader&gt;|&lt;combo&gt;</c>: a static combo's variable write sequence (name, binding slot, control, layout set, register offset).</summary>
    [Fact]
    public void RtxWrites()
    {
        if (Environment.GetEnvironmentVariable("RTXWRITES") is not { Length: > 0 } spec || spec.Split('|') is not [var shader, var combo] || CS2Fixtures.StockPak() is not { } pak)
            return;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        using var shaders = new ValvePak.Package();
        shaders.Read(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"));
        shaders.ReadEntry(shaders.FindEntry($"shaders/vfx/{shader}_vulkan_60_rtx.vcs")!, out var bytes);
        using var p = new ValveResourceFormat.CompiledShader.VfxProgramData();
        p.Read(shader + "_vulkan_60_rtx.vcs", new MemoryStream(bytes));
        var data = p.GetStaticCombo(long.Parse(combo, System.Globalization.CultureInfo.InvariantCulture));
        void Dump(string label, ValveResourceFormat.CompiledShader.VfxVariableIndexArray a)
        {
            output.WriteLine($"{label}: {a.Fields.Length} fields, render state from {a.FirstRenderStateElement}, constants from {a.FirstConstantElement}");
            foreach (var f in a.Fields)
                output.WriteLine($"  {p.VariableDescriptions[f.VariableIndex].Name}\t{p.VariableDescriptions[f.VariableIndex].VfxType}\tslot {f.BindingSlot}\tcontrol {f.Control}\tset {f.LayoutSet}\treg {f.RegisterOffset}");
        }
        Dump("all", data.AllVariables);
        for (var i = 0; i < data.DynamicComboVariables.Length; i++)
            Dump($"dynamic {i}", data.DynamicComboVariables[i]);
    }

    /// <summary><c>RTXVARS=&lt;shader&gt;</c>: the _vulkan_60_rtx program's variables: source, defaults and dynamic expression.</summary>
    [Fact]
    public void RtxVariables()
    {
        if (Environment.GetEnvironmentVariable("RTXVARS") is not { Length: > 0 } shader || CS2Fixtures.StockPak() is not { } pak)
            return;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        using var shaders = new ValvePak.Package();
        shaders.Read(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"));
        shaders.ReadEntry(shaders.FindEntry($"shaders/vfx/{shader}_vulkan_60_rtx.vcs")!, out var bytes);
        using var p = new ValveResourceFormat.CompiledShader.VfxProgramData();
        p.Read(shader + "_vulkan_60_rtx.vcs", new MemoryStream(bytes));
        foreach (var cb in p.ExtConstantBufferDescriptions)
            output.WriteLine($"cbuffer {cb.Name} size {cb.BufferSize}: {string.Join(", ", cb.Variables.Select(x => $"{x.Name}@{x.Offset}[{x.VectorSize}x{x.RowCount}]"))}");
        foreach (var v in p.VariableDescriptions)
        {
            var expr = v.CompiledExpression.Length > 0 ? ValveResourceFormat.CompiledShader.ShaderUtilHelpers.ParseDynamicExpression(v.CompiledExpression) : "";
            output.WriteLine($"{v.Name}\t{v.VfxType}\t{v.VariableSource}\tsrc '{v.SourceString}'\tf [{string.Join(",", v.FloatDefs)}] i [{string.Join(",", v.IntDefs)}] tex '{v.DefaultInputTexture}'\treg {v.RegisterType} off {v.RegisterOffset} srcidx {v.SourceIndex} elems {v.RegisterElements} bits {v.TypeSpecificBits} srgb {v.SrgbRead}\t{expr.Replace("\n", " ")}");
        }
    }

    /// <summary><c>LISTVPK=&lt;vpk&gt;|&lt;substring&gt;</c>: matching entries with their sizes; textures with size, format, depth and flags.</summary>
    [Fact]
    public void ListVpk()
    {
        if (Environment.GetEnvironmentVariable("LISTVPK") is not { Length: > 0 } spec || spec.Split('|') is not [var vpk, var part])
            return;
        using var pkg = new ValvePak.Package();
        pkg.Read(vpk);
        foreach (var e in pkg.Entries.SelectMany(x => x.Value).Where(e => e.GetFullPath().Contains(part, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.GetFullPath(), StringComparer.Ordinal))
        {
            var line = $"{e.GetFullPath()} {e.TotalLength}";
            if (e.TypeName == "vtex_c")
            {
                pkg.ReadEntry(e, out var bytes);
                using var res = new ValveResourceFormat.Resource();
                res.Read(new MemoryStream(bytes));
                var t = (Texture)res.DataBlock!;
                line += $" {t.Width}x{t.Height}x{t.Depth} {t.Format} flags {t.Flags} mips {t.NumMipLevels}";
            }
            output.WriteLine(line);
        }
    }

    /// <summary><c>VTEXDUMP=&lt;vpk&gt;|&lt;path part&gt;</c>: each matching .vtex_c's resource blocks, the DATA head in hex and the edit info as text.</summary>
    [Fact]
    public void VtexDump()
    {
        if (Environment.GetEnvironmentVariable("VTEXDUMP") is not { Length: > 0 } spec || spec.Split('|') is not [var vpk, var part])
            return;
        using var pkg = new ValvePak.Package();
        pkg.Read(vpk);
        foreach (var e in pkg.Entries.SelectMany(x => x.Value).Where(e => e.TypeName == "vtex_c" && e.GetFullPath().Contains(part, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.GetFullPath(), StringComparer.Ordinal))
        {
            pkg.ReadEntry(e, out var bytes);
            using var res = new ValveResourceFormat.Resource();
            res.Read(new MemoryStream(bytes));
            output.WriteLine($"== {e.GetFullPath()} {bytes.Length} bytes, header version {res.HeaderVersion} version {res.Version}");
            output.WriteLine("file head: " + Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 16 + 12 * res.Blocks.Count))));
            foreach (var b in res.Blocks)
                output.WriteLine($"  {b.Type} offset {b.Offset} size {b.Size}");
            var data = res.DataBlock!;
            output.WriteLine("DATA head: " + Convert.ToHexString(bytes.AsSpan((int)data.Offset, (int)Math.Min(data.Size, 96))));
            var t = (Texture)data;
            output.WriteLine($"  {t.Width}x{t.Height}x{t.Depth} {t.Format} flags {t.Flags} mips {t.NumMipLevels} reflectivity {string.Join(",", t.Reflectivity)}");
            foreach (var b in res.Blocks.Where(b => b != data))
                output.WriteLine($"--- {b.Type}" + Environment.NewLine + b);
        }
    }

    /// <summary><c>VPKX=&lt;vpk&gt;|&lt;entry&gt;|&lt;out file&gt;</c>: one entry's bytes to a file.</summary>
    [Fact]
    public void VpkExtract()
    {
        if (Environment.GetEnvironmentVariable("VPKX") is not { Length: > 0 } spec || spec.Split('|') is not [var vpk, var entry, var file])
            return;
        using var pkg = new ValvePak.Package();
        pkg.Read(vpk);
        pkg.ReadEntry(pkg.FindEntry(entry)!, out var bytes);
        File.WriteAllBytes(file, bytes);
    }

    /// <summary>
    /// <c>LPVATLAS=&lt;vrad3 folder&gt;</c>: the light probe atlases built by <see cref="LightProbeAtlasImages"/> from
    /// vrad3's own per-volume EXRs (volumes in script order, placed by <see cref="ProbeAtlas.Pack"/>) against the
    /// lpv_atlas_ambientcube.exr and lpv_atlas_dlshd.exr resourcecompiler wrote, texel for texel.
    /// </summary>
    [Fact]
    public void LpvAtlasImages()
    {
        if (Environment.GetEnvironmentVariable("LPVATLAS") is not { Length: > 0 } folder)
            return;
        var volumes = new List<(string Id, int X, int Y, int Z)>();
        string? id = null;
        foreach (var raw in File.ReadAllLines(Path.Combine(folder, "script-gpu.vrad3")))
        {
            var line = raw.Trim();
            if (line.StartsWith("compute_lpv_", StringComparison.Ordinal) && line.EndsWith(':'))
                id = line["compute_lpv_".Length..^1];
            else if (line.StartsWith("lpv_cells ", StringComparison.Ordinal) && id != null)
            {
                var c = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1..].Select(int.Parse).ToArray();
                volumes.Add((id, c[0], c[1], c[2]));
            }
        }
        var (places, size) = ProbeAtlas.PackWithSize(volumes.Select(v => new ProbeAtlas.Place(v.X, v.Y, v.Z)).ToList());
        var list = volumes.Select((v, i) => new LightProbeAtlasImages.Volume(v.X, v.Y, v.Z, places[i],
            LightProbeAtlasImages.Rgba(Vrad3Exr.Read(File.ReadAllBytes(Path.Combine(folder, $"lpv_{v.Id}_ambientcube.exr")))),
            LightProbeAtlasImages.Rgba(Vrad3Exr.Read(File.ReadAllBytes(Path.Combine(folder, $"lpv_{v.Id}_dlshd.exr")))))).ToList();
        output.WriteLine($"atlas {size.X}x{size.Y}x{size.Z} (boxes' extent {LightProbeAtlasImages.Size(list)}); places {string.Join(" ", places.Select(p => $"({p.X},{p.Y},{p.Z})"))}");
        var different = 0;
        foreach (var (file, image) in new[] { ("lpv_atlas_ambientcube.exr", LightProbeAtlasImages.AmbientCube(size, list)), ("lpv_atlas_dlshd.exr", LightProbeAtlasImages.Shadows(size, list)) })
        {
            var valve = Vrad3Exr.Read(File.ReadAllBytes(Path.Combine(folder, file)));
            var ours = LightProbeAtlasImages.Rgba(valve);
            var same = 0;
            var n = Math.Min(ours.Length, image.Length);
            for (var i = 0; i < n; i++)
                if ((Half)image[i] == (Half)ours[i])
                    same++;
            output.WriteLine($"{file}: valve {valve.Width}x{valve.Height}, ours {size.X * size.Z}x{image.Length / 4 / (size.X * size.Z)}, identical {same} of {ours.Length} values");
            if (same != ours.Length || ours.Length != image.Length)
                different++;
        }
        Assert.Equal(0, different);
    }

    /// <summary>
    /// <c>QUERYDATA=&lt;vrad3 folder&gt;|&lt;map vpk&gt;|&lt;map&gt;</c>: the shipped lightmap_query_data.kv3 decoded (keys,
    /// counts, the first vertices and indices) beside the packing geometry it comes from.
    /// </summary>
    [Fact]
    public void QueryDataDump()
    {
        if (Environment.GetEnvironmentVariable("QUERYDATA") is not { Length: > 0 } spec || spec.Split('|') is not [var folder, var vpk, var map])
            return;
        using var pkg = new ValvePak.Package();
        pkg.Read(vpk);
        pkg.ReadEntry(pkg.FindEntry($"maps/{map}/lightmaps/lightmap_query_data.kv3")!, out var bytes);
        output.WriteLine($"file {bytes.Length} bytes, head {Convert.ToHexString(bytes.AsSpan(0, 64))}");
        var root = SceneInfoTests.ReadBinary(bytes);
        foreach (var c in root.Children)
            output.WriteLine($"  {c.Key}: {SceneInfoTests.Render(c.Value)}");
        var vertices = root["vertices"].AsBlob();
        var indices = root["indices"].AsBlob();
        output.WriteLine($"vertices blob {vertices.Length}, indices blob {indices.Length}");
        for (var i = 0; i < 6 && i * 16 < vertices.Length; i++)
            output.WriteLine($"  v{i}: {BitConverter.ToSingle(vertices, i * 16)} {BitConverter.ToSingle(vertices, i * 16 + 4)} {BitConverter.ToSingle(vertices, i * 16 + 8)} uv {BitConverter.ToUInt16(vertices, i * 16 + 12)} {BitConverter.ToUInt16(vertices, i * 16 + 14)}");
        output.WriteLine("  indices " + Convert.ToHexString(indices.AsSpan(0, Math.Min(48, indices.Length))));
        var g = PackingGeometry.Read(File.ReadAllBytes(Path.Combine(folder, "lightmap_packing_geometry.dat")));
        output.WriteLine($"packing: {g.VertexCount} vertices (stride {g.VertexStride}), {g.Indices.Length} indices, offsets {string.Join(",", g.Offsets)}");
        for (var i = 0; i < 6; i++)
            output.WriteLine($"  p{i}: {BitConverter.ToSingle(g.Vertices, i * g.VertexStride)} {BitConverter.ToSingle(g.Vertices, i * g.VertexStride + 4)} {BitConverter.ToSingle(g.Vertices, i * g.VertexStride + 8)} uv {g.AtlasUv[i * 2]} {g.AtlasUv[i * 2 + 1]}");
        output.WriteLine($"  uv range {g.AtlasUv.Min()} .. {g.AtlasUv.Max()}");

        // Rebuilt from the packing geometry (probe01's log: no triangle is culled, so no volumes are needed).
        var ours = LightmapQueryData.Build(g, 4096, 4096, []);
        var mine = SceneInfoTests.ReadBinary(ours);
        output.WriteLine($"ours: {ours.Length} bytes, vertex_count {mine["vertex_count"]}, index_count {mine["index_count"]}");
        var ov = mine["vertices"].AsBlob();
        var oi = mine["indices"].AsBlob();
        output.WriteLine($"  vertices identical {ov.AsSpan().SequenceEqual(vertices)}, indices identical {oi.AsSpan().SequenceEqual(indices)}");
        if (!ov.AsSpan().SequenceEqual(vertices))
        {
            var first = Enumerable.Range(0, Math.Min(ov.Length, vertices.Length) / 16).FirstOrDefault(v => !ov.AsSpan(v * 16, 16).SequenceEqual(vertices.AsSpan(v * 16, 16)), -1);
            output.WriteLine($"  first vertex difference {first}: ours {Convert.ToHexString(ov.AsSpan(Math.Max(0, first) * 16, 16))} valve {Convert.ToHexString(vertices.AsSpan(Math.Max(0, first) * 16, 16))}");
            // The vertices one side has and the other lacks, with the packing vertices at those positions.
            var setOurs = Enumerable.Range(0, ov.Length / 16).Select(v => Convert.ToHexString(ov.AsSpan(v * 16, 16))).ToHashSet();
            var setValve = Enumerable.Range(0, vertices.Length / 16).Select(v => Convert.ToHexString(vertices.AsSpan(v * 16, 16))).ToHashSet();
            foreach (var v in setOurs.Except(setValve))
                output.WriteLine($"  only ours: {v}");
            foreach (var v in setValve.Except(setOurs))
                output.WriteLine($"  only valve: {v}");
            var pu = 65535f / LightmapQueryData.UvScale(4096);
            foreach (var v in setOurs.Except(setValve).Concat(setValve.Except(setOurs)).Select(h => Convert.FromHexString(h)).ToList())
                for (var i = 0; i < g.VertexCount; i++)
                    if (BitConverter.ToSingle(g.Vertices, i * g.VertexStride) == BitConverter.ToSingle(v, 0) && BitConverter.ToSingle(g.Vertices, i * g.VertexStride + 4) == BitConverter.ToSingle(v, 4)
                        && BitConverter.ToSingle(g.Vertices, i * g.VertexStride + 8) == BitConverter.ToSingle(v, 8))
                        output.WriteLine($"    packing {i}: uv {g.AtlasUv[i * 2]:R} {g.AtlasUv[i * 2 + 1]:R} -> {MathF.Round(g.AtlasUv[i * 2] / pu * 65535f, MidpointRounding.AwayFromZero)} {MathF.Round(g.AtlasUv[i * 2 + 1] / pu * 65535f, MidpointRounding.AwayFromZero)}");
        }
        if (!oi.AsSpan().SequenceEqual(indices))
        {
            var first = Enumerable.Range(0, Math.Min(oi.Length, indices.Length) / 4).FirstOrDefault(i => BitConverter.ToUInt32(oi, i * 4) != BitConverter.ToUInt32(indices, i * 4), -1);
            output.WriteLine($"  first index difference at {first} of {oi.Length / 4} / {indices.Length / 4}");
        }
        var firstByte = Enumerable.Range(0, Math.Min(ours.Length, bytes.Length)).FirstOrDefault(i => ours[i] != bytes[i], -1);
        output.WriteLine($"  file {(firstByte < 0 && ours.Length == bytes.Length ? "identical" : $"differs at {firstByte}")}");
        Assert.Equal(bytes, ours);
    }

    /// <summary><c>QUERYBUILD=&lt;vrad3 folder&gt;</c>: lightmap_query_data.kv3 built from a packing geometry alone (no reference), its counts and time.</summary>
    [Fact]
    public void QueryDataBuild()
    {
        if (Environment.GetEnvironmentVariable("QUERYBUILD") is not { Length: > 0 } folder)
            return;
        var g = PackingGeometry.Read(File.ReadAllBytes(Path.Combine(folder, "lightmap_packing_geometry.dat")));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var bytes = LightmapQueryData.Build(g, 4096, 4096, []);
        var root = SceneInfoTests.ReadBinary(bytes);
        output.WriteLine($"packing {g.VertexCount} vertices, {g.Indices.Length} indices -> {bytes.Length} bytes, vertex_count {root["vertex_count"]}, index_count {root["index_count"]} in {watch.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// <c>DESCCRC=1</c>: the texture descriptor's KV3 text CRC32 under a few variants, against RED2's override
    /// fingerprints on probe01 (DXT1 1275215511, ATI1N 3323439452, BC6H 2895899433).
    /// </summary>
    [Fact]
    public void DescriptorCrc()
    {
        if (Environment.GetEnvironmentVariable("DESCCRC") is not { Length: > 0 })
            return;
        var targets = new Dictionary<string, uint> { ["DXT1"] = 1275215511, ["ATI1N"] = 3323439452, ["BC6H"] = 2895899433 };
        var guid = new Guid("7412167c-06e9-4698-aff2-e63eb59037e7");
        foreach (var (format, target) in targets)
            foreach (var minBits in new[] { -1, 0 })
                foreach (var slices in new[] { -1, 0 })
                    foreach (var minDim in new[] { 4096, 0 })
                    {
                        var text = Kv3TextWriter.Write(LightmapTextures.Descriptor("2DARRAY", "lightmapTextureArg", format, minDim, 0, "None", slices, slices, slices, minBits), guid);
                        foreach (var nul in new[] { false, true })
                            foreach (var lf in new[] { false, true })
                            {
                                var t = lf ? text.Replace("\r\n", "\n") : text;
                                var bytes = System.Text.Encoding.UTF8.GetBytes(t + (nul ? "\0" : ""));
                                var crc = System.IO.Hashing.Crc32.HashToUInt32(bytes);
                                if (crc == target)
                                    output.WriteLine($"{format}: MATCH minBits {minBits} slices {slices} minDim {minDim} nul {nul} lf {lf}");
                            }
                    }
        // The probe volume atlases: 36 x 44, 96 slices (ambient cube, BC6H) and 16 (dlshd, BC7).
        foreach (var (format, count, target) in new[] { ("BC6H", 96, 1648992586u), ("BC7", 16, 3808101860u) })
            foreach (var processor in new[] { "None", "none" })
                foreach (var minDim in new[] { 0, 4096, 36, 44 })
                    foreach (var nul in new[] { false, true })
                    {
                        var text = Kv3TextWriter.Write(LightmapTextures.Descriptor("3D", "lightProbeVolumeTextureArg", format, minDim, 0, "None", count, 36, 44, -1, processor), guid).Replace("\r\n", "\n");
                        var crc = System.IO.Hashing.Crc32.HashToUInt32(System.Text.Encoding.UTF8.GetBytes(text + (nul ? "\0" : "")));
                        if (crc == target)
                            output.WriteLine($"{format}: MATCH processor {processor} minDim {minDim} nul {nul}");
                    }
    }

    /// <summary><c>LMTEX=...</c>: the ImageArg fingerprint of each lightmap EXR against the shipped RED2.</summary>
    [Fact]
    public void ImageFingerprints()
    {
        if (Environment.GetEnvironmentVariable("LMTEX") is not { Length: > 0 } spec || spec.Split('|') is not [var folder, var vpk, var map])
            return;
        using var pkg = new ValvePak.Package();
        pkg.Read(vpk);
        foreach (var (exrName, channel) in new[] { ("irradiance.exr", "irradiance"), ("directional_irradiance.exr", "directional_irradiance"), ("lightmap_chart_color.exr", "debug_chart_color"), ("direct_light_shadows.exr", "direct_light_shadows"), ("lpv_atlas_dlshd.exr", "env_light_probe_volume_atlas_dlshd") })
        {
            pkg.ReadEntry(pkg.FindEntry($"maps/{map}/lightmaps/{channel}.vtex_c")!, out var shipped);
            using var res = new ValveResourceFormat.Resource();
            res.Read(new MemoryStream(shipped));
            var want = (uint)res.EditInfo!.ArgumentDependencies.First(a => a.ParameterType == "ImageArg").Fingerprint;
            var exr = Vrad3Exr.Read(File.ReadAllBytes(Path.Combine(folder, exrName)));
            var image = LightmapTextures.Bitmap(exr);
            var ours = LightmapTextures.ImageFingerprint(image, 1);
            output.WriteLine($"{channel}: ours {ours} shipped {want} {(ours == want ? "MATCH" : "")} [{string.Join(",", exr.Channels)}]");
        }
    }

    /// <summary>
    /// <c>LMTEX=&lt;vrad3 folder&gt;|&lt;map vpk&gt;|&lt;map&gt;</c>: every lightmaps/ file <see cref="LightmapOutputs.FromVrad3"/>
    /// writes from vrad3's folder against the package's, byte for byte, and any the package has that it does not write.
    /// </summary>
    [Fact]
    public void LightmapOutputsFiles()
    {
        if (Environment.GetEnvironmentVariable("LMTEX") is not { Length: > 0 } spec || spec.Split('|') is not [var folder, var vpk, var map] || CS2Fixtures.StockPak() is not { } pak)
            return;
        var bin = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, "..", "bin", "win64"));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vpk))!);
        using var pkg = new ValvePak.Package();
        pkg.Read(vpk);
        // LMTEX_ARGS: the compile's lighting arguments, "name=value;..." (a value with a dot is a float).
        var arguments = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in (Environment.GetEnvironmentVariable("LMTEX_ARGS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (pair.Split('=') is [var k, var v])
                arguments[k] = v.Contains('.') ? float.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : int.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
        var outputs = LightmapOutputs.FromVrad3(folder, map, addon, bin, [], LightmapOutputs.Options.From(arguments), note: output.WriteLine);
        var different = new List<string>();
        foreach (var o in outputs)
        {
            var e = pkg.FindEntry(o.Path);
            byte[]? shipped = null;
            if (e != null)
                pkg.ReadEntry(e, out shipped);
            var same = shipped != null && shipped.AsSpan().SequenceEqual(o.Bytes);
            output.WriteLine($"{o.Path}: {o.Bytes.Length} bytes, {(shipped == null ? "not in the package" : same ? "identical" : $"differs (package {shipped.Length})")}");
            if (!same)
            {
                different.Add(o.Path);
                if (shipped != null && o.Path.EndsWith(".vtex_c", StringComparison.Ordinal))
                {
                    foreach (var (who, bytes) in new[] { ("valve", shipped), ("ours", o.Bytes) })
                        output.WriteLine($"  {who}: {DescribeVtex(bytes)}");
                    var firstByte = Enumerable.Range(0, Math.Min(shipped.Length, o.Bytes.Length)).FirstOrDefault(i => shipped[i] != o.Bytes[i], -1);
                    output.WriteLine($"  first differing byte {firstByte}" + (firstByte >= 0
                        ? $": valve {Convert.ToHexString(shipped.AsSpan(Math.Max(0, firstByte - 8), Math.Min(24, shipped.Length - Math.Max(0, firstByte - 8))))} ours {Convert.ToHexString(o.Bytes.AsSpan(Math.Max(0, firstByte - 8), Math.Min(24, o.Bytes.Length - Math.Max(0, firstByte - 8))))}" : ""));
                    foreach (var line in Red2Diff(shipped, o.Bytes))
                        output.WriteLine($"  red2 {line}");
                    for (var m = 0; m < MipCount(shipped); m++)
                        output.WriteLine($"  mip {m}: {CompareMip0(shipped, o.Bytes, m)}");
                }
            }
        }
        var prefix = $"maps/{map}/lightmaps/";
        foreach (var path in pkg.Entries!.Values.SelectMany(x => x).Select(x => x.GetFullPath()).Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            if (!outputs.Any(o => o.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                output.WriteLine($"{path}: in the package, not written");
                different.Add(path);
            }
        Assert.Empty(different);
    }

    // The RED2 blocks' KV3 trees, value types included, where they differ, and the raw block sizes.
    private static IEnumerable<string> Red2Diff(byte[] a, byte[] b)
    {
        using var ra = new ValveResourceFormat.Resource();
        ra.Read(new MemoryStream(a));
        using var rb = new ValveResourceFormat.Resource();
        rb.Read(new MemoryStream(b));
        var ba = (ValveResourceFormat.Blocks.ResourceEditInfo2)ra.Blocks.First(x => x.Type == ValveResourceFormat.BlockType.RED2);
        var bb = (ValveResourceFormat.Blocks.ResourceEditInfo2)rb.Blocks.First(x => x.Type == ValveResourceFormat.BlockType.RED2);
        yield return $"sizes {ba.Size} vs {bb.Size}";
        foreach (var line in KvTreeDiff.Diff(ba.Data!.Root, bb.Data!.Root))
            yield return line;
    }

    private static int MipCount(byte[] vtex)
    {
        using var r = new ValveResourceFormat.Resource();
        r.Read(new MemoryStream(vtex));
        return ((ValveResourceFormat.ResourceTypes.Texture)r.DataBlock!).NumMipLevels;
    }

    private static string CompareMip0(byte[] a, byte[] b, int mip = 0)
    {
        using var ra = new ValveResourceFormat.Resource();
        ra.Read(new MemoryStream(a));
        using var rb = new ValveResourceFormat.Resource();
        rb.Read(new MemoryStream(b));
        var ta = (ValveResourceFormat.ResourceTypes.Texture)ra.DataBlock!;
        var tb = (ValveResourceFormat.ResourceTypes.Texture)rb.DataBlock!;
        if (ta.Width != tb.Width || ta.Height != tb.Height || ta.Depth != tb.Depth || ta.Format != tb.Format)
            return "not comparable";
        if (mip >= ta.NumMipLevels || mip >= tb.NumMipLevels)
            return "missing";
        var ma = new byte[ta.CalculateBufferSizeForMipLevel((uint)mip)];
        ta.ReadTextureMipLevel(ma, (uint)mip);
        var mb = new byte[tb.CalculateBufferSizeForMipLevel((uint)mip)];
        tb.ReadTextureMipLevel(mb, (uint)mip);
        var block = ta.Format is ValveResourceFormat.VTexFormat.DXT1 or ValveResourceFormat.VTexFormat.ATI1N ? 8 : 16;
        var bw = (Math.Max(1, ta.Width >> mip) + 3) / 4;
        int differ = 0, first = -1;
        for (var k = 0; k < ma.Length / block; k++)
            if (!ma.AsSpan(k * block, block).SequenceEqual(mb.AsSpan(k * block, block)))
            {
                differ++;
                if (first < 0)
                    first = k;
            }
        return differ == 0 ? "identical blocks" : $"{differ} of {ma.Length / block} blocks differ, first {first} (x {first % bw * 4}, y {first / bw * 4})"
            + $" valve {Convert.ToHexString(ma.AsSpan(first * block, block))} ours {Convert.ToHexString(mb.AsSpan(first * block, block))}";
    }

    // A .vtex_c's header and RED2 arguments, for the option builds' differences.
    private static string DescribeVtex(byte[] bytes)
    {
        using var res = new ValveResourceFormat.Resource();
        res.Read(new MemoryStream(bytes));
        var t = (ValveResourceFormat.ResourceTypes.Texture)res.DataBlock!;
        var args = string.Join(" ", res.EditInfo!.ArgumentDependencies.Select(a => $"{a.ParameterName}:{a.ParameterType}={a.Fingerprint:x}/{a.FingerprintDefault:x}"));
        return $"{t.Width}x{t.Height}x{t.Depth} {t.Format} mips {t.NumMipLevels} flags {t.Flags} | {args}";
    }

    [Fact]
    public void TextureApi()
    {
        if (Environment.GetEnvironmentVariable("CPUBAKE_TEXAPI") is not { } name || CS2Fixtures.StockPak() is not { } pak)
            return;
        foreach (var m in typeof(ValveResourceFormat.ResourceTypes.Texture).GetMethods().Where(m => m.DeclaringType == typeof(ValveResourceFormat.ResourceTypes.Texture)))
            output.WriteLine($"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
        foreach (var p in typeof(ValveResourceFormat.ResourceTypes.Texture).GetProperties())
            output.WriteLine($"prop {p.PropertyType.Name} {p.Name}");
        using var content = new GameContent(pak);
        var bytes = content.Read(name + "_c");
        using var res = new ValveResourceFormat.Resource();
        res.Read(new MemoryStream(bytes!));
        var t = (ValveResourceFormat.ResourceTypes.Texture)res.DataBlock!;
        output.WriteLine($"{t.Width}x{t.Height} depth {t.Depth} format {t.Format} flags {t.Flags} mips {t.NumMipLevels}");
    }
}
