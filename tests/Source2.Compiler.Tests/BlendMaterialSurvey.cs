using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: every csgo_environment_blend material in a pak, and how many
/// set F_USE_NEW_BLENDING (the material sampler only computes layers for
/// those; without it every sample reads layer 0) or have two surfaces.
/// <c>BLENDMATSURVEY=&lt;pak01_dir.vpk&gt;</c>.
/// </summary>
public class BlendMaterialSurvey(ITestOutputHelper output)
{
    [Fact]
    public void NewBlending()
    {
        if (Environment.GetEnvironmentVariable("BLENDMATSURVEY") is not { Length: > 0 } pak)
            return;
        using var package = new Package();
        package.Read(pak);
        int total = 0, newBlending = 0, twoSurfaces = 0, both = 0;
        var examples = new List<string>();
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
            if (resource.DataBlock is not Material mat || !mat.ShaderName.StartsWith("csgo_environment_blend", StringComparison.OrdinalIgnoreCase))
                continue;
            total++;
            var nb = mat.IntParams.TryGetValue("F_USE_NEW_BLENDING", out var v) && v != 0;
            var s1 = mat.StringAttributes.GetValueOrDefault("PhysicsSurfaceProperties1") ?? mat.StringAttributes.GetValueOrDefault("PhysicsSurfaceProperties");
            var s2 = mat.StringAttributes.GetValueOrDefault("PhysicsSurfaceProperties2") ?? mat.StringAttributes.GetValueOrDefault("PhysicsSurfaceProperties");
            var two = s1 != s2;
            if (nb)
                newBlending++;
            if (two)
                twoSurfaces++;
            if (nb && two)
            {
                both++;
                examples.Add(entry.GetFullPath());
            }
        }
        output.WriteLine($"environment_blend materials {total}: F_USE_NEW_BLENDING {newBlending}, two surfaces {twoSurfaces}, both {both}");
        foreach (var e in examples)
            output.WriteLine("  " + e);
        if (Environment.GetEnvironmentVariable("BLENDMATSURVEY_OUT") is { } outPath)
            File.WriteAllLines(outPath, examples.Select(e => e.Replace(".vmat_c", ".vmat")));
    }
}
