using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Every material a map's meshes use, with the compiled material's
/// <c>mapbuilder.*</c> and <c>tools.*</c> attributes, read from the game's
/// <c>pak01_dir.vpk</c> and the addon's own package. Exploration for the .rte
/// inclusion rules. <c>MATVIS=&lt;vmap&gt;;&lt;addon vpk or dir&gt;</c>.
/// </summary>
public class MaterialVisAttributes(ITestOutputHelper output)
{
    [Fact]
    public void List()
    {
        if (Environment.GetEnvironmentVariable("MATVIS") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split(';');
        var materials = Source2.Compiler.Maps.MapMeshes.Read(DmxBinary.ReadFile(parts[0]))
            .SelectMany(m => m.Faces).Select(f => f.Material).Distinct().OrderBy(x => x).ToList();
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        var packages = new List<Package>();
        foreach (var dir in new[] { "csgo", "core" })
        {
            var package = new Package();
            package.Read(Path.Combine(cs2, "game", dir, "pak01_dir.vpk"));
            packages.Add(package);
        }
        foreach (var name in materials)
        {
            var compiled = name.Replace('\\', '/') + "_c";
            byte[]? bytes = null;
            if (packages.Select(p => (Package: p, Entry: p.FindEntry(compiled))).FirstOrDefault(x => x.Entry is not null) is { Entry: { } entry } found)
                found.Package.ReadEntry(entry, out bytes);
            else
            {
                var loose = Path.Combine(parts[1], compiled);
                if (File.Exists(loose))
                    bytes = File.ReadAllBytes(loose);
            }
            if (bytes is null)
            {
                output.WriteLine($"{name}: not found");
                continue;
            }
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            var material = (Material)resource.DataBlock!;
            var attributes = material.IntAttributes.Where(a => a.Key.StartsWith("mapbuilder", StringComparison.OrdinalIgnoreCase)
                                                             || a.Key.StartsWith("tools.", StringComparison.OrdinalIgnoreCase))
                                                   .Select(a => $"{a.Key}={a.Value}");
            output.WriteLine($"{name}: shader {material.ShaderName}; {string.Join(" ", attributes)}");
        }
    }
}
