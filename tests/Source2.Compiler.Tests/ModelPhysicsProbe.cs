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
            // MODELPHYS_HULLS=1: each hull's stored bounds as the file holds them,
            // as the reader gives them, and the extremes of its vertices.
            if (Environment.GetEnvironmentVariable("MODELPHYS_HULLS") == "1")
                foreach (var h in s.Hulls)
                {
                    var v = h.Shape.GetVertexPositions().ToArray();
                    static string Hex(System.Numerics.Vector3 x) => $"{BitConverter.SingleToUInt32Bits(x.X):x8} {BitConverter.SingleToUInt32Bits(x.Y):x8} {BitConverter.SingleToUInt32Bits(x.Z):x8}";
                    var hull = h.Shape.Data;
                    var bounds = hull.ContainsKey("m_Bounds") ? hull.GetSubCollection("m_Bounds") : null;
                    output.WriteLine($"  hull: reader min {Hex(h.Shape.Min)} max {Hex(h.Shape.Max)}; vertices min {Hex(v.Aggregate(System.Numerics.Vector3.Min))} max {Hex(v.Aggregate(System.Numerics.Vector3.Max))}"
                        + $"; stored {string.Join(" ", bounds?.Children.Select(c => $"{c.Key}={c.Value}") ?? [])}");
                }
        }
    }
}
