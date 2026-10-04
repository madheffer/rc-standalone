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
        foreach (var (i, point, hit, free) in ends)
        {
            var what = hit is { } h ? $"{scene.Instances[h.Instance].Source} tri {h.Triangle} flags {h.Flags:x}" : "no hit";
            output.WriteLine($"ray {i}: end {point.X:F4} {point.Y:F4} {point.Z:F4} (free {free.X:F2} {free.Y:F2} {free.Z:F2}) {what} dist {hit?.Distance:F4}");
        }
    }
}
