using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>Scratch (PROPCHECK=vmap): the prop_statics of a map and their override keys, and what our collision lookup finds.</summary>
public class PropOverrideCheck(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("PROPCHECK") is not { Length: > 0 } vmap || CS2Fixtures.StockPak() is not { } pak)
            return;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", "s2c_rc_probe"));
        foreach (var e in MapEntities.From(DmxBinary.ReadFile(vmap)).Where(e => e.ClassName == "prop_static"))
        {
            string K(string k) => e.Keys.FirstOrDefault(x => x.Key == k).Value ?? "-";
            output.WriteLine($"{e.NodeId} {K("model")} solid {K("solid")} co '{K("collision_override")}' so '{K("surface_property_override")}' lookup {content.CollisionProperty(K("collision_override"))}");
        }
    }
}
