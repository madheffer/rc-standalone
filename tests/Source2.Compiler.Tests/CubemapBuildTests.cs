using Source2.Compiler.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="CubemapBuild"/> against the game's own build cubemaps step: <c>CUBEBUILD_VPK</c> is the captured map's
/// package, <c>CUBEBUILD_GAME</c> the step's files as run_probe.py --keep-intermediates kept them (each
/// env_cubemap_&lt;handshake&gt;.exr, env_cubemap_array.vtex and .vtex_c), <c>CUBEBUILD_ADDON</c> the package's addon
/// (s2c_cubeprobe by default). Every file must be the game's byte for byte. Nothing of a dump is read.
/// </summary>
public sealed class CubemapBuildTests(ITestOutputHelper output)
{
    [Fact]
    public void BuildMakesTheGamesFiles()
    {
        if (Environment.GetEnvironmentVariable("CUBEBUILD_VPK") is not { Length: > 0 } vpk
            || Environment.GetEnvironmentVariable("CUBEBUILD_GAME") is not { Length: > 0 } dir)
            return;
        var addon = Environment.GetEnvironmentVariable("CUBEBUILD_ADDON") is { Length: > 0 } a ? a : "s2c_cubeprobe";
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game");
        var scripted = Environment.GetEnvironmentVariable("CUBEREPLAY_SCRIPTEDCAMERA") != "0";
        var result = CubemapBuild.Build(game, vpk, addon, scripted, new XunitWriter(output));
        Assert.NotNull(result);
        var failures = new List<string>();
        void Check(string name, byte[] ours)
        {
            var theirs = File.ReadAllBytes(Path.Combine(dir, name));
            var equal = ours.AsSpan().SequenceEqual(theirs);
            output.WriteLine($"{name}: {(equal ? "the game's, byte for byte" : "DIFFERS")} ({ours.Length} / {theirs.Length} bytes)");
            if (!equal)
                failures.Add(name);
        }
        Assert.Equal(Directory.GetFiles(dir, "env_cubemap_*.exr").Select(Path.GetFileName).Order(StringComparer.Ordinal),
            result.Exrs.Select(e => Path.GetFileName(e.RelativePath)).Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in result.Exrs)
            Check(Path.GetFileName(path), bytes);
        Check("env_cubemap_array.vtex", result.Vtex);
        Check("env_cubemap_array.vtex_c", result.Array);
        Assert.Empty(failures);
    }

    private sealed class XunitWriter(ITestOutputHelper output) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void WriteLine(string? value) => output.WriteLine(value ?? "");
    }
}
