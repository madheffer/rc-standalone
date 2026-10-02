using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>INSTPROPS=&lt;vmap&gt;</c>): the entity classes and models a map
/// reaches through instances, counted.
/// </summary>
public class InstancePropsProbe(ITestOutputHelper output)
{
    [Fact]
    public void Count()
    {
        if (Environment.GetEnvironmentVariable("INSTPROPS") is not { } path)
            return;
        var (_, entities) = Maps.MapMeshes.ReadWithEntities(DmxBinary.ReadFile(path));
        foreach (var g in entities.Where(n => n.Through.Count > 0)
                     .Select(n => n.Element.Get<DmxBinary.Element>("entity_properties"))
                     .GroupBy(k => $"{k?.Get<string>("classname")} solid {k?.Get<string>("solid")} {k?.Get<string>("model")}"))
            output.WriteLine($"{g.Count()} {g.Key}");
    }
}
