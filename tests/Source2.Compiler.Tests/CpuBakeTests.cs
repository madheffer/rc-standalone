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
        using (var s = File.OpenRead(Path.Combine(folder, "scene_info.kv3")))
            sceneInfo = KVSerializer.Create(KVSerializationFormat.KeyValues3Text).Deserialize(s);
        var sceneMeshes = SceneInfoTests.ReadBinary(Path.Combine(folder, "scene_meshes.kv3"));
        var scene = BakeScene.Load(sceneInfo, sceneMeshes, Material, Cubemap);
        output.WriteLine($"{scene.Instances.Count} instances, {scene.Meshes.Count} meshes, {scene.Lights.Count} lights");
        if (Environment.GetEnvironmentVariable("CPUBAKE_INSTANCES") == "1")
            foreach (var inst in scene.Instances)
                output.WriteLine($"  mesh {inst.Mesh} {inst.Material.Name}: mask {inst.Mask:x2} material flags {inst.MaterialFlags:x} instance flags {inst.InstanceFlags:x} and {inst.Material.MaskAnd:x} or {inst.Material.MaskOr:x}, colour {(inst.Material.Color is { } c ? $"{c.Width}x{c.Height}" : "none")} tint {inst.Material.ColorTint}");
        var packing = PackingGeometry.Read(File.ReadAllBytes(Path.Combine(folder, "lightmap_packing_geometry.dat")));
        using var halton = LoadResource(Core("textures/dev/scrambled_halton.vdata_c")!);
        var permutations = HaltonSamples.Permutations(((BinaryKV3)halton.DataBlock!).Data.Root["permutations"].AsBlob());
        var baker = new LightmapBaker(scene, packing, permutations)
        {
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
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var image = baker.Compute(File.ReadAllBytes(Path.Combine(folder, "lightmap_info.dat")), output.WriteLine);
        output.WriteLine($"compute {watch.Elapsed.TotalSeconds:F1} s");
        if (baker.CropBlock is { } block)
        {
            // The block alone: AHD-encoded (no filtering) against the same texels of Valve's, where we traced.
            var traced = new bool[image.Width * image.Height];
            for (var i = 0; i < traced.Length; i++)
                traced[i] = image.SampleCount(i) > 0;
            var bx = baker.Width / baker.BlockSize;
            var (ox, oy) = (block % bx * baker.BlockSize, block / bx * baker.BlockSize);
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
            var am = xa / m;
            var bm = xb / m;
            var bc = (xab / m - am * bm) / Math.Sqrt(Math.Max(1e-30, (xaa / m - am * am) * (xbb / m - bm * bm)));
            ratios.Sort();
            string Q(double q) => ratios.Count == 0 ? "-" : ratios[(int)(q * (ratios.Count - 1))].ToString("F3");
            output.WriteLine($"     bins {m}: correlation {bc:F3}, ratio p10 {Q(0.1)} p50 {Q(0.5)} p90 {Q(0.9)}");
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
            using var rgba = new SkiaSharp.SKBitmap(w, h, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
            bmp.CopyTo(rgba, SkiaSharp.SKColorType.Rgba8888);
            return BakeTexture.FromRgba8(w, h, rgba.GetPixelSpan(), true);
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
