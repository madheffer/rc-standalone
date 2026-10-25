using System.Numerics;
using System.Text.Json.Nodes;
using Source2.Compiler.Cubemaps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="CubemapShadowCascades"/> against the game. <c>CUBEREPLAY_ALL=&lt;rdc_replay.py output for the whole
/// frame&gt;</c>: probe01 cube 0's sun cascades on all six faces (the per-view block of each cascade's
/// csm_depth_only draws, by atlas viewport: (0, 0), (1280, 0), (2560, 0) for the three cascades, (0, 2048) for
/// the viewmodel cascade), word for word against our fit. Inputs: the sun direction as the light block carries
/// it, the game's inputs (CubemapShadowCascades.Inputs, ledger 121): split to 560, cascade 0 at 75, near 1, max visible 7500,
/// pushback 7000, resolution 1280; the viewmodel cascade 0.5 .. 21 with max visible 1000.
/// </summary>
public sealed class CubemapCascadeTests(ITestOutputHelper output)
{
    [Fact]
    public void CascadesEqualTheCapture()
    {
        if (Environment.GetEnvironmentVariable("CUBEREPLAY_ALL") is not { Length: > 0 } dir)
            return;
        // Per face, per cascade slot, the first per-view block seen.
        var faces = new List<Dictionary<int, byte[]>>();
        foreach (var line in File.ReadLines(Path.Combine(dir, "actions.jsonl")))
        {
            var rec = JsonNode.Parse(line)!;
            if (rec["stages"]?["vs"] is not { } vs || !((string)vs["shader"]!).EndsWith(" 1258", StringComparison.Ordinal)
                || (string?)rec["depthOut"] != "2D Depth Attachment 64905")
                continue;
            var vp = rec["viewportScissor"]!["viewportScissors"]![0]!["vp"]!;
            var slot = ((float)vp["y"]! > 0 ? 3 : 0) + (int)((float)vp["x"]! / 1280f);
            // A face's shadow passes start at cascade 0 again after the others; repeats within a face are skipped.
            if (faces.Count == 0 || (slot == 0 && faces[^1].ContainsKey(3)))
                faces.Add([]);
            var cb = vs["constantBlocks"]!.AsArray().First(c => (int)c!["block"]!["fixedBindNumber"]! == 1)!;
            faces[^1].TryAdd(slot, File.ReadAllBytes(Path.Combine(dir, "blobs", (string)cb["bytes"]!["sha1"]! + ".bin")));
        }
        output.WriteLine($"{faces.Count} faces");
        // The direction the cascades use: minus the light view's forward row, as face 0's cascade 0 carries it
        // (g_matWorldToView column 2); not the light block's g_vFastPathSunLightDir, which differs in the last bits.
        var (sun, _, cameraDirection) = CubemapShadowCascades.SunDirections(new Vector3(74.99999f, 180.00002f, 0f));
        output.WriteLine($"sun {sun.X:R} {sun.Y:R} {sun.Z:R}");
        var distances = CubemapShadowCascades.GameDistances(1f, 10000f);
        output.WriteLine($"distances {string.Join(", ", distances.Select(d => d.ToString("R")))}");
        var failures = 0;
        // The +Z face (4) sees only sky here and renders no cascades: the fifth group is face 5.
        int[] faceOf = faces.Count == 5 ? [0, 1, 2, 3, 5] : [.. Enumerable.Range(0, faces.Count)];
        for (var g = 0; g < faces.Count; g++)
        {
            var f = faceOf[g];
            var view = CubemapFaceViews.Face(new Vector3(512, 96, 112), Vector3.Zero, 1f, 10000f, f);
            var ours = CubemapShadowCascades.GameCascades(view, sun, 1f, 10000f);
            foreach (var (slot, b) in faces[g].OrderBy(k => k.Key))
            {
                var c = ours[slot];
                var projection = CubemapFaceViews.Mul4([1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f], c.Projection);
                float[] rotation = [c.View[0], c.View[1], c.View[2], 0f, c.View[4], c.View[5], c.View[6], 0f, c.View[8], c.View[9], c.View[10], 0f, 0f, 0f, 0f, 1f];
                var relative = CubemapFaceViews.Mul4(projection, rotation);
                var diffs = new List<string>();
                void Check(string what, int offset, float value)
                {
                    if (BitConverter.SingleToUInt32Bits(value) != BitConverter.ToUInt32(b, offset))
                        diffs.Add($"{what}+{offset}: {value:G9} vs {BitConverter.ToSingle(b, offset):G9}");
                }
                for (var r = 0; r < 4; r++)
                    for (var k = 0; k < 4; k++)
                    {
                        Check("WorldToProjection", (r * 4 + k) * 4, relative[k * 4 + r]);
                        Check("WorldToView", 128 + (r * 4 + k) * 4, c.View[k * 4 + r]);
                        Check("ViewToProjection", 192 + (r * 4 + k) * 4, projection[r * 4 + k]);
                    }
                output.WriteLine($"face {f} cascade {slot}: {(diffs.Count == 0 ? "equal" : string.Join("; ", diffs.Take(6)) + $" ({diffs.Count})")}");
                failures += diffs.Count;
                // The whole block: the shadow draws read +0 .. +63 and +384 .. +395 (required); the rest reported.
                int[][] tiles = [[0, 0], [1280, 0], [2560, 0], [0, 2048]];
                var block = CubemapFaceConstants.CascadePerView(c, cameraDirection, tiles[slot][0], tiles[slot][1], CubemapShadowCascades.Inputs.Resolution, CubemapShadowCascades.Inputs.AtlasWidth, CubemapShadowCascades.Inputs.AtlasHeight);
                var words = Enumerable.Range(0, 160).Where(i => BitConverter.ToUInt32(block, i * 4) != BitConverter.ToUInt32(b, i * 4)).ToList();
                if (words.Count > 0)
                    output.WriteLine($"  block differs at {string.Join(", ", words.Select(i => $"+{i * 4} ({BitConverter.ToSingle(block, i * 4):G9} vs {BitConverter.ToSingle(b, i * 4):G9})"))}");
                failures += words.Count(i => i < 16 || (i >= 96 && i < 99));
            }
        }
        Assert.Equal(0, failures);
    }
}

/// <summary>Scratch: the sun direction variants from light_environment's angles.</summary>
public sealed class CubemapSunProbe(ITestOutputHelper output)
{
    [Fact]
    public void Variants()
    {
        if (Environment.GetEnvironmentVariable("CUBESUNPROBE") != "1")
            return;
        var angles = new Vector3(74.99999f, 180.00002f, 0f);
        var m = CubemapFaceViews.AngleMatrix(angles, Vector3.Zero);
        output.WriteLine($"AngleMatrix forward {m[0]:R} {m[4]:R} {m[8]:R}  left {m[1]:R} {m[5]:R} {m[9]:R}  up {m[2]:R} {m[6]:R} {m[10]:R}");
        var q = CubemapFaceViews.AngleQuaternion(angles);
        var qm = CubemapFaceViews.QuaternionMatrix(q);
        output.WriteLine($"QuaternionMatrix forward {qm[0]:R} {qm[4]:R} {qm[8]:R}  up {qm[2]:R} {qm[6]:R} {qm[10]:R}");
        var a2 = CubemapFaceViews.MatrixAngles(qm);
        var m2 = CubemapFaceViews.AngleMatrix(a2, Vector3.Zero);
        output.WriteLine($"round trip angles {a2.X:R} {a2.Y:R} {a2.Z:R}; forward {m2[0]:R} {m2[4]:R} {m2[8]:R} up {m2[2]:R} {m2[6]:R} {m2[10]:R}");
        float fx = -qm[0], fy = -qm[4], fz = -qm[8];
        var len2 = (fx * fx + fy * fy) + fz * fz;
        var len2b = (fz * fz + fy * fy) + fx * fx;
        foreach (var l2 in new[] { len2, len2b })
        {
            var len = MathF.Sqrt(l2);
            var inv = 1f / len;
            output.WriteLine($"normalised by 1/len: {fx * inv:G9} {fy * inv:G9} {fz * inv:G9}; by /len: {fx / len:G9} {fy / len:G9} {fz / len:G9}; rsqrt-ish {(fx * (1f / MathF.Sqrt(l2))):R}");
        }
        foreach (var qd in new[] { (q.X * q.X + q.Y * q.Y) + (q.Z * q.Z + q.W * q.W), ((q.X * q.X + q.Y * q.Y) + q.Z * q.Z) + q.W * q.W })
        {
            var qi = 1f / MathF.Sqrt(qd);
            var nq = new Vector4(q.X * qi, q.Y * qi, q.Z * qi, q.W * qi);
            var nm = CubemapFaceViews.QuaternionMatrix(nq);
            output.WriteLine($"normalised quaternion: forward {nm[0]:G9} {nm[4]:G9} {nm[8]:G9} up {nm[2]:G9} {nm[6]:G9} {nm[10]:G9}");
            var nq2 = new Vector4(q.X / MathF.Sqrt(qd), q.Y / MathF.Sqrt(qd), q.Z / MathF.Sqrt(qd), q.W / MathF.Sqrt(qd));
            var nm2 = CubemapFaceViews.QuaternionMatrix(nq2);
            output.WriteLine($"divided quaternion: forward {nm2[0]:G9} {nm2[4]:G9} {nm2[8]:G9} up {nm2[2]:G9} {nm2[6]:G9} {nm2[10]:G9}");
        }
        output.WriteLine($"quaternion forward {qm[0]:G9} {qm[4]:G9} {qm[8]:G9}; anglematrix forward {m[0]:G9} {m[4]:G9} {m[8]:G9} up {m[2]:G9} {m[6]:G9} {m[10]:G9}");
        {
            // v' = v + w t + q x t, t = 2 (q x v), v = (1, 0, 0)
            Vector3 Cross(Vector3 a, Vector3 c) => new(a.Y * c.Z - a.Z * c.Y, a.Z * c.X - a.X * c.Z, a.X * c.Y - a.Y * c.X);
            var qv = new Vector3(q.X, q.Y, q.Z);
            var v = new Vector3(1f, 0f, 0f);
            var t = Cross(qv, v); t = new Vector3(t.X + t.X, t.Y + t.Y, t.Z + t.Z);
            var c2 = Cross(qv, t);
            var r1 = new Vector3((v.X + q.W * t.X) + c2.X, (v.Y + q.W * t.Y) + c2.Y, (v.Z + q.W * t.Z) + c2.Z);
            var r2 = new Vector3(v.X + (q.W * t.X + c2.X), v.Y + (q.W * t.Y + c2.Y), v.Z + (q.W * t.Z + c2.Z));
            output.WriteLine($"rotate t-form: {r1.X:G9} {r1.Y:G9} {r1.Z:G9} | {r2.X:G9} {r2.Y:G9} {r2.Z:G9}");
            // v' = (w^2 - |q|^2) v + 2 (q.v) q + 2 w (q x v)
            var dot = q.X;
            var ww = q.W * q.W - (q.X * q.X + q.Y * q.Y + q.Z * q.Z);
            var cr = Cross(qv, v);
            var r3 = new Vector3(ww * 1f + 2f * dot * q.X + 2f * q.W * cr.X, 2f * dot * q.Y + 2f * q.W * cr.Y, 2f * dot * q.Z + 2f * q.W * cr.Z);
            output.WriteLine($"rotate expanded: {r3.X:G9} {r3.Y:G9} {r3.Z:G9}");
            // matrix column with double math
            double X = q.X, Y = q.Y, Z = q.Z, W = q.W;
            output.WriteLine($"double column: {(float)(1 - 2 * (Y * Y + Z * Z)):G9} {(float)(2 * (X * Y + Z * W)):G9} {(float)(2 * (X * Z - Y * W)):G9}");
            output.WriteLine($"q = {q.X:G9} {q.Y:G9} {q.Z:G9} {q.W:G9}");
        }
        var mag = MathF.Sqrt(fx * fx + fy * fy + fz * fz);
        output.WriteLine($"len {mag:R}");
        foreach (var d in new[] { 7832.5547f })
            output.WriteLine($"pos candidates: {(-m[0] * d):R} {(0.258819252f * d):R} {(0.25881922f * d):R}");
    }
}

/// <summary>Scratch: how the light block's g_mWorldToShadowCascade relates to the cascade matrices.</summary>
public sealed class CubemapLightCascadeProbe(ITestOutputHelper output)
{
    [Fact]
    public void Candidates()
    {
        if (Environment.GetEnvironmentVariable("CUBELIGHTBLOCK") is not { Length: > 0 } path)
            return;
        var block = File.ReadAllBytes(path);
        var view = CubemapFaceViews.Face(new Vector3(512, 96, 112), Vector3.Zero, 1f, 10000f, 0);
        var (sun, _, _) = CubemapShadowCascades.SunDirections(new Vector3(74.99999f, 180.00002f, 0f));
        var cascades = CubemapShadowCascades.GameCascades(view, sun, 1f, 10000f);
        float[] I = [1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f];
        float[] T(Vector3 o) => [1f, 0f, 0f, o.X, 0f, 1f, 0f, o.Y, 0f, 0f, 1f, o.Z, 0f, 0f, 0f, 1f];
        var o = new Vector3(512, 96, 112);
        for (var k = 0; k < 4; k++)
        {
            var c = cascades[k];
            var p = CubemapFaceViews.Mul4(I, c.Projection);
            var cands = new Dictionary<string, float[]>
            {
                ["(PV)T"] = CubemapFaceViews.Mul4(CubemapFaceViews.Mul4(p, c.View), T(o)),
                ["P(VT)"] = CubemapFaceViews.Mul4(p, CubemapFaceViews.Mul4(c.View, T(o))),
                ["(P0V)T"] = CubemapFaceViews.Mul4(CubemapFaceViews.Mul4(c.Projection, c.View), T(o)),
                ["P0(VT)"] = CubemapFaceViews.Mul4(c.Projection, CubemapFaceViews.Mul4(c.View, T(o))),
            };
            var off = 31296 + k * 64;
            foreach (var (name, m) in cands)
            {
                var diff = Enumerable.Range(0, 16).Count(i => BitConverter.SingleToUInt32Bits(m[(i % 4) * 4 + i / 4]) != BitConverter.ToUInt32(block, off + i * 4));
                output.WriteLine($"cascade {k} {name}: {16 - diff} of 16 words equal");
            }
        }
    }
}

/// <summary>
/// <see cref="CubemapFaceConstants.LightBlock"/> against probe01 face 0's captured light block
/// (<c>CUBELIGHTBLOCK=&lt;the 32256-byte blob&gt;</c>): every word that differs, by offset.
/// </summary>
public sealed class CubemapLightBlockTests(ITestOutputHelper output)
{
    [Fact]
    public void AgainstTheCapture()
    {
        if (Environment.GetEnvironmentVariable("CUBELIGHTBLOCK") is not { Length: > 0 } path)
            return;
        var theirs = File.ReadAllBytes(path);
        var view = CubemapFaceViews.Face(new Vector3(512, 96, 112), Vector3.Zero, 1f, 10000f, 0);
        var (toSun, lightDir, _) = CubemapShadowCascades.SunDirections(new Vector3(74.99999f, 180.00002f, 0f));
        var distances = CubemapShadowCascades.GameDistances(1f, 10000f);
        var cascades = CubemapShadowCascades.GameCascades(view, toSun, 1f, 10000f);
        var color = Lighting.SceneLights.SrgbToLinear(248, 249, 232) * 2.25f;
        var sky = Lighting.SceneLights.SrgbToLinear(138, 200, 255);
        var sun = new CubemapFaceConstants.Sun(lightDir, color, 0.1f, 0.05f, sky, 0.875f);
        var volume = new CubemapFaceConstants.LightProbeVolume(new Vector3(512, 96, 112), new Vector3(-768, -1088, -112), new Vector3(768, 896, 176), new Vector3(0, 32, 0));
        var stretched = CubemapShadowCascades.Slice(view, 1f, distances[^1]).Diameter * 1.42f;
        int[][] tiles = [[0, 0], [1280, 0], [2560, 0], [0, 2048]];
        var ours = CubemapFaceConstants.LightBlock(sun, [volume], cascades, tiles, CubemapShadowCascades.Inputs.Resolution, CubemapShadowCascades.Inputs.AtlasWidth, CubemapShadowCascades.Inputs.AtlasHeight, new Vector3(512, 96, 112), stretched);
        var diffs = Enumerable.Range(0, theirs.Length / 4).Where(i => BitConverter.ToUInt32(ours, i * 4) != BitConverter.ToUInt32(theirs, i * 4)).ToList();
        output.WriteLine($"{theirs.Length / 4 - diffs.Count} of {theirs.Length / 4} words equal");
        foreach (var i in diffs)
            output.WriteLine($"  +{i * 4}: ours {BitConverter.ToSingle(ours, i * 4):G9} ({BitConverter.ToUInt32(ours, i * 4):x8}), game {BitConverter.ToSingle(theirs, i * 4):G9} ({BitConverter.ToUInt32(theirs, i * 4):x8})");
    }
}
