using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>Exploration: a model's embedded physics, parts, shapes and joints. <c>MODELPHYS=&lt;addon&gt;|&lt;model&gt;</c>.</summary>
public class ModelPhysicsProbe(ITestOutputHelper output)
{
    [Fact]
    public void PrintPhysics()
    {
        if (Environment.GetEnvironmentVariable("MODELPHYS") is not { Length: > 0 } spec || spec.Split('|') is not [var addon, var model])
            return;
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        var game = Path.Combine(cs2, "game");
        using var content = new Maps.GameContent(Path.Combine(game, "csgo", "pak01_dir.vpk"), Path.Combine(game, "csgo_addons", addon));
        var phys = content.Physics(model);
        if (phys == null)
        {
            output.WriteLine("no physics");
            return;
        }
        output.WriteLine($"parts {phys.Parts.Length}, joints {(phys.Data.ContainsKey("m_joints") ? phys.Data.GetArray<ValveKeyValue.KVObject>("m_joints").Length : 0)}, bind pose {phys.BindPose.Length}, bones {string.Join(",", phys.BoneNames ?? [])}");
        for (var i = 0; i < phys.Parts.Length; i++)
        {
            var s = phys.Parts[i].Shape;
            output.WriteLine($"part {i}: mass {phys.Parts[i].Mass} spheres {s.Spheres.Length} capsules {s.Capsules.Length} hulls {s.Hulls.Length} meshes {s.Meshes.Length}");
        }
    }
}
