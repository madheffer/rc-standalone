using System.Numerics;
using Source2.Compiler.Anim;
using Source2.Compiler.Kv3;
using ValvePak;
using ValveResourceFormat;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="NmClipCodec"/> against Valve's own clips. What a builder needs is that an encoding
/// decodes back to what was encoded, within one quantization step; that is pinned on every clip in
/// the sample. Matching RC's bytes is the stronger claim and holds for most clips (2,404 of 2,735
/// in the full <c>nmclip-parity</c> sweep); the rest differ only where decoding already lost
/// precision (near-tied quaternion components, sub-ULP ranges), so that is pinned as a floor.
/// </summary>
public class NmClipCodecTests
{
    private static IEnumerable<(string Path, byte[] Data)> ViewmodelClips(int take)
    {
        if (CS2Fixtures.StockPak() is not { } path) yield break;
        using var pak = new Package();
        pak.Read(path);
        foreach (var e in pak.Entries["vnmclip_c"].Where(e => e.GetFullPath().StartsWith("animation/anims/viewmodel/", StringComparison.Ordinal)).Take(take))
        {
            pak.ReadEntry(e, out var bytes);
            using var res = new Resource();
            res.Read(new MemoryStream(bytes), verifyFileSize: false);
            var b = res.GetBlockByType(BlockType.DATA)!;
            yield return (e.GetFullPath(), bytes.AsSpan((int)b.Offset, (int)b.Size).ToArray());
        }
    }

    private static List<Kv3Node> Nodes(Kv3Tree t) => [t.Root, .. NmClipCodec.Secondaries(t, t.Root)];

    [Fact]
    public void Encoding_decodes_back_within_one_step_and_mostly_matches_rc()
    {
        var clips = ViewmodelClips(300).ToList();
        if (clips.Count == 0) { CS2Fixtures.Skip("viewmodel .vnmclip_c"); return; }
        var identical = 0;
        foreach (var (path, data) in clips)
        {
            var orig = Kv3Tree.Read(data);
            var work = Kv3Tree.Read(data);
            var o = Nodes(orig);
            var w = Nodes(work);
            for (var c = 0; c < o.Count; c++)
            {
                var input = NmClipCodec.Decode(orig, o[c]);
                NmClipCodec.Encode(work, w[c], input);
                var output = NmClipCodec.Decode(work, w[c]);
                Assert.Equal(input.Length, output.Length);
                for (var f = 0; f < input.Length; f++)
                    for (var i = 0; i < input[f].Length; i++)
                    {
                        var a = input[f][i];
                        var b = output[f][i];
                        // 15-bit smallest-three: one step is ~4.3e-5 per component.
                        Assert.True(MathF.Abs(Quaternion.Dot(a.Rotation, b.Rotation)) > 0.99999f, $"{path} #{c} f{f} t{i} rotation");
                        Assert.True(Vector3.Distance(a.Translation, b.Translation) < 1e-3f, $"{path} #{c} f{f} t{i} translation");
                        Assert.True(MathF.Abs(a.Scale - b.Scale) < 1e-3f, $"{path} #{c} f{f} t{i} scale");
                    }
            }
            var (b1, b2, bl, _) = work.Encode();
            if (b1.AsSpan().SequenceEqual(orig.ReadSections.Buffer1) && b2.AsSpan().SequenceEqual(orig.ReadSections.Buffer2)
                && bl.AsSpan().SequenceEqual(orig.ReadSections.Blobs)) identical++;
        }
        Assert.True(identical * 10 >= clips.Count * 7, $"only {identical}/{clips.Count} viewmodel clips re-encode byte-identical to RC");
    }
}
