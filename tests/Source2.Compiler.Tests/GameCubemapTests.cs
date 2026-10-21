using Source2.Compiler.Cubemaps;
using Source2.Compiler.Gpu;
using Source2.Compiler.Lighting;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Against the game's own cubemap build (tools/cubehost/run_probe.py --keep-intermediates: the engine in our
/// own process, its _buildcubemaps files copied before it deletes them), <c>CUBEGAME=&lt;intermediates dir&gt;</c>:
/// <list type="bullet">
/// <item>our .vtex source text is the game's, byte for byte;</item>
/// <item>each game EXR re-written from its own values through <see cref="Vrad3Exr"/> is the same file;</item>
/// <item><see cref="CubemapArrayVtex.Build"/> on the game's EXRs is the game's env_cubemap_array.vtex_c.</item>
/// </list>
/// </summary>
public sealed class GameCubemapTests(ITestOutputHelper output)
{
    [Fact]
    public void AgainstTheGamesBuild()
    {
        if (Environment.GetEnvironmentVariable("CUBEGAME") is not { Length: > 0 } dir || CS2Fixtures.StockPak() is not { } stock)
            return;
        var bin = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(stock)!, "..", "bin", "win64"));
        var shaderVpk = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(stock)!, "..", "core", "shaders_vulkan_dir.vpk"));
        var exrs = Directory.GetFiles(dir, "env_cubemap_*.exr").Order(StringComparer.Ordinal).ToList();
        var rel = exrs.Select(p => $"_buildcubemaps/{Path.GetFileName(p)}").ToList();
        var gameVtex = File.ReadAllBytes(Path.Combine(dir, "env_cubemap_array.vtex"));
        var ourVtex = System.Text.Encoding.UTF8.GetBytes(CubemapArrayVtex.Source(rel));
        output.WriteLine($"{exrs.Count} cubes; .vtex source {(ourVtex.AsSpan().SequenceEqual(gameVtex) ? "equal" : "DIFFERENT")}");
        Assert.Equal(gameVtex, ourVtex);

        foreach (var p in exrs)
        {
            var bytes = File.ReadAllBytes(p);
            var exr = Vrad3Exr.Read(bytes);
            output.WriteLine($"{Path.GetFileName(p)}: {exr.Width} x {exr.Height}, channels {string.Join("", exr.Channels)}, rewrite {(exr.Write().AsSpan().SequenceEqual(bytes) ? "equal" : "DIFFERENT")}");
            Assert.Equal(bytes, exr.Write());
        }

        var game = File.ReadAllBytes(Path.Combine(dir, "env_cubemap_array.vtex_c"));
        var red2 = WorldPhysicsAuthorTests.Trees(game)["RED2"];
        var searchPath = (string)red2["m_InputDependencies"].Values.First()["m_SearchPath"]!;
        var cubes = exrs.Select((p, i) =>
        {
            var bytes = File.ReadAllBytes(p);
            return new CubemapArrayVtex.Cube(rel[i], bytes, CubemapArrayVtex.FacesFromStrip(LightmapTextures.Bitmap(Vrad3Exr.Read(bytes))));
        }).ToList();
        var size = (int)Math.Sqrt(cubes[0].Faces[0].Length / 4);
        using var blur = new GpuCubemapBlur(shaderVpk);
        var ours = CubemapArrayVtex.Build(cubes, size, faces => blur.Filter(faces, size), "_buildcubemaps/env_cubemap_array.vtex", ourVtex, searchPath, bin);
        var first = 0;
        while (first < Math.Min(ours.Length, game.Length) && ours[first] == game[first])
            first++;
        output.WriteLine($"array: ours {ours.Length} bytes, the game's {game.Length}, first difference at {(first == game.Length && ours.Length == game.Length ? "none" : first.ToString())}");
        foreach (var line in WorldPhysicsAuthorTests.Compare(game, ours).Take(12))
            output.WriteLine("  " + line);
        Assert.Equal(game, ours);
    }
}
