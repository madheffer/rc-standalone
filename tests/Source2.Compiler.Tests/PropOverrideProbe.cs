using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>Scratch probe (PROPOVERRIDE=dir): every prop with a collision or surface property override.</summary>
public class PropOverrideProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("PROPOVERRIDE") is not { Length: > 0 } dir)
            return;
        foreach (var file in Directory.EnumerateFiles(dir, "*.vmap", SearchOption.AllDirectories))
        {
            try
            {
                foreach (var e in MapEntities.From(DmxBinary.ReadFile(file)))
                    foreach (var (k, v) in e.Keys)
                        if (k is "collision_override" or "surface_property_override" && v.Length > 0)
                            output.WriteLine($"{Path.GetFileName(file)} {e.ClassName}#{e.NodeId} {k}={v}");
            }
            catch (Exception ex)
            {
                output.WriteLine($"{Path.GetFileName(file)}: {ex.GetType().Name}");
            }
        }
    }
}
