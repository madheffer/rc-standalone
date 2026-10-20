using Source2.Compiler.Lighting;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="LightmapOidn"/> against vrad3 on the same input: OIDNORACLE=&lt;dir&gt; holding pre_post (vrad3's
/// lightmap_image_save after the compute), out_oidn and out_shoidn (tools/vrad3/post_oracle.py run on it with
/// lightmap_image_filter_oidn 1 / lightmap_image_filter_sh_oidn 1), and OIDNJOB=&lt;the job's _vrad3 folder&gt; for
/// lightmap_packing_geometry.dat. Each channel compared at the half floats lightmap_image_save writes.
/// </summary>
public sealed class LightmapOidnTests(ITestOutputHelper output)
{
    private static string Bin => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(CS2Fixtures.StockPak()!)!, "..", "bin", "win64"));

    [Theory]
    [InlineData("out_oidn", false)]
    [InlineData("out_shoidn", true)]
    public void FiltersMatchVrad3(string oracle, bool sh)
    {
        if (Environment.GetEnvironmentVariable("OIDNORACLE") is not { Length: > 0 } dir || Environment.GetEnvironmentVariable("OIDNJOB") is not { Length: > 0 } job
            || CS2Fixtures.StockPak() is null || !Directory.Exists(Path.Combine(dir, oracle)))
            return;
        var image = LightmapImage.Load(Path.Combine(dir, "pre_post"));
        var packing = PackingGeometry.Read(File.ReadAllBytes(Path.Combine(job, "lightmap_packing_geometry.dat")));
        using var oidn = Oidn.Open(Bin) ?? throw new InvalidOperationException("no OpenImageDenoise.dll");
        output.WriteLine($"OIDN device {oidn.DeviceName}, type {oidn.DeviceType}");
        if (sh)
            LightmapOidn.FilterSh(image, packing, 1, oidn);
        else
            LightmapOidn.Filter(image, packing, 1, oidn);
        var differ = 0;
        for (var c = 0; c < LightmapImage.ChannelCount; c++)
        {
            var path = Path.Combine(dir, oracle, $"channel{c:00}.exr");
            if (image.Channels[c] is not { } channel || !File.Exists(path))
                continue;
            var valve = Vrad3Exr.Read(File.ReadAllBytes(path));
            foreach (var name in valve.Channels)
            {
                var k = "RGBA".IndexOf(name[0]);
                if ((channel.Mask >> k & 1) == 0)
                    continue;
                var plane = valve.Plane(name);
                int unequal = 0, first = -1;
                for (var i = 0; i < plane.Length; i++)
                    if (BitConverter.HalfToInt16Bits(LightmapImage.ToHalfAway(channel.Data[i * 4 + k])) != BitConverter.HalfToInt16Bits(plane[i]))
                    {
                        if (unequal++ == 0)
                            first = i;
                    }
                if (unequal > 0)
                {
                    differ++;
                    output.WriteLine($"channel {c} {name}: {unequal} texels differ, first {first % image.Width},{first / image.Width}: ours {channel.Data[first * 4 + k]:R} valve {(float)plane[first]:R}");
                }
                else
                    output.WriteLine($"channel {c} {name}: identical");
            }
        }
        Assert.Equal(0, differ);
    }
}
