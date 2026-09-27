using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>Exploration: every parameter and attribute of a compiled material in pak01. <c>MATINFO=&lt;materials/...vmat&gt;</c>.</summary>
public class MaterialInfoProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("MATINFO") is not { Length: > 0 } name)
            return;
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game", "csgo");
        using var package = new Package();
        package.Read(Path.Combine(game, "pak01_dir.vpk"));
        package.ReadEntry(package.FindEntry(name + "_c")!, out var bytes);
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        var mat = (Material)resource.DataBlock!;
        output.WriteLine($"shader {mat.ShaderName}");
        foreach (var (k, v) in mat.IntParams) output.WriteLine($"int {k} = {v}");
        foreach (var (k, v) in mat.FloatParams) output.WriteLine($"float {k} = {v}");
        foreach (var (k, v) in mat.VectorParams) output.WriteLine($"vector {k} = {v}");
        foreach (var (k, v) in mat.TextureParams) output.WriteLine($"texture {k} = {v}");
        foreach (var (k, v) in mat.IntAttributes) output.WriteLine($"intattr {k} = {v}");
        foreach (var (k, v) in mat.StringAttributes) output.WriteLine($"strattr {k} = {v}");
        foreach (var (k, v) in mat.DynamicExpressions) output.WriteLine($"dyn {k}");
    }
}
