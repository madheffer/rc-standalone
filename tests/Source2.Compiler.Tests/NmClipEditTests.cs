using System.Numerics;
using Source2.Compiler.Anim;
using Source2.Compiler.Kv3;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes.ModelAnimation2;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="NmClipEdit"/> on Valve's own clips. Every edit must leave what it does not touch
/// exactly as RC wrote it: that is what keeps an edited clip as loadable as the stock one.
/// </summary>
public class NmClipEditTests
{
    private const string DrawM4a4 = "animation/anims/viewmodel/rifle/rifle_m4a4/draw_m4a4.vnmclip_c";

    private static byte[]? Stock(string path)
    {
        if (CS2Fixtures.StockPak() is not { } pakPath) return null;
        using var pak = new Package();
        pak.Read(pakPath);
        var e = pak.FindEntry(path);
        if (e is null) return null;
        pak.ReadEntry(e, out var bytes);
        return bytes;
    }

    private static byte[] Data(byte[] vnmclipC)
    {
        using var res = new Resource();
        res.Read(new MemoryStream(vnmclipC), verifyFileSize: false);
        var b = res.GetBlockByType(BlockType.DATA)!;
        return vnmclipC.AsSpan((int)b.Offset, (int)b.Size).ToArray();
    }

    [Fact]
    public void Rebuilding_every_frame_with_nothing_replaced_is_byte_identical()
    {
        if (CS2Fixtures.StockPak() is not { } pakPath) { CS2Fixtures.Skip("pak01_dir.vpk"); return; }
        using var pak = new Package();
        pak.Read(pakPath);
        var n = 0;
        foreach (var e in pak.Entries["vnmclip_c"].Where(e => e.GetFullPath().StartsWith("animation/anims/viewmodel/", StringComparison.Ordinal)).Take(300))
        {
            pak.ReadEntry(e, out var bytes);
            var data = Data(bytes);
            var t = Kv3Tree.Read(data);
            foreach (var c in NmClipEdit.Clips(t))
                NmClipCodec.Rebuild(t, c, Enumerable.Range(0, NmClipCodec.FrameCount(t, c)).ToArray(), new Dictionary<int, TrackPose[]>());
            var (b1, b2, bl, _) = t.Encode();
            Assert.True(b1.AsSpan().SequenceEqual(t.ReadSections.Buffer1) && b2.AsSpan().SequenceEqual(t.ReadSections.Buffer2)
                && bl.AsSpan().SequenceEqual(t.ReadSections.Blobs), e.GetFullPath());
            n++;
        }
        Assert.True(n > 100);
    }

    [Fact]
    public void Retime_changes_only_the_duration()
    {
        if (Stock(DrawM4a4) is not { } bytes) { CS2Fixtures.Skip(DrawM4a4); return; }
        var t = Kv3Tree.Read(Data(bytes));
        var before = NmClipEdit.Clips(t).Select(c => NmClipCodec.Decode(t, c)).ToList();
        NmClipEdit.Retime(t, 0.5);
        Assert.All(NmClipEdit.Clips(t), c => Assert.Equal(0.5, Kv3Tree.AsDouble(t.Need(c, "m_flDuration")), 5));
        var after = NmClipEdit.Clips(t).Select(c => NmClipCodec.Decode(t, c)).ToList();
        for (var c = 0; c < before.Count; c++)
            Assert.True(before[c].Zip(after[c]).All(p => p.First.SequenceEqual(p.Second)));
    }

    [Fact]
    public void Trim_keeps_exactly_the_chosen_frames()
    {
        if (Stock(DrawM4a4) is not { } bytes) { CS2Fixtures.Skip(DrawM4a4); return; }
        var t = Kv3Tree.Read(Data(bytes));
        var n = NmClipCodec.FrameCount(t, t.Root);
        var duration = NmClipEdit.Duration(t);
        var before = NmClipEdit.Clips(t).Select(c => NmClipCodec.Decode(t, c)).ToList();
        NmClipEdit.Trim(t, 5, n - 6);
        var after = NmClipEdit.Clips(t).Select(c => NmClipCodec.Decode(t, c)).ToList();
        for (var c = 0; c < before.Count; c++)
        {
            Assert.Equal(n - 10, after[c].Length);
            for (var f = 0; f < after[c].Length; f++) Assert.True(before[c][f + 5].SequenceEqual(after[c][f]), $"clip {c} frame {f}");
        }
        Assert.Equal(duration * (n - 11) / (n - 1), NmClipEdit.Duration(t), 4);
    }

    [Fact]
    public void Transform_moves_only_the_chosen_tracks()
    {
        if (Stock(DrawM4a4) is not { } bytes) { CS2Fixtures.Skip(DrawM4a4); return; }
        var t = Kv3Tree.Read(Data(bytes));
        var before = NmClipCodec.Decode(t, t.Root);
        var moving = Enumerable.Range(0, before[0].Length).First(i => before.Select(f => f[i].Translation).Distinct().Count() > 1);
        var offset = new Vector3(0, 0, 1.5f);
        NmClipEdit.TransformTracks(t, 0, [moving], (_, p) => p with { Translation = p.Translation + offset });
        var after = NmClipCodec.Decode(t, t.Root);
        for (var f = 0; f < before.Length; f++)
            for (var i = 0; i < before[f].Length; i++)
                if (i == moving) Assert.True(Vector3.Distance(before[f][i].Translation + offset, after[f][i].Translation) < 1e-3f);
                else Assert.Equal(before[f][i], after[f][i]);
    }

    [Fact]
    public void Rewritten_file_loads_in_vrf_with_other_blocks_untouched()
    {
        if (Stock(DrawM4a4) is not { } bytes) { CS2Fixtures.Skip(DrawM4a4); return; }
        var edited = NmClipEdit.Rewrite(bytes, t => NmClipEdit.Retime(t, 0.75));
        using var res = new Resource { FileName = DrawM4a4 };
        res.Read(new MemoryStream(edited));
        var clip = Assert.IsType<AnimationClip>(res.DataBlock);
        Assert.Equal(0.75f, clip.Duration, 4);
        Assert.Equal(35, clip.NumFrames);
        using var orig = new Resource();
        orig.Read(new MemoryStream(bytes), verifyFileSize: false);
        var dataAt = (int)orig.GetBlockByType(BlockType.DATA)!.Offset;
        Assert.Equal(dataAt, (int)res.GetBlockByType(BlockType.DATA)!.Offset);
        // Only the DATA entry's size may differ before DATA; restore it and the rest must be Valve's bytes.
        var head = edited[..dataAt];
        for (var i = 16; i + 12 <= dataAt; i += 12)
            if (head.AsSpan(i, 4).SequenceEqual("DATA"u8)) bytes.AsSpan(i + 8, 4).CopyTo(head.AsSpan(i + 8));
        Assert.True(bytes.AsSpan(4, dataAt - 4).SequenceEqual(head.AsSpan(4)), "header or block table changed");
        Assert.Equal(edited.Length, BitConverter.ToInt32(edited, 0));
        foreach (var b in orig.Blocks.Where(b => b.Type != BlockType.DATA))
        {
            var mine = res.GetBlockByType(b.Type)!;
            Assert.True(bytes.AsSpan((int)b.Offset, (int)b.Size).SequenceEqual(edited.AsSpan((int)mine.Offset, (int)mine.Size)), $"{b.Type} changed");
        }
    }
}
