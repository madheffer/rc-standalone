using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: the shader and feature flags of every material a .vmap's
/// meshes use, read from the game's pak01 (or the addon's compiled copy).
/// <c>MATSURVEY=&lt;vmap&gt;|&lt;pak01_dir.vpk&gt;|&lt;addon game dir&gt;</c>.
/// </summary>
public class MaterialShaderSurvey(ITestOutputHelper output)
{
    [Fact]
    public void ShadersOfMapMaterials()
    {
        if (Environment.GetEnvironmentVariable("MATSURVEY") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var doc = DmxBinary.Read(File.ReadAllBytes(parts[0]));
        using var pak = new Package();
        pak.Read(parts[1]);
        var names = doc.OfType("CMapMesh")
            .SelectMany(m => m.Get<DmxBinary.Element>("meshData")?.Get<object?[]>("materials") ?? [])
            .Select(x => (x as string ?? "").Replace('\\', '/').ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal);
        foreach (var name in names)
        {
            byte[]? bytes = null;
            if (pak.FindEntry(name + "_c") is { } entry)
                pak.ReadEntry(entry, out bytes);
            else if (parts.Length > 2 && File.Exists(Path.Combine(parts[2], name + "_c")))
                bytes = File.ReadAllBytes(Path.Combine(parts[2], name + "_c"));
            if (bytes == null)
            {
                output.WriteLine($"MAT {Path.GetFileNameWithoutExtension(name)} | missing");
                continue;
            }
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            if (resource.DataBlock is not Material mat)
                continue;
            var flags = string.Join(",", mat.IntParams.Where(p => p.Key.StartsWith("F_", StringComparison.Ordinal) && p.Value != 0).Select(p => $"{p.Key}={p.Value}"));
            output.WriteLine($"MAT {Path.GetFileNameWithoutExtension(name)} | {mat.ShaderName} | {flags}");
        }
    }
}
