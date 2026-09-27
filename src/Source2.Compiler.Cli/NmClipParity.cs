using ValvePak;
using ValveResourceFormat;
using Source2.Compiler.Anim;
using Source2.Compiler.Kv3;

namespace Source2.Compiler.Cli;

/// <summary>
/// <c>nmclip-parity &lt;pak01_dir.vpk&gt; [--limit N]</c>: decodes every stock clip, re-encodes the
/// poses with <see cref="NmClipCodec"/> and compares with Valve's encoding: static flags, ranges,
/// offsets, pose words, and finally the whole DATA block's uncompressed sections.
/// </summary>
internal static class NmClipParity
{
    public static int Run(string[] args)
    {
        var li = Array.IndexOf(args, "--limit");
        var limit = li >= 0 ? int.Parse(args[li + 1]) : int.MaxValue;
        using var pak = new Package();
        pak.Read(args[0]);
        int clips = 0, sub = 0, blockSame = 0;
        long words = 0, wordSame = 0, maxDiff = 0;
        var miss = new Dictionary<string, int>();
        var diffs = new Dictionary<string, int>();
        var detail = new List<string>();
        var examples = new List<string>();
        void Miss(string what, string where) { miss[what] = miss.GetValueOrDefault(what) + 1; if (examples.Count < 14) examples.Add($"{what}: {where}"); }

        foreach (var e in pak.Entries["vnmclip_c"])
        {
            if (clips >= limit) break;
            pak.ReadEntry(e, out var bytes);
            using var res = new Resource();
            res.Read(new MemoryStream(bytes), verifyFileSize: false);
            var data = res.GetBlockByType(BlockType.DATA)!;
            var raw = bytes.AsSpan((int)data.Offset, (int)data.Size).ToArray();
            var orig = Kv3Tree.Read(raw);
            var work = Kv3Tree.Read(raw);
            clips++;
            var o = new List<Kv3Node> { orig.Root };
            o.AddRange(NmClipCodec.Secondaries(orig, orig.Root));
            var w = new List<Kv3Node> { work.Root };
            w.AddRange(NmClipCodec.Secondaries(work, work.Root));
            for (var c = 0; c < o.Count; c++)
            {
                sub++;
                var where = $"{e.GetFullPath()} #{c}";
                NmClipCodec.Encode(work, w[c], NmClipCodec.Decode(orig, o[c]));
                var os = orig.Need(o[c], "m_trackCompressionSettings").Items!;
                var ws = work.Need(w[c], "m_trackCompressionSettings").Items!;
                for (var i = 0; i < os.Count; i++)
                {
                    foreach (var k in new[] { "m_bIsRotationStatic", "m_bIsTranslationStatic", "m_bIsScaleStatic", "m_nTrackReadOffset" })
                        if (Kv3Tree.AsLong(orig.Need(os[i], k)) != Kv3Tree.AsLong(work.Need(ws[i], k))) Miss(k, $"{where} track {i}");
                    foreach (var r in new[] { "m_translationRangeX", "m_translationRangeY", "m_translationRangeZ", "m_scaleRange" })
                        foreach (var f in new[] { "m_flRangeStart", "m_flRangeLength" })
                        {
                            var a = orig.Need(orig.Need(os[i], r), f);
                            var b = work.Need(work.Need(ws[i], r), f);
                            if (a.Type != b.Type || a.Bits != b.Bits)
                                Miss($"{r}.{f}", $"{where} track {i}: {Kv3Tree.AsDouble(a):R} ({a.Type}) vs {Kv3Tree.AsDouble(b):R} ({b.Type})");
                        }
                }
                var oa = orig.Need(o[c], "m_compressedPoseData").Blob!;
                var wa = work.Need(w[c], "m_compressedPoseData").Blob!;
                if (oa.Length != wa.Length) Miss("pose length", $"{where}: {wa.Length} vs {oa.Length}");
                // Channel of each word in a frame, from the original's settings.
                var chan = new List<string>();
                var chanAt = new List<(int Track, int Axis)>();
                for (var ti = 0; ti < os.Count; ti++)
                {
                    var ts = os[ti];
                    if (!Kv3Tree.AsBool(orig.Need(ts, "m_bIsRotationStatic"))) { chan.AddRange(["rot0", "rot1", "rot2"]); chanAt.AddRange([(ti, 0), (ti, 1), (ti, 2)]); }
                    if (!Kv3Tree.AsBool(orig.Need(ts, "m_bIsTranslationStatic"))) { chan.AddRange(["pos", "pos", "pos"]); chanAt.AddRange([(ti, 0), (ti, 1), (ti, 2)]); }
                    if (!Kv3Tree.AsBool(orig.Need(ts, "m_bIsScaleStatic"))) { chan.Add("scale"); chanAt.Add((ti, 0)); }
                }
                for (var i = 0; i + 1 < Math.Min(oa.Length, wa.Length); i += 2)
                {
                    int ov = BitConverter.ToUInt16(oa, i), wv = BitConverter.ToUInt16(wa, i);
                    var d = Math.Abs(ov - wv);
                    words++;
                    if (d == 0) { wordSame++; continue; }
                    maxDiff = Math.Max(maxDiff, d);
                    var ch = chan.Count > 0 ? chan[(i / 2) % chan.Count] : "?";
                    var bucket = d == 1 ? "1" : d <= 3 ? "2-3" : ch.StartsWith("rot") && ((ov ^ wv) & 0x8000) != 0 ? "index-bit" : d < 100 ? "4-99" : "100+";
                    diffs[$"{ch}:{bucket}"] = diffs.GetValueOrDefault($"{ch}:{bucket}") + 1;
                    if (detail.Count < 10 && bucket == "1" && ch == "pos")
                    {
                        var wi = i / 2;
                        var q0 = wi - (wi % chan.Count) + chan.FindIndex(x => x.StartsWith("rot") ) ;
                        var (tr, ax) = chanAt[wi % chanAt.Count];
                        var rng = orig.Need(os[tr], ax == 0 ? "m_translationRangeX" : ax == 1 ? "m_translationRangeY" : "m_translationRangeZ");
                        var rs = Kv3Tree.AsDouble(orig.Need(rng, "m_flRangeStart"));
                        var rl = Kv3Tree.AsDouble(orig.Need(rng, "m_flRangeLength"));
                        var vf = (float)(ov / 65535.0f * (float)rl + (float)rs);
                        detail.Add($"pos {where} track {tr} axis {ax}: valve {ov} ours {wv} start {rs:R} len {rl:R} decoded {vf:R} exact {(vf - rs) / rl * 65535:R}");
                    }
                }
                var oo = orig.Need(o[c], "m_compressedPoseOffsets").Items!.Select(Kv3Tree.AsLong).ToArray();
                var wo = work.Need(w[c], "m_compressedPoseOffsets").Items!.Select(Kv3Tree.AsLong).ToArray();
                if (!oo.SequenceEqual(wo)) Miss("frame offsets", where);
            }
            var (b1, b2, bl, _) = work.Encode();
            if (b1.AsSpan().SequenceEqual(orig.ReadSections.Buffer1) && b2.AsSpan().SequenceEqual(orig.ReadSections.Buffer2)
                && bl.AsSpan().SequenceEqual(orig.ReadSections.Blobs)) blockSame++;
        }
        Console.WriteLine($"{clips} clip(s), {sub} clip node(s): {blockSame} DATA blocks byte-identical after re-encode");
        Console.WriteLine($"  pose words: {wordSame}/{words} identical, largest difference {maxDiff}");
        foreach (var (k, v) in diffs.OrderByDescending(x => x.Value)) Console.WriteLine($"  word diff {k}: {v}");
        foreach (var d in detail) Console.WriteLine("  detail " + d);
        foreach (var (k, v) in miss.OrderByDescending(x => x.Value)) Console.WriteLine($"  {k}: {v}");
        foreach (var x in examples) Console.WriteLine("  e.g. " + x);
        return 0;
    }
}
