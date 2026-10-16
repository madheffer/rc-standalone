using Source2.Compiler.Lighting;
using Source2.Compiler.Maps;
using ValveKeyValue;
using ValveResourceFormat.ResourceTypes;
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

    /// <summary>scene_meshes.kv3 from the .vmap against Valve's (<c>SCENEMESHES</c>, <c>NODEENTRIES_VMAP</c>), mesh by mesh and key by key.</summary>
    [Fact]
    public void SceneMeshesAreValves()
    {
        if (Environment.GetEnvironmentVariable("SCENEMESHES") is not { } path || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap)
            return;
        var ours = OurWorldMeshes(vmap);
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
}
