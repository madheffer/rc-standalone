using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: a .vmap's prop_static entities, every attribute of their
/// elements and entity properties, with a tally of the keys across all of
/// them. <c>PROPPROBE=&lt;.vmap&gt;</c>, <c>PROPPROBE_SHOW</c> (default 3).
/// </summary>
public class StaticPropProbe(ITestOutputHelper output)
{
    [Fact]
    public void Props()
    {
        if (Environment.GetEnvironmentVariable("PROPPROBE") is not { Length: > 0 } path)
            return;
        var show = int.TryParse(Environment.GetEnvironmentVariable("PROPPROBE_SHOW"), out var s) ? s : 3;
        var doc = DmxBinary.ReadFile(path);
        var keys = new Dictionary<string, int>();
        var values = new Dictionary<string, Dictionary<string, int>>();
        var count = 0;
        foreach (var e in doc.Elements.Where(e => e.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname") == "prop_static"))
        {
            count++;
            var props = e.Get<DmxBinary.Element>("entity_properties")!;
            foreach (var (k, v) in e.Attributes.Concat(props.Attributes.Select(kv => new KeyValuePair<string, object?>("kv." + kv.Key, kv.Value))))
            {
                keys[k] = keys.GetValueOrDefault(k) + 1;
                if (k is "kv.solid" or "kv.scales" or "kv.modelscale" or "scales")
                {
                    values.TryAdd(k, []);
                    var text = v?.ToString() ?? "null";
                    values[k][text] = values[k].GetValueOrDefault(text) + 1;
                }
            }
            if (count <= show)
            {
                output.WriteLine($"prop {e.Type} {e.Name}:");
                foreach (var (k, v) in e.Attributes)
                    output.WriteLine($"  {k} = {Describe(v)}");
                foreach (var (k, v) in props.Attributes)
                    output.WriteLine($"  kv {k} = {Describe(v)}");
            }
        }
        output.WriteLine($"{count} prop_static");
        foreach (var (k, n) in keys.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            output.WriteLine($"key {k}: {n}");
        foreach (var (k, vs) in values)
            output.WriteLine($"values {k}: {string.Join(", ", vs.OrderByDescending(kv => kv.Value).Take(12).Select(kv => $"{kv.Key} x{kv.Value}"))}");
    }

    private static string Describe(object? v) => v switch
    {
        null => "null",
        DmxBinary.Element el => $"<{el.Type} {el.Name}>",
        object?[] arr => $"[{arr.Length}] {string.Join(" ", arr.Take(4))}",
        _ => v.ToString() ?? "",
    };
}

/// <summary>Exploration: element type counts of a .vmap, and the attributes of its reference-like elements. <c>VMAPTYPES=&lt;.vmap&gt;</c>.</summary>
public class VmapTypesProbe(ITestOutputHelper output)
{
    [Fact]
    public void Types()
    {
        if (Environment.GetEnvironmentVariable("VMAPTYPES") is not { Length: > 0 } path)
            return;
        var doc = DmxBinary.ReadFile(path);
        foreach (var g in doc.Elements.GroupBy(e => e.Type).OrderByDescending(g => g.Count()))
            output.WriteLine($"{g.Key}: {g.Count()}");
        foreach (var e in doc.Elements.Where(e => e.Type is "CMapPrefab" or "CMapInstance").Take(3))
            foreach (var (k, v) in e.Attributes)
                output.WriteLine($"  {e.Type} {k} = {v}");
    }
}

/// <summary>
/// Exploration: a compiled model's physics tables (collision attributes,
/// surface properties) and each shape's indices into them, part by part.
/// <c>PHYSTABLES=&lt;.vpk&gt;|&lt;entry&gt;</c> (entry in the vpk, or a model path in pak01 with vpk "pak01").
/// </summary>
public class PhysTablesProbe(ITestOutputHelper output)
{
    [Fact]
    public void Tables()
    {
        if (Environment.GetEnvironmentVariable("PHYSTABLES") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        var vpk = p[0] == "pak01" ? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\pak01_dir.vpk" : p[0];
        using var package = new ValvePak.Package();
        package.Read(vpk);
        package.ReadEntry(package.FindEntry(p[1])!, out var bytes);
        using var resource = new ValveResourceFormat.Resource();
        resource.Read(new MemoryStream(bytes));
        var phys = ((ValveResourceFormat.ResourceTypes.Model)resource.DataBlock!).GetEmbeddedPhys()!;
        var data = phys.Data;
        foreach (var key in new[] { "m_collisionAttributes", "m_surfacePropertyHashes", "m_boneNames", "m_indexNames", "m_indexHash", "m_bindPose" })
            if (data.ContainsKey(key) && key != "m_bindPose")
                output.WriteLine($"{key}: {string.Join(" ", data[key]!.ToKV3String().Split((char)10).Select(x => x.Trim()))}");
        var i = 0;
        foreach (var part in phys.Parts)
        {
            var shape = part.Shape;
            output.WriteLine($"part {i++}: hulls {shape.Hulls.Length} meshes {shape.Meshes.Length} spheres {shape.Spheres.Length} capsules {shape.Capsules.Length}");
            output.WriteLine("  hull attr/surf: " + string.Join(" ", shape.Hulls.Select(h => $"{h.CollisionAttributeIndex}/{h.SurfacePropertyIndex}")));
            output.WriteLine("  mesh attr/surf: " + string.Join(" ", shape.Meshes.Select(h => $"{h.CollisionAttributeIndex}/{h.SurfacePropertyIndex}")));
        }
    }
}

/// <summary>Exploration: the entities of a map (instances included) whose prop model has mesh, sphere or capsule collision. <c>PROPMESHES=&lt;addon&gt;|&lt;map&gt;</c>.</summary>
public class PropMeshesProbe(ITestOutputHelper output)
{
    [Fact]
    public void Meshes()
    {
        if (Environment.GetEnvironmentVariable("PROPMESHES") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        var cs2 = @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        using var models = new SettleBuildTests.PakModels(Path.Combine(cs2, "game", "csgo", "pak01_dir.vpk"), Path.Combine(cs2, "game", "csgo_addons", p[0]));
        var doc = DmxBinary.ReadFile(Path.Combine(cs2, "content", "csgo_addons", p[0], "maps", p[1] + ".vmap"));
        foreach (var entity in Source2.Compiler.Maps.MapMeshes.ReadWithEntities(doc).Entities)
        {
            var kv = entity.Element.Get<DmxBinary.Element>("entity_properties");
            var model = kv?.Get<string>("model");
            if (model == null || models.Physics(model) is not { } phys)
                continue;
            int meshes = phys.Parts.Sum(x => x.Shape.Meshes.Length), spheres = phys.Parts.Sum(x => x.Shape.Spheres.Length), capsules = phys.Parts.Sum(x => x.Shape.Capsules.Length);
            if (meshes + spheres + capsules == 0)
                continue;
            var prop = Source2.Compiler.Physics.WorldCollision.PropOf(entity);
            output.WriteLine($"{kv!.Get<string>("classname")} node {prop.NodeId} solid {kv.Get<string>("solid")} seq {entity.Sequence} {model} at {prop.Origin}: meshes {meshes} [{string.Join(",", phys.Parts.SelectMany(x => x.Shape.Meshes).Select(m => $"{m.Shape.GetVertices().Length}v/{m.Shape.Materials?.Length ?? 0}mat"))}] spheres {spheres} capsules {capsules} instances {entity.Instances.Length}");
        }
    }
}

/// <summary>Exploration: a .vmap's node tree, depth first in stored order, runs of same-kind siblings collapsed. <c>VMAPTREE=&lt;.vmap&gt;</c>, <c>VMAPTREE_DEPTH</c>.</summary>
public class VmapTreeProbe(ITestOutputHelper output)
{
    [Fact]
    public void Tree()
    {
        if (Environment.GetEnvironmentVariable("VMAPTREE") is not { Length: > 0 } path)
            return;
        var maxDepth = int.TryParse(Environment.GetEnvironmentVariable("VMAPTREE_DEPTH"), out var d) ? d : 3;
        var doc = DmxBinary.ReadFile(path);
        string Label(DmxBinary.Element e) => e.Type == "CMapEntity" ? $"entity:{e.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname")}" : e.Type;
        void Walk(DmxBinary.Element node, int depth)
        {
            var kids = node.GetElements("children").ToList();
            for (var i = 0; i < kids.Count;)
            {
                var label = Label(kids[i]);
                var j = i;
                while (j < kids.Count && Label(kids[j]) == label && kids[j].GetElements("children").Count() == 0)
                    j++;
                if (j - i > 1)
                {
                    output.WriteLine($"{new string(' ', depth * 2)}{label} x{j - i} (ids {kids[i].GetValue<int>("nodeID")}..{kids[j - 1].GetValue<int>("nodeID")})");
                    i = j;
                    continue;
                }
                var k = kids[i];
                output.WriteLine($"{new string(' ', depth * 2)}{label} {k.GetValue<int>("nodeID")} children {k.GetElements("children").Count()}{(k.Get<DmxBinary.Element>("target") is { } t ? $" target {t.GetValue<int>("nodeID")}" : "")}");
                if (depth < maxDepth)
                    Walk(k, depth + 1);
                i++;
            }
        }
        foreach (var w in doc.OfType("CMapWorld"))
            Walk(w, 0);
    }
}

/// <summary>Exploration: every attribute of the nodes with given ids, nested elements one level down. <c>VMAPNODE=&lt;.vmap&gt;|id,id,...</c>.</summary>
public class VmapNodeProbe(ITestOutputHelper output)
{
    [Fact]
    public void Node()
    {
        if (Environment.GetEnvironmentVariable("VMAPNODE") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        var ids = p[1].Split(',').Select(int.Parse).ToHashSet();
        var doc = DmxBinary.ReadFile(p[0]);
        foreach (var e in doc.Elements.Where(e => e.GetValue<int>("nodeID") is { } id && ids.Contains(id)))
        {
            output.WriteLine($"{e.Type} {e.GetValue<int>("nodeID")}:");
            foreach (var (k, v) in e.Attributes)
            {
                output.WriteLine($"  {k} = {(v is object?[] a ? $"[{a.Length}] " + string.Join(" ", a.Take(6)) : v)}");
                if (v is DmxBinary.Element sub)
                    foreach (var (k2, v2) in sub.Attributes.Take(40))
                        output.WriteLine($"    {k2} = {(v2 is object?[] a2 ? $"[{a2.Length}] " + string.Join(" ", a2.Take(6)) : v2)}");
            }
        }
    }
}

/// <summary>Exploration: an element tree under a node's attribute, recursively. <c>VMAPDEEP=&lt;.vmap&gt;|id|attribute|depth</c>.</summary>
public class VmapDeepProbe(ITestOutputHelper output)
{
    [Fact]
    public void Deep()
    {
        if (Environment.GetEnvironmentVariable("VMAPDEEP") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        var doc = DmxBinary.ReadFile(p[0]);
        var node = doc.Elements.First(e => e.GetValue<int>("nodeID") == int.Parse(p[1]));
        var max = int.Parse(p[3]);
        var seen = new HashSet<DmxBinary.Element>(ReferenceEqualityComparer.Instance);
        void Dump(object? v, string name, int depth)
        {
            var pad = new string(' ', depth * 2);
            switch (v)
            {
                case DmxBinary.Element e:
                    output.WriteLine($"{pad}{name}: <{e.Type} \"{e.Name}\">");
                    if (depth < max && seen.Add(e))
                        foreach (var (k, x) in e.Attributes)
                            Dump(x, k, depth + 1);
                    break;
                case object?[] a:
                    output.WriteLine($"{pad}{name}: [{a.Length}]");
                    for (var i = 0; i < a.Length && i < 40; i++)
                        Dump(a[i], $"[{i}]", depth + 1);
                    break;
                default:
                    output.WriteLine($"{pad}{name} = {v}");
                    break;
            }
        }
        Dump(node.Attributes[p[2]], p[2], 0);
    }
}

/// <summary>Exploration: mesh nodes' transform, face count and subdivision. <c>MESHINFO=&lt;.vmap&gt;|id,id,...</c>.</summary>
public class MeshInfoProbe(ITestOutputHelper output)
{
    [Fact]
    public void Info()
    {
        if (Environment.GetEnvironmentVariable("MESHINFO") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        var ids = p[1].Split(',').Select(int.Parse).ToHashSet();
        var doc = DmxBinary.ReadFile(p[0]);
        foreach (var e in doc.OfType("CMapMesh").Where(e => e.GetValue<int>("nodeID") is { } id && ids.Contains(id)))
        {
            var data = e.Get<DmxBinary.Element>("meshData")!;
            var levels = data.Get<DmxBinary.Element>("subdivisionData")?.Get<object?[]>("subdivisionLevels") ?? [];
            var faces = (data.Get<object?[]>("faceEdgeIndices") ?? []).Length;
            output.WriteLine($"mesh {e.GetValue<int>("nodeID")}: origin {e.GetValue<System.Numerics.Vector3>("origin")} angles {e.GetValue<System.Numerics.Vector3>("angles")} scales {e.GetValue<System.Numerics.Vector3>("scales")} faces {faces} subdivided {levels.Count(x => x is int i && i > 0)} physicsType {e.Get<string>("physicsType")}");
        }
    }
}
