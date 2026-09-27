using System.Text.RegularExpressions;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

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
    private readonly Dictionary<string, PhysAggregateData?> _physics = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Model?> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SettleWorld.MaterialInfo?> _materials = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Resource> _keep = [];
    private Dictionary<string, SettleWorld.CollisionProperty>? _collision;
    private Dictionary<uint, Simulation.ContactSolver.Material>? _surfaces;
    private Dictionary<uint, string>? _surfaceNames;

    /// <summary>The game's pak01 (<c>game/csgo/pak01_dir.vpk</c>), core's beside it, and loose game folders searched first.</summary>
    public GameContent(string pak, params string[] loose)
    {
        _pak.Read(pak);
        var core = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, "..", "core", "pak01_dir.vpk"));
        if (File.Exists(core))
        {
            _core = new Package();
            _core.Read(core);
        }
        _loose = loose;
    }

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
            mat.IntAttributes.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase),
            mat.StringAttributes.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase))
        {
            Shader = mat.ShaderName,
            Params = mat.IntParams.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase),
            Floats = mat.FloatParams.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase),
            Vectors = mat.VectorParams.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase),
        };
    }

    public SettleWorld.CollisionProperty? CollisionProperty(string name)
    {
        if (_collision == null)
        {
            var text = System.Text.Encoding.UTF8.GetString(Read("scripts/collision_properties.txt") ?? []);
            _collision = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>A model's embedded physics, read from the game's pak01.</summary>
    public PhysAggregateData? Physics(string model)
    {
        if (_physics.TryGetValue(model, out var phys))
            return phys;
        var entry = _pak.FindEntry(Path.ChangeExtension(model, ".vmdl_c"));
        if (entry != null)
        {
            _pak.ReadEntry(entry, out var bytes);
            var res = new Resource();
            res.Read(new MemoryStream(bytes));
            _keep.Add(res);
            _models[model] = res.DataBlock as Model;
            phys = (res.DataBlock as Model)?.GetEmbeddedPhys();
        }
        return _physics[model] = phys;
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
