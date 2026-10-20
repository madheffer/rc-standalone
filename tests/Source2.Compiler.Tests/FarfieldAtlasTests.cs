using Source2.Compiler.Gpu;
using Source2.Compiler.Lighting;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The far field atlas stage by stage against tools/vrad3/ff_oracle.py's folder (vrad3's far field
/// routine alone, the atlas written after the lighting, the median, the dilate, the fireflies filter
/// and at the end): <c>FFATLAS=&lt;oracle folder&gt;|&lt;addon&gt;</c>. Our bake runs the same script
/// (script-ff.vrad3) on a copy; every ff/*.exr is compared, grouped by suffix. An optional third
/// part is a second oracle run's folder (Valve's dilate differs run to run): texels that differ
/// between Valve's two runs are counted apart.
/// </summary>
public sealed class FarfieldAtlasTests(ITestOutputHelper output)
{
    [Fact]
    public void AtlasIsValves()
    {
        if (Environment.GetEnvironmentVariable("FFATLAS") is not { Length: > 0 } spec || CS2Fixtures.StockPak() is not { } pak)
            return;
        var p = spec.Split('|');
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var work = Path.Combine(Path.GetTempPath(), "s2c_ffatlas");
        if (Directory.Exists(work))
            Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        foreach (var f in new[] { "scene_info.kv3", "scene_meshes.kv3", "lightmap_packing_geometry.dat", "lightmap_info.dat" })
            File.Copy(Path.Combine(p[0], f), Path.Combine(work, f));
        File.Copy(Path.Combine(p[0], "script-ff.vrad3"), Path.Combine(work, "script-gpu.vrad3"));
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", p[1]));
        var log = new StringWriter();
        OwnLightingBake.Run(work, content, log, farfieldOnly: true);
        output.WriteLine(log.ToString());
        var all = true;
        foreach (var group in Directory.GetFiles(Path.Combine(p[0], "ff"), "*.exr").GroupBy(Suffix).OrderBy(g => g.Key))
        {
            int same = 0, missing = 0;
            long texels = 0, unstable = 0;
            var worst = 0f;
            foreach (var valve in group)
            {
                var mine = Path.Combine(work, "ff", Path.GetFileName(valve));
                if (!File.Exists(mine))
                {
                    missing++;
                    continue;
                }
                var a = File.ReadAllBytes(mine);
                var b = File.ReadAllBytes(valve);
                if (a.AsSpan().SequenceEqual(b))
                {
                    same++;
                    continue;
                }
                var x = Vrad3Exr.Read(a);
                var y = Vrad3Exr.Read(b);
                var other = p.Length > 2 && File.Exists(Path.Combine(p[2], "ff", Path.GetFileName(valve))) ? Vrad3Exr.Read(File.ReadAllBytes(Path.Combine(p[2], "ff", Path.GetFileName(valve)))) : null;
                for (var c = 0; c < x.Planes.Length; c++)
                    for (var i = 0; i < x.Planes[c].Length; i++)
                    {
                        if (x.Planes[c][i].Equals(y.Planes[c][i]))
                            continue;
                        if (other != null && !other.Planes[c][i].Equals(y.Planes[c][i]))
                        {
                            unstable++;
                            continue;
                        }
                        texels++;
                        worst = MathF.Max(worst, MathF.Abs((float)x.Planes[c][i] - (float)y.Planes[c][i]));
                    }
            }
            output.WriteLine($"{(group.Key.Length == 0 ? "(final)" : group.Key)}: {same}/{group.Count()} same, {missing} missing, {texels} texel channels differ where Valve's runs agree (max |d| {worst}), {unstable} where they do not");
            all &= same == group.Count();
        }
        Assert.True(all);
    }

    // "_raw" of farfield_0_dc_z05_raw.exr, "" of the final farfield_0_dc_z05.exr.
    private static string Suffix(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var z = name.LastIndexOf("_z", StringComparison.Ordinal);
        return name[(z + 4)..];
    }
}
