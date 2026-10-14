using Source2.Compiler.Kv3;
using ValvePak;

namespace Source2.Compiler.Cli;

/// <summary>
/// <c>reencode-parity &lt;pak01_dir.vpk&gt; &lt;ext&gt;... [--limit N]</c>: decodes every stock KV3 v5 block
/// to a tree (<see cref="Kv3Binary.Read"/>, what an edit works on) and encodes the tree again
/// (<see cref="Kv3Binary.Write"/>), unedited. Whatever comes back different is what any edit of that
/// block loses. Misses are grouped by the first section that differs.
/// </summary>
internal static class ReencodeParity
{
    public static int Run(string[] args)
    {
        var li = Array.IndexOf(args, "--limit");
        var limit = li >= 0 ? int.Parse(args[li + 1], System.Globalization.CultureInfo.InvariantCulture) : int.MaxValue;
        var vi = Array.IndexOf(args, "--version");
        int? only = vi >= 0 ? int.Parse(args[vi + 1], System.Globalization.CultureInfo.InvariantCulture) : null;
        var exts = args.Skip(1).Where((a, i) => !a.StartsWith("--") && i + 1 != li + 1 && i + 1 != vi + 1).Select(e => e.TrimStart('.')).ToList();
        using var pak = new Package();
        pak.Read(args[0]);
        var bad = 0;
        foreach (var ext in exts)
        {
            if (!Source2.Compiler.Io.VpkEntries.ByExtension(pak).TryGetValue(ext, out var list)) continue;
            int files = 0, blocks = 0, same = 0;
            var why = new Dictionary<string, int>();
            var examples = new List<string>();
            foreach (var e in list)
            {
                if (files >= limit) break;
                files++;
                pak.ReadEntry(e, out var bytes);
                ResourceFile file;
                try { file = ResourceFile.Read(bytes); } catch (InvalidDataException) { continue; }
                foreach (var b in file.Blocks)
                {
                    var raw = b.Data;
                    var version = Kv3Binary.Version(raw);
                    if (raw.Length < 72 || version is not (4 or 5) || (only is { } v && v != version)) continue;
                    blocks++;
                    string? miss;
                    try
                    {
                        var doc = Kv3Binary.Read(raw);
                        var written = Kv3Binary.Write(doc.Root, doc.Header!.Format, version: version);
                        if (written.AsSpan().SequenceEqual(raw)) { same++; continue; }
                        miss = version == 5 ? Section(raw, written) : SectionV4(raw, written);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
                    {
                        miss = "failed: " + ex.GetType().Name;
                    }
                    why[miss] = why.GetValueOrDefault(miss) + 1;
                    if (examples.Count < 5) examples.Add($"{miss}: {e.GetFullPath()} {b.Type}");
                }
            }
            Console.WriteLine($"{ext}: {same}/{blocks} KV3 v4/v5 blocks byte-identical after decode and encode");
            foreach (var (k, v) in why.OrderByDescending(x => x.Value).Take(8)) Console.WriteLine($"  {v,6} x {k}");
            foreach (var x in examples) Console.WriteLine("    e.g. " + x);
            bad += blocks - same;
        }
        return bad == 0 ? 0 : 1;
    }

    // A v4 block: which header field (u32 index) differs, else whether the decompressed buffer does.
    private static string SectionV4(byte[] valve, byte[] ours)
    {
        for (var i = 0; i < 18; i++)
            if (i * 4 + 4 > ours.Length || BitConverter.ToUInt32(valve, i * 4) != BitConverter.ToUInt32(ours, i * 4))
                return $"v4 header field {i}";
        if (BitConverter.ToUInt32(valve, 20) != 1) return "v4 bytes";
        byte[] Buffer(byte[] b)
        {
            var o = new byte[BitConverter.ToInt32(b, 48)];
            Compression.Lz4.Decode(b.AsSpan(72, BitConverter.ToInt32(b, 52)), o);
            return o;
        }
        return Buffer(valve).AsSpan().SequenceEqual(Buffer(ours)) ? "v4 compressed bytes" : "v4 buffer";
    }

    // Which part differs, judged on the uncompressed sections so compression is told apart.
    private static string Section(byte[] valve, byte[] ours)
    {
        Kv3Tree a, b;
        try { a = Kv3Tree.Read(valve); b = Kv3Tree.Read(ours); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { return "unreadable"; }
        if (a.Compression != b.Compression) return $"compression {a.Compression} -> {b.Compression}";
        if (!a.ReadSections.Buffer1.AsSpan().SequenceEqual(b.ReadSections.Buffer1)) return "buffer 1";
        if (!a.ReadSections.Buffer2.AsSpan().SequenceEqual(b.ReadSections.Buffer2)) return "buffer 2";
        if (!a.ReadSections.Blobs.AsSpan().SequenceEqual(b.ReadSections.Blobs)) return "blobs";
        for (var i = 0; i < a.ReadHeader.Length; i++)
            if (a.ReadHeader[i] != b.ReadHeader[i]) return $"header field {i}";
        return "compressed bytes";
    }
}
