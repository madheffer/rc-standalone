using System.Numerics;
using ValveResourceFormat.Serialization.KeyValues;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Scratch probe (LIGHTRAY=addon|map|nodeId|record|axis|count): traces one
/// light record (-1 the whole light, else that cube face) through the editor
/// scene and prints the rays whose ends reach furthest along the axis
/// (x, y, z, or -x, -y, -z), each with the instance it stopped on. Extras:
/// LIGHTRAY_HIDE=id,id masks those props out first; LIGHTRAY_SWEEP=box masks
/// each hit instance in turn and marks the one whose box matches;
/// LIGHTRAY_WALL=y,lo,hi lists rays crossing that plane; LIGHTRAY_RAY=i
/// shows one ray's hit in its instance's space; LIGHTRAY_PROP=id,tri dumps a
/// prop's model, meshes, materials (LIGHTRAY_NEAR=x,y,z: its triangles there).
/// </summary>
public class LightRayProbe(ITestOutputHelper output)
{
    private sealed class Recorder(EditorTraceScene scene) : ILightTracer
    {
        public readonly List<(Vector3 Start, Vector3 End, EditorTraceScene.SceneHit? Hit)> Rays = [];

        public float? Trace(Vector3 start, Vector3 end) => throw new NotSupportedException();

        LightTraceHit? ILightTracer.Hit(Vector3 start, Vector3 end, uint mask)
        {
            var h = scene.Trace(start, end, mask);
            if (mask != 8)
                Rays.Add((start, end, h));
            return h is { } v ? new LightTraceHit(v.Distance, v.Flags) : null;
        }
    }

    /// <summary>
    /// SCENEFLAGS=addon|map|survey.jsonl (capture_scenekd.py --bounds survey):
    /// each of our triangle scenes' local box looked up among Valve's scenes,
    /// counted by our scene kind and Valve's flag word.
    /// </summary>
    [Fact]
    public void SceneFlagsAgainstSurvey()
    {
        if (Environment.GetEnvironmentVariable("SCENEFLAGS") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        if (MapFixtures.VmapSource(p[0], p[1]) is not { } source)
            return;
        var scene = (EditorTraceScene)SettleLumpTests.LightScene(MapSource.Read(source), source)!;
        static string Key(IEnumerable<float> v) => string.Join(",", v.Select(x => MathF.Round(x, 2).ToString("R")));
        var valve = new Dictionary<string, List<uint>>();
        foreach (var line in File.ReadLines(p[2]))
        {
            var e = System.Text.Json.JsonDocument.Parse(line).RootElement;
            if (!e.TryGetProperty("survey", out _))
                continue;
            var k = Key(e.GetProperty("box").EnumerateArray().Select(x => x.GetSingle()));
            (valve.TryGetValue(k, out var l) ? l : valve[k] = []).Add(e.GetProperty("flags").GetUInt32());
        }
        var counts = new SortedDictionary<string, int>();
        foreach (var (path, box, _) in scene.TriangleScenes())
        {
            var kind = string.Join("/", path.Split('/').Select(x => x.Split(' ')[0]));
            var k = Key([box.Mins.X, box.Mins.Y, box.Mins.Z, box.Maxs.X, box.Maxs.Y, box.Maxs.Z]);
            var found = valve.TryGetValue(k, out var fl) ? string.Join("|", fl.Distinct().Select(f => f.ToString("x"))) : "not found";
            counts[$"{kind}: {found}"] = counts.GetValueOrDefault($"{kind}: {found}") + 1;
            if (Environment.GetEnvironmentVariable("SCENEFLAGS_LIST") is { Length: > 0 } only && kind.StartsWith(only, StringComparison.Ordinal))
                output.WriteLine($"  {path}: {found} box {k}");
        }
        foreach (var (k, n) in counts)
            output.WriteLine($"{n,6} {k}");
    }

    /// <summary>
    /// SCENEDUMP=addon|map|source|file: the triangles (local corners, flag word)
    /// of every triangle scene under the top-level instance with that source
    /// name ("smartprop 33513"), one JSON line per scene.
    /// </summary>
    [Fact]
    public void DumpScene()
    {
        if (Environment.GetEnvironmentVariable("SCENEDUMP") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        if (MapFixtures.VmapSource(p[0], p[1]) is not { } source)
            return;
        var scene = (EditorTraceScene)SettleLumpTests.LightScene(MapSource.Read(source), source)!;
        using var sink = new StreamWriter(p[3]);
        void Walk(EditorTraceScene.Instance inst, string path)
        {
            if (inst.Children is { } kids)
            {
                sink.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { instance = path, toWorld = inst.ToWorld }));
                foreach (var k in kids)
                    Walk(k, path + "/" + k.Source);
                return;
            }
            sink.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { instance = path, toWorld = inst.ToWorld }));
            var tris = Enumerable.Range(0, inst.Flags.Length).Select(t => inst.Records.Skip(t * 22 + 13).Take(9).ToArray()).ToList();
            var records = Enumerable.Range(0, inst.Flags.Length).Select(t => inst.Records.Skip(t * 22).Take(13).ToArray()).ToList();
            sink.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { path, flags = inst.Flags, corners = tris, records }));
        }
        foreach (var inst in scene.Instances.Where(i => i.Source == p[2]))
            Walk(inst, inst.Source);
    }

    /// <summary>
    /// LIGHTKEYS=addon|map|nodeId: one light's keys as the map holds them (with
    /// the FGD defaults it falls back on), how many records it makes, and the
    /// precomputed keys we write for it.
    /// </summary>
    [Fact]
    public void OneLightsKeys()
    {
        if (Environment.GetEnvironmentVariable("LIGHTKEYS") is not { Length: > 0 } spec || MapFixtures.GameSchema() is not { } schema)
            return;
        var p = spec.Split('|');
        if (MapFixtures.VmapSource(p[0], p[1]) is not { } source)
            return;
        var document = MapSource.Read(source);
        var walked = MapEntities.From(document);
        var (copies, _) = MapInstances.Expand(document, walked, SmartProps.NodesCreatedOnLoad(document, MapFixtures.SmartPropLocators));
        var e = walked.Concat(copies.Select(c => walked[c.Template] with { NodeId = c.NodeId, Origin = c.Origin, Angles = c.Angles, Instanced = true }))
            .First(x => x.NodeId == int.Parse(p[2]));
        output.WriteLine($"{e.ClassName} origin {e.Origin} angles {e.Angles} instanced {e.Instanced}");
        foreach (var k in e.Keys)
            output.WriteLine($"  {k.Key} = {k.Value}");
        string? Key(string name) => e.Keys.FirstOrDefault(k => k.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value ?? schema.KeyOf(e.ClassName, name)?.Default;
        foreach (var name in new[] { "castshadows", "fog", "fogshadows", "enabled", "directlight", "bakelight" })
            output.WriteLine($"  [{name}] -> {Key(name) ?? "(null)"}");
        var world = LightPrecompute.World(e.Origin, e.Angles);
        output.WriteLine($"records: whole {LightPrecompute.Records(e.ClassName, Key, world, false).Length}, faces {LightPrecompute.Records(e.ClassName, Key, world, true).Length}");
        var scene = (EditorTraceScene)SettleLumpTests.LightScene(document, source)!;
        foreach (var kv in LightPrecompute.Keys(e.ClassName, Key, world, scene))
            output.WriteLine($"  ours {kv.Key} = {kv.Value}");
    }

    /// <summary>
    /// LIGHTRAYCMP=addon|map|capture.bin (tools/entities/capture_lightrays.py):
    /// Valve's own rays for one light traced through our light scene under the
    /// light pass's mask, against Valve's hits (+0xc triangle, +0x10 distance):
    /// counts per verdict, and the instances behind the disagreements.
    /// </summary>
    [Fact]
    public void AgainstCapture()
    {
        if (Environment.GetEnvironmentVariable("LIGHTRAYCMP") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        if (MapFixtures.VmapSource(p[0], p[1]) is not { } source)
            return;
        var scene = (EditorTraceScene)SettleLumpTests.LightScene(MapSource.Read(source), source)!;
        var data = File.ReadAllBytes(p[2]);
        var verdicts = new SortedDictionary<string, int>();
        var oursOnly = new Dictionary<string, int>();
        var valveOnly = new Dictionary<string, int>();
        var farther = new Dictionary<string, int>();
        var call = 0;
        for (var at = 0; at < data.Length; call++)
        {
            var n = BitConverter.ToInt32(data, at);
            at += 4;
            var starts = at;
            var ends = at + 12 * n;
            var results = ends + 12 * n;
            at = results + 0x38 * n;
            Vector3 V(int o) => new(BitConverter.ToSingle(data, o), BitConverter.ToSingle(data, o + 4), BitConverter.ToSingle(data, o + 8));
            if (call == 0 && Environment.GetEnvironmentVariable("LIGHTRAYCMP_ALIGN") is not null)
            {
                // Our distance per ray, then how often Valve's record j matches ray j + k.
                var ourDist = new float[n];
                for (var i = 0; i < n; i++)
                    ourDist[i] = scene.Trace(V(starts + 12 * i), V(ends + 12 * i), 0xc00060b1) is { } h ? h.Distance : -1f;
                var valveDist = Enumerable.Range(0, n).Select(j => BitConverter.ToUInt32(data, results + 0x38 * j + 0xc) == 0xffffffff ? -1f : BitConverter.ToSingle(data, results + 0x38 * j + 0x10)).ToArray();
                for (var k = -40; k <= 40; k++)
                {
                    var same = 0;
                    for (var j = 0; j < n; j++)
                        if (j + k >= 0 && j + k < n && valveDist[j] >= 0 && MathF.Abs(valveDist[j] - ourDist[j + k]) <= 1e-3f * MathF.Max(1f, valveDist[j]))
                            same++;
                    if (same > n / 50)
                        output.WriteLine($"offset {k}: {same} of {n} agree");
                }
                // The distance multiset: how many of Valve's distances occur among ours at all.
                var ourSet = ourDist.Where(x => x >= 0).Select(x => MathF.Round(x, 2)).ToHashSet();
                output.WriteLine($"valve distances found among ours (2 dp): {valveDist.Count(x => x >= 0 && ourSet.Contains(MathF.Round(x, 2)))} of {valveDist.Count(x => x >= 0)}");
            }
            // LIGHTRAYCMP_WATCH=instance,triangle,call: the kd walk's visits and tests of that triangle during that call.
            var watch = Environment.GetEnvironmentVariable("LIGHTRAYCMP_WATCH")?.Split(',').Select(int.Parse).ToArray();
            var watchLog = new List<string>();
            if (watch is not null && watch[2] == call)
                scene.WatchTriangle(watch[0], watch[1], watchLog);
            // The call's rays go through as one batch, as Valve's do (packets by octant).
            var batch = Environment.GetEnvironmentVariable("LIGHTRAYCMP_SWAP") is not null ? null
                : scene.TraceBatch([.. Enumerable.Range(0, n).Select(i => V(starts + 12 * i))], [.. Enumerable.Range(0, n).Select(i => V(ends + 12 * i))], 0xc00060b1);
            if (watch is not null && watch[2] == call)
            {
                scene.WatchTriangle(watch[0], watch[1], null);
                foreach (var line in watchLog)
                    output.WriteLine("  watch " + line);
            }
            for (var i = 0; i < n; i++)
            {
                var (s, e) = (V(starts + 12 * i), V(ends + 12 * i));
                if (Environment.GetEnvironmentVariable("LIGHTRAYCMP_SWAP") is not null)
                    (s, e) = (e, s);
                var tri = BitConverter.ToUInt32(data, results + 0x38 * i + 0xc);
                var dist = BitConverter.ToSingle(data, results + 0x38 * i + 0x10);
                var ours = batch is null ? scene.Trace(s, e, 0xc00060b1) : batch[i];
                string verdict;
                if (tri == 0xffffffff && ours is null)
                    verdict = "both pass";
                else if (tri == 0xffffffff)
                {
                    verdict = "ours hit, valve passes";
                    var k = scene.Instances[ours!.Value.Instance].Source;
                    oursOnly[k] = oursOnly.GetValueOrDefault(k) + 1;
                }
                else if (ours is null)
                {
                    verdict = "valve hit, ours passes";
                    if (Environment.GetEnvironmentVariable("LIGHTRAYCMP_DETAIL") is not null)
                        output.WriteLine($"  ray {call}:{i} ours passes; valve tri {tri} at {dist:F4}");
                    if (Environment.GetEnvironmentVariable("LIGHTRAYCMP_DETAIL") is not null)
                        foreach (var c in scene.FindTriangle(s, e, (int)tri, dist, 0.05f))
                            output.WriteLine($"    candidate {c.Path} tri {tri} at {c.T:F4} denom {c.Denominator:G6} a {c.A:G6} b {c.B:G6} a+b {c.A + c.B:G7} flags {c.Flags:x}");
                    valveOnly[$"tri {tri}"] = valveOnly.GetValueOrDefault($"tri {tri}") + 1;
                }
                else if (MathF.Abs(ours.Value.Distance - dist) <= 1e-3f * MathF.Max(1f, dist))
                {
                    verdict = "both hit, same distance";
                    // LIGHTRAYCMP_BITS=1: also tell bit-equal distances from near ones.
                    if (Environment.GetEnvironmentVariable("LIGHTRAYCMP_BITS") is not null && ours.Value.Distance != dist)
                    {
                        verdict = "both hit, distance off by bits";
                        if (Environment.GetEnvironmentVariable("LIGHTRAYCMP_DETAIL") is not null)
                            output.WriteLine($"  ray {call}:{i} ours {scene.Instances[ours.Value.Instance].Source} tri {ours.Value.Triangle} at {ours.Value.Distance:R}; valve tri {tri} at {dist:R}");
                    }
                }
                else
                {
                    verdict = ours.Value.Distance < dist ? "both hit, ours nearer" : "both hit, ours farther";
                    if (Environment.GetEnvironmentVariable("LIGHTRAYCMP_DETAIL") is not null && ours.Value.Distance > dist)
                    {
                        output.WriteLine($"  ray {call}:{i} ours {scene.Instances[ours.Value.Instance].Source} tri {ours.Value.Triangle} at {ours.Value.Distance:F4}; valve tri {tri} at {dist:F4} (ours FARTHER)");
                        foreach (var c in scene.FindTriangle(s, e, (int)tri, dist, 0.05f))
                            output.WriteLine($"    candidate {c.Path} tri {tri} at {c.T:F4} denom {c.Denominator:G6} a {c.A:G6} b {c.B:G6} a+b {c.A + c.B:G7} flags {c.Flags:x}");
                    }
                    if (Environment.GetEnvironmentVariable("LIGHTRAYCMP_DETAIL") is not null && ours.Value.Distance < dist)
                    {
                        // What lies behind our hit: trace on from just past it.
                        var dir = Vector3.Normalize(e - s);
                        var from = s + dir * (ours.Value.Distance + 0.01f);
                        var next = scene.Trace(from, e, 0xc00060b1);
                        var inst = scene.Instances[ours.Value.Instance];
                        var pr = scene.Probe(ours.Value.Instance, ours.Value.Triangle, s, e);
                        var bary = pr is { } q ? $" denom {q.Denominator:G6} a {q.A:G6} b {q.B:G6} a+b {q.A + q.B:G7}" : " (model)";
                        var hp = s + dir * ours.Value.Distance;
                        var (bmn, bmx) = scene.BoxOf(ours.Value.Instance);
                        var gap = new[] { hp.X - bmn.X, bmx.X - hp.X, hp.Y - bmn.Y, bmx.Y - hp.Y, hp.Z - bmn.Z, bmx.Z - hp.Z }.Min();
                        var thin = new[] { bmx.X - bmn.X, bmx.Y - bmn.Y, bmx.Z - bmn.Z }.Min();
                        bary += $" box gap {gap:G4} (box thinnest {thin:G4})";
                        output.WriteLine($"  ray {call}:{i} ours {inst.Source} tri {ours.Value.Triangle} at {ours.Value.Distance:F4}{bary}; valve tri {tri} at {dist:F4}; "
                            + $"behind ours: {(next is { } nx ? $"{scene.Instances[nx.Instance].Source} tri {nx.Triangle} at {ours.Value.Distance + 0.01f + nx.Distance:F4}" : "nothing")}");
                    }
                    var k = $"{scene.Instances[ours.Value.Instance].Source} ({verdict})";
                    farther[k] = farther.GetValueOrDefault(k) + 1;
                }
                verdicts[verdict] = verdicts.GetValueOrDefault(verdict) + 1;
            }
        }
        output.WriteLine($"{call} calls: " + string.Join("; ", verdicts.Select(kv => $"{kv.Key} {kv.Value}")));
        // LIGHTRAYCMP_NODE=<node id>: our own rays for that light (the whole record and each
        // face, as LightPrecompute traces them) against each of Valve's calls, bit for bit.
        if (Environment.GetEnvironmentVariable("LIGHTRAYCMP_NODE") is { Length: > 0 } nodeSpec && MapFixtures.GameSchema() is { } schema)
        {
            var document = MapSource.Read(source);
            var walked = MapEntities.From(document);
            var (copies, _) = MapInstances.Expand(document, walked, SmartProps.NodesCreatedOnLoad(document, MapFixtures.SmartPropLocators));
            var e = walked.Concat(copies.Select(c => walked[c.Template] with { NodeId = c.NodeId, Origin = c.Origin, Angles = c.Angles, Instanced = true }))
                .First(x => x.NodeId == int.Parse(nodeSpec));
            string? Key(string name) => e.Keys.FirstOrDefault(k => k.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value ?? schema.KeyOf(e.ClassName, name)?.Default;
            var world = LightPrecompute.World(e.Origin, e.Angles);
            var ours = new List<(string Name, Vector3[] Starts, Vector3[] Ends)>();
            var whole = LightPrecompute.Records(e.ClassName, Key, world, false)[0];
            var wr = LightTrace.Run(whole, LightPrecompute.Rays, scene);
            ours.Add(("whole", [.. wr.Starts], [.. wr.Ends]));
            var faces = LightPrecompute.Records(e.ClassName, Key, world, true);
            for (var i = 0; i < faces.Length && faces.Length >= 2; i++)
            {
                LightBuild.SphereLuminaire(faces[i], 1e-6f);
                var fr = LightTrace.Run(faces[i], LightPrecompute.Rays, scene);
                ours.Add(($"face {i}", [.. fr.Starts], [.. fr.Ends]));
            }
            static bool Same(Vector3 a, Vector3 b) => a.X.Equals(b.X) && a.Y.Equals(b.Y) && a.Z.Equals(b.Z) && BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X)
                && BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y) && BitConverter.SingleToInt32Bits(a.Z) == BitConverter.SingleToInt32Bits(b.Z);
            var c2 = 0;
            for (var at = 0; at < data.Length; c2++)
            {
                var n = BitConverter.ToInt32(data, at);
                var starts = at + 4;
                var ends = starts + 12 * n;
                at = ends + 12 * n + 0x38 * n;
                Vector3 V(int o) => new(BitConverter.ToSingle(data, o), BitConverter.ToSingle(data, o + 4), BitConverter.ToSingle(data, o + 8));
                var report = new List<string>();
                foreach (var (name, os, oe) in ours)
                {
                    if (os.Length != n)
                    {
                        report.Add($"{name}: {os.Length} rays");
                        continue;
                    }
                    int s = 0, en = 0, first = -1;
                    for (var i = 0; i < n; i++)
                    {
                        var ss = Same(os[i], V(starts + 12 * i));
                        // Ours keep the end clipped at the hit; Valve's is the full ray: compare directions.
                        var (dOurs, dValve) = (Vector3.Normalize(oe[i] - os[i]), Vector3.Normalize(V(ends + 12 * i) - V(starts + 12 * i)));
                        var ee = Same(oe[i], V(ends + 12 * i)) || Vector3.Dot(dOurs, dValve) > 0.9999999f;
                        s += ss ? 1 : 0;
                        en += ee ? 1 : 0;
                        if ((!ss || !ee) && first < 0)
                            first = i;
                    }
                    report.Add($"{name}: starts {s}/{n} ends {en}/{n}" + (first >= 0 ? $" (first {first}: ours {os[first]} {oe[first]} valve {V(starts + 12 * first)} {V(ends + 12 * first)})" : ""));
                }
                output.WriteLine($"valve call {c2}: " + string.Join(" | ", report));
            }
        }
        foreach (var (k, v) in oursOnly.OrderByDescending(kv => kv.Value).Take(25))
            output.WriteLine($"  ours hits {k}: {v}");
        foreach (var (k, v) in farther.OrderByDescending(kv => kv.Value).Take(25))
            output.WriteLine($"  {k}: {v}");
        foreach (var (k, v) in valveOnly.OrderByDescending(kv => kv.Value).Take(10))
            output.WriteLine($"  valve hits {k}: {v}");
    }

    [Fact]
    public void FurthestRays()
    {
        if (Environment.GetEnvironmentVariable("LIGHTRAY") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        if (MapFixtures.VmapSource(p[0], p[1]) is not { } source || MapFixtures.GameSchema() is not { } schema)
            return;
        var document = MapSource.Read(source);
        var scene = (EditorTraceScene)SettleLumpTests.LightScene(document, source)!;
        var walked = MapEntities.From(document);
        var (copies, _) = MapInstances.Expand(document, walked,
            SmartProps.NodesCreatedOnLoad(document, MapFixtures.SmartPropLocators));
        var e = walked.Concat(copies.Select(c => walked[c.Template] with { NodeId = c.NodeId, Origin = c.Origin, Angles = c.Angles, Instanced = true }))
            .First(x => p[2].Contains(':') ? x.IdPath == p[2] : x.NodeId == int.Parse(p[2]));
        string? Key(string name) => e.Keys.FirstOrDefault(k => k.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value
                                    ?? schema.KeyOf(e.ClassName, name)?.Default;
        var record = int.Parse(p[3]);
        var world = LightPrecompute.World(e.Origin, e.Angles);
        var light = record < 0 ? LightPrecompute.Records(e.ClassName, Key, world, false)[0]
                               : LightPrecompute.Records(e.ClassName, Key, world, true)[record];
        if (record >= 0)
            LightBuild.SphereLuminaire(light, 1e-6f);
        if (Environment.GetEnvironmentVariable("LIGHTRAY_HIDE") is { } hide)
            foreach (var id in hide.Split(','))
                scene.Instances.First(x => x.Source == $"prop {id}").ObjectFlags |= 0x4000;
        var recorder = new Recorder(scene);
        var result = LightTrace.Run(light, LightPrecompute.Rays, recorder);
        if (Environment.GetEnvironmentVariable("LIGHTRAY_EMPTY") is not null)
        {
            var empty = LightTrace.Run(light, LightPrecompute.Rays, EmptyLightScene.Instance);
            output.WriteLine($"empty scene: mins {empty.Mins:F6} maxs {empty.Maxs:F6} box origin {empty.Box.Origin:F6} extent {empty.Box.Extent:F6}");
        }
        output.WriteLine($"rays {recorder.Rays.Count}; box origin {result.Box.Origin} extent {result.Box.Extent}; mins {result.Mins} maxs {result.Maxs}");
        if (Environment.GetEnvironmentVariable("LIGHTRAY_SWEEP") is { } target)
        {
            // Hide each instance the rays hit, one at a time, and show the box.
            foreach (var index in recorder.Rays.Where(r => r.Hit is not null).Select(r => r.Hit!.Value.Instance).Distinct())
            {
                var inst = scene.Instances[index];
                var saved = inst.ObjectFlags;
                inst.ObjectFlags |= 0x4000;
                var again = LightTrace.Run(light, LightPrecompute.Rays, scene);
                inst.ObjectFlags = saved;
                var text = $"{LightPrecompute.Vector(again.Box.Origin)} {LightPrecompute.Vector(again.Box.Extent)}";
                output.WriteLine($"hide {inst.Source}: {text}{(text == target ? "  <== MATCH" : "")}");
            }
        }
        // LIGHTRAY_TRISWEEP=<instance source>|<target box>: mask each triangle of that instance the rays hit, in turn.
        if (Environment.GetEnvironmentVariable("LIGHTRAY_TRISWEEP") is { } triSpec)
        {
            var q = triSpec.Split('|');
            var index = scene.Instances.ToList().FindIndex(x => x.Source == q[0]);
            var inst = scene.Instances[index];
            var hitTris = recorder.Rays.Where(r => r.Hit is { } h && h.Instance == index).Select(r => r.Hit!.Value.Triangle).Distinct().Order().ToList();
            if (q.Length > 2 && q[2] == "pairs")
                for (var i = 0; i < hitTris.Count; i++)
                    for (var j = i + 1; j < hitTris.Count; j++)
                    {
                        var (a, b) = (inst.Flags[hitTris[i]], inst.Flags[hitTris[j]]);
                        inst.Flags[hitTris[i]] |= 0x20;
                        inst.Flags[hitTris[j]] |= 0x20;
                        var again = LightTrace.Run(light, LightPrecompute.Rays, scene);
                        (inst.Flags[hitTris[i]], inst.Flags[hitTris[j]]) = (a, b);
                        var text = $"{LightPrecompute.Vector(again.Box.Origin)} {LightPrecompute.Vector(again.Box.Extent)}";
                        output.WriteLine($"mask tris {hitTris[i]},{hitTris[j]}: {text}{(text == q[1] ? "  <== MATCH" : "")}");
                    }
            foreach (var tri in hitTris)
            {
                var saved = inst.Flags[tri];
                inst.Flags[tri] = (ushort)(saved | 0x20);
                var again = LightTrace.Run(light, LightPrecompute.Rays, scene);
                inst.Flags[tri] = saved;
                var text = $"{LightPrecompute.Vector(again.Box.Origin)} {LightPrecompute.Vector(again.Box.Extent)}";
                output.WriteLine($"mask tri {tri}: {text}{(text == q[1] ? "  <== MATCH" : "")}");
            }
        }
        var sign = p[4].StartsWith('-') ? -1f : 1f;
        var axis = p[4].TrimStart('-') switch { "x" => 0, "y" => 1, _ => 2 };
        float Along(Vector3 v) => sign * (axis == 0 ? v.X : axis == 1 ? v.Y : v.Z);
        var ends = recorder.Rays.Select((r, i) =>
        {
            float dx = r.End.X - r.Start.X, dy = r.End.Y - r.Start.Y, dz = r.End.Z - r.Start.Z;
            var length = MathF.Sqrt((dy * dy + dz * dz) + dx * dx);
            var inv = 1f / length;
            var t = r.Hit is { } h && h.Distance <= length ? h.Distance : length;
            var point = new Vector3(dx * inv * t + r.Start.X, dy * inv * t + r.Start.Y, dz * inv * t + r.Start.Z);
            return (Index: i, Point: point, r.Hit, Free: r.End);
        }).OrderByDescending(x => Along(x.Point)).Take(int.Parse(p.Length > 5 ? p[5] : "12"));
        if (Environment.GetEnvironmentVariable("LIGHTRAY_WALL") is { } wallSpec)
        {
            // Rays that would cross the plane (axis 1 = y) at value w with x in [lo, hi].
            var w = wallSpec.Split(',').Select(float.Parse).ToArray();
            for (var i = 0; i < recorder.Rays.Count; i++)
            {
                var r = recorder.Rays[i];
                var d = r.End - r.Start;
                var t = (w[0] - r.Start.Y) / d.Y;
                if (t is <= 0 or > 1)
                    continue;
                var x = r.Start.X + d.X * t;
                if (x < w[1] || x > w[2])
                    continue;
                var what = r.Hit is { } h ? $"{scene.Instances[h.Instance].Source} tri {h.Triangle} dist {h.Distance:F4}" : "no hit";
                output.WriteLine($"wall ray {i}: x {x:F4} z {r.Start.Z + d.Z * t:F4} at {t * d.Length():F4}; {what}");
            }
        }
        if (Environment.GetEnvironmentVariable("LIGHTRAY_PROP") is { } propSpec && CS2Fixtures.StockPak() is { } pak)
        {
            // prop node id, triangle: the model, and each mesh's triangle of that index.
            var q = propSpec.Split(',').Select(int.Parse).ToArray();
            var (_, nodes) = MapMeshes.ReadWithEntities(document);
            var node = nodes.First(n => n.Element.GetValue<int>("nodeID") == q[0]);
            var model = node.Element.Get<DmxBinary.Element>("entity_properties")!.Get<string>("model")!;
            var parts = Path.GetFullPath(source).Split(Path.DirectorySeparatorChar);
            var at = Array.FindIndex(parts, x => x.Equals("csgo_addons", StringComparison.OrdinalIgnoreCase));
            var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, "..", ".."));
            using var models = new SettleBuildTests.PakModels(pak, Path.Combine(root, "game", "csgo_addons", parts[at + 1]));
            var meshes = models.RenderMeshes(model);
            var inst = scene.Instances.First(x => x.Source == $"prop {q[0]}");
            if (models.ModelKeyValues(model) is { } mkv)
                foreach (var child in mkv.Children)
                    output.WriteLine($"  modelkv {child.Key}: {child.Value}");
            output.WriteLine($"  through {node.Through.Count}, origin {node.Element.GetValue<Vector3>("origin")} angles {node.Element.GetValue<Vector3>("angles")} scales {node.Element.GetValue<Vector3>("scales")}");
            output.WriteLine($"  world {string.Join(" ", inst.ToWorld.Select(v => v.ToString("F5")))}");
            foreach (var kv in node.Element.Get<DmxBinary.Element>("entity_properties")!.Attributes)
                output.WriteLine($"  key {kv.Key} = {kv.Value}");
            output.WriteLine($"prop {q[0]}: {model}, {meshes.Count} meshes: {string.Join(", ", meshes.Select(m => m.Count))}");
            for (var m = 0; m < meshes.Count; m++)
                if (q[1] < meshes[m].Count)
                {
                    var t = meshes[m][q[1]];
                    output.WriteLine($"  mesh {m} tri {q[1]}: {t.A} {t.B} {t.C} {t.Material}");
                }
            if (models.Read(model + "_c") is { } bytes)
            {
                using var res = new ValveResourceFormat.Resource();
                res.Read(new MemoryStream(bytes));
                var vm = (ValveResourceFormat.ResourceTypes.Model)res.DataBlock!;
                output.WriteLine($"  default group mask {(vm.Data.ContainsKey("m_nDefaultMeshGroupMask") ? vm.Data.GetUnsignedIntegerProperty("m_nDefaultMeshGroupMask") : 0)}");
                foreach (var (mesh, index, name, lod) in vm.GetEmbeddedMeshesAndLoD())
                {
                    var tris = mesh.Data.GetArray("m_sceneObjects").SelectMany(o => o.GetArray("m_drawCalls")).Sum(c => c.GetInt32Property("m_nIndexCount") / 3);
                    output.WriteLine($"  embedded mesh {index} {name} lod {lod} tris {tris} group {(vm.Data.ContainsKey("m_refMeshGroupMasks") ? string.Join(",", vm.Data.GetIntegerArray("m_refMeshGroupMasks")) : "-")}");
                }
            }
            if (Environment.GetEnvironmentVariable("LIGHTRAY_NEAR") is { } nearSpec)
            {
                var c = nearSpec.Split(',').Select(float.Parse).ToArray();
                var pt = new Vector3(c[0], c[1], c[2]);
                for (var m = 0; m < meshes.Count; m++)
                    for (var k = 0; k < meshes[m].Count; k++)
                    {
                        var t = meshes[m][k];
                        var n = Vector3.Cross(t.B - t.A, t.C - t.A);
                        if (n.Length() == 0f)
                            continue;
                        n = Vector3.Normalize(n);
                        var dist = Vector3.Dot(pt - t.A, n);
                        var lo = Vector3.Min(t.A, Vector3.Min(t.B, t.C)) - new Vector3(0.05f);
                        var hi = Vector3.Max(t.A, Vector3.Max(t.B, t.C)) + new Vector3(0.05f);
                        if (MathF.Abs(dist) < 0.05f && pt.X >= lo.X && pt.Y >= lo.Y && pt.Z >= lo.Z && pt.X <= hi.X && pt.Y <= hi.Y && pt.Z <= hi.Z)
                            output.WriteLine($"  near: mesh {m} tri {k}: {t.A:F4} {t.B:F4} {t.C:F4} n {n:F4} plane dist {dist:F5}");
                    }
            }
            foreach (var mat in meshes.SelectMany(m => m).Select(t => t.Material).Distinct())
            {
                var info = models.Material(mat);
                output.WriteLine($"  material {mat}: flags {TraceScene.MaterialFlags(info):x} shader {info?.Shader}");
                if (info is not null)
                {
                    output.WriteLine($"    ints {string.Join(", ", info.Ints.Select(kv => $"{kv.Key}={kv.Value}"))}");
                    output.WriteLine($"    params {string.Join(", ", info.Params.Select(kv => $"{kv.Key}={kv.Value}"))}");
                }
            }
        }
        if (Environment.GetEnvironmentVariable("LIGHTRAY_RAY") is { } rayIndex)
        {
            var r = recorder.Rays[int.Parse(rayIndex)];
            var d = r.End - r.Start;
            output.WriteLine($"ray {rayIndex}: start {r.Start} end {r.End} hit {r.Hit}");
            if (r.Hit is { } h)
            {
                var inst = scene.Instances[h.Instance];
                var hitWorld = r.Start + Vector3.Normalize(d) * h.Distance;
                var m = inst.ToLocal;
                Vector3 X(Vector3 v) => new(m[0] * v.X + m[1] * v.Y + m[2] * v.Z + m[3], m[4] * v.X + m[5] * v.Y + m[6] * v.Z + m[7],
                                            m[8] * v.X + m[9] * v.Y + m[10] * v.Z + m[11]);
                output.WriteLine($"  local hit {X(hitWorld):F5}; local start {X(r.Start):F5} local end {X(r.End):F5}");
            }
        }
        // LIGHTRAY_FREE=axis,value: rays whose unblocked end lies within 0.01 of value on that axis.
        if (Environment.GetEnvironmentVariable("LIGHTRAY_FREE") is { } freeSpec)
        {
            var q = freeSpec.Split(',');
            var axisIndex = q[0] switch { "x" => 0, "y" => 1, _ => 2 };
            var want = float.Parse(q[1], System.Globalization.CultureInfo.InvariantCulture);
            for (var i = 0; i < recorder.Rays.Count; i++)
            {
                var r = recorder.Rays[i];
                var v = axisIndex == 0 ? r.End.X : axisIndex == 1 ? r.End.Y : r.End.Z;
                if (MathF.Abs(v - want) > 0.01f)
                    continue;
                var what = r.Hit is { } h ? $"{scene.Instances[h.Instance].Source} tri {h.Triangle} flags {h.Flags:x} dist {h.Distance:F4}" : "no hit";
                output.WriteLine($"free ray {i}: start {r.Start:F4} end {r.End:F4}: {what}");
            }
        }
        foreach (var (i, point, hit, free) in ends)
        {
            var what = hit is { } h ? $"{scene.Instances[h.Instance].Source} tri {h.Triangle} flags {h.Flags:x}" : "no hit";
            output.WriteLine($"ray {i}: end {point.X:F4} {point.Y:F4} {point.Z:F4} (free {free.X:F2} {free.Y:F2} {free.Z:F2}) {what} dist {hit?.Distance:F4}");
        }
    }
}
