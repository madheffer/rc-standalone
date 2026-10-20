using Source2.Compiler.Lighting;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="LightmapAmeliorate"/> against vrad3 on the same input: AMELORACLE=&lt;dir&gt;[;&lt;dir&gt;...], each
/// holding pre_amel (an image saved right before the command), out_amel (vrad3 run on pre_amel with
/// lightmap_ameliorate_block_compression_artefacts -mips 1, tools/vrad3/post_oracle.py chain),
/// chart_repacking.dat (the file that run wrote) and the job's lightmap_packing_geometry.dat. Channels
/// compared at the half floats lightmap_image_save writes; the luxel and shadow records and the repacking
/// file byte for byte.
/// </summary>
public sealed class LightmapAmeliorateTests(ITestOutputHelper output)
{
    [Fact]
    public void MatchesVrad3()
    {
        if (Environment.GetEnvironmentVariable("AMELORACLE") is not { Length: > 0 } dirs)
            return;
        var differ = 0;
        foreach (var dir in dirs.Split(';'))
        {
            output.WriteLine(dir);
            differ += Compare(dir);
        }
        Assert.Equal(0, differ);
    }

    private int Compare(string dir)
    {
        var image = LightmapImage.Load(Path.Combine(dir, "pre_amel"));
        var packing = PackingGeometry.Read(File.ReadAllBytes(Path.Combine(dir, "lightmap_packing_geometry.dat")));
        // FillBlocks ties (equal counts, equal squared lengths) go by a slot order that hashes a stack value
        // vrad3's job thread does not share with its workers: either tied colour is Valve's.
        var ties = new HashSet<(int, int, int)>();
        LightmapAmeliorate.TieObserver = (x, y, c) => { lock (ties) ties.Add((x, y, c)); };
        byte[]? repacking;
        try
        {
            repacking = LightmapAmeliorate.Run(image, packing.Charts, 1, output.WriteLine);
        }
        finally
        {
            LightmapAmeliorate.TieObserver = null;
        }
        output.WriteLine($"{ties.Count} tied blocks");
        var valve = LightmapImage.Load(Path.Combine(dir, "out_amel"));
        var differ = 0;
        for (var c = 0; c < LightmapImage.ChannelCount; c++)
        {
            if (image.Channels[c] is not { } ours || valve.Channels[c] is not { } theirs)
                continue;
            for (var k = 0; k < 4; k++)
            {
                if ((ours.Mask >> k & 1) == 0)
                    continue;
                int unequal = 0, first = -1, tied = 0;
                for (var i = 0; i < image.Width * image.Height; i++)
                    if (BitConverter.HalfToInt16Bits(LightmapImage.ToHalfAway(ours.Data[i * 4 + k])) != BitConverter.HalfToInt16Bits(LightmapImage.ToHalfAway(theirs.Data[i * 4 + k])))
                    {
                        if (ties.Contains((i % image.Width / 4 * 4, i / image.Width / 4 * 4, c)) && image.ChartId(i) == 0xffffffff)
                            tied++;
                        else if (unequal++ == 0)
                            first = i;
                    }
                if (tied > 0)
                    output.WriteLine($"channel {c}.{"RGBA"[k]}: {tied} empty texels of tied blocks differ");
                if (unequal > 0)
                {
                    differ++;
                    output.WriteLine($"channel {c}.{"RGBA"[k]}: {unequal} texels differ, first {first % image.Width},{first / image.Width}: ours {ours.Data[first * 4 + k]:R} valve {theirs.Data[first * 4 + k]:R}");
                }
            }
        }
        // The flags word's bits above the three flags are a stack value vrad3 leaves in cleared texels
        // (different on each run of Valve's own); compared without them.
        foreach (var records in new[] { image.LuxelRecords, valve.LuxelRecords })
            for (var i = 0; i < records.Length; i += 12)
                (records[i + 8], records[i + 9], records[i + 10], records[i + 11]) = ((byte)(records[i + 8] & 7), 0, 0, 0);
        differ += Records("luxel", image.LuxelRecords, valve.LuxelRecords, 12, image.Width);
        differ += Records("shadow", image.ShadowRecords, valve.ShadowRecords, 20, image.Width);
        var expected = File.ReadAllBytes(Path.Combine(dir, "chart_repacking.dat"));
        Assert.NotNull(repacking);
        if (!repacking.AsSpan().SequenceEqual(expected))
        {
            differ++;
            var at = 0;
            while (at < Math.Min(repacking.Length, expected.Length) && repacking[at] == expected[at])
                at++;
            output.WriteLine($"chart_repacking.dat: {repacking.Length} bytes vs {expected.Length}, first difference at {at}");
        }
        return differ;
    }

    private int Records(string name, byte[] ours, byte[] valve, int size, int width)
    {
        int unequal = 0, first = -1;
        for (var i = 0; i < ours.Length / size; i++)
            if (!ours.AsSpan(i * size, size).SequenceEqual(valve.AsSpan(i * size, size)))
            {
                if (unequal++ == 0)
                    first = i;
            }
        if (unequal == 0)
            return 0;
        output.WriteLine($"{name} records: {unequal} differ, first {first % width},{first / width}: ours {Convert.ToHexString(ours, first * size, size)} valve {Convert.ToHexString(valve, first * size, size)}");
        return 1;
    }
}
