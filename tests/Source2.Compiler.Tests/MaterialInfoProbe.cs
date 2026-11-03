using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>Exploration: every parameter and attribute of a compiled material in pak01. <c>MATINFO=&lt;materials/...vmat&gt;</c>.</summary>
public class MaterialInfoProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("MATINFO") is not { Length: > 0 } name)
            return;
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game", "csgo");
        // csgo's pak, then core's (materials/error.vmat lives there).
        using var package = new Package();
        package.Read(Path.Combine(game, "pak01_dir.vpk"));
        using var core = new Package();
        core.Read(Path.Combine(game, "..", "core", "pak01_dir.vpk"));
        byte[] bytes;
        if (package.FindEntry(name + "_c") is { } entry)
            package.ReadEntry(entry, out bytes);
        else
            core.ReadEntry(core.FindEntry(name + "_c")!, out bytes);
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        var mat = (Material)resource.DataBlock!;
        output.WriteLine($"shader {mat.ShaderName}");
        foreach (var (k, v) in mat.IntParams) output.WriteLine($"int {k} = {v}");
        foreach (var (k, v) in mat.FloatParams) output.WriteLine($"float {k} = {v}");
        foreach (var (k, v) in mat.VectorParams) output.WriteLine($"vector {k} = {v}");
        foreach (var (k, v) in mat.TextureParams) output.WriteLine($"texture {k} = {v}");
        foreach (var (k, v) in mat.IntAttributes) output.WriteLine($"intattr {k} = {v}");
        foreach (var (k, v) in mat.StringAttributes) output.WriteLine($"strattr {k} = {v}");
        foreach (var (k, v) in mat.DynamicExpressions) output.WriteLine($"dyn {k}");
    }

    /// <summary><c>MATKEEPS=1</c>: every material of csgo's pak whose meshes MapMeshCorners.KeepsTexcoords leaves unshifted.</summary>
    [Fact]
    public void KeptTexcoords()
    {
        if (Environment.GetEnvironmentVariable("MATKEEPS") != "1")
            return;
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game", "csgo");
        var content = new Source2.Compiler.Maps.GameContent(Path.Combine(game, "pak01_dir.vpk"));
        using var package = new Package();
        package.Read(Path.Combine(game, "pak01_dir.vpk"));
        foreach (var entry in package.Entries!.GetValueOrDefault("vmat_c") ?? [])
        {
            var path = entry.GetFullPath()[..^2];
            Source2.Compiler.Maps.SettleWorld.MaterialInfo? info;
            try
            {
                info = content.Material(path);
            }
            catch (Exception)
            {
                continue;
            }
            if (info != null && Source2.Compiler.Maps.MapMeshCorners.KeepsTexcoords(info))
                output.WriteLine($"KEEPS {path} {info.Shader}");
        }
    }

    /// <summary>
    /// <c>MATSCAN=&lt;shader&gt;|&lt;attribute&gt;</c>: every material of csgo's pak on that shader, with the int attribute's
    /// stored value (or "absent") and its texcoord set 1 parameters, then a count per value.
    /// </summary>
    [Fact]
    public void ScanAttribute()
    {
        if (Environment.GetEnvironmentVariable("MATSCAN") is not { Length: > 0 } spec || spec.Split('|') is not [var shader, var attribute])
            return;
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game", "csgo");
        using var package = new Package();
        package.Read(Path.Combine(game, "pak01_dir.vpk"));
        var counts = new Dictionary<string, int>();
        foreach (var entry in package.Entries!.GetValueOrDefault("vmat_c") ?? [])
        {
            package.ReadEntry(entry, out var bytes);
            using var resource = new Resource();
            try
            {
                resource.Read(new MemoryStream(bytes));
            }
            catch (Exception)
            {
                continue;
            }
            if (resource.DataBlock is not Material mat || !mat.ShaderName.Equals(shader + ".vfx", StringComparison.OrdinalIgnoreCase))
                continue;
            var value = mat.IntAttributes.TryGetValue(attribute, out var v) ? v.ToString() : "absent";
            var set = mat.IntParams.TryGetValue("g_nUVSet1", out var s) ? s.ToString() : "-";
            var rot = mat.FloatParams.TryGetValue("g_flTexCoordRotation1", out var r) ? r.ToString() : "-";
            var scale = mat.VectorParams.TryGetValue("g_vTexCoordScale1", out var sc) ? $"{sc.X},{sc.Y}" : "-";
            var key = $"{value} set {(set == "-" ? "-" : "given")} transformed {(rot is not ("-" or "0") || scale is not ("-" or "1,1") ? "yes" : "no")}";
            counts[key] = counts.GetValueOrDefault(key) + 1;
            if (value != "absent" || rot is not ("-" or "0") || scale is not ("-" or "1,1"))
                output.WriteLine($"{entry.GetFullPath()} {attribute}={value} g_nUVSet1={set} rotation={rot} scale={scale}");
        }
        foreach (var (k, n) in counts.OrderBy(x => x.Key))
            output.WriteLine($"COUNT {k}: {n}");
    }
}
