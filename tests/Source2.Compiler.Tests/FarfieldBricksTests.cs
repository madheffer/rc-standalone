using Source2.Compiler.Gpu;
using Source2.Compiler.Lighting;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="FarfieldBricks"/> through our bake host's metrics on a vrad3 job folder vrad3 ran:
/// <c>FFBRICKS=&lt;job folder&gt;|&lt;addon&gt;</c>. The job's inputs are copied aside, the far field alone
/// is baked there, and each farfield_&lt;n&gt;_bricks.kv3 is compared with vrad3's.
/// </summary>
public sealed class FarfieldBricksTests(ITestOutputHelper output)
{
    [Fact]
    public void BricksAreValves()
    {
        if (Environment.GetEnvironmentVariable("FFBRICKS") is not { Length: > 0 } spec || CS2Fixtures.StockPak() is not { } pak)
            return;
        var p = spec.Split('|');
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var work = Path.Combine(Path.GetTempPath(), "s2c_ffbricks");
        if (Directory.Exists(work))
            Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        foreach (var f in new[] { "scene_info.kv3", "scene_meshes.kv3", "script-gpu.vrad3", "lightmap_packing_geometry.dat", "lightmap_info.dat" })
            File.Copy(Path.Combine(p[0], f), Path.Combine(work, f));
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", p[1]));
        var log = new StringWriter();
        OwnLightingBake.Run(work, content, log, farfieldOnly: true);
        output.WriteLine(log.ToString());
        var all = true;
        foreach (var valve in Directory.GetFiles(p[0], "farfield_*_bricks.kv3"))
        {
            var ours = File.ReadAllBytes(Path.Combine(work, Path.GetFileName(valve)));
            var theirs = File.ReadAllBytes(valve);
            var at = 0;
            while (at < Math.Min(ours.Length, theirs.Length) && ours[at] == theirs[at])
                at++;
            output.WriteLine($"{Path.GetFileName(valve)}: ours {ours.Length} bytes, valve {theirs.Length}, first difference at {at}");
            all &= ours.AsSpan().SequenceEqual(theirs);
        }
        Assert.True(all);
    }
}
