using System.Numerics;
using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The model pose resourcecompiler poses aggregate bodies from, against a
/// capture of s2c_ragdollprobe's settle with tools/settle/probes/settle_pose.js
/// and settle_posewriter.js (GROUND_TRUTH 166). RAGDOLL_POSE is that capture's
/// events.jsonl: "posewrite" is a CAnimationHelper call that changed its pose
/// (slot 0, the skeleton set; slot 0x88, the update), "graphpose" rc's
/// 1817dd320 with the locals it composed and the model-space pose it wrote.
/// </summary>
public sealed class ModelPoseCaptureTests(ITestOutputHelper output)
{
    private static string AddonGame => @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo_addons\s2c_rc_probe";

    // The probe's models with an animation helper, by bone count.
    private static readonly Dictionary<int, string> Models = new()
    {
        [94] = "agents/models/ctm_sas/ctm_sas.vmdl",
        [117] = "models/chicken/chicken.vmdl",
    };

    private static CTransform[] Pose(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        var pose = new CTransform[bytes.Length / 0x20];
        for (var i = 0; i < pose.Length; i++)
            pose[i] = Transform(bytes.AsSpan(i * 0x20, 0x20));
        return pose;
    }

    private static CTransform Transform(ReadOnlySpan<byte> b)
    {
        var f = new float[8];
        for (var k = 0; k < 8; k++)
            f[k] = BitConverter.ToSingle(b[(k * 4)..]);
        return new CTransform(new Vector3(f[0], f[1], f[2]), f[3], new Quaternion(f[4], f[5], f[6], f[7]));
    }

    private static bool Same(CTransform a, CTransform b)
    {
        static uint U(float f) => BitConverter.SingleToUInt32Bits(f);
        return U(a.Position.X) == U(b.Position.X) && U(a.Position.Y) == U(b.Position.Y) && U(a.Position.Z) == U(b.Position.Z)
               && U(a.Scale) == U(b.Scale) && U(a.Rotation.X) == U(b.Rotation.X) && U(a.Rotation.Y) == U(b.Rotation.Y)
               && U(a.Rotation.Z) == U(b.Rotation.Z) && U(a.Rotation.W) == U(b.Rotation.W);
    }

    [Fact]
    public void PosesEqualTheCapture()
    {
        if (Environment.GetEnvironmentVariable("RAGDOLL_POSE") is not { } path || !File.Exists(path)
            || CS2Fixtures.StockPak() is not { } pak)
            return;
        using var models = new SettleBuildTests.PakModels(pak, AddonGame);
        var misses = new List<string>();
        int composed = 0, resets = 0;
        foreach (var line in File.ReadLines(path))
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            var ev = e.GetProperty("ev").GetString();
            if (ev == "graphpose")
            {
                // rc 1817dd320: the captured locals composed from the captured root.
                var locals = Pose(e.GetProperty("locals").GetString()!);
                var parents = e.GetProperty("bones").EnumerateArray().Select(b => b.GetProperty("parent").GetInt32()).ToArray();
                var root = Transform(Convert.FromHexString(e.GetProperty("root").GetString()!));
                var theirs = Pose(e.GetProperty("out").GetString()!);
                var ours = SettleWorld.ModelSpace(locals, parents, root);
                for (var i = 0; i < theirs.Length; i++)
                    if (!Same(theirs[i], ours[i]))
                        misses.Add($"{locals.Length} bones, compose of bone {i}: {theirs[i]} ours {ours[i]}");
                composed++;
            }
            else if (ev == "posewrite" && e.GetProperty("slot").GetInt32() == 0)
            {
                // The skeleton set (18006d560 -> 1804abdb0 -> 1804acdd0): the reference pose.
                var after = e.GetProperty("after");
                var theirs = Pose(after.GetProperty("hex").GetString()!);
                var bones = models.ModelSkeleton(Models[theirs.Length]);
                Assert.NotNull(bones);
                var ours = SettleWorld.ReferencePose(bones!);
                Assert.Equal(theirs.Length, ours.Length);
                for (var i = 0; i < theirs.Length; i++)
                    if (!Same(theirs[i], ours[i]))
                        misses.Add($"{Models[theirs.Length]}, reference pose bone {i}: {theirs[i]} ours {ours[i]}");
                resets++;
            }
        }
        output.WriteLine($"{composed} compositions, {resets} skeleton sets compared");
        foreach (var m in misses.Take(20))
            output.WriteLine(m);
        Assert.True(composed > 0 && resets > 0, "the capture has no graphpose or slot-0 posewrite events");
        Assert.Empty(misses);
    }
}
