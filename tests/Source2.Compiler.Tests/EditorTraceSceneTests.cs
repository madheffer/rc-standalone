using System.Collections.Concurrent;
using System.Runtime.Intrinsics;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The light precompute's keys traced against <see cref="EditorTraceScene"/>,
/// against Valve's lump of the same map. ETS_MAP picks one map, ETS_DUMP a
/// directory for the per-light differences.
/// </summary>
public sealed class EditorTraceSceneTests(ITestOutputHelper output)
{
    /// <summary>Records every ray of one light and prints the ones ending lowest in x (ETS_RAYS=map|id).</summary>
    [Fact]
    public void RaysOfOneLight()
    {
        if (Environment.GetEnvironmentVariable("ETS_RAYS") is not { } spec || CS2Fixtures.StockPak() is not { } pak)
            return;
        var p = spec.Split('|');
        var addon = p[0] == "atixref" ? "s2probe" : "s2c_lighting";
        var source = MapFixtures.VmapSource(addon, p[0])!;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        var document = DmxBinary.ReadFile(source);
        var scene = new EditorTraceScene(EditorTraceScene.MapMeshInstances(document, m => TraceScene.MaterialFlags(content.Material(m))));
        var e = MapEntities.From(document).First(x => x.NodeId == int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture));
        var schema = MapFixtures.GameSchema();
        string? Key(string name) => e.Keys.FirstOrDefault(k => k.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value
                                    ?? schema?.KeyOf(e.ClassName, name)?.Default;
        var record = LightPrecompute.Records(e.ClassName, Key, LightPrecompute.World(e.Origin, e.Angles), split: false)[0];
        var log = new Recorder(scene);
        LightTrace.Run(record, LightPrecompute.Rays, log);
        foreach (var r in log.Rays.OrderBy(r => r.End.X).Take(12))
            output.WriteLine($"  ray end {r.End.X:G9} {r.End.Y:G9} {r.End.Z:G9} hit {r.Hit?.Distance:G9} {(r.Hit is { } hh ? scene.Instances[hh.Instance].Source + " tri " + hh.Triangle : "none")}");
        // Rays whose first blocker, taken away, would let them reach lower x.
        foreach (var r in log.Rays.Where(r => r.Hit is { } hx && scene.Instances[hx.Instance].Source != "mesh 409"))
        {
            var without = new EditorTraceScene([.. scene.Instances.Where((_, i) => i != r.Hit!.Value.Instance)]);
            var hh = without.Trace(r.Start, r.To, EditorTraceScene.LightMask);
            var d0 = r.To - r.Start;
            var l0 = MathF.Sqrt((d0.Y * d0.Y + d0.Z * d0.Z) + d0.X * d0.X);
            var dist0 = hh is { } q0 && q0.Distance <= l0 ? q0.Distance : l0;
            var end0 = r.Start + d0 * (1f / l0) * dist0;
            if (end0.X < -6720.0f)
                output.WriteLine($"  unblocked {scene.Instances[r.Hit!.Value.Instance].Source} tri {r.Hit.Value.Triangle} d {r.Hit.Value.Distance:G9}: would end {end0.X:G9} via {(hh is { } q1 ? without.Instances[q1.Instance].Source : "none")}");
        }
        foreach (var r in log.Rays.OrderBy(r => r.End.X).Take(1))
        {
            output.WriteLine($"{r.Start} -> {r.End}: hit {r.Hit} inst {(r.Hit is { } h ? scene.Instances[h.Instance].Source : "")} tri {r.Hit?.Triangle}");
            if (r.Hit is not { } hit)
                continue;
            var inst = scene.Instances[hit.Instance];
            output.WriteLine($"  toWorld {string.Join(" ", inst.ToWorld.Select(v => v.ToString("G9")))}");
            output.WriteLine($"  toLocal {string.Join(" ", inst.ToLocal.Select(v => v.ToString("G9")))}");
            var c = inst.Records.AsSpan(hit.Triangle * 22 + 13, 9).ToArray();
            output.WriteLine($"  corners {string.Join(" ", c.Select(v => v.ToString("G9")))}");
            output.WriteLine($"  record {string.Join(" ", inst.Records.AsSpan(hit.Triangle * 22, 13).ToArray().Select(v => v.ToString("G9")))}");
            output.WriteLine($"  start {r.Start.X:G9} {r.Start.Y:G9} {r.Start.Z:G9} dist {hit.Distance:G9} endX {r.End.X:G9}");
            // Variants: the plane met in world space along the same direction.
            var s = r.Start;
            var e0 = r.To;
            var dd = e0 - s;
            var len = MathF.Sqrt((dd.Z * dd.Z + dd.Y * dd.Y) + dd.X * dd.X);
            var sc = RayTraceEnvironment.Refined(len);
            var dir = new System.Numerics.Vector3(dd.X * sc, dd.Y * sc, dd.Z * sc);
            var rec = inst.Records.AsSpan(hit.Triangle * 22, 13).ToArray();
            var wplane = rec[3] + 153.5f * rec[2];
            var denom = (dir.Z * rec[2] + dir.Y * rec[1]) + dir.X * rec[0];
            var tw = (wplane - ((s.Z * rec[2] + s.Y * rec[1]) + rec[0] * s.X)) / denom;
            float Ux(float dist)
            {
                var len1 = MathF.Sqrt((dd.Y * dd.Y + dd.Z * dd.Z) + dd.X * dd.X);
                var inv = 1f / len1;
                return dd.X * inv * dist + s.X;
            }
            output.WriteLine($"  end {e0.X:G9} {e0.Y:G9} {e0.Z:G9} len {len:G9}; world t {tw:G9} -> x {Ux(tw):G9}; ours -> x {Ux(hit.Distance):G9}");
            for (var ii = 0; ii < scene.Instances.Count; ii++)
            {
                var one = new EditorTraceScene([scene.Instances[ii]]);
                if (one.Trace(s, e0, EditorTraceScene.LightMask) is { } oh)
                    output.WriteLine($"  also {scene.Instances[ii].Source} flags 0x{scene.Instances[ii].ObjectFlags:x}: dist {oh.Distance:G9} tri {oh.Triangle}");
            }
            foreach (var variant in Enumerable.Range(0, 8))
            {
                var m = inst.ToLocal;
                System.Numerics.Vector3 X(float[] mm, System.Numerics.Vector3 p) => new(
                    mm[3] + ((p.X * mm[0] + p.Y * mm[1]) + p.Z * mm[2]), mm[7] + ((p.X * mm[4] + p.Y * mm[5]) + p.Z * mm[6]), mm[11] + ((p.X * mm[8] + p.Y * mm[9]) + p.Z * mm[10]));
                var lo = X(m, s);
                var lp = X(m, s + dir);
                var ld = lp - lo;
                if (variant == 1) ld = dir;
                var l2 = (ld.Y * ld.Y + ld.X * ld.X) + ld.Z * ld.Z;
                var rr = System.Runtime.Intrinsics.X86.Sse.ReciprocalSqrtScalar(System.Runtime.Intrinsics.Vector128.CreateScalarUnsafe(l2)).ToScalar();
                var invLen = (rr * (3f - (rr * rr) * l2)) * 0.5f;
                if (variant == 2) invLen = 1f / MathF.Sqrt(l2);
                if (variant == 3) invLen = rr;
                var ldn = ld * invLen;
                var cc = System.Runtime.Intrinsics.X86.Sse.ReciprocalScalar(System.Runtime.Intrinsics.Vector128.CreateScalarUnsafe(invLen)).ToScalar();
                var ln = (cc + cc) - (cc * cc) * invLen;
                var den = (ldn.Z * rec[2] + ldn.Y * rec[1]) + ldn.X * rec[0];
                var tt = (rec[3] - ((lo.Z * rec[2] + lo.Y * rec[1]) + rec[0] * lo.X)) / den;
                if (variant == 4) tt = tt / ln * ln;
                var hitL = new System.Numerics.Vector3(tt * ldn.X + lo.X, tt * ldn.Y + lo.Y, tt * ldn.Z + lo.Z);
                var wpt = X(inst.ToWorld, hitL);
                var q = wpt - s;
                var dist = MathF.Sqrt((q.Z * q.Z + q.Y * q.Y) + q.X * q.X);
                if (variant == 5) dist = tt / ln;
                if (variant == 6) dist = tt * (1f / ln);
                if (variant == 7) dist = tt * invLen;
                output.WriteLine($"  variant {variant}: t {tt:G9} dist {dist:G9} -> x {Ux(dist):G9}");
            }
            // The same triangle placed with other splits of its translation.
            var cw = inst.Records.AsSpan(hit.Triangle * 22 + 13, 9).ToArray();
            foreach (var shift in new[] { 0f, 1f, 0.5f })
            {
                var t0 = new System.Numerics.Vector3(inst.ToWorld[3], inst.ToWorld[7], inst.ToWorld[11]) * shift;
                var rest = new System.Numerics.Vector3(inst.ToWorld[3], inst.ToWorld[7], inst.ToWorld[11]) - t0;
                System.Numerics.Vector3 C(int k) => new System.Numerics.Vector3(cw[k * 3], cw[k * 3 + 1], cw[k * 3 + 2]) + rest;
                var mini = new EditorTraceScene([EditorTraceScene.MakeInstance([(C(0), C(1), C(2), 0)], [1, 0, 0, t0.X, 0, 1, 0, t0.Y, 0, 0, 1, t0.Z], 0, "v")]);
                if (mini.Trace(s, e0, 0) is { } mh)
                    output.WriteLine($"  translation part {shift}: dist {mh.Distance:G9} -> x {Ux(mh.Distance):G9}");
            }
            output.WriteLine(string.Join(" ", Enumerable.Range(-5, 11).Select(k => k * 4).Select(k => $"{k}:{Ux(BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(hit.Distance) + k)):G9}")));
        }
    }

    private sealed class Recorder(EditorTraceScene scene) : ILightTracer
    {
        public ConcurrentBag<(System.Numerics.Vector3 Start, System.Numerics.Vector3 End, EditorTraceScene.SceneHit? Hit, System.Numerics.Vector3 To)> Rays { get; } = [];

        public float? Trace(System.Numerics.Vector3 start, System.Numerics.Vector3 end) => throw new NotSupportedException();

        public LightTraceHit? Hit(System.Numerics.Vector3 start, System.Numerics.Vector3 end, uint mask)
        {
            var h = scene.Trace(start, end, mask);
            var d = end - start;
            var len = MathF.Sqrt((d.Y * d.Y + d.Z * d.Z) + d.X * d.X);
            var dist = h is { } x && x.Distance <= len ? x.Distance : len;
            Rays.Add((start, start + d / len * dist, h, end));
            return h is { } y ? new LightTraceHit(y.Distance, y.Flags) : null;
        }
    }

    [Theory]
    [InlineData("s2c_lighting", "ze_hold_em_p")]
    [InlineData("s2probe", "atixref")]
    public void PrecomputedKeysAgainstValve(string addon, string map)
    {
        // A diagnostic that prints and asserts nothing, and traces every
        // light's 24,576 samples: it runs with ETS_MAP=<map> or ETS_MAP=all.
        if (Environment.GetEnvironmentVariable("ETS_MAP") is not { } only || (only != map && only != "all"))
            return;
        if (MapFixtures.VmapSource(addon, map) is not { } source || MapFixtures.RcCompiledLumps(source) is not { } lumps
            || CS2Fixtures.StockPak() is not { } pak)
            return;
        var valve = new Dictionary<string, EntityLumpComparison.Entity>();
        foreach (var (path, bytes) in lumps)
            foreach (var e in EntityLumpComparison.Read(bytes, path))
                if (e.ClassName.StartsWith("light_", StringComparison.Ordinal))
                    valve[e.HammerId] = e;

        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        var document = DmxBinary.ReadFile(source);
        var scene = new EditorTraceScene(EditorTraceScene.MapMeshInstances(document, m => TraceScene.MaterialFlags(content.Material(m))));
        output.WriteLine($"scene: {scene.Instances.Count} instances, {scene.Instances.Sum(i => i.Records.Length / 22)} triangles");

        var walked = MapEntities.From(document);
        var (copies, templates) = MapInstances.Expand(document, walked,
            SmartProps.NodesCreatedOnLoad(document, MapFixtures.SmartPropLocators));
        var lights = walked.Where((_, i) => !templates.Contains(i))
            .Concat(copies.Select(c => walked[c.Template] with { NodeId = c.NodeId, Origin = c.Origin, Angles = c.Angles, Instanced = true }))
            .Where(e => !e.Hidden && e.ClassName is "light_barn" or "light_omni2")
            .ToList();
        var schema = MapFixtures.GameSchema();
        var results = new ConcurrentDictionary<int, (List<KeyValuePair<string, string>>? Keys, string? Error)>();
        Parallel.ForEach(lights, e =>
        {
            string? Key(string name) => e.Keys.FirstOrDefault(k => k.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value
                                        ?? schema?.KeyOf(e.ClassName, name)?.Default;
            try
            {
                results[e.NodeId] = (LightPrecompute.Keys(e.ClassName, Key, LightPrecompute.World(e.Origin, e.Angles), scene), null);
            }
            catch (NotSupportedException ex)
            {
                results[e.NodeId] = (null, ex.Message);
            }
        });
        int exact = 0, compared = 0, keysSame = 0, keysAll = 0;
        var lines = new List<string>();
        foreach (var e in lights)
        {
            var id = e.NodeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!valve.TryGetValue(id, out var theirs))
                continue;
            var (ours, error) = results[e.NodeId];
            if (ours is null)
            {
                lines.Add($"{e.ClassName}#{id}: {error}");
                continue;
            }
            compared++;
            var same = true;
            foreach (var (k, v) in ours)
            {
                keysAll++;
                var t = theirs.Values.TryGetValue(k, out var tv) ? tv.Value.Split(':', 2)[^1] : null;
                if (t == v)
                    keysSame++;
                else
                {
                    same = false;
                    lines.Add($"{e.ClassName}#{id}{(e.Instanced ? " (inst)" : "")} {k}: valve {t ?? "(none)"} ours {v}");
                }
            }
            foreach (var k in theirs.Values.Keys.Where(k => k.StartsWith("precomputed", StringComparison.Ordinal) && k != "precomputed_vis_clusters" && ours.All(o => o.Key != k)))
            {
                same = false;
                lines.Add($"{e.ClassName}#{id} {k}: valve {theirs.Values[k].Value} ours (none)");
            }
            if (same)
                exact++;
        }
        var summary = $"{map}: {compared} lights, {exact} exact, keys {keysSame}/{keysAll}";
        output.WriteLine(summary);
        foreach (var l in lines.Take(40))
            output.WriteLine(l);
        if (Environment.GetEnvironmentVariable("ETS_DUMP") is { } dump)
            File.WriteAllLines(Path.Combine(dump, $"{map}.ets.txt"), [summary, .. lines]);

        // The shadow slot assignment run on our own precomputed keys (the
        // compiles with baked lighting only; atixref's did not run it).
        if (map != "ze_hold_em_p")
            return;
        var all = walked.Where((_, i) => !templates.Contains(i))
            .Concat(copies.Select(c => walked[c.Template] with { NodeId = c.NodeId, Origin = c.Origin, Angles = c.Angles, Instanced = true }))
            .Where(e => !e.Hidden && e.ClassName.StartsWith("light_", StringComparison.Ordinal))
            .ToList();
        var assigned = BakedShadowAssignment.Assign([.. all.Select(e =>
        {
            var ours = results.TryGetValue(e.NodeId, out var r) ? r.Keys : null;
            string? Key(string name) => name.StartsWith("precomputed", StringComparison.Ordinal)
                ? ours?.FirstOrDefault(k => k.Key == name).Value
                : e.Keys.FirstOrDefault(k => k.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value ?? schema?.KeyOf(e.ClassName, name)?.Default;
            return new BakedShadowAssignment.Light(e.ClassName, Key, LightPrecompute.World(e.Origin, e.Angles), [e.NodeId]);
        })], "maps/" + map + ".vmap");
        int slotKeys = 0, slotSame = 0;
        for (var i = 0; i < all.Count; i++)
            foreach (var (k, v) in assigned[i].Keys)
            {
                if (k == "light_map_uniqueid")
                    continue;
                slotKeys++;
                var id = all[i].NodeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (valve.TryGetValue(id, out var t) && t.Values.TryGetValue(k, out var tv) && tv.Value.Split(':', 2)[^1] == v)
                    slotSame++;
            }
        output.WriteLine($"shadow slots on our keys: {slotSame}/{slotKeys} keys (light_map_uniqueid left out)");
    }
}

public sealed class EditorTraceSceneMeshProbe(ITestOutputHelper output)
{
    [Fact]
    public void Mesh()
    {
        if (Environment.GetEnvironmentVariable("ETS_MESH") is not { } spec)
            return;
        var p = spec.Split('|');
        var source = MapFixtures.VmapSource(p[0], p[1])!;
        var doc = DmxBinary.ReadFile(source);
        foreach (var m in MapMeshes.Read(doc).Where(m => p[2].Split(',').Contains(m.NodeId.ToString(System.Globalization.CultureInfo.InvariantCulture))))
        {
            var el = m.Element!;
            output.WriteLine($"{m.NodeId} parent {m.ParentType} {m.ParentClass} origin {m.Origin.X:G9} {m.Origin.Y:G9} {m.Origin.Z:G9} angles {m.Angles.X:G9} {m.Angles.Y:G9} {m.Angles.Z:G9} scales {m.Scales} path {string.Join(" ", m.Path)} faces {m.Faces.Length}");
            foreach (var (k, v) in el.Attributes)
                if (v is not DmxBinary.Element && v is not object?[])
                    output.WriteLine($"   {k} = {v}");
        }
    }
}
