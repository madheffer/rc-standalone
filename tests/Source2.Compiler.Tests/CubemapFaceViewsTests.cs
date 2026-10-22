using System.Numerics;
using System.Text.Json.Nodes;
using Source2.Compiler.Cubemaps;
using Source2.Compiler.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="CubemapFaceViews"/> against the game. <c>CUBEREPLAY_ALL=&lt;rdc_replay.py output for the whole
/// frame&gt;</c>: each face's per-view block (generic's set 1 binding 1 on that face's colour draws) against our
/// view for probe01's cube 0 (origin 512 96 112, angles 0, near 1, far 10000), word for word. Without the
/// capture, <see cref="CrtMath.Atan2"/> runs against tier0's V_atan2f and the DPPS sum against its
/// documented order.
/// </summary>
public sealed class CubemapFaceViewsTests(ITestOutputHelper output)
{
    private static float W(byte[] b, int offset) => BitConverter.ToSingle(b, offset);

    /// <summary>
    /// <c>CUBEVIEWCASES=&lt;tools/re/emu_cube_views.py --cases N output&gt;</c>: random records (origins, angles
    /// including 0, +-90, 180 and arbitrary) through client.dll's own face setup under unicorn: the view's
    /// angles, camera-to-world and world-to-view, bit for bit. (The projection products are checked against
    /// the capture instead: unicorn's DPPS sums from +0.)
    /// </summary>
    [Fact]
    public void RecordsEqualTheEmulatedClient()
    {
        if (Environment.GetEnvironmentVariable("CUBEVIEWCASES") is not { Length: > 0 } path)
            return;
        int cases = 0, differ = 0;
        foreach (var line in File.ReadLines(path))
        {
            var c = JsonNode.Parse(line)!;
            Vector3 V(string key) => new(BitConverter.UInt32BitsToSingle((uint)c[key]![0]!), BitConverter.UInt32BitsToSingle((uint)c[key]![1]!), BitConverter.UInt32BitsToSingle((uint)c[key]![2]!));
            var v = CubemapFaceViews.Face(V("origin"), V("angles"), 1f, 10000f, (int)c["face"]!);
            var want = new[] { "viewAngles", "cameraToWorld", "worldToView" }.SelectMany(k => c[k]!.AsArray().Select(x => (uint)x!)).ToArray();
            var got = new[] { v.Angles.X, v.Angles.Y, v.Angles.Z }.Concat(v.CameraToWorld).Concat(v.WorldToView).Select(BitConverter.SingleToUInt32Bits).ToArray();
            cases++;
            if (!want.SequenceEqual(got) && differ++ < 5)
                output.WriteLine($"{line[..Math.Min(line.Length, 160)]}\n  ours {string.Join(" ", got.Select(x => x.ToString("x8")))}\n  emu  {string.Join(" ", want.Select(x => x.ToString("x8")))}");
        }
        output.WriteLine($"{cases - differ} of {cases} face views equal client.dll's");
        Assert.Equal(0, differ);
    }

    [Fact]
    public void FaceBlocksEqualTheCapture()
    {
        if (Environment.GetEnvironmentVariable("CUBEREPLAY_ALL") is not { Length: > 0 } dir)
            return;
        // The per-view block of each face, in face order: the sky draw's (vertex shader 26720, set 0 binding 1;
        // every face draws the sky, while +Z draws no world). It differs from the world's only in the depth range.
        var blocks = new List<byte[]>();
        foreach (var line in File.ReadLines(Path.Combine(dir, "actions.jsonl")))
        {
            var rec = JsonNode.Parse(line)!;
            if (rec["stages"]?["vs"] is not { } vs || !((string)vs["shader"]!).EndsWith(" 26720", StringComparison.Ordinal))
                continue;
            var cb = vs["constantBlocks"]!.AsArray().First(c => (int)c!["block"]!["fixedBindSetOrSpace"]! == 0 && (int)c["block"]!["fixedBindNumber"]! == 1)!;
            blocks.Add(File.ReadAllBytes(Path.Combine(dir, "blobs", (string)cb["bytes"]!["sha1"]! + ".bin")));
        }
        output.WriteLine($"{blocks.Count} face blocks");
        var failures = 0;
        for (var face = 0; face < blocks.Count; face++)
        {
            var v = CubemapFaceViews.Face(new Vector3(512, 96, 112), Vector3.Zero, 1f, 10000f, face);
            var b = blocks[face];
            var diffs = new List<string>();
            void Check(string what, int offset, float ours)
            {
                if (BitConverter.SingleToUInt32Bits(ours) != BitConverter.ToUInt32(b, offset))
                    diffs.Add($"{what} +{offset}: ours {ours:G9} ({BitConverter.SingleToUInt32Bits(ours):x8}), game {W(b, offset):G9} ({BitConverter.ToUInt32(b, offset):x8})");
            }
            for (var r = 0; r < 4; r++)
                for (var c = 0; c < 4; c++)
                {
                    Check("g_matWorldToProjection", (r * 4 + c) * 4, v.CameraRelative[c * 4 + r]);
                    Check("g_matViewToProjection", 192 + (r * 4 + c) * 4, v.Projection[r * 4 + c]);
                    Check("g_matWorldToView", 128 + (r * 4 + c) * 4, c < 3 ? v.WorldToView[c * 4 + r] : r == 3 ? 1f : 0f);
                }
            output.WriteLine($"face {face}: angles {v.Angles}, {(diffs.Count == 0 ? "equal" : string.Join("; ", diffs))}");
            failures += diffs.Count;

            // The whole block (the sky's depth range 1 .. 1): words that differ, by offset. The face depends on
            // +0 .. +63, +384 .. +395 and +416 .. +427 only (ledger 120); the rest is reported, not required.
            var ours = CubemapFaceConstants.PerView(v, 1024, 1f, 1f);
            var words = Enumerable.Range(0, 160).Where(i => BitConverter.ToUInt32(ours, i * 4) != BitConverter.ToUInt32(b, i * 4)).ToList();
            output.WriteLine($"  block: {160 - words.Count} of 160 words equal; differ at {string.Join(", ", words.Select(i => $"+{i * 4} ({BitConverter.ToSingle(ours, i * 4):G9} vs {BitConverter.ToSingle(b, i * 4):G9})"))}");
            failures += words.Count(i => i < 16 || (i >= 96 && i < 99) || (i >= 104 && i < 107));
        }
        Assert.Equal(0, failures);
    }
}
