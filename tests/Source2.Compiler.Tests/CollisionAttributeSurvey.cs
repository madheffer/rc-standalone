using ValvePak;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: the collision attribute table of every local compiled map's
/// world_physics.vmdl_c, one line per attribute: group, then the interact-as,
/// -with and -exclude names with their hashes. <c>ATTRSURVEY=1</c>.
/// </summary>
public class CollisionAttributeSurvey(ITestOutputHelper output)
{
    [Fact]
    public void Survey()
    {
        if (Environment.GetEnvironmentVariable("ATTRSURVEY") != "1")
            return;
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        var vpks = Directory.GetDirectories(Path.Combine(cs2, "game", "csgo_addons"))
            .SelectMany(a => Directory.Exists(Path.Combine(a, "maps")) ? Directory.GetFiles(Path.Combine(a, "maps"), "*.vpk") : []).ToList();
        vpks.Add(Path.Combine(Path.GetTempPath(), "worldcol7", "atixref_compiled.vpk"));
        var seen = new HashSet<string>();
        foreach (var vpk in vpks.Where(File.Exists))
        {
            using var package = new Package();
            package.Read(vpk);
            var entry = package.Entries?.SelectMany(kv => kv.Value).FirstOrDefault(e => e.GetFullPath().EndsWith("/world_physics.vmdl_c", StringComparison.Ordinal));
            if (entry == null)
                continue;
            package.ReadEntry(entry, out var bytes);
            var phys = WorldPhysicsAuthorTests.Trees(bytes)["PHYS"];
            foreach (var a in phys["m_collisionAttributes"]!.Values)
            {
                string Names(string strings, string hashes)
                    => string.Join(",", a[strings]!.Values.Zip(a[hashes]!.Values, (s, h) => $"{s}#{h}"));
                var line = $"{a["m_CollisionGroupString"]}#{a["m_CollisionGroup"]} as[{Names("m_InteractAsStrings", "m_InteractAs")}] with[{Names("m_InteractWithStrings", "m_InteractWith")}] ex[{Names("m_InteractExcludeStrings", "m_InteractExclude")}]";
                if (seen.Add(Path.GetFileNameWithoutExtension(vpk) + line))
                    output.WriteLine($"{Path.GetFileNameWithoutExtension(vpk)}: {line}");
            }
        }
    }
}
