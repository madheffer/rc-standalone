using Source2.Compiler.Io;
using System.Text.RegularExpressions;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler.Maps;

/// <summary>
/// The compiled game content a map compile reads: loose files under the given
/// game folders (an addon's), then the game's pak01, then core's pak01 beside
/// it. Models, materials, surface properties, collision properties and smart
/// prop definitions, each read once.
/// </summary>
public class GameContent : SettleWorld.IModels, IDisposable
{
    private readonly Package _pak = new();
    private readonly Package? _core;
    private readonly string[] _loose;
    private readonly Dictionary<string, PhysAggregateData?> _physics = new(Tier0Strings.IgnoreCase);
    private readonly Dictionary<string, Model?> _models = new(Tier0Strings.IgnoreCase);
    private readonly Dictionary<string, SettleWorld.MaterialInfo?> _materials = new(Tier0Strings.IgnoreCase);
    private readonly List<Resource> _keep = [];
    private Dictionary<string, SettleWorld.CollisionProperty>? _collision;
    private Dictionary<uint, Simulation.ContactSolver.Material>? _surfaces;
    private Dictionary<uint, string>? _surfaceNames;

    /// <summary>The game's pak01 (<c>game/csgo/pak01_dir.vpk</c>), core's beside it, and loose game folders searched first.</summary>
    public GameContent(string pak, params string[] loose)
    {
        PakPath = pak;
        _pak.Read(pak);
        var core = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, "..", "core", "pak01_dir.vpk"));
        if (File.Exists(core))
        {
            _core = new Package();
            _core.Read(core);
        }
        _loose = loose;
    }

    /// <summary>The pak01 this reads, as given.</summary>
    public string PakPath { get; }

    /// <summary>A file's bytes by its game path, or null.</summary>
    public byte[]? Read(string path)
    {
        path = path.Replace((char)92, '/');
        foreach (var dir in _loose)
            if (File.Exists(Path.Combine(dir, path)))
                return File.ReadAllBytes(Path.Combine(dir, path));
        foreach (var pak in new[] { _pak, _core })
            if (pak?.FindEntry(path) is { } entry)
            {
                pak.ReadEntry(entry, out var bytes);
                return bytes;
            }
        return null;
    }

    public SettleWorld.MaterialInfo? Material(string path)
    {
        if (_materials.TryGetValue(path, out var found))
            return found;
        if (Read(path + "_c") is not { } bytes)
            return _materials[path] = null;
        using var res = new Resource();
        res.Read(new MemoryStream(bytes));
        var mat = (Material)res.DataBlock!;
        return _materials[path] = new SettleWorld.MaterialInfo(
            mat.IntAttributes.ToDictionary(x => x.Key, x => x.Value, Tier0Strings.IgnoreCase),
            mat.StringAttributes.ToDictionary(x => x.Key, x => x.Value, Tier0Strings.IgnoreCase))
        {
            Shader = mat.ShaderName,
            Params = mat.IntParams.ToDictionary(x => x.Key, x => x.Value, Tier0Strings.IgnoreCase),
            Floats = mat.FloatParams.ToDictionary(x => x.Key, x => x.Value, Tier0Strings.IgnoreCase),
            Vectors = mat.VectorParams.ToDictionary(x => x.Key, x => x.Value, Tier0Strings.IgnoreCase),
            Textures = mat.TextureParams.ToDictionary(x => x.Key, x => x.Value, Tier0Strings.IgnoreCase),
        };
    }

    public SettleWorld.CollisionProperty? CollisionProperty(string name)
    {
        if (_collision == null)
        {
            var text = System.Text.Encoding.UTF8.GetString(Read("scripts/collision_properties.txt") ?? []);
            _collision = new(Tier0Strings.IgnoreCase);
            foreach (Match m in Regex.Matches(text, @"\{([^{}]*)\}"))
            {
                var body = m.Groups[1].Value;
                string Str(string key) => Regex.Match(body, key + @"\s*=\s*""([^""]*)""").Groups[1].Value;
                string List(string key) => string.Join(", ", Regex.Matches(
                    Regex.Match(body, key + @"\s*=\s*\[([^\]]*)\]").Groups[1].Value, @"""([^""]*)""").Select(x => x.Groups[1].Value));
                if (Str("name") is { Length: > 0 } n)
                    _collision[n] = new SettleWorld.CollisionProperty(Str("collision_group"), List("interact_as"), List("interact_with"), List("interact_exclude"));
            }
        }
        return _collision.GetValueOrDefault(name);
    }

    public Simulation.ContactSolver.Material? Surface(uint nameHash)
    {
        ReadSurfaces();
        return _surfaces!.TryGetValue(nameHash, out var m) ? m : null;
    }

    public string? SurfaceName(uint nameHash)
    {
        ReadSurfaces();
        return _surfaceNames!.GetValueOrDefault(nameHash);
    }

    private void ReadSurfaces()
    {
        if (_surfaces != null)
            return;
        _pak.ReadEntry(_pak.FindEntry("surfaceproperties/surfaceproperties.vsurf_c")!, out var bytes);
        using var res = new Resource();
        res.Read(new MemoryStream(bytes));
        _surfaces = SettleWorld.SurfaceMaterials(((BinaryKV3)res.DataBlock!).Data);
        _surfaceNames = SettleWorld.SurfaceNames(((BinaryKV3)res.DataBlock!).Data);
    }

    public ValveKeyValue.KVObject? ModelKeyValues(string model)
    {
        Physics(model);
        return _models.GetValueOrDefault(model)?.KeyValues;
    }

    /// <summary>A model's embedded physics: an addon's own compiled model first, then the paks'.</summary>
    public PhysAggregateData? Physics(string model)
    {
        if (_physics.TryGetValue(model, out var phys))
            return phys;
        if (Read(Path.ChangeExtension(model, ".vmdl_c")) is { } bytes)
        {
            var res = new Resource();
            res.Read(new MemoryStream(bytes));
            _keep.Add(res);
            _models[model] = res.DataBlock as Model;
            phys = (res.DataBlock as Model)?.GetEmbeddedPhys();
        }
        return _physics[model] = phys;
    }

    private readonly Dictionary<string, List<List<(System.Numerics.Vector3 A, System.Numerics.Vector3 B, System.Numerics.Vector3 C, string Material)>>> _renderMeshes
        = new(Tier0Strings.IgnoreCase);

    /// <summary>
    /// A model's render meshes as the light scene takes them (Model_RayScene,
    /// Model_MeshSelected), one triangle list per mesh: each embedded mesh
    /// whose LOD mask is 0 or holds LOD 0 and whose mesh group mask meets the
    /// model's default, its draw calls in order, each draw call's triangles in
    /// index order, positions from its first vertex buffer, with the draw
    /// call's material.
    /// </summary>
    public IReadOnlyList<List<(System.Numerics.Vector3 A, System.Numerics.Vector3 B, System.Numerics.Vector3 C, string Material)>> RenderMeshes(string model)
    {
        if (_renderMeshes.TryGetValue(model, out var cached))
            return cached;
        var meshes = new List<List<(System.Numerics.Vector3, System.Numerics.Vector3, System.Numerics.Vector3, string)>>();
        Physics(model);
        if (_models.GetValueOrDefault(model) is { } m)
        {
            var groupMasks = m.Data.ContainsKey("m_refMeshGroupMasks")
                ? m.Data.GetIntegerArray("m_refMeshGroupMasks").Select(v => unchecked((ulong)v)).ToArray() : [];
            var defaultMask = m.Data.ContainsKey("m_nDefaultMeshGroupMask") ? m.Data.GetUnsignedIntegerProperty("m_nDefaultMeshGroupMask") : ulong.MaxValue;
            foreach (var (mesh, index, _, lod) in m.GetEmbeddedMeshesAndLoD())
            {
                if (lod != 0 && (lod & 1) == 0)
                    continue;
                if (index < groupMasks.Length && (groupMasks[index] & defaultMask) == 0)
                    continue;
                var vbib = mesh.VBIB;
                var found = new List<(System.Numerics.Vector3, System.Numerics.Vector3, System.Numerics.Vector3, string)>();
                meshes.Add(found);
                foreach (var sceneObject in mesh.Data.GetArray("m_sceneObjects"))
                    foreach (var call in sceneObject.GetArray("m_drawCalls"))
                    {
                        var vb = vbib.VertexBuffers[call.GetArray("m_vertexBuffers")[0].GetInt32Property("m_hBuffer")];
                        var field = vb.InputLayoutFields.First(f => f.SemanticName == "POSITION");
                        var positions = ValveResourceFormat.Blocks.VBIB.GetVector3AttributeArray(vb, field);
                        var ib = vbib.IndexBuffers[call.GetSubCollection("m_indexBuffer").GetInt32Property("m_hBuffer")];
                        int Index(int i) => ib.ElementSizeInBytes == 2
                            ? BitConverter.ToUInt16(ib.Data, i * 2) : BitConverter.ToInt32(ib.Data, i * 4);
                        var start = call.GetInt32Property("m_nStartIndex");
                        var count = call.GetInt32Property("m_nIndexCount");
                        var baseVertex = call.GetInt32Property("m_nBaseVertex");
                        var material = call.GetStringProperty("m_material") ?? "";
                        for (var i = start; i + 2 < start + count; i += 3)
                            found.Add((positions[baseVertex + Index(i)], positions[baseVertex + Index(i + 1)],
                                       positions[baseVertex + Index(i + 2)], material));
                    }
            }
        }
        return _renderMeshes[model] = meshes;
    }

    /// <summary>A compiled texture's width and height (its vtex_c header), or null.</summary>
    public (int Width, int Height)? TextureSize(string texture)
    {
        if (Read(texture.EndsWith("_c", StringComparison.Ordinal) ? texture : texture + "_c") is not { } bytes)
            return null;
        using var res = new Resource();
        res.Read(new MemoryStream(bytes));
        return res.DataBlock is ValveResourceFormat.ResourceTypes.Texture t ? (t.Width, t.Height) : null;
    }

    /// <summary>The loaded model resource (null when missing), for its render meshes.</summary>
    public ValveResourceFormat.ResourceTypes.Model? LoadedModel(string model)
    {
        Physics(model);
        return _models.GetValueOrDefault(model);
    }

    /// <summary>A smart prop definition's compiled data (.vsmart_c), from loose files or the paks.</summary>
    public ValveKeyValue.KVObject? SmartProp(string file)
    {
        if (Read(Path.ChangeExtension(file, ".vsmart_c")) is not { } bytes)
            return null;
        var res = new Resource();
        res.Read(new MemoryStream(bytes));
        _keep.Add(res);
        return (res.DataBlock as BinaryKV3)?.Data;
    }

    public void Dispose()
    {
        foreach (var r in _keep)
            r.Dispose();
        _pak.Dispose();
        _core?.Dispose();
        GC.SuppressFinalize(this);
    }
}
