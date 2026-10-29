using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>MATERIALS=&lt;path&gt;;&lt;path&gt;...</c>, .vmat paths in the game's paks): each compiled material's
/// shader, int, float, vector and texture parameters, and its render attributes.
/// </summary>
public class MaterialProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("MATERIALS") is not { Length: > 0 } list)
            return;
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game");
        foreach (var path in list.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[]? bytes = null;
            foreach (var pak in new[] { Path.Combine(game, "csgo", "pak01_dir.vpk"), Path.Combine(game, "core", "pak01_dir.vpk") })
            {
                using var p = new ValvePak.Package();
                p.Read(pak);
                if (bytes == null && p.FindEntry(path + "_c") is { } e)
                    p.ReadEntry(e, out bytes);
            }
            if (bytes == null)
            {
                output.WriteLine($"{path}: not found");
                continue;
            }
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            var m = (Material)resource.DataBlock!;
            output.WriteLine($"{path}: shader {m.ShaderName}");
            output.WriteLine("  int " + string.Join(", ", m.IntParams.Select(kv => $"{kv.Key}={kv.Value}")));
            output.WriteLine("  float " + string.Join(", ", m.FloatParams.Select(kv => $"{kv.Key}={kv.Value}")));
            output.WriteLine("  vector " + string.Join(", ", m.VectorParams.Select(kv => $"{kv.Key}={kv.Value}")));
            output.WriteLine("  texture " + string.Join(", ", m.TextureParams.Select(kv => $"{kv.Key}={kv.Value}")));
            output.WriteLine("  attributes " + string.Join(", ", m.IntAttributes.Select(kv => $"{kv.Key}={kv.Value}")
                .Concat(m.FloatAttributes.Select(kv => $"{kv.Key}={kv.Value}"))));
        }
    }
}
