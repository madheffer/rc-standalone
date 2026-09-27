using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using Source2.Compiler.Kv3;

namespace Source2.Compiler.Cli;

/// <summary>
/// <c>kv3-parity &lt;pak01_dir.vpk&gt; &lt;ext&gt;... [--limit N] [--write]</c>: re-encodes every KV3 v5
/// block of the given compiled types through <see cref="Kv3Tree"/> and compares the uncompressed
/// sections and every header field it computes with Valve's. <c>--write</c> also runs the full
/// writer and requires that both our reader and VRF's decode the result to the original.
/// See docs/RC_PARITY.md, "KV3 v5 lossless".
/// </summary>
internal static class Kv3Parity
{
    private static readonly (int Index, string Name)[] Fields =
    [
        (7, "countBytes1"), (8, "countBytes4"), (9, "countBytes8"), (10, "countTypes"), (11, "objects|arrays u16"),
        (14, "countBlocks"), (15, "sizeBlobs"), (16, "countBytes2"), (18, "unc1"), (20, "unc2"),
        (22, "b2.bytes1"), (23, "b2.bytes2"), (24, "b2.bytes4"), (25, "b2.bytes8"), (26, "described"),
        (27, "b2.objects"), (28, "b2.arrays"), (29, "b2.elements"),
    ];

    public static int Run(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: kv3-parity <pak01_dir.vpk> <ext>... [--limit N] [--write]"); return 2; }
        var li = Array.IndexOf(args, "--limit");
        var limit = li >= 0 ? int.Parse(args[li + 1]) : int.MaxValue;
        var writeCheck = args.Contains("--write");
        var exts = args.Skip(1).Where((a, i) => !a.StartsWith("--") && i + 1 != li + 1).Select(e => e.TrimStart('.')).ToHashSet();
        using var pak = new Package();
        pak.Read(args[0]);

        int files = 0, blocks = 0, identical = 0, failed = 0;
        var misses = new Dictionary<string, int>();
        var examples = new List<string>();
        void Miss(string what, string where)
        {
            misses[what] = misses.GetValueOrDefault(what) + 1;
            if (examples.Count < 12) examples.Add($"{what}: {where}");
        }

        foreach (var ext in exts)
        {
            if (!pak.Entries.TryGetValue(ext, out var list)) continue;
            foreach (var e in list)
            {
                if (files >= limit) break;
                files++;
                pak.ReadEntry(e, out var bytes);
                using var res = new Resource();
                try { res.Read(new MemoryStream(bytes), verifyFileSize: false); }
                catch { failed++; continue; }
                foreach (var b in res.Blocks)
                {
                    if (b.Size < 120 || BitConverter.ToUInt32(bytes, (int)b.Offset) != 0x4B563305) continue;
                    blocks++;
                    var raw = bytes.AsSpan((int)b.Offset, (int)b.Size).ToArray();
                    var where = $"{e.GetFullPath()} {b.Type}";
                    Kv3Tree t;
                    try { t = Kv3Tree.Read(raw); }
                    catch (Exception ex) { failed++; examples.Insert(0, $"READ {where}: {ex.Message}"); continue; }

                    var before = misses.Values.Sum();
                    var (b1, b2, blobs, h) = t.Encode();
                    h[20] += t.ReadHeader[17];   // LZ4 blob frame sizes trail buffer 2 on the wire
                    if (!b1.AsSpan().SequenceEqual(t.ReadSections.Buffer1)) Miss("section buffer1", where);
                    if (!b2.AsSpan().SequenceEqual(t.ReadSections.Buffer2)) Miss("section buffer2", where);
                    if (!blobs.AsSpan().SequenceEqual(t.ReadSections.Blobs)) Miss("section blobs", where);
                    foreach (var (i, name) in Fields)
                        if (h[i] != t.ReadHeader[i]) Miss($"field {name}", $"{where} mine {h[i]} vs {t.ReadHeader[i]}");

                    if (writeCheck)
                    {
                        var written = t.Write();
                        var back = Kv3Tree.Read(written);
                        var (wb1, wb2, wbl, _) = back.Encode();
                        if (!wb1.AsSpan().SequenceEqual(t.ReadSections.Buffer1) || !wb2.AsSpan().SequenceEqual(t.ReadSections.Buffer2)
                            || !wbl.AsSpan().SequenceEqual(t.ReadSections.Blobs))
                            Miss("write re-read", where);
                        if (t.Compression == 0 && !back.ReadHeader.AsSpan().SequenceEqual(t.ReadHeader)) Miss("write header", where);
                        if (VrfText(raw) != VrfText(written)) Miss("write VRF text", where);
                    }
                    if (misses.Values.Sum() == before) identical++;
                }
            }
        }
        Console.WriteLine($"{files} file(s), {blocks} KV3 v5 block(s): {identical} identical, {failed} unreadable");
        foreach (var (k, v) in misses) Console.WriteLine($"  {k}: {v}");
        foreach (var ex in examples) Console.WriteLine("  e.g. " + ex);
        return identical == blocks && failed == 0 ? 0 : 1;
    }

    private static string VrfText(byte[] block)
    {
        var kv3 = new BinaryKV3(BlockType.DATA) { Resource = null!, Size = (uint)block.Length };
        using var r = new BinaryReader(new MemoryStream(block));
        kv3.Read(r);
        return kv3.ToString();
    }
}
