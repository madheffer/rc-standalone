using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>MERGEFACTS=&lt;merger capture&gt;</c>): the values every
/// <see cref="WrbMeshEntry"/> field takes over the captured merger inputs,
/// per call, to see which a .vmap build must derive and which are constant.
/// </summary>
public class MergerFactsProbe(ITestOutputHelper output)
{
    [Fact]
    public void Distinct()
    {
        if (Environment.GetEnvironmentVariable("MERGEFACTS") is not { } path)
            return;
        var data = File.ReadAllBytes(path);
        var values = new SortedDictionary<string, Dictionary<string, int>>();
        void Add(string field, object? v)
        {
            if (!values.TryGetValue(field, out var d))
                values[field] = d = [];
            var key = v switch { float[] a => string.Join(",", a.Select(x => x.ToString("R"))), System.Collections.IEnumerable e and not string => string.Join(",", e.Cast<object>()), _ => v?.ToString() ?? "null" };
            d[key] = d.GetValueOrDefault(key) + 1;
        }
        for (var at = 0; at < data.Length;)
        {
            var n = BitConverter.ToInt32(data, at);
            var head = JsonDocument.Parse(data.AsMemory(at + 4, n)).RootElement;
            at += 4 + n;
            var m = BitConverter.ToInt32(data, at);
            var blob = data.AsSpan(at + 4, m).ToArray();
            at += 4 + m;
            if (head.GetProperty("ev").GetString() != "in" || !head.TryGetProperty("meshRaw", out var raw))
                continue;
            var call = head.GetProperty("call").GetInt32();
            var mesh = Convert.FromHexString(raw.GetString()!);
            var streams = head.GetProperty("streams").EnumerateArray().Select(x => $"{x.GetProperty("name").GetString()}/{x.GetProperty("index").GetInt32()}/{x.GetProperty("count").GetInt32()}/{x.GetProperty("precise").GetInt32()}/{x.GetProperty("type").GetInt32()}");
            var floats = head.GetProperty("floats").EnumerateArray().Select(x => x.GetSingle()).ToArray();
            var f = WrbMeshEntry.FromBytes(blob.AsSpan(0, 0x238), mesh, "", floats, head.GetProperty("entryName").GetString() ?? "", []);
            var c = $"c{call} ";
            Add(c + "overlayOrder", f.OverlayOrder);
            Add(c + "objectFlags", $"{f.ObjectFlags:x}");
            Add(c + "debugColor", f.DebugColor);
            if (f.DebugColor)
                Add(c + "debugColorValue", f.DebugColorValue);
            Add(c + "streams", string.Join(" ", streams));
            Add(c + "mesh58", f.Mesh58);
            Add(c + "meshFloats", floats);
            Add(c + "1a3", f.Field1a3);
            Add(c + "b8", f.Field_b8);
            Add(c + "1a5", f.Field1a5);
            Add(c + "1a8", f.Field1a8);
            Add(c + "cubemap", f.Cubemap);
            Add(c + "lightProbe", f.LightProbe);
            Add(c + "mesh184", f.Mesh184);
            Add(c + "9c", f.Field9c);
            Add(c + "1a1", f.Field1a1);
            Add(c + "1a0", f.Field1a0);
            Add(c + "b0", f.Field_b0);
            Add(c + "fadeMax", f.FadeMax);
            Add(c + "name", f.Name);
            Add(c + "matrix", f.Matrix);
            Add(c + "mesh152", f.Mesh152);
            Add(c + "field28", f.Field28);
        }
        foreach (var (field, d) in values)
            output.WriteLine($"{field}: {string.Join(" | ", d.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key} x{kv.Value}"))}{(d.Count > 6 ? $" (+{d.Count - 6})" : "")}");
    }
}

/// <summary>Exploration (<c>MATFLAGS=&lt;material&gt;</c>): a material's entry flags and representative texture size under a plain world record.</summary>
public class MaterialFlagsProbe(ITestOutputHelper output)
{
    [Fact]
    public void Flags()
    {
        if (Environment.GetEnvironmentVariable("MATFLAGS") is not { } material || CS2Fixtures.StockPak() is not { } pak)
            return;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = material.Contains('|') ? material.Split('|')[0] : "s2c_rc_probe";
        material = material.Contains('|') ? material.Split('|')[1] : material;
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_pc_dir.vpk"), Path.Combine(game, "core", "shaders_pc_dir.vpk"));
        var info = content.Material(material);
        output.WriteLine(info is null ? "not found" : $"shader {info.Shader}");
        var attributes = info is null ? MaterialAttributes.Empty : MaterialAttributes.Of(info, shaders, content.TextureSize);
        var flags = MeshEntryFlags.Compute(attributes, new MeshEntryFlags.Record(0, 0, 0, false, null, false));
        output.WriteLine($"flags {flags.Flags:x} size {flags.Width}x{flags.Height}, trace flags {TraceScene.MaterialFlags(info):x}");
        if (info is not null)
            output.WriteLine($"ints {string.Join(" ", info.Ints.Select(kv => $"{kv.Key}={kv.Value}"))}");
    }
}

/// <summary>Exploration (<c>PREFABCOUNT=&lt;vmap&gt;</c>): each prefab's map node count, smart props and the node types it holds.</summary>
public class PrefabCountProbe(ITestOutputHelper output)
{
    [Fact]
    public void Count()
    {
        if (Environment.GetEnvironmentVariable("PREFABCOUNT") is not { } vmap)
            return;
        var doc = MapSource.Read(vmap);
        static int Nodes(DmxBinary.Element g) => g.GetElements("children").Sum(c => 1 + Nodes(c));
        foreach (var prefab in doc.OfType("CMapPrefab"))
        {
            var world = prefab.Get<DmxBinary.Element>(MapPrefabs.WorldKey);
            var loaded = prefab.Attributes.GetValueOrDefault(MapPrefabs.DocumentKey);
            output.WriteLine($"prefab {prefab.GetValue<int>("nodeID")}: tree {(world is null ? -1 : Nodes(world))}, document {loaded?.GetType().Name}");
            if (world is not null && Environment.GetEnvironmentVariable("PREFABSLOTS") is { } range)
            {
                var r = range.Split(',').Select(int.Parse).ToArray();
                var slot = 0;
                void Walk(DmxBinary.Element g, int depth)
                {
                    foreach (var c in g.GetElements("children"))
                    {
                        var at = slot++;
                        if (at >= r[0] && at < r[1])
                            output.WriteLine($"  slot {at} id {2142 + at}: {new string(' ', depth)}{c.Type} {c.GetValue<int>("nodeID")} {c.Get<DmxBinary.Element>("target")?.GetValue<int>("nodeID")} children {c.GetElements("children").Count()}");
                        Walk(c, depth + 1);
                    }
                }
                Walk(world, 0);
            }
            if (loaded is DmxBinary.Document d)
            {
                output.WriteLine($"  smart props {d.OfType("CMapSmartProp").Count()}, max id {d.Elements.Max(e => e.GetValue<int>("nodeID") ?? 0)}");
                foreach (var g in d.Elements.Where(e => e.GetValue<int>("nodeID") is not null).GroupBy(e => e.Type))
                    output.WriteLine($"  {g.Key} {g.Count()}");
            }
        }
    }
}

/// <summary>
/// Exploration (<c>PREFABLIGHT=&lt;map&gt;|&lt;light id path&gt;</c>): one light's
/// precomputed keys traced against the editor scene with parts of the prefabs'
/// contents left out, to find what Valve's scene holds there.
/// </summary>
public class PrefabLightProbe(ITestOutputHelper output)
{
    [Fact]
    public void Variants()
    {
        if (Environment.GetEnvironmentVariable("PREFABLIGHT") is not { } spec || CS2Fixtures.StockPak() is not { } pak)
            return;
        var p = spec.Split('|');
        if (p.Length == 2)
            p = ["s2c_rc_probe", p[0], p[1]];
        var source = MapFixtures.VmapSource(p[0], p[1])!;
        var document = MapSource.Read(source);
        p = [p[1], p[2]];
        var schema = MapFixtures.GameSchema();
        var light = MapEntities.From(document).First(e => e.IdPath == p[1]);
        var table = EntityLumpAuthor.KeyTable(light, schema);
        string? Key(string name) => table.FirstOrDefault(k => k.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        using var models = new SettleBuildTests.PakModels(pak, Path.Combine(game, "csgo_addons", Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(source)))!));
        ushort Flags(string m) => TraceScene.MaterialFlags(models.Material(m));
        var (meshes, entities) = MapMeshes.ReadWithEntities(document);
        var variants = new (string Name, Func<MapMeshes.Mesh, bool> Mesh, Func<MapMeshes.EntityNode, bool> Prop)[]
        {
            ("all", _ => true, _ => true),
            ("no prefab props", _ => true, n => n.PrefabChain.Count == 0),
            ("no prefab instance meshes", m => !(m.PrefabChain.Count > 0 && m.Through.Count > 0), _ => true),
            ("no prefab instance props", _ => true, n => !(n.PrefabChain.Count > 0 && n.Through.Count > 0)),
            ("no prefab instance meshes or props", m => !(m.PrefabChain.Count > 0 && m.Through.Count > 0), n => !(n.PrefabChain.Count > 0 && n.Through.Count > 0)),
            ("no hidden-free prefab meshes", m => m.PrefabChain.Count == 0, _ => true),
            ("prefab only", m => m.PrefabChain.Count > 0, n => n.PrefabChain.Count > 0),
            ("no layer-child meshes", m => m.ParentType != "CMapWorldLayer", _ => true),
            ("prefab meshes only, all props", m => m.PrefabChain.Count > 0, _ => true),
        };
        // Each prefab's own map world, unmoved (prefab-local), as a second copy.
        var extraMeshes = new List<MapMeshes.Mesh>();
        var extraProps = new List<MapMeshes.EntityNode>();
        foreach (var prefab in document.OfType("CMapPrefab"))
            if (prefab.Attributes.GetValueOrDefault(MapPrefabs.DocumentKey) is DmxBinary.Document loaded)
            {
                var (m2, e2) = MapMeshes.ReadWithEntities(loaded);
                extraMeshes.AddRange(m2);
                extraProps.AddRange(e2);
            }
        output.WriteLine($"prefab-local copy: {extraMeshes.Count} meshes, {extraProps.Count} entity nodes");
        variants = [.. variants, ("plus prefab-local copy", _ => true, _ => true)];
        foreach (var (name, meshOk, propOk) in variants)
        {
            var plus = name == "plus prefab-local copy";
            var scene = new EditorTraceScene([.. EditorTraceScene.MapMeshInstances(meshes.Where(meshOk).Concat(plus ? extraMeshes : []), Flags),
                                              .. EditorTraceScene.StaticPropInstances(entities.Where(propOk).Concat(plus ? extraProps : []), models, Flags)]);
            var keys = LightPrecompute.Keys(light.ClassName, Key, LightPrecompute.World(light.Origin, light.Angles), scene);
            var face = Environment.GetEnvironmentVariable("PREFABLIGHT_FACE") ?? "3";
            output.WriteLine($"{name}: " + string.Join("; ", keys.Where(k => k.Key.EndsWith(face)).Select(k => $"{k.Key}={k.Value}")));
        }
    }
}

/// <summary>Exploration (<c>MESHFACES=&lt;map&gt;|&lt;node id&gt;</c>): a map mesh's placement and its faces' materials and flags.</summary>
public class MeshFacesProbe(ITestOutputHelper output)
{
    [Fact]
    public void Faces()
    {
        if (Environment.GetEnvironmentVariable("MESHFACES") is not { } spec)
            return;
        var p = spec.Split('|');
        if (p.Length == 2)
            p = ["s2c_rc_probe", p[0], p[1]];
        var document = MapSource.Read(MapFixtures.VmapSource(p[0], p[1])!);
        foreach (var mesh in MapMeshes.Read(document).Where(m => m.NodeId == int.Parse(p[2])))
        {
            var node = mesh.Element!;
            output.WriteLine($"mesh {mesh.NodeId}: parent {mesh.ParentType}, through {mesh.Through.Count}, prefabs {mesh.PrefabChain.Count}, hidden {mesh.Hidden}, disableShadows {node.GetValue<int>("disableShadows")}");
            output.WriteLine($"  world {string.Join(" ", mesh.World.Select(v => v.ToString("F3")))}");
            var data = node.Get<DmxBinary.Element>("meshData");
            foreach (var kv in node.Attributes.Where(a => a.Value is string or int or bool or float))
                output.WriteLine($"  {kv.Key} = {kv.Value}");
            var materials = data?.Get<object?[]>("materials") ?? [];
            output.WriteLine($"  materials: {string.Join(", ", materials)}");
            object?[] Stream(string group, string name) => data?.Get<DmxBinary.Element>(group)?.GetElements("streams")
                .FirstOrDefault(st => st.Name.StartsWith(name + ":", StringComparison.Ordinal))?.Get<object?[]>("data") ?? [];
            var mi = Stream("faceData", "materialindex");
            var ff = Stream("faceData", "flags");
            output.WriteLine($"  faces {mi.Length}: by material {string.Join(", ", mi.GroupBy(x => x).Select(g => $"{g.Key}x{g.Count()}"))}; flags {string.Join(", ", ff.GroupBy(x => x).Select(g => $"{g.Key}x{g.Count()}"))}");
            output.WriteLine($"  subdivision {data?.Get<DmxBinary.Element>("subdivisionData") is not null}, streams: {string.Join(" ", data?.Attributes.Keys.ToArray() ?? [])}");
            if (data?.Get<DmxBinary.Element>("subdivisionData") is { } sd)
            {
                output.WriteLine($"  subdivision attributes: {string.Join(" ", sd.Attributes.Keys)}");
                output.WriteLine($"  levels: {string.Join(" ", sd.Get<object?[]>("subdivisionLevels") ?? [])}");
            }
            var (corners, faces) = MeshTessellation.RayScene(data!);
            output.WriteLine($"  ray scene triangles {faces.Count}");
            for (var t = 0; t < faces.Count; t++)
                output.WriteLine($"    tri {t} face {faces[t]}: {corners[3 * t]} {corners[3 * t + 1]} {corners[3 * t + 2]}");
            var edgeVertex = data!.Get<object?[]>("edgeVertexIndices") ?? [];
            var edgeNext = data.Get<object?[]>("edgeNextIndices") ?? [];
            var faceEdge = data.Get<object?[]>("faceEdgeIndices") ?? [];
            object?[] VStream(string name) => data.Get<DmxBinary.Element>("vertexData")?.GetElements("streams")
                .FirstOrDefault(st => st.Name.StartsWith(name + ":", StringComparison.Ordinal))?.Get<object?[]>("data") ?? [];
            var pos = VStream("position");
            var vdi = data.Get<object?[]>("vertexDataIndices") ?? [];
            for (var f = 0; f < faceEdge.Length; f++)
            {
                var e0 = (int)faceEdge[f]!;
                var e = e0;
                var loop = new List<string>();
                do
                {
                    var v = (int)edgeVertex[e]!;
                    loop.Add($"{pos[(int)vdi[v]!]}");
                    e = (int)edgeNext[e]!;
                } while (e != e0 && loop.Count < 100);
                output.WriteLine($"  face {f} loop ({loop.Count}): {string.Join(" ", loop)}");
            }
        }
    }
}

/// <summary>Exploration (<c>ENTKEYS=&lt;addon&gt;|&lt;map&gt;|&lt;id path&gt;|&lt;filter&gt;</c>): an entity's key table (source keys, then class defaults).</summary>
public class EntityKeysProbe(ITestOutputHelper output)
{
    [Fact]
    public void Keys()
    {
        if (Environment.GetEnvironmentVariable("ENTKEYS") is not { } spec)
            return;
        var p = spec.Split('|');
        var document = MapSource.Read(MapFixtures.VmapSource(p[0], p[1])!);
        var e = MapEntities.From(document).First(x => x.IdPath == p[2]);
        foreach (var kv in EntityLumpAuthor.KeyTable(e, MapFixtures.GameSchema()).Where(k => k.Key.Contains(p.Length > 3 ? p[3] : "", StringComparison.OrdinalIgnoreCase)))
            output.WriteLine($"key {kv.Key} = {kv.Value}{(e.Keys.Any(s => s.Key.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)) ? "" : "  (default)")}");
    }
}

/// <summary>Exploration (<c>SHADERDEFS=&lt;shader&gt;[,...]|&lt;name part&gt;[,...]</c>): the defaults a shader's programs declare for its variables.</summary>
public class ShaderDefaultsProbe(ITestOutputHelper output)
{
    [Fact]
    public void Defaults()
    {
        if (Environment.GetEnvironmentVariable("SHADERDEFS") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game", "csgo");
        using var package = new ValvePak.Package();
        package.Read(Path.Combine(game, "shaders_pc_dir.vpk"));
        foreach (var shader in parts[0].Split(',').SelectMany(x => new[] { x + "_pc_50_features", x + "_pc_50_vs", x + "_pc_50_ps" }))
        {
            var entry = package.FindEntry($"shaders/vfx/{shader}.vcs");
            if (entry == null)
                continue;
            package.ReadEntry(entry, out var bytes);
            using var program = new ValveResourceFormat.CompiledShader.VfxProgramData();
            program.Read($"{shader}.vcs", new MemoryStream(bytes));
            foreach (var v in program.VariableDescriptions.Where(v => parts[1].Split(',').Any(w => v.Name.Contains(w, StringComparison.OrdinalIgnoreCase))))
                output.WriteLine($"{shader} {v.Name} {v.VfxType} i[{string.Join(",", v.IntDefs)}] f[{string.Join(",", v.FloatDefs)}] src {v.VariableSource}");
        }
    }
}

/// <summary>Exploration (<c>MATFLAGS_CAPTURE=&lt;capture_matflags jsonl&gt;|&lt;addon&gt;</c>): every captured material's flag word against TraceScene.MaterialFlags.</summary>
public class MaterialFlagsCaptureProbe(ITestOutputHelper output)
{
    [Fact]
    public void AgainstCapture()
    {
        if (Environment.GetEnvironmentVariable("MATFLAGS_CAPTURE") is not { } spec || CS2Fixtures.StockPak() is not { } pak)
            return;
        var p = spec.Split('|');
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", p[1]));
        var valve = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(p[0]))
        {
            using var d = System.Text.Json.JsonDocument.Parse(line);
            if (!d.RootElement.TryGetProperty("name", out var n) || n.GetString() is not { } name)
                continue;
            if (!valve.TryGetValue(name, out var set))
                valve[name] = set = [];
            set.Add(d.RootElement.GetProperty("flags").GetInt32());
        }
        int same = 0, differ = 0;
        foreach (var (name, flags) in valve.OrderBy(kv => kv.Key))
        {
            var ours = TraceScene.MaterialFlags(content.Material(name));
            // The array is stored once before the material loads (0) and once after.
            var loaded = flags.Where(f => f != 0).DefaultIfEmpty(0).Max();
            if (flags.Contains(ours) && (ours == loaded || flags.Count == 1))
                same++;
            else
            {
                differ++;
                output.WriteLine($"{name}: valve {string.Join(",", flags.Select(f => $"0x{f:x}"))}, ours 0x{ours:x}");
            }
        }
        output.WriteLine($"materials {valve.Count}: {same} same, {differ} differ");
    }
}

/// <summary>Exploration (<c>NODETYPE=&lt;addon&gt;|&lt;map&gt;|&lt;element type&gt;</c>): every element of a type with its scalar attributes.</summary>
public class NodeTypeProbe(ITestOutputHelper output)
{
    [Fact]
    public void Attributes()
    {
        if (Environment.GetEnvironmentVariable("NODETYPE") is not { } spec)
            return;
        var p = spec.Split('|');
        var document = DmxBinary.ReadFile(MapFixtures.VmapSource(p[0], p[1])!);
        foreach (var e in document.OfType(p[2]))
            output.WriteLine($"{p[2]} {e.GetValue<int>("nodeID")}: " + string.Join(", ", e.Attributes.Where(a => a.Value is string or int or bool or float).Select(a => $"{a.Key}={a.Value}"))
                             + $"; children {e.GetElements("children").Count()}");
    }
}

/// <summary>Exploration (<c>TREE=&lt;addon&gt;|&lt;map&gt;|&lt;from id&gt;|&lt;to id&gt;</c>): the map's node tree in walk order, nodes in an id range with their depth and type.</summary>
public class MapTreeProbe(ITestOutputHelper output)
{
    [Fact]
    public void Tree()
    {
        if (Environment.GetEnvironmentVariable("TREE") is not { } spec)
            return;
        var p = spec.Split('|');
        var document = DmxBinary.ReadFile(MapFixtures.VmapSource(p[0], p[1])!);
        int lo = int.Parse(p[2]), hi = int.Parse(p[3]);
        var at = 0;
        void Walk(DmxBinary.Element node, int depth)
        {
            foreach (var c in node.GetElements("children"))
            {
                var id = c.GetValue<int>("nodeID") ?? -1;
                var cls = c.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname") ?? "";
                if (id >= lo && id <= hi || c.GetElements("children").Any(k => (k.GetValue<int>("nodeID") ?? -1) is var kid && kid >= lo && kid <= hi))
                    output.WriteLine($"{at} {new string(' ', depth * 2)}{c.Type} {id} {cls} {c.Get<DmxBinary.Element>("entity_properties")?.Get<string>("model")}");
                at++;
                Walk(c, depth + 1);
            }
        }
        Walk(document.OfType("CMapWorld").First(), 0);
    }
}

/// <summary>Survey (<c>SIMPLIFY=1</c>): every installed .vmap's CMapMesh nodes with physicsSimplificationOverride or a non-zero physicsSimplificationError.</summary>
public class PhysicsSimplificationSurvey(ITestOutputHelper output)
{
    [Fact]
    public void Survey()
    {
        if (Environment.GetEnvironmentVariable("SIMPLIFY") != "1")
            return;
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(CS2Fixtures.StockPak()!)!, "..", "..", "content", "csgo_addons"));
        foreach (var path in MapFixtures.VmapSources(long.MaxValue, int.MaxValue))
        {
            int meshes = 0, flagged = 0;
            try
            {
                var document = DmxBinary.ReadFile(path);
                foreach (var e in document.OfType("CMapMesh"))
                {
                    meshes++;
                    var over = e.Attributes.TryGetValue("physicsSimplificationOverride", out var o) ? o : null;
                    var err = e.Attributes.TryGetValue("physicsSimplificationError", out var r) ? r : null;
                    if (over is true || err is float f && f != 0)
                    {
                        flagged++;
                        output.WriteLine($"  {Path.GetFileName(path)} node {e.GetValue<int>("nodeID")}: override={over} error={err}");
                    }
                }
            }
            catch (Exception ex)
            {
                output.WriteLine($"{path}: {ex.Message}");
            }
            var multi = 0;
            try
            {
                foreach (var e in DmxBinary.ReadFile(path).OfType("CMapMesh"))
                    if (e.Get<string>("physicsType") == "convex_multi")
                        multi++;
            }
            catch (Exception)
            {
            }
            output.WriteLine($"{Path.GetRelativePath(root, path)}: {meshes} meshes, {flagged} flagged, {multi} convex_multi");
        }
    }
}

/// <summary>Exploration (<c>SUBDIVMESH=&lt;addon&gt;|&lt;map&gt;</c>): every CMapMesh with a subdivision level above 0, its parent chain and physics keys.</summary>
public class SubdividedMeshProbe(ITestOutputHelper output)
{
    [Fact]
    public void List()
    {
        if (Environment.GetEnvironmentVariable("SUBDIVMESH") is not { } spec)
            return;
        var p = spec.Split('|');
        var document = DmxBinary.ReadFile(MapFixtures.VmapSource(p[0], p[1])!);
        void Walk(DmxBinary.Element node, string chain)
        {
            foreach (var c in node.GetElements("children"))
            {
                var here = $"{chain}/{c.Type}:{c.GetValue<int>("nodeID")}";
                if (c.Type == "CMapMesh" && c.Get<DmxBinary.Element>("meshData")?.Get<DmxBinary.Element>("subdivisionData")?.Get<object?[]>("subdivisionLevels") is { } levels
                    && levels.Any(x => x is int i && i > 0))
                    output.WriteLine($"{here}: levels {string.Join(",", levels.Where(x => x is int i && i > 0).GroupBy(x => x).Select(g => $"{g.Key}x{g.Count()}"))}; "
                                     + string.Join(", ", c.Attributes.Where(a => a.Value is string or int or bool or float).Select(a => $"{a.Key}={a.Value}")));
                Walk(c, here);
            }
        }
        Walk(document.OfType("CMapWorld").First(), "");
        output.WriteLine(string.Join(", ", document.Elements.GroupBy(e => e.Type).OrderByDescending(g => g.Count()).Take(15).Select(g => $"{g.Key} {g.Count()}")));
        var all = document.OfType("CMapMesh").ToList();
        output.WriteLine($"{all.Count} meshes; with levels > 0: " + string.Join(" ", all.Where(c => c.Get<DmxBinary.Element>("meshData")?.Get<DmxBinary.Element>("subdivisionData")?.Get<object?[]>("subdivisionLevels") is { } l && l.Any(x => x is int i && i > 0)).Select(c => c.GetValue<int>("nodeID"))));
    }
}
