using System.Numerics;
using System.Text.Json.Nodes;
using Source2.Compiler.Cubemaps;
using Source2.Compiler.Maps;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="AggregateCulling"/> against a cubehost capture's draw arguments. <c>CUBECULL=&lt;capture dir&gt;</c>
/// holds the captured map's package (*.vpk), <c>indirect.jsonl</c> (tools/cubehost/rdc_indirect.py) and
/// <c>replay/</c> (rdc_replay.py without dumps: each action's targets); <c>CUBECULL_ORIGIN=x,y,z</c> overrides cube 0's
/// origin (by default the first cubemap entity in the package's lumps). For all six faces, for each of the face view's indirect
/// draw calls (one material list each), the captured commands are split back into fragments and regenerated from
/// our visible order: the index ranges must be equal, and each aggregate's first instances must differ from ours
/// by one constant (the game hands out each view's instance block per aggregate in thread order). The face view's
/// depth prepass lists are matched against the Large / Small split (<see cref="AggregateCulling.PrepassSplit"/>);
/// the sun cascades' lists are skipped unless <c>CUBECULL_CASCADES=1</c> (their culling is not ported).
/// </summary>
public sealed class AggregateCullingCaptureTests(ITestOutputHelper output)
{
    /// <summary>One aggregate and its model's first vertex buffer (size in bytes, stride), which tells its draws apart in a capture.</summary>
    private sealed record Aggregate(string Name, List<AggregateCulling.Fragment> Fragments, bool OwnTransforms, long VertexBytes, uint VertexStride, List<string> Flags, List<AggregateCulling.LodSetup> Lods);

    private static string SceneInfoTestsText(KVObject o, string key)
        => o.Children.FirstOrDefault(c => c.Key == key) is { } c ? (c.Value.IsArray ? string.Join("|", c.Value.Values.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture))) : c.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)) : "";

    private static List<Aggregate> Aggregates(string vpk)
    {
        using var package = new ValvePak.Package();
        package.Read(vpk);
        var result = new List<Aggregate>();
        foreach (var entry in package.Entries!.SelectMany(kv => kv.Value).Where(e => e.GetFullPath().EndsWith(".vwnod_c", StringComparison.Ordinal))
                     .OrderBy(e => e.GetFullPath(), StringComparer.Ordinal))
        {
            package.ReadEntry(entry, out var nodeBytes);
            using var nodeResource = new Resource();
            nodeResource.Read(new MemoryStream(nodeBytes));
            foreach (var o in ((WorldNode)nodeResource.DataBlock!).Data.GetArray("m_aggregateSceneObjects") ?? [])
            {
                var name = o.GetStringProperty("m_renderableModel") ?? "";
                package.ReadEntry(package.FindEntry(Path.ChangeExtension(name, ".vmdl_c"))!, out var bytes);
                using var resource = new Resource();
                resource.Read(new MemoryStream(bytes));
                var draws = new List<AggregateCulling.DrawCall>();
                long vertexBytes = 0;
                uint vertexStride = 0;
                foreach (var (mesh, _, _, _) in ((Model)resource.DataBlock!).GetEmbeddedMeshesAndLoD())
                {
                    if (mesh.VBIB.VertexBuffers.Count > 0 && vertexBytes == 0)
                        (vertexBytes, vertexStride) = (mesh.VBIB.VertexBuffers[0].Data.Length, mesh.VBIB.VertexBuffers[0].ElementSizeInBytes);
                    foreach (var so in mesh.Data.GetArray("m_sceneObjects"))
                    {
                        var bounds = so.GetArray("m_drawBounds");
                        var calls = so.GetArray("m_drawCalls");
                        for (var i = 0; i < calls.Count; i++)
                        {
                            var dc = calls[i];
                            var tint = dc.ContainsKey("m_vTintColor") ? dc.GetSubCollection("m_vTintColor").ToVector3() : Vector3.One;
                            draws.Add(new AggregateCulling.DrawCall(dc.GetInt32Property("m_nStartIndex"), dc.GetInt32Property("m_nIndexCount"),
                                dc.GetInt32Property("m_nBaseVertex"), bounds[i].GetSubCollection("m_vMinBounds").ToVector3(),
                                bounds[i].GetSubCollection("m_vMaxBounds").ToVector3(), new Vector4(tint, dc.GetFloatProperty("m_flAlpha"))));
                        }
                    }
                }
                var transforms = o.GetArray("m_fragmentTransforms") ?? [];
                var nextTransform = 0;
                var meshes = new List<AggregateCulling.MeshInfo>();
                var flags = new List<string>();
                foreach (var m in o.GetArray("m_aggregateMeshes"))
                {
                    flags.Add(SceneInfoTestsText(m, "m_objectFlags") + $" lod {m.GetInt32Property("m_nLODSetupIndex")} mask {m.GetInt32Property("m_nLODGroupMask")}");
                    float[]? transform = null;
                    if (m.GetBooleanProperty("m_bHasTransform"))
                        transform = [.. transforms[nextTransform++].Values.Select(v => (float)v.ToDouble(System.Globalization.CultureInfo.InvariantCulture))];
                    var tint = m.GetArray<long>("m_vTintColor") is { } t ? (uint)(t[0] | t[1] << 8 | t[2] << 16) : 0xffffffu;
                    meshes.Add(new AggregateCulling.MeshInfo(m.GetInt32Property("m_nDrawCallIndex"), transform, tint, m.GetInt32Property("m_nLODSetupIndex"), m.GetInt32Property("m_nLODGroupMask")));
                }
                var fragments = AggregateCulling.Fragments(meshes, draws);
                var lods = (o.GetArray("m_lodSetups") ?? []).Select(l => new AggregateCulling.LodSetup(l.GetSubCollection("m_vLODOrigin").ToVector3(),
                    l.GetFloatProperty("m_fMaxObjectScale"), [.. l.GetArray<double>("m_fSwitchDistances").Select(d => (float)d)])).ToList();
                var slots = fragments.Count == 0 ? 0 : fragments[^1].TransformSlot + 1;
                result.Add(new Aggregate($"{entry.FileName} {name}", fragments, slots == fragments.Count, vertexBytes, vertexStride, flags, lods));
            }
        }
        return result;
    }

    /// <summary>
    /// Cube 0's origin as client 1801ea0a0 gathers the records: env_cubemap first, then env_cubemap_box and the
    /// combined probe volumes, each class in entity order (taken here as lump order).
    /// </summary>
    private static Vector3 CubeZeroOrigin(string vpk) => CubeZero(vpk).Origin;

    /// <summary>Cube 0's origin and light_environment's angles as the scene light keeps them (through AngleQuaternion and back).</summary>
    private static (Vector3 Origin, Vector3 SunAngles) CubeZero(string vpk)
    {
        using var package = new ValvePak.Package();
        package.Read(vpk);
        var entities = new List<(string Class, LightPrecompute.KeyReader Key)>();
        foreach (var entry in package.Entries!.GetValueOrDefault("vents_c") ?? [])
        {
            package.ReadEntry(entry, out var bytes);
            entities.AddRange(SceneInfoTests.LumpEntities(bytes, entry.GetFullPath()));
        }
        static Vector3 V(string text) => text.Split(' ').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray() is var a ? new Vector3(a[0], a[1], a[2]) : default;
        var sun = entities.FirstOrDefault(e => e.Class == "light_environment") is { Key: not null } light && light.Key("angles") is { } angles
            ? CubemapFaceViews.MatrixAngles(CubemapFaceViews.QuaternionMatrix(CubemapFaceViews.AngleQuaternion(V(angles))))
            : Vector3.Zero;
        foreach (var cls in new[] { "env_cubemap", "env_cubemap_box", "env_combined_light_probe_volume", "func_combined_light_probe_volume" })
            if (entities.FirstOrDefault(e => e.Class == cls) is { Key: not null } e)
                return (V(e.Key("origin")!), sun);
        throw new InvalidDataException("no cubemap entity");
    }

    [Fact]
    public void FaceDrawsEqualTheCapture()
    {
        if (Environment.GetEnvironmentVariable("CUBECULL") is not { Length: > 0 } dir)
            return;
        var vpk = Directory.GetFiles(dir, "*.vpk").Single();
        var origin = Environment.GetEnvironmentVariable("CUBECULL_ORIGIN") is { Length: > 0 } given
            ? given.Split(',').Select(float.Parse).ToArray() is var g ? new Vector3(g[0], g[1], g[2]) : default
            : CubeZeroOrigin(vpk);
        var aggregates = Aggregates(vpk);
        var sunAngles = CubeZero(vpk).SunAngles;
        output.WriteLine($"cube 0 at {origin}, sun angles {sunAngles.X:R} {sunAngles.Y:R} {sunAngles.Z:R}");
        foreach (var a in aggregates)
            output.WriteLine($"{a.Name}: {a.Fragments.Count} fragments, {(a.OwnTransforms ? "own transforms" : $"{a.Fragments[^1].TransformSlot + 1} transform slots")}");
        var failures = new List<string>();
        for (var face = 0; face < 6; face++)
            CompareFace(dir, origin, sunAngles, face, aggregates, failures);
        foreach (var f in failures)
            output.WriteLine("FAIL " + f);
        Assert.Empty(failures);
    }

    /// <summary>The cascade tiles in the shadow atlas (shadow quality 2): cascades 0 to 2 in the top row, the viewmodel cascade below.</summary>
    private static readonly (int X, int Y)[] CascadeTiles = [(0, 0), (1280, 0), (2560, 0), (0, 2048)];

    private void CompareFace(string dir, Vector3 origin, Vector3 sunAngles, int face, List<Aggregate> aggregates, List<string> failures)
    {
        var view = CubemapFaceViews.Face(origin, Vector3.Zero, 1f, 10000f, face);
        var (toSun, _, cameraDirection) = CubemapShadowCascades.SunDirections(sunAngles);
        var cascades = CubemapShadowCascades.GameCascades(view, toSun, 1f, 10000f);
        // Each view's frustum and camera: -1 the face, 0 to 3 its cascades.
        var views = new Dictionary<int, AggregateCulling.Frustum> { [-1] = AggregateCulling.FaceFrustum(view) };
        for (var c = 0; c < cascades.Count; c++)
            views[c] = AggregateCulling.CascadeFrustum(cascades[c], cameraDirection, zeroToOneDepth: true);
        var visibleOf = views.ToDictionary(kv => kv.Key, kv => aggregates.Select(a => AggregateCulling.Visible(kv.Value, a.Fragments, a.Lods, origin)).ToList());
        for (var k = 0; k < aggregates.Count; k++)
            output.WriteLine($"  visible in face {face}: {aggregates[k].Name}: [{string.Join(", ", visibleOf[-1][k])}]");

        // The face's indirect draw calls: those whose depth target is the face's (render size square), from the
        // face's first draw to its readback.
        var resources = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "replay", "resources.json")))!["textures"]!;
        var actions = File.ReadLines(Path.Combine(dir, "replay", "actions.jsonl")).Select(l => JsonNode.Parse(l)!).ToList();
        var readbacks = actions.Where(r => ((string)r["name"]!).StartsWith("vkCmdCopyImageToBuffer", StringComparison.Ordinal)).Select(r => (int)r["eid"]!).ToList();
        var last = readbacks[face];
        var firstEid = face == 0 ? 0 : readbacks[face - 1];
        var depthOf = actions.Where(r => r["depthOut"] is not null).ToDictionary(r => (int)r["eid"]!, r => (string)r["depthOut"]!);
        var buffers = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "replay", "resources.json")))!["buffers"]!;
        // Each draw's first vertex stream (its model's own buffer; the index buffer is pooled).
        var colourOf = actions.Where(r => r["outputs"] is JsonArray).ToDictionary(r => (int)r["eid"]!, r => ((JsonArray)r["outputs"]!).Count > 0);
        var streamOf = actions.Where(r => r["vbuffers"] is JsonArray { Count: > 0 }).ToDictionary(r => (int)r["eid"]!,
            r => ((long)buffers[(string)r["vbuffers"]![0]!["resourceId"]!]!, (uint)r["vbuffers"]![0]!["byteStride"]!));
        var offsets = new Dictionary<(int View, int Aggregate), int>();
        var scissorOf = actions.Where(r => r["viewportScissor"]?["viewportScissors"] is JsonArray { Count: > 0 }).ToDictionary(r => (int)r["eid"]!,
            r => ((int)r["viewportScissor"]!["viewportScissors"]![0]!["scissor"]!["x"]!, (int)r["viewportScissor"]!["viewportScissors"]![0]!["scissor"]!["y"]!));
        foreach (var line in File.ReadLines(Path.Combine(dir, "indirect.jsonl")))
        {
            var rec = JsonNode.Parse(line)!;
            var eid = (int)rec["eid"]!;
            if (eid <= firstEid || eid > last || rec["commands"] is not JsonArray cmds)
                continue;
            // A multi-draw's sub-commands carry the state; take the first one's target.
            var target = depthOf.GetValueOrDefault(eid) ?? depthOf.Where(kv => kv.Key > eid).OrderBy(kv => kv.Key).Select(kv => kv.Value).FirstOrDefault();
            if (target == null)
                continue;
            // The face's own target is square; the shadow atlas is not, and the scissor names the cascade.
            int viewKey;
            if ((int)resources[target]!["width"]! == (int)resources[target]!["height"]!)
                viewKey = -1;
            else
            {
                var tile = scissorOf.GetValueOrDefault(eid, scissorOf.Where(kv => kv.Key > eid).OrderBy(kv => kv.Key).Select(kv => kv.Value).FirstOrDefault());
                // The cascades' own culling (scenesystem 180494b40: the shadow view 1804a7a60, the previous cascade's
                // box shrunk by 15% as an exclusion volume) is not ported; CUBECULL_CASCADES=1 compares them anyway.
                if (Environment.GetEnvironmentVariable("CUBECULL_CASCADES") != "1")
                    continue;
                viewKey = Array.IndexOf(CascadeTiles, tile);
                if (viewKey < 0 || viewKey >= cascades.Count)
                {
                    failures.Add($"face {face} {eid}: a shadow draw in no known cascade tile {tile}");
                    continue;
                }
            }
            var visible = visibleOf[viewKey];
            var viewName = viewKey < 0 ? "face" : $"cascade {viewKey}";
            var (streamBytes, streamStride) = streamOf.GetValueOrDefault(eid, streamOf.Where(kv => kv.Key > eid).OrderBy(kv => kv.Key).Select(kv => kv.Value).FirstOrDefault());
            var captured = cmds.Select(c => c!.AsArray().Select(v => (int)v!).ToArray())
                .Select(c => new AggregateCulling.Command(c[0], c[1], c[2], c[3], c[4])).ToList();
            // The list's aggregate (its model's vertex stream, and every command on its draw calls' index ranges) and
            // draw calls (those inside a command's range); a material list holds every visible fragment on them.
            // Every aggregate that fits: its model's vertex stream, and each command on its draw calls' index ranges.
            var candidates = new List<(int Aggregate, HashSet<int> Calls)>();
            for (var k = 0; k < aggregates.Count; k++)
            {
                if (aggregates[k].VertexBytes != streamBytes || aggregates[k].VertexStride != streamStride)
                    continue;
                var fr = aggregates[k].Fragments;
                var inside = new HashSet<int>();
                var fits = true;
                foreach (var c in captured)
                {
                    var covered = fr.Where(f => f.BaseVertex == c.VertexOffset && f.StartIndex >= c.FirstIndex
                                                && f.StartIndex + f.IndexCount <= c.FirstIndex + c.IndexCount).Select(f => f.DrawCall).ToHashSet();
                    var bounds = fr.Where(f => covered.Contains(f.DrawCall)).Select(f => (f.StartIndex, f.IndexCount)).Distinct().ToList();
                    if (bounds.Count == 0 || bounds.Min(b => b.StartIndex) != c.FirstIndex || bounds.Sum(b => b.IndexCount) != c.IndexCount)
                    {
                        fits = false;
                        break;
                    }
                    inside.UnionWith(covered);
                }
                if (fits)
                    candidates.Add((k, inside));
            }
            if (candidates.Count == 0)
            {
                // Not a world aggregate's draw (entity models, the sky): outside this port.
                output.WriteLine($"face {face} {eid}: not an aggregate draw [{string.Join(" ", captured)}]");
                continue;
            }
            // A depth-only list may be one of the split prepass layers (Large, Small) or the unsplit AlphaTested one.
            var outputs = colourOf.GetValueOrDefault(eid, colourOf.Where(kv => kv.Key > eid).OrderBy(kv => kv.Key).Select(kv => kv.Value).FirstOrDefault());
            float[] splits = outputs || viewKey >= 0 ? [0f] : [AggregateCulling.PrepassSplit, -AggregateCulling.PrepassSplit, 0f];
            List<AggregateCulling.Command>? ours = null;
            List<(int Fragment, int Position)> list = [];
            var same = false;
            var offset = 0;
            var layer = "";
            var owner = candidates[0].Aggregate;
            var aggregateSize = 0f;
            foreach (var (k, calls) in candidates)
            {
                var fragments = aggregates[k].Fragments;
                var size = AggregateCulling.AggregateSize(fragments, origin, 1f);
                foreach (var split in splits)
                {
                    var candidate = visible[k].Select((f, pos) => (Fragment: f, Position: pos))
                        .Where(e => calls.Contains(fragments[e.Fragment].DrawCall)
                                    && AggregateCulling.InLayer(split, AggregateCulling.ScreenSize(origin, 1f, fragments[e.Fragment].Centre, fragments[e.Fragment].Radius), size))
                        .ToList();
                    if (candidate.Count == 0)
                        continue;
                    var commands = AggregateCulling.Commands(candidate, fragments, 0, aggregates[k].OwnTransforms);
                    var candidateOffset = captured[0].FirstInstance - commands[0].FirstInstance;
                    var equal = commands.Count == captured.Count && commands.Zip(captured).All(p => p.First with { FirstInstance = p.First.FirstInstance + candidateOffset } == p.Second);
                    if (ours == null || equal)
                        (ours, list, offset, same, layer, owner, aggregateSize) = (commands, candidate, candidateOffset, equal, split > 0 ? " (large)" : split < 0 ? " (small)" : "", k, size);
                    if (equal)
                        break;
                }
                if (same)
                    break;
            }
            if (ours == null)
            {
                failures.Add($"face {face} {viewName} {eid}: {aggregates[owner].Name} ({candidates.Count} candidates) draws [{string.Join(" ", captured)}] but we cull every fragment of the list");
                continue;
            }
            if (offsets.TryGetValue((viewKey, owner), out var known) && known != offset)
                failures.Add($"face {face} {viewName} {eid}: instance offset {offset}, earlier lists of this aggregate {known}");
            offsets[(viewKey, owner)] = offset;
            output.WriteLine($"face {face} {viewName} {eid} {rec["name"]}: {captured.Count} commands{layer}, {(same ? "equal" : "DIFFERENT")}"
                             + (same ? "" : $": ours [{string.Join(" ", ours)}] captured [{string.Join(" ", captured)}]"));
            if (!same)
            {
                failures.Add($"face {face} {viewName} {eid}: commands differ");
                foreach (var (f, pos) in list)
                {
                    var fr = aggregates[owner].Fragments[f];
                    output.WriteLine($"    fragment {f} at {pos}: draw {fr.DrawCall}, flags {aggregates[owner].Flags[f]}, box {fr.Min} {fr.Max}, size {AggregateCulling.ScreenSize(origin, 1f, fr.Centre, fr.Radius)} (aggregate {aggregateSize})");
                }
            }
        }
    }
}
