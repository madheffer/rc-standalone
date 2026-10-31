using System.Numerics;
using System.Text.Json.Nodes;
using Source2.Compiler.Cubemaps;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="BarnLightBounds"/> against the boxes 1800ce950 stores, captured in cubehost
/// (<c>CUBELIGHTBOX=&lt;frida.jsonl of tools/cubehost/light_radius.js&gt;</c>, <c>CUBELIGHTBOX_VPK</c> the map's
/// package): each captured object is found by its transform (a light_barn's AngleMatrix), and its stored min and max
/// (after the snap) must be ours bit for bit. The captures' objects had +0x251 clear when their bounds were made,
/// so the illumination clip is not exercised here.
/// </summary>
public sealed class BarnLightBoundsTests(ITestOutputHelper output)
{
    [Fact]
    public void BoundsEqualTheCapture()
    {
        if (Environment.GetEnvironmentVariable("CUBELIGHTBOX") is not { Length: > 0 } path
            || Environment.GetEnvironmentVariable("CUBELIGHTBOX_VPK") is not { Length: > 0 } vpk)
            return;
        var lights = new List<(LightPrecompute.KeyReader Key, float[] Transform)>();
        using (var package = new ValvePak.Package())
        {
            package.Read(vpk);
            foreach (var entry in (package.Entries!.GetValueOrDefault("vents_c") ?? []).OrderBy(e => e.GetFullPath(), StringComparer.Ordinal))
            {
                package.ReadEntry(entry, out var bytes);
                foreach (var (_, key) in Lighting.Vrad3Job.LumpEntities(bytes, entry.GetFullPath()).Where(e => e.Class == "light_barn"))
                    lights.Add((key, BarnLightDescriptions.Transform(key)));
            }
        }
        var boxes = File.ReadLines(path).Select(l => JsonNode.Parse(l)!).Where(n => (string?)n["kind"] == "box" && n["transform"] is not null).ToList();
        var checkedLights = new HashSet<int>();
        var failures = new List<string>();
        foreach (var b in boxes)
        {
            var xf = b["transform"]!.AsArray().Select(v => (float)v!).ToArray();
            var light = lights.FindIndex(l => l.Transform.SequenceEqual(xf));
            if (light < 0 || !checkedLights.Add(light))
                continue;
            Assert.Equal(0, (int)b["illumination"]!);
            var box = b["box"]!.AsArray().Select(v => (float)v!).ToArray();
            var (min, max) = BarnLightBounds.Frustum(BarnLightDescriptions.Description(lights[light].Key), lights[light].Transform);
            var (sMin, sMax) = BarnLightBounds.Snap(min, max);
            float[] ours = [sMin.X, sMin.Y, sMin.Z, sMax.X, sMax.Y, sMax.Z];
            float[] theirs = [box[0], box[1], box[2], box[4], box[5], box[6]];
            if (!ours.Select(BitConverter.SingleToInt32Bits).SequenceEqual(theirs.Select(BitConverter.SingleToInt32Bits)))
            {
                failures.Add($"light {light}: ours ({string.Join(", ", ours)}) from ({min}, {max}), game ({string.Join(", ", theirs)})");
                output.WriteLine(failures[^1]);
            }
        }
        output.WriteLine($"{checkedLights.Count} light_barn boxes checked, {failures.Count} differ");
        Assert.NotEmpty(checkedLights);
        Assert.Empty(failures);
    }
}

/// <summary>
/// Diagnosis for the entry's screen size (+0x1bc): for each captured (entry, record) pair
/// (<c>CUBEBARN=&lt;frida.jsonl of tools/cubehost/barn_record_io.js&gt;</c>, <c>CUBEBARN_VPK</c>), the light's snapped
/// bounds (<see cref="BarnLightBounds"/>) sized from the pair's camera (binner +0xe540) with 18004c390
/// (<see cref="AggregateCulling.ScreenSize"/>) over the box's centre and half diagonal, both summation orders,
/// against the entry's value. Prints how many are bit for bit.
/// </summary>
public sealed class BarnLightSizeProbe(ITestOutputHelper output)
{
    [Fact]
    public void SizesAgainstTheCapture()
    {
        if (Environment.GetEnvironmentVariable("CUBEBARN") is not { Length: > 0 } path
            || Environment.GetEnvironmentVariable("CUBEBARN_VPK") is not { Length: > 0 } vpk)
            return;
        var lights = new List<(LightPrecompute.KeyReader Key, float[] Transform)>();
        using (var package = new ValvePak.Package())
        {
            package.Read(vpk);
            foreach (var entry in (package.Entries!.GetValueOrDefault("vents_c") ?? []).OrderBy(e => e.GetFullPath(), StringComparer.Ordinal))
            {
                package.ReadEntry(entry, out var bytes);
                foreach (var (_, key) in Lighting.Vrad3Job.LumpEntities(bytes, entry.GetFullPath()).Where(e => e.Class == "light_barn"))
                    lights.Add((key, BarnLightDescriptions.Transform(key)));
            }
        }
        int total = 0, xyFirst = 0, zFirst = 0, fades = 0, shadows = 0;
        foreach (var pair in File.ReadLines(path).Select(l => JsonNode.Parse(l)!).Where(n => (string?)n["kind"] == "pair"))
        {
            var entry = Convert.FromHexString((string)pair["entry"]!);
            var xf = Enumerable.Range(0, 12).Select(i => BitConverter.ToSingle(entry, 0x144 + 4 * i)).ToArray();
            var light = lights.FindIndex(l => l.Transform.SequenceEqual(xf));
            if (light < 0)
                continue;
            var e540 = Convert.FromHexString((string)pair["e540"]!);
            var camera = new Vector3(BitConverter.ToSingle(e540, 0), BitConverter.ToSingle(e540, 4), BitConverter.ToSingle(e540, 8));
            var (min, max) = BarnLightBounds.Frustum(BarnLightDescriptions.Description(lights[light].Key), xf);
            (min, max) = BarnLightBounds.Snap(min, max);
            var centre = new Vector3((max.X + min.X) * 0.5f, (max.Y + min.Y) * 0.5f, (max.Z + min.Z) * 0.5f);
            var (dx, dy, dz) = (min.X - max.X, min.Y - max.Y, min.Z - max.Z);
            var a = AggregateCulling.ScreenSize(camera, 1f, centre, MathF.Sqrt((dx * dx + dy * dy) + dz * dz) * 0.5f);
            var b = AggregateCulling.ScreenSize(camera, 1f, centre, MathF.Sqrt((dz * dz + dy * dy) + dx * dx) * 0.5f);
            var game = BitConverter.ToSingle(entry, 0x1bc);
            // The gather's fades (18019fe30, the convar at 180923bf8 off): 1 - clamp((start - d) k, 0, 1), k = 1 / max(0.0001,
            // start - end), from fade_size_* (object +0x260 / +0x264) and shadowfade_size_* (+0x268 / +0x26c).
            static float F(string? t) => float.Parse(t ?? "0", System.Globalization.CultureInfo.InvariantCulture);
            var key = lights[light].Key;
            float Fade(float start, float end) => 1f - Math.Clamp((start - game) * (1f / MathF.Max(0.0001f, start - end)), 0f, 1f);
            var fade = Fade(F(key("fade_size_start")), F(key("fade_size_end")));
            var shadow = Fade(F(key("shadowfade_size_start")), F(key("shadowfade_size_end")));
            if (BitConverter.SingleToInt32Bits(fade) == BitConverter.SingleToInt32Bits(BitConverter.ToSingle(entry, 0x1b8)))
                fades++;
            if (BitConverter.SingleToInt32Bits(shadow) == BitConverter.SingleToInt32Bits(BitConverter.ToSingle(entry, 0x1e0))
                && BitConverter.SingleToInt32Bits(shadow) == BitConverter.SingleToInt32Bits(BitConverter.ToSingle(entry, 0x1e4)))
                shadows++;
            total++;
            if (BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(game))
                xyFirst++;
            if (BitConverter.SingleToInt32Bits(b) == BitConverter.SingleToInt32Bits(game))
                zFirst++;
            else if (zFirst + 5 > total && total < 10)
                output.WriteLine($"light {light} camera {camera}: ours {a:R} / {b:R}, game {game:R}");
        }
        output.WriteLine($"{total} entries: {xyFirst} equal with (x x + y y) + z z, {zFirst} with (z z + y y) + x x; fades {fades}, shadow fades {shadows}");
    }
}

/// <summary>
/// Diagnosis for which barn lights a cube face gathers (<c>CUBELIGHTFACES_VPK</c>, cube <c>CUBELIGHTFACES_CUBE</c>):
/// per face, the light_barns whose snapped bounds pass the face frustum (<see cref="AggregateCulling.Outside"/>) and
/// whose distance fade is above 0, in the order 1801a9e70 sorts them (size descending, then squared distance), as
/// light origins x, to set beside the capture's per-face lists.
/// </summary>
public sealed class BarnLightFacesProbe(ITestOutputHelper output)
{
    [Fact]
    public void LightsPerFace()
    {
        if (Environment.GetEnvironmentVariable("CUBELIGHTFACES_VPK") is not { Length: > 0 } vpk)
            return;
        var cube = int.Parse(Environment.GetEnvironmentVariable("CUBELIGHTFACES_CUBE") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game");
        var lights = new List<(LightPrecompute.KeyReader Key, float[] Transform)>();
        List<CubemapFaceBlocks.Cube> cubes;
        using (var package = new ValvePak.Package())
        {
            package.Read(vpk);
            foreach (var entry in (package.Entries!.GetValueOrDefault("vents_c") ?? []).OrderBy(e => e.GetFullPath(), StringComparer.Ordinal))
            {
                package.ReadEntry(entry, out var bytes);
                foreach (var (_, key) in Lighting.Vrad3Job.LumpEntities(bytes, entry.GetFullPath()).Where(e => e.Class == "light_barn"))
                    lights.Add((key, BarnLightDescriptions.Transform(key)));
            }
            cubes = CubemapFaceBlocks.FromPackage(package, File.ReadAllText(Path.Combine(game, "csgo", "gameinfo.gi")), true, out _);
        }
        static float F(string? t) => float.Parse(t ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        for (var f = 0; f < 6; f++)
        {
            var view = CubemapFaceViews.Face(cubes[cube].Scene.Origin, Vector3.Zero, 1f, 10000f, f);
            var frustum = AggregateCulling.FaceFrustum(view);
            var camera = view.Origin;
            var kept = new List<(float D, float Dist, float X)>();
            foreach (var (key, xf) in lights)
            {
                var (min, max) = BarnLightBounds.Snap(BarnLightBounds.Frustum(BarnLightDescriptions.Description(key), xf).Min, BarnLightBounds.Frustum(BarnLightDescriptions.Description(key), xf).Max);
                if (AggregateCulling.Outside(frustum, min, max))
                    continue;
                var centre = new Vector3((max.X + min.X) * 0.5f, (max.Y + min.Y) * 0.5f, (max.Z + min.Z) * 0.5f);
                var (dx, dy, dz) = (min.X - max.X, min.Y - max.Y, min.Z - max.Z);
                var d = AggregateCulling.ScreenSize(camera, 1f, centre, MathF.Sqrt((dx * dx + dy * dy) + dz * dz) * 0.5f);
                var (start, end) = (F(key("fade_size_start")), F(key("fade_size_end")));
                var fade = 1f - Math.Clamp((start - d) * (1f / MathF.Max(0.0001f, start - end)), 0f, 1f);
                if (fade <= 0f)
                    continue;
                // 1801aa3f0: the frustum's footprint in the light's frame, then the illumination box's (precomputedobb*).
                var bin = new LightBinnerItems.View(view.Origin, new Vector3(view.CameraToWorld[0], view.CameraToWorld[4], view.CameraToWorld[8]),
                    AggregateCulling.WorldToProjection(view), CubemapFaceDrawList.FaceSize, CubemapFaceDrawList.FaceSize, CubemapFaceBlocks.BinnerTilePixels);
                var (planes, corners) = BarnLightBounds.FrustumPlanes(BarnLightDescriptions.Description(key));
                if (LightBinnerItems.Bounds(bin, planes, corners, xf) == null)
                    continue;
                static Vector3 V(string? t) => t!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray() is var a ? new(a[0], a[1], a[2]) : default;
                if (key("precomputedfieldsvalid") == "1")
                {
                    var half = V(key("precomputedobbextent"));
                    var box = BarnLightBounds.QuaternionMatrix(CubemapFaceViews.AngleQuaternion(V(key("precomputedobbangles"))), V(key("precomputedobborigin")));
                    if (LightBinnerItems.Bounds(bin, -half, half, box) == null)
                        continue;
                }
                var (px, py, pz) = (xf[3] - camera.X, xf[7] - camera.Y, xf[11] - camera.Z);
                kept.Add((d, (py * py + px * px) + pz * pz, xf[3]));
            }
            var order = kept.OrderByDescending(k => k.D).ThenBy(k => k.Dist).ToList();
            output.WriteLine($"face {f}: {order.Count} lights: {string.Join(", ", order.Select(k => k.X))}");
        }
    }
}
