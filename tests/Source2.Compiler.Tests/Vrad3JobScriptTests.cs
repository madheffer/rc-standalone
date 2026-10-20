using Source2.Compiler.Lighting;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="Vrad3Job.Script"/>'s process_all_lightmap_blocks routine against resourcecompiler's
/// (probe01's -lightmapCompressionDisabled, -lightmapVRadQuality 1 and 2 builds, 2026-10-20;
/// tools/re/out/captures/probe01_options): quality 0's median, quality 1 and 2's OIDN filters and the
/// ameliorate pass after the weld, and each quality's chart colour sample counts.
/// </summary>
public sealed class Vrad3JobScriptTests
{
    private static string Routine(int quality, int min, int max, bool repack) =>
        "process_all_lightmap_blocks:\n"
        + "    lightmap_image_consolidate_small_charts 1\n"
        + "    lightmap_image_remove_fireflies 4.0 fireflies.exr\n"
        + (quality == 0 ? "    lightmap_image_filter_median 7\n" : "    lightmap_image_filter_oidn 1\n    lightmap_image_filter_sh_oidn 1\n")
        + "    lightmap_image_dilate_over_invalid_jfa\n"
        + "    lightmap_image_encode_ahd\n"
        + "    lightmap_image_fill_gutters 1\n"
        + "    uber_weld lightmap_seams.dat\n"
        + (repack ? "    lightmap_ameliorate_block_compression_artefacts -mips 1\n" : "")
        + "    lightmap_image_write_ahd irradiance.exr directional_irradiance.exr\n"
        + "    lightmap_image_write_direct_light_shadows direct_light_shadows.exr -indexlights 4 \n"
        + $"    lightmap_image_write_chart_color   lightmap_chart_color.exr {min} {max}\n"
        + "    done\n";

    [Theory]
    [InlineData(0, 16, 16)]
    [InlineData(1, 128, 512)]
    [InlineData(2, 512, 4096)]
    public void RoutineIsResourcecompilers(int quality, int min, int max)
    {
        var repack = LightmapChartRepacking.RepacksAt(quality);
        var script = Vrad3Job.Script(4096, 4096, 1024, 1, [], quality, repack);
        var start = script.IndexOf("process_all_lightmap_blocks:", StringComparison.Ordinal);
        var end = script.IndexOf("    done\n", start, StringComparison.Ordinal) + "    done\n".Length;
        Assert.Equal(Routine(quality, min, max, repack), script[start..end]);
    }

    [Fact]
    public void NoRepackDropsTheAmeliorate()
    {
        var script = Vrad3Job.Script(4096, 4096, 1024, 1, [], 1, repack: false);
        Assert.DoesNotContain("lightmap_ameliorate_block_compression_artefacts", script, StringComparison.Ordinal);
        Assert.Contains("lightmap_image_filter_oidn 1", script, StringComparison.Ordinal);
    }
}
