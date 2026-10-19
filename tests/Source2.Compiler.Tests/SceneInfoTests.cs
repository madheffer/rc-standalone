using Source2.Compiler.Lighting;
using Source2.Compiler.Maps;
using ValveKeyValue;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The path-trace scene resourcecompiler hands vrad3 (scene_info.kv3, scene_meshes.kv3;
/// WRB_BuildPathTraceSceneInfo 180285eb0).
/// </summary>
public class SceneInfoTests(ITestOutputHelper output)
{
    internal static KVObject ReadBinary(string path) => ReadBinary(File.ReadAllBytes(path));

    internal static KVObject ReadBinary(byte[] data)
    {
        using var reader = new BinaryReader(new MemoryStream(data));
        using var owner = new ValveResourceFormat.Resource();
        var kv = new BinaryKV3 { Resource = owner };
        kv.Read(reader);
        return kv.Data.Root;
    }

    internal static string Render(KVObject v, int depth = 0)
        => v.IsArray ? "[" + string.Join(", ", v.Values.Take(depth == 0 ? 8 : 16).Select(x => Render(x, depth + 1))) + (v.Values.Count() > 16 ? $", ... {v.Values.Count()}" : "") + "]"
         : v.ValueType == KVValueType.Collection ? "{" + string.Join(", ", v.Children.Select(c => c.Key + "=" + Render(c.Value, depth + 1))) + "}"
         : v.ValueType == KVValueType.BinaryBlob ? $"<blob {v.AsBlob().Length}: {Convert.ToHexString(v.AsBlob().AsSpan(0, Math.Min(48, v.AsBlob().Length)))}>"
         : $"{v.ValueType}{(v.Flag != 0 ? "/" + v.Flag : "")}:{v}";

    /// <summary>Exploration (<c>SCENEMESHES=&lt;scene_meshes.kv3&gt;</c>): the first meshes in full.</summary>
    [Fact]
    public void DumpSceneMeshes()
    {
        if (Environment.GetEnvironmentVariable("SCENEMESHES") is not { } path)
            return;
        var root = ReadBinary(path);
        output.WriteLine($"root: {string.Join(",", root.Children.Select(c => c.Key))}");
        var meshes = root["meshes"];
        output.WriteLine($"{meshes.Values.Count()} meshes");
        foreach (var m in meshes.Values.Take(int.TryParse(Environment.GetEnvironmentVariable("SCENEMESHES_N"), out var n) ? n : 2))
            foreach (var c in m.Children)
                output.WriteLine($"  {c.Key} = {Render(c.Value)}");
    }

    /// <summary>Exploration (<c>SCENEMESHES</c>): Valve's file read back and written by Kv3V5Writer, per compression.</summary>
    [Fact]
    public void SceneMeshesRoundTrip()
    {
        if (Environment.GetEnvironmentVariable("SCENEMESHES") is not { } path)
            return;
        var bytes = File.ReadAllBytes(path);
        var root = ReadBinary(path);
        var generic = new Guid("7412167c-06e9-4698-aff2-e63eb59037e7");
        foreach (var method in new KV3BinaryCompressionMethod?[] { null, KV3BinaryCompressionMethod.Uncompressed, KV3BinaryCompressionMethod.Lz4, KV3BinaryCompressionMethod.Zstd })
        {
            var ours = Kv3V5Writer.Write(root, generic, method);
            var at = Enumerable.Range(0, Math.Min(ours.Length, bytes.Length)).FirstOrDefault(i => ours[i] != bytes[i], -1);
            output.WriteLine($"{method?.ToString() ?? "auto"}: {ours.Length} vs {bytes.Length} bytes, first difference {at}");
        }
    }

    /// <summary>The scene's world meshes from the .vmap: the node's entries as BuildNode receives them.</summary>
    internal static List<PathTraceScene.Mesh> OurWorldMeshes(string vmap)
    {
        if (CS2Fixtures.StockPak() is not { } pak)
            return [];
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vmap)))!;
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_pc_dir.vpk"), Path.Combine(game, "core", "shaders_pc_dir.vpk"));
        var doc = MapSource.Read(vmap);
        var world = WorldNodeBuild.WorldEntries(doc, content, shaders, NodeEntriesFromVmap.RendersAsWorld(game), buildBakes: true);
        return PathTraceScene.WorldMeshes(world);
    }

    /// <summary>
    /// The whole scene's meshes and instances: the world entries', then the
    /// static models' (<see cref="PathTraceProps"/>): every prop_static of the
    /// .vmap and every lightmapstatic brush entity of the compiled lump, its
    /// generated model read from Valve's package (ours are the same bytes).
    /// </summary>
    internal static (List<PathTraceScene.Mesh> Meshes, List<KVObject> Instances)? OurScene(string vmap)
    {
        if (CS2Fixtures.StockPak() is not { } pak)
            return null;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vmap)))!;
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_pc_dir.vpk"), Path.Combine(game, "core", "shaders_pc_dir.vpk"));
        var doc = MapSource.Read(vmap);
        var world = WorldNodeBuild.WorldEntries(doc, content, shaders, NodeEntriesFromVmap.RendersAsWorld(game), buildBakes: true);
        var meshes = PathTraceScene.WorldMeshes(world);
        var instances = PathTraceScene.WorldInstances(world);
        var props = new List<PathTraceProps.Prop>();
        var (_, entities) = MapMeshes.ReadWithEntities(doc);
        foreach (var ent in entities)
        {
            if (ent.Hidden)
                continue;
            if (ent.Element.Type == "CMapSmartProp")
                throw new NotSupportedException("a smart prop's records are not ported");
            var keys = ent.Element.Get<DmxBinary.Element>("entity_properties");
            if (keys?.Get<string>("classname") != "prop_static")
                continue;
            var prop = Physics.WorldCollision.PropOf(ent);
            props.Add(PathTraceProps.Record(keys.Get<string>("model") ?? "", k => keys.Get<string>(k), PropTransform.Matrix(prop.Origin, prop.Angles, prop.Scales), true)!);
        }
        if (MapFixtures.RcCompiledLumps(vmap) is { } lumps)
            foreach (var (_, key) in lumps.SelectMany(l => LumpEntities(l.Value, l.Key)))
                if (key("model") is { } model && model.StartsWith("maps/", StringComparison.Ordinal) && model.Contains("/entities/", StringComparison.Ordinal)
                    && PathTraceProps.Record(model, key, PropTransform.Matrix(Vec(key("origin")), Vec(key("angles")), Vec(key("scales") ?? "1 1 1")), false) is { } record)
                    props.Add(record);
        var package = new ValvePak.Package();
        package.Read(Path.Combine(game, "csgo_addons", addon, "maps", Path.GetFileNameWithoutExtension(vmap) + ".vpk"));
        Model? Load(string name)
        {
            if (!name.StartsWith("maps/", StringComparison.Ordinal))
                return content.LoadedModel(name + "_c");
            if (package.FindEntry(name + "_c") is not { } entry)
                return null;
            var resource = new ValveResourceFormat.Resource();
            resource.Read(new MemoryStream(Source2.Compiler.Io.VpkEntries.Read(package, entry)));
            return (Model)resource.DataBlock!;
        }
        PathTraceProps.Add(meshes, instances, props, Load);
        return (meshes, instances);
    }

    /// <summary>scene_meshes.kv3 from the .vmap against Valve's (<c>SCENEMESHES</c>, <c>NODEENTRIES_VMAP</c>), mesh by mesh and key by key.</summary>
    [Fact]
    public void SceneMeshesAreValves()
    {
        if (Environment.GetEnvironmentVariable("SCENEMESHES") is not { } path || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap)
            return;
        if (OurScene(vmap) is not var (ours, _))
            return;
        var bytes = PathTraceScene.MeshesFile(ours);
        var valve = ReadBinary(path)["meshes"].Values.ToList();
        var mine = ReadBinary(bytes)["meshes"].Values.ToList();
        output.WriteLine($"meshes: ours {mine.Count}, valve {valve.Count}");
        var misses = new List<string>();
        for (var i = 0; i < Math.Min(mine.Count, valve.Count); i++)
            foreach (var c in valve[i].Children)
            {
                var o = mine[i].Children.FirstOrDefault(x => x.Key == c.Key).Value;
                if (o is null || Render(o, 1) != Render(c.Value, 1))
                    misses.Add($"mesh {i} {c.Key}: ours {(o is null ? "-" : Render(o, 1))} valve {Render(c.Value, 1)}");
            }
        foreach (var m in misses.Take(16))
            output.WriteLine(m.Length > 600 ? m[..600] : m);
        Assert.Empty(misses);
        Assert.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "scene_meshes.kv3 bytes differ");
    }

    /// <summary>Exploration (<c>SCENEINFO=&lt;scene_info.kv3&gt;</c>): Valve's text read back and written by Kv3TextWriter.</summary>
    [Fact]
    public void SceneInfoRoundTrip()
    {
        if (Environment.GetEnvironmentVariable("SCENEINFO") is not { } path)
            return;
        var text = File.ReadAllText(path);
        using var stream = File.OpenRead(path);
        var doc = KVSerializer.Create(KVSerializationFormat.KeyValues3Text).Deserialize(stream);
        var ours = Kv3TextWriter.Write(Floats(doc), PathTraceScene.GenericFormat);
        var at = Enumerable.Range(0, Math.Min(ours.Length, text.Length)).FirstOrDefault(i => ours[i] != text[i], -1);
        output.WriteLine($"{ours.Length} vs {text.Length} chars, first difference {at}");
        if (at >= 0)
        {
            output.WriteLine($"ours  ...{ours[Math.Max(0, at - 80)..Math.Min(ours.Length, at + 80)]}");
            output.WriteLine($"valve ...{text[Math.Max(0, at - 80)..Math.Min(text.Length, at + 80)]}");
        }
        Assert.Equal(text, ours);
    }

    // The text reader parses numbers as doubles; Valve's were floats.
    internal static KVObject Floats(KVObject v)
    {
        if (v.IsArray)
        {
            var a = KVObject.Array();
            foreach (var x in v.Values)
                a.Add(Floats(x));
            return a;
        }
        if (v.ValueType == KVValueType.Collection)
        {
            var o = KVObject.Collection();
            foreach (var c in v.Children)
                o.Add(c.Key, Floats(c.Value));
            return o;
        }
        return v.ValueType == KVValueType.FloatingPoint64 ? new KVObject((double)(float)v.ToDouble(System.Globalization.CultureInfo.InvariantCulture)) : v;
    }

    /// <summary>Exploration (<c>NODEENTRIES_VMAP</c>, <c>SCENEKEYS=class,class</c>): those entities' keys.</summary>
    [Fact]
    public void EntityKeys()
    {
        if (Environment.GetEnvironmentVariable("SCENEKEYS") is not { } classes || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap)
            return;
        var wanted = classes.Split(',');
        foreach (var e in MapEntities.From(MapSource.Read(vmap)).Where(e => wanted.Contains(e.ClassName)))
            output.WriteLine($"{e.ClassName}: {string.Join(" | ", e.Keys.Select(k => k.Key + "=" + k.Value))}");
    }

    /// <summary>A compiled lump's entities as key readers (strings as the entity system holds them), with their class names.</summary>
    internal static List<(string Class, LightPrecompute.KeyReader Key)> LumpEntities(byte[] lump, string name)
    {
        using var resource = ResourceTrees.Read(lump, name);
        var data = (ValveResourceFormat.ResourceTypes.EntityLump)resource.DataBlock!;
        var list = new List<(string, LightPrecompute.KeyReader)>();
        foreach (var entity in data.Data.GetArray("m_entityKeyValues"))
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in entity.GetSubCollection("keyValues3Data")?.GetSubCollection("values")?.Children ?? [])
                values[kv.Key] = Text(kv.Value);
            list.Add((values.GetValueOrDefault("classname", ""), k => values.TryGetValue(k, out var v) ? v : null));
        }
        return list;
    }

    private static string Text(KVObject v)
    {
        var c = System.Globalization.CultureInfo.InvariantCulture;
        if (v.IsArray)
            return string.Join(" ", v.Values.Select(Text));
        return v.ValueType switch
        {
            KVValueType.Boolean => v.ToBoolean(c) ? "1" : "0",
            KVValueType.FloatingPoint or KVValueType.FloatingPoint64 => ((float)v.ToDouble(c)).ToString("R", c),
            _ => v.ToString(c),
        };
    }

    /// <summary>
    /// The light_barn tables of scene_info.kv3 (<see cref="SceneLights"/>) from the map's
    /// compiled lump keys (<c>SCENEINFO</c>, <c>NODEENTRIES_VMAP</c>), light by light.
    /// </summary>
    [Fact]
    public void BarnTablesAreValves()
    {
        if (Environment.GetEnvironmentVariable("SCENEINFO") is not { } path || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap
            || MapFixtures.RcCompiledLumps(vmap) is not { } lumps)
            return;
        using var stream = File.OpenRead(path);
        var valve = Floats(KVSerializer.Create(KVSerializationFormat.KeyValues3Text).Deserialize(stream))["lights"].Values.ToList();
        var lights = lumps.SelectMany(l => LumpEntities(l.Value, l.Key)).Where(e => e.Class == "light_barn").ToList();
        output.WriteLine($"barns in the lumps {lights.Count}, scene lights {valve.Count}");
        var misses = new List<string>();
        for (var i = 0; i < Math.Min(lights.Count, valve.Count); i++)
        {
            var key = lights[i].Key;
            var origin = Vec(key("origin"));
            var angles = Vec(key("angles"));
            var table = SceneLights.Table(SceneLights.Barn(key, LightPrecompute.World(origin, angles)));
            foreach (var c in valve[i]["barn"].Children)
            {
                var o = table.Children.FirstOrDefault(x => x.Key == c.Key).Value;
                if (o is null || Loose(Render(o, 1)) != Loose(Render(c.Value, 1)))
                    misses.Add($"light {i} {c.Key}: ours {(o is null ? "-" : Render(o, 1))} valve {Render(c.Value, 1)}");
            }
        }
        foreach (var m in misses.Take(24))
            output.WriteLine(m);
        Assert.Empty(misses);
    }

    private static System.Numerics.Vector3 Vec(string? s)
    {
        var p = (s ?? "0 0 0").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        return new(p[0], p[1], p[2]);
    }

    // Text KV3 reads every int as Int64; the width is the writer's, not the value's.
    internal static string Loose(string rendered) => rendered.Replace("UInt32:", "Int:").Replace("UInt64:", "Int:").Replace("Int32:", "Int:").Replace("Int64:", "Int:");

    /// <summary>The map's compiled lumps, from <c>SCENEINFO_PKG</c> (a package) when set.</summary>
    private static IReadOnlyDictionary<string, byte[]>? Lumps(string vmap)
    {
        if (Environment.GetEnvironmentVariable("SCENEINFO_PKG") is not { } package)
            return MapFixtures.RcCompiledLumps(vmap);
        using var pkg = new ValvePak.Package();
        pkg.Read(package);
        var lumps = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in pkg.Entries.GetValueOrDefault("vents_c") ?? [])
            lumps[entry.GetFullPath()] = Io.VpkEntries.Read(pkg, entry);
        return lumps;
    }

    /// <summary>scene_info.kv3's whole lights array (barns, rects, omni2s, directionals; <c>SCENEINFO_PKG</c> for the lumps) (<c>SCENEINFO</c>, <c>NODEENTRIES_VMAP</c>), as text.</summary>
    [Fact]
    public void LightsAreValves()
    {
        if (Environment.GetEnvironmentVariable("SCENEINFO") is not { } path || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap
            || Lumps(vmap) is not { } lumps)
            return;
        using var stream = File.OpenRead(path);
        var valve = Floats(KVSerializer.Create(KVSerializationFormat.KeyValues3Text).Deserialize(stream))["lights"];
        var ids = new Dictionary<int, int>();
        var ours = KVObject.Array();
        var keyDump = Environment.GetEnvironmentVariable("SCENEINFO_KEYS") is { } keyPath ? new System.Text.StringBuilder() : null;
        var lumpLights = lumps.SelectMany(l => LumpEntities(l.Value, l.Key)).Select(e => (e.Class, e.Key, World: (float[]?)null)).ToList();
        if (Environment.GetEnvironmentVariable("SCENEINFO_WALK") == "1")
        {
            // Hypothesis: the document's walk (the world's lights, then instance copies), placed by the document.
            var byId = lumpLights.Where(e => e.Key("hammeruniqueid") is not null).GroupBy(e => e.Key("hammeruniqueid")!).ToDictionary(g => g.Key, g => g.First());
            var document = DmxBinary.ReadFile(vmap);
            var walked = MapEntities.From(document);
            var (copies, templates) = MapInstances.Expand(document, walked, SmartProps.NodesCreatedOnLoad(document, MapFixtures.SmartPropLocators));
            var pending = copies.GroupBy(c => c.EmitAt).ToDictionary(g => g.Key, g => g.ToList());
            var spliced = new List<MapEntities.Entity>();
            for (var i = 0; i <= walked.Count; i++)
            {
                if (pending.TryGetValue(i, out var here))
                    spliced.AddRange(here.Select(c => walked[c.Template] with { Instanced = true, NodeId = c.NodeId, Origin = c.Origin, Angles = c.Angles }));
                if (i < walked.Count && !templates.Contains(i))
                    spliced.Add(walked[i]);
            }
            var ordered = spliced.Where(e => !e.Hidden && e.ClassName.StartsWith("light_", StringComparison.Ordinal)).ToList();
            if (Environment.GetEnvironmentVariable("SCENEINFO_WALK_ELEMENTS") == "1")
            {
                // Hypothesis 2: the world's flat node list in DMX element order; copies keep their spliced place.
                var elementIndex = new Dictionary<int, int>();
                for (var i = 0; i < document.Elements.Count; i++)
                    if (document.Elements[i].GetValue<int>("nodeID") is { } id && !elementIndex.ContainsKey(id))
                        elementIndex[id] = i;
                var world = ordered.Where(e => !e.Instanced).OrderBy(e => elementIndex.GetValueOrDefault(e.NodeId, int.MaxValue)).ToList();
                output.WriteLine("world-level lights by element index: " + string.Join(",", world.Take(30).Select(e => e.NodeId)));
                ordered = [.. world, .. ordered.Where(e => e.Instanced)];
            }
            output.WriteLine($"walk: {ordered.Count} lights, {ordered.Count(e => byId.ContainsKey(e.NodeId.ToString(System.Globalization.CultureInfo.InvariantCulture)))} found in the lumps");
            lumpLights = [.. ordered.Where(e => byId.ContainsKey(e.NodeId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .Select(e => { var l = byId[e.NodeId.ToString(System.Globalization.CultureInfo.InvariantCulture)]; return (l.Class, l.Key, World: (float[]?)LightPrecompute.World(e.Origin, e.Angles)); })];
        }
        foreach (var (cls, key, placed) in lumpLights)
        {
            if (keyDump != null && cls.StartsWith("light_", StringComparison.Ordinal))
                keyDump.AppendLine(string.Join(" | ", new[] { "classname", "origin", "shape", "size_params", "color", "colormode", "brightness_units", "brightness", "brightness_lumens", "brightness_legacy", "brightnessscale", "outer_angle", "inner_angle", "range", "bouncescale" }.Select(k => $"{k}={key(k)}")));
            LightShape record;
            if (cls is "light_barn" or "light_rect" or "light_omni2")
                record = SceneLights.Record(cls, key, placed ?? LightPrecompute.World(Vec(key("origin")), Vec(key("angles"))));
            else if (cls is "light_environment" or "light_directional")
                record = SceneLights.Directional(key, Vec(key("origin")), Vec(key("angles")));
            else
                continue;
            foreach (var entry in SceneLights.Entries(key, record, ids))
                ours.Add(entry);
        }
        string Text(KVObject lights) { var root = KVObject.Collection(); root.Add("lights", lights); return Kv3TextWriter.Write(root, PathTraceScene.GenericFormat); }
        var (a, b) = (Text(ours), Text(valve));
        if (Environment.GetEnvironmentVariable("SCENEINFO_OUT") is { } outPath)
            File.WriteAllText(outPath, a);
        if (keyDump != null)
            File.WriteAllText(Environment.GetEnvironmentVariable("SCENEINFO_KEYS")!, keyDump.ToString());
        var at = Enumerable.Range(0, Math.Min(a.Length, b.Length)).FirstOrDefault(i => a[i] != b[i], -1);
        output.WriteLine($"{a.Length} vs {b.Length} chars, first difference {at}");
        if (at >= 0)
        {
            output.WriteLine($"ours  ...{a[Math.Max(0, at - 200)..Math.Min(a.Length, at + 100)]}");
            output.WriteLine($"valve ...{b[Math.Max(0, at - 200)..Math.Min(b.Length, at + 100)]}");
        }
        Assert.Equal(b, a);
    }

    /// <summary>The world instances of scene_info.kv3 against the first of Valve's (<c>SCENEINFO</c>, <c>NODEENTRIES_VMAP</c>).</summary>
    [Fact]
    public void WorldInstancesAreValves()
    {
        if (Environment.GetEnvironmentVariable("SCENEINFO") is not { } path || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap
            || CS2Fixtures.StockPak() is not { } pak)
            return;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vmap)))!;
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_pc_dir.vpk"), Path.Combine(game, "core", "shaders_pc_dir.vpk"));
        var world = WorldNodeBuild.WorldEntries(MapSource.Read(vmap), content, shaders, NodeEntriesFromVmap.RendersAsWorld(game), buildBakes: true);
        var ours = PathTraceScene.WorldInstances(world);
        using var stream = File.OpenRead(path);
        var valve = Floats(KVSerializer.Create(KVSerializationFormat.KeyValues3Text).Deserialize(stream))["instances"].Values.ToList();
        output.WriteLine($"world instances: ours {ours.Count}, valve's instances {valve.Count}");
        var misses = new List<string>();
        for (var i = 0; i < ours.Count && i < valve.Count; i++)
            if (Loose(Render(ours[i], 1)) != Loose(Render(valve[i], 1)))
                misses.Add($"instance {i}: ours {Render(ours[i], 1)} valve {Render(valve[i], 1)}");
        foreach (var m in misses.Take(10))
            output.WriteLine(m);
        Assert.Empty(misses);
    }

    /// <summary>
    /// scene_info.kv3 whole (<c>SCENEINFO</c>, <c>NODEENTRIES_VMAP</c>): the instances
    /// (<see cref="OurScene"/>), the IBL and the lights from the compiled lump's keys.
    /// </summary>
    [Fact]
    public void SceneInfoIsValves()
    {
        if (Environment.GetEnvironmentVariable("SCENEINFO") is not { } path || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap
            || CS2Fixtures.StockPak() is not { } pak || MapFixtures.RcCompiledLumps(vmap) is not { } lumps)
            return;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vmap)))!;
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_pc_dir.vpk"), Path.Combine(game, "core", "shaders_pc_dir.vpk"));
        var world = WorldNodeBuild.WorldEntries(MapSource.Read(vmap), content, shaders, NodeEntriesFromVmap.RendersAsWorld(game), buildBakes: true);
        var entities = lumps.SelectMany(l => LumpEntities(l.Value, l.Key)).ToList();
        var ids = new Dictionary<int, int>();
        var lights = new List<KVObject>();
        LightPrecompute.KeyReader? sky = null;
        foreach (var (cls, key) in entities)
        {
            LightShape record;
            if (cls == "light_barn")
                record = SceneLights.Barn(key, LightPrecompute.World(Vec(key("origin")), Vec(key("angles"))));
            else if (cls is "light_environment" or "light_directional")
            {
                record = SceneLights.Directional(key, Vec(key("origin")), Vec(key("angles")));
                if (cls == "light_environment")
                    sky ??= key;
            }
            else if (cls.StartsWith("light_", StringComparison.Ordinal))
                throw new NotSupportedException(cls);
            else
                continue;
            lights.AddRange(SceneLights.Entries(key, record, ids));
        }
        (LightPrecompute.KeyReader, float[])? SkyNamed(string name)
        {
            foreach (var (cls, key) in entities)
                if (cls == "env_sky" && key("targetname") == name)
                {
                    var m = MapMeshes.AngleMatrix(Vec(key("angles")));
                    var o = Vec(key("origin"));
                    (m[3], m[7], m[11]) = (o.X, o.Y, o.Z);
                    return (key, m);
                }
            return null;
        }
        var ours = PathTraceScene.InfoFile(OurScene(vmap)!.Value.Instances, SceneLights.Ibl(sky, SkyNamed), lights);
        var text = File.ReadAllText(path);
        var at = Enumerable.Range(0, Math.Min(ours.Length, text.Length)).FirstOrDefault(i => ours[i] != text[i], -1);
        output.WriteLine($"{ours.Length} vs {text.Length} chars, first difference {at}");
        if (at >= 0)
        {
            output.WriteLine($"ours  ...{ours[Math.Max(0, at - 200)..Math.Min(ours.Length, at + 100)]}");
            output.WriteLine($"valve ...{text[Math.Max(0, at - 200)..Math.Min(text.Length, at + 100)]}");
        }
        Assert.Equal(text, ours);
    }

    /// <summary>Exploration (<c>SCENEPROPS_VMAP</c>): each prop_static's element attributes, apart from its keys.</summary>
    [Fact]
    public void DumpSceneProps()
    {
        if (Environment.GetEnvironmentVariable("SCENEPROPS_VMAP") is not { } vmap)
            return;
        var (_, entities) = MapMeshes.ReadWithEntities(MapSource.Read(vmap));
        var n = 0;
        foreach (var ent in entities)
        {
            var keys = ent.Element.Get<DmxBinary.Element>("entity_properties");
            if (keys?.Get<string>("classname") != "prop_static")
                continue;
            if (n++ > 0 && keys.Get<string>("model")?.Contains("flourescent") != false)
            {
                output.WriteLine($"  {keys.Get<string>("model")} baketoworld={keys.Get<string>("baketoworld")} disableshadows={keys.Get<string>("disableshadows")} emissive={keys.Get<string>("emissive")} skin={keys.Get<string>("skin")}");
                continue;
            }
            output.WriteLine($"{ent.Element.Type} {keys.Get<string>("model")} hidden={ent.Hidden}");
            foreach (var (k, v) in ent.Element.Attributes)
                output.WriteLine($"   {k} = {Describe(v)}");
            if (n > 300)
                break;
        }
        string Describe(object? v) => v switch
        {
            DmxBinary.Element e => $"<{e.Type} {e.Name}: {string.Join(", ", e.Attributes.Select(a => a.Key + "=" + Short(a.Value)))}>",
            System.Collections.IList l => $"[{l.Count}] {string.Join(", ", l.Cast<object?>().Take(4).Select(Describe))}",
            _ => Short(v),
        };
        static string Short(object? v) => v switch
        {
            DmxBinary.Element e => $"<{e.Type} {e.Name}>",
            System.Collections.IList l => $"[{l.Count}]",
            _ => v?.ToString() ?? "null",
        };
    }

    /// <summary>Exploration (<c>SCENEPROPS_VMAP</c>): the compiled lump's entities with a model, in lump order.</summary>
    [Fact]
    public void DumpLumpModels()
    {
        if (Environment.GetEnvironmentVariable("SCENEPROPS_VMAP") is not { } vmap || MapFixtures.RcCompiledLumps(vmap) is not { } lumps)
            return;
        var i = 0;
        foreach (var (name, lump) in lumps)
            foreach (var (cls, key) in LumpEntities(lump, name))
                if (key("model") is { Length: > 0 } model)
                    output.WriteLine($"{i++} {name} {cls} {model} skin={key("skin")} color={key("rendercolor")} origin={key("origin")} angles={key("angles")} scales={key("scales")} disableshadows={key("disableshadows")}");
    }

    /// <summary>Exploration (<c>SCENEPKG=&lt;map.vpk&gt;</c>): each entity model's draw calls.</summary>
    [Fact]
    public void DumpEntityModels()
    {
        if (Environment.GetEnvironmentVariable("SCENEPKG") is not { } vpk)
            return;
        using var pkg = new ValvePak.Package();
        pkg.Read(vpk);
        foreach (var entry in (pkg.Entries.GetValueOrDefault("vmdl_c") ?? []).OrderBy(e => e.GetFullPath(), StringComparer.Ordinal))
        {
            using var resource = new ValveResourceFormat.Resource();
            resource.Read(new MemoryStream(Source2.Compiler.Io.VpkEntries.Read(pkg, entry)));
            var model = (Model)resource.DataBlock!;
            var calls = new List<string>();
            foreach (var (mesh, _, _, lod) in model.GetEmbeddedMeshesAndLoD())
                foreach (var so in mesh.Data.GetArray("m_sceneObjects"))
                    foreach (var call in so.GetArray("m_drawCalls"))
                        calls.Add($"{Path.GetFileNameWithoutExtension(call.GetStringProperty("m_material"))}:{call.GetInt32Property("m_nIndexCount")}:{call.GetInt32Property("m_nVertexCount")}");
            foreach (var (mesh, _, _, _) in model.GetEmbeddedMeshesAndLoD().Take(1))
                foreach (var vb in mesh.VBIB.VertexBuffers)
                    output.WriteLine($"   vb: {string.Join(", ", vb.InputLayoutFields.Select(f => $"{f.SemanticName}{f.SemanticIndex}:{f.Format}/'{f.ShaderSemantic}'"))}");
            output.WriteLine($"{entry.GetFullPath()} meshes={model.GetEmbeddedMeshesAndLoD().Count()} refmeshes={model.GetReferenceMeshNamesAndLoD().Count()} calls={calls.Count}: {string.Join(" ", calls.Take(12))}");
        }
        if (CS2Fixtures.StockPak() is not { } pak)
            return;
        using var content = new GameContent(pak);
        foreach (var name in new[] { "models/de_overpass/lights/flourescent/flourescent_light_2.vmdl", "models/de_overpass/lights/flourescent/flourescent_light_2_bulbs.vmdl" })
            if (content.LoadedModel(name + "_c") is { } model)
            {
                output.WriteLine($"{name}: lodmasks {string.Join(",", model.GetEmbeddedMeshesAndLoD().Select(m => m.Item4))}");
                foreach (var (mesh, _, _, _) in model.GetEmbeddedMeshesAndLoD())
                    foreach (var vb in mesh.VBIB.VertexBuffers)
                        output.WriteLine($"   vb: {string.Join(", ", vb.InputLayoutFields.Select(f => $"{f.SemanticName}{f.SemanticIndex}:{f.Format}/'{f.ShaderSemantic}'"))}");
            }
    }

    /// <summary>Exploration (<c>SCENEMESHES</c>, <c>SCENEPKG</c>): Valve's meshes 7 and 8 beside our unpacking of the entity models' first draw.</summary>
    [Fact]
    public void DumpEntityMeshVertices()
    {
        if (Environment.GetEnvironmentVariable("SCENEMESHES") is not { } path || Environment.GetEnvironmentVariable("SCENEPKG") is not { } vpk)
            return;
        var meshes = ReadBinary(path)["meshes"].Values.ToList();
        foreach (var i in new[] { 7, 8, 73 })
        {
            var m = meshes[i];
            var stride = m["vertex_stride"].ToInt32(null);
            var v = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(m["vertices"].AsBlob()).ToArray();
            for (var k = 0; k < 2; k++)
                output.WriteLine($"valve {i}.{k}: {string.Join(" ", v.Skip(k * stride).Take(stride).Select(f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))}");
            output.WriteLine($"  {string.Join(" ", m.Children.Where(c => c.Key is not ("vertices" or "indices" or "attributes")).Select(c => c.Key + "=" + Render(c.Value)))}");
        }
        using var pkg = new ValvePak.Package();
        pkg.Read(vpk);
        foreach (var name in new[] { "end_breakable_240", "end_breakable_475" })
        {
            var entry = pkg.Entries["vmdl_c"].First(e => e.GetFullPath().Contains(name));
            using var resource = new ValveResourceFormat.Resource();
            resource.Read(new MemoryStream(Source2.Compiler.Io.VpkEntries.Read(pkg, entry)));
            var (mesh, _, _, _) = ((Model)resource.DataBlock!).GetEmbeddedMeshesAndLoD().First();
            var vb = mesh.VBIB.VertexBuffers[0];
            var (ours, color) = NodePropEntries.Vertices(vb, 0, 2);
            for (var k = 0; k < 2; k++)
            {
                var raw = vb.Data.AsSpan(k * (int)vb.ElementSizeInBytes, (int)vb.ElementSizeInBytes).ToArray();
                output.WriteLine($"ours {name}.{k} color={color}: {string.Join(" ", ours.Skip(k * 18).Take(18).Select(f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))} raw {Convert.ToHexString(raw)}");
            }
        }
    }
}
