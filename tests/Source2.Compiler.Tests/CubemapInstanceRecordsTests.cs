using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Source2.Compiler.Cubemaps;
using Source2.Compiler.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="CubemapFaceRenderer.InstanceBuffers"/> against a capture's face 0 (<c>CUBEINST=&lt;capture dir&gt;</c>):
/// for each view (the face, each sun cascade by its atlas tile) and world aggregate (by its model's vertex buffer),
/// the records the captured draws read (each instance's tint and the 3 x 4 its transform index names, bit for bit)
/// must be the records our draw list's commands read. The game hands out each block in thread order and keeps its
/// transforms where its allocator puts them, so records compare as a multiset per view and aggregate, each instance
/// once. <c>CUBEREPLAY_SCRIPTEDCAMERA=0</c> for a build with no scripted camera.
/// </summary>
public sealed class CubemapInstanceRecordsTests(ITestOutputHelper output)
{
    [Fact]
    public void RecordsEqualTheCapture()
    {
        if (Environment.GetEnvironmentVariable("CUBEINST") is not { Length: > 0 } dir)
            return;
        var vpk = Directory.GetFiles(dir, "*.vpk").Single();
        var aggregates = AggregateCullingCaptureTests.Aggregates(vpk);
        var memberships = CubemapLayerFlagsCaptureTests.Memberships(vpk);
        var models = CubemapFaceRenderer.AggregateModels(vpk);
        var byVertexSha = new Dictionary<string, int>();
        for (var a = 0; a < models.Count; a++)
            byVertexSha.TryAdd(Sha(models[a].Vertices), a);
        var (origin, sunAngles) = AggregateCullingCaptureTests.CubeZero(vpk);
        var (toSun, _, _) = CubemapShadowCascades.SunDirections(sunAngles);
        var scripted = Environment.GetEnvironmentVariable("CUBEREPLAY_SCRIPTEDCAMERA") != "0";
        var view = CubemapFaceViews.Face(origin, Vector3.Zero, 1f, 10000f, 0);
        var listAggregates = aggregates.Select(a => new CubemapFaceDrawList.Aggregate(a.Fragments, a.OwnTransforms, a.Lods, memberships[a.Name.Split(' ', 2)[1]])).ToList();
        var (draws, instances) = CubemapFaceDrawList.Face(view, toSun, listAggregates, scripted);
        var (transforms, records) = CubemapFaceRenderer.InstanceBuffers(draws, instances, [.. aggregates.Select(a => (IReadOnlyList<AggregateCulling.Fragment>)a.Fragments)]);
        string Record(byte[] inst, byte[] xf, int id)
        {
            var tint = BitConverter.ToUInt32(inst, id * 32);
            var t = BitConverter.ToInt32(inst, id * 32 + 4);
            return $"{tint:x8} {Convert.ToHexString(xf, t * 48, 48)}";
        }
        var ours = new Dictionary<(int View, int Aggregate), Dictionary<int, string>>();
        foreach (var d in draws)
            foreach (var c in d.Commands)
                for (var i = c.FirstInstance; i < c.FirstInstance + c.InstanceCount; i++)
                {
                    var set = ours.TryGetValue((d.View, d.Aggregate), out var s) ? s : ours[(d.View, d.Aggregate)] = [];
                    set[i] = Record(records, transforms, i);
                }

        var resources = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "replay_face0", "resources.json")))!;
        var theirs = new Dictionary<(int View, int Aggregate), Dictionary<int, string>>();
        foreach (var line in File.ReadLines(Path.Combine(dir, "replay_face0", "actions.jsonl")))
        {
            var r = JsonNode.Parse(line)!;
            if (r["contents"] is null || r["stages"]?["vs"] is not { } vs || r["vertexInput"]?["vertexBuffers"] is not JsonArray { Count: > 0 } vbs)
                continue;
            var before = r["contents"]!["before"]!;
            if (Blob(dir, before, (string)vbs[0]!["resourceId"]!) is not { } vb0 || !byVertexSha.TryGetValue(Sha(vb0), out var aggregate))
                continue;
            byte[]? inst = null, xf = null;
            foreach (var x in vs["readWrite"]!.AsArray().Concat(vs["readOnly"]!.AsArray()))
                if ((int)x!["bind"]!["set"]! == 4)
                {
                    var data = Blob(dir, before, (string)x["descriptor"]!["resource"]!);
                    if ((int)x["bind"]!["binding"]! == 32)
                        inst = data;
                    else if ((int)x["bind"]!["binding"]! == 30)
                        xf = data;
                }
            if (inst == null || xf == null)
                continue;
            var depth = (string?)r["depthOut"];
            var atlas = depth != null && (string)resources["textures"]![depth]!["format"]! == "D16";
            var sc = r["viewportScissor"]!["viewportScissors"]![0]!["scissor"]!;
            var v = atlas ? Array.IndexOf(CubemapFaceDrawList.CascadeTiles, ((int)sc["x"]!, (int)sc["y"]!)) : -1;
            if (atlas && v < 0)
                continue;   // a shadowed light's own tile
            var first = (int)r["instanceOffset"]!;
            for (var i = first; i < first + (int)r["numInstances"]!; i++)
            {
                var set = theirs.TryGetValue((v, aggregate), out var s) ? s : theirs[(v, aggregate)] = [];
                set[i] = Record(inst, xf, i);
            }
        }
        var failures = new List<string>();
        var compared = 0;
        foreach (var key in ours.Keys.Union(theirs.Keys))
        {
            var a = (ours.GetValueOrDefault(key)?.Values ?? Enumerable.Empty<string>()).Order(StringComparer.Ordinal).ToList();
            var b = (theirs.GetValueOrDefault(key)?.Values ?? Enumerable.Empty<string>()).Order(StringComparer.Ordinal).ToList();
            compared += b.Count;
            if (!a.SequenceEqual(b))
                failures.Add($"view {key.View} aggregate {key.Aggregate} {aggregates[key.Aggregate].Name}: {a.Count} ours, {b.Count} captured, "
                             + $"{a.Except(b).Count()} of ours not captured (first {a.Except(b).FirstOrDefault()?[..40]}), {b.Except(a).Count()} captured not ours (first {b.Except(a).FirstOrDefault()?[..40]})");
        }
        output.WriteLine($"{ours.Count} (view, aggregate) blocks ours, {theirs.Count} captured, {compared} captured instances; {failures.Count} differ");
        foreach (var f in failures.Take(40))
            output.WriteLine("  " + f);
        Assert.Empty(failures);
    }

    private static byte[]? Blob(string dir, JsonNode before, string name)
    {
        var e = before[name];
        var sha = e is JsonArray arr ? (string?)arr[0]?["sha1"] : (string?)e?["sha1"];
        return sha == null ? null : File.ReadAllBytes(Path.Combine(dir, "replay_face0", "blobs", sha + ".bin"));
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA1.HashData(data));
}
