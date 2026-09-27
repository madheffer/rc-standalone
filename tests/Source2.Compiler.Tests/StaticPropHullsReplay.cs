using System.Numerics;
using Source2.Compiler.Physics;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The hulls our <see cref="StaticPropHulls"/> makes for a map's solid
/// prop_static entities, matched by vertex set to the hulls of the map's
/// shipped world_physics.vmdl_c and compared field by field.
/// <c>PROPHULLS=&lt;addon&gt;|&lt;map&gt;</c>, <c>PROPHULLS_SHOW</c>.
/// </summary>
public class StaticPropHullsReplay(ITestOutputHelper output)
{
    [Fact]
    public void AgainstWorldPhysics()
    {
        if (Environment.GetEnvironmentVariable("PROPHULLS") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var show = int.TryParse(Environment.GetEnvironmentVariable("PROPHULLS_SHOW"), out var s) ? s : 10;
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        var game = Path.Combine(cs2, "game");
        var vmap = Path.Combine(cs2, "content", "csgo_addons", parts[0], "maps", parts[1] + ".vmap");
        using var models = new SettleBuildTests.PakModels(Path.Combine(game, "csgo", "pak01_dir.vpk"), Path.Combine(game, "csgo_addons", parts[0]));

        using var package = new Package();
        package.Read(Path.Combine(game, "csgo_addons", parts[0], "maps", parts[1] + ".vpk"));
        package.ReadEntry(package.FindEntry($"maps/{parts[1]}/world_physics.vmdl_c")!, out var bytes);
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        var shipped = ((Model)resource.DataBlock!).GetEmbeddedPhys()?.Parts.SelectMany(p => p.Shape.Hulls).Select(h => h.Shape).ToList() ?? [];
        output.WriteLine($"shipped world_physics: {shipped.Count} hulls");

        var doc = DmxBinary.ReadFile(vmap);
        var tally = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Count(string key) => tally[key] = tally.GetValueOrDefault(key) + 1;
        var pool = Enumerable.Range(0, shipped.Count).ToList();
        var shown = 0;
        foreach (var e in doc.Elements.Where(e => e.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname") == "prop_static"))
        {
            var kv = e.Get<DmxBinary.Element>("entity_properties")!;
            if (int.TryParse(kv.Get<string>("solid") ?? "6", out var solid) && solid != 6)
                continue;
            var model = kv.Get<string>("model") ?? "";
            if (models.Physics(model) is not { } phys)
            {
                Count("model without physics");
                continue;
            }
            var prop = new StaticPropHulls.Prop(e.GetValue<int>("nodeID") ?? 0, model, e.GetValue<Vector3>("origin") ?? Vector3.Zero,
                e.GetValue<Vector3>("angles") ?? Vector3.Zero, e.GetValue<Vector3>("scales") ?? Vector3.One);
            foreach (var node in StaticPropHulls.Nodes(prop, phys))
            {
                var hull = StaticPropHulls.Shape(node);
                if (hull == null)
                {
                    Count("dropped");
                    continue;
                }
                var set = hull.VertexPositions.ToHashSet();
                var at = pool.FindIndex(i => shipped[i].GetVertexPositions().Length == set.Count && shipped[i].GetVertexPositions().ToArray().All(set.Contains));
                if (at < 0)
                {
                    Count("no shipped hull with its vertices");
                    if (shown++ < show)
                    {
                        var c = hull.Centroid;
                        var near = pool.OrderBy(i => Vector3.Distance(shipped[i].Centroid, c)).FirstOrDefault(-1);
                        output.WriteLine($"node {prop.NodeId} {Path.GetFileName(model)} part {node.Part}: ours {hull.VertexPositions.Length} verts centroid {c}" +
                            (near < 0 ? "" : $"; nearest shipped {shipped[near].GetVertexPositions().Length} verts centroid {shipped[near].Centroid}, first ours {hull.VertexPositions[0]} vs {shipped[near].GetVertexPositions()[0]}"));
                    }
                    continue;
                }
                var index = pool[at];
                pool.RemoveAt(at);
                var diff = HullFromVmap.Differences(shipped[index], hull);
                var svm = HullFromVmap.SvmDifference(shipped[index], hull);
                Count(diff.Count == 0 ? "hull exact" : "hull differs: " + diff[0].Split(' ')[0]);
                Count("svm " + (svm ?? "exact").Split(' ')[0]);
                if (svm != null && shown++ < show)
                {
                    output.WriteLine($"node {prop.NodeId} {Path.GetFileName(model)} part {node.Part} svm: {svm}");
                    if (shown == 1)
                    {
                        // The planes before the move, and Valve's after it.
                        var raw = RnHullBuilder.Create(BrushHulls.ShapePoints(node.Points)!, RnHullBuilder.Options.Compile, out _)!;
                        var pre = RegionSvmBuilder.Build(raw)!;
                        var valvePlanes = shipped[index].RegionSVM!.Data.GetArray<byte>("m_Planes");
                        for (var i = 0; i < Math.Min(6, pre.Planes.Length); i++)
                        {
                            var v = Enumerable.Range(0, 4).Select(k => BitConverter.ToSingle(valvePlanes, (i * 16) + (k * 4)).ToString("R")).ToArray();
                            output.WriteLine($"  plane {i}: before {pre.Planes[i].Normal.X:R},{pre.Planes[i].Normal.Y:R},{pre.Planes[i].Normal.Z:R},{pre.Planes[i].Offset:R} ours after {hull.RegionSvm!.Planes[i].Normal.X:R},{hull.RegionSvm.Planes[i].Normal.Y:R},{hull.RegionSvm.Planes[i].Normal.Z:R},{hull.RegionSvm.Planes[i].Offset:R} valve {string.Join(",", v)}");
                        }
                        var t = new Source2.Compiler.Maps.CTransform(node.Origin, 1f, Source2.Compiler.Maps.CTransform.AngleQuaternion(node.Angles)).Matrix();
                        output.WriteLine($"  matrix {string.Join(",", t.Select(x => x.ToString("R")))} angles {node.Angles}");
                    }
                }
                if (diff.Count > 0 && shown++ < show)
                    output.WriteLine($"node {prop.NodeId} {Path.GetFileName(model)} part {node.Part}: {string.Join("; ", diff.Take(6))}");
            }
        }
        output.WriteLine($"shipped hulls left unmatched: {pool.Count}");
        foreach (var (k, v) in tally)
            output.WriteLine($"{k}: {v}");
    }
}
