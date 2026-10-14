using ValvePak;
using ValveResourceFormat;

namespace Source2.Compiler.Cli;

/// <summary>
/// <c>recompile-parity &lt;pak01_dir.vpk&gt; &lt;ext&gt;... [--limit N]</c>: decompiles every stock
/// resource of the given KV3 text types (vdata_c, vsndevts_c, vpcf_c) to KV3 text, compiles that
/// text with <see cref="Kv3SourceCompiler"/>, and requires the DATA block to be Valve's byte for
/// byte. Valve's own source files are not shipped, so RED2 (which records the source's path and
/// CRC) cannot be compared this way; DATA is the compiled tree itself, typed and laid out by the
/// same rules a text compile follows. A miss names the first differing offset.
/// </summary>
internal static class RecompileParity
{
    public static int Run(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: recompile-parity <pak01_dir.vpk> <ext>... [--limit N]"); return 2; }
        var li = Array.IndexOf(args, "--limit");
        var limit = li >= 0 ? int.Parse(args[li + 1]) : int.MaxValue;
        var exts = args.Skip(1).Where((a, i) => !a.StartsWith("--") && i + 1 != li + 1).Select(e => e.TrimStart('.')).ToList();
        using var pak = new Package();
        pak.Read(args[0]);

        var failures = 0;
        foreach (var ext in exts)
        {
            if (!Source2.Compiler.Io.VpkEntries.ByExtension(pak).TryGetValue(ext, out var list)) continue;
            int files = 0, same = 0, noText = 0, threw = 0;
            var examples = new List<string>();
            var causes = new Dictionary<string, int>();
            foreach (var e in list)
            {
                if (files >= limit) break;
                files++;
                pak.ReadEntry(e, out var bytes);
                var path = e.GetFullPath();
                string? text;
                byte[] valveData;
                {
                    var res = CompiledResource.Read(bytes);
                    text = ResourceDecompiler.BlockToKv3(res, "DATA", out _);
                    valveData = res.Block("DATA")!.Data;
                }
                if (text is null) { noText++; continue; }

                byte[] ours;
                try
                {
                    ours = Kv3SourceCompiler.Compile(System.Text.Encoding.UTF8.GetBytes(text), "." + ext[..^2], path[..^2]);
                }
                catch (Exception ex)
                {
                    threw++;
                    if (examples.Count < 8) examples.Add($"THREW {path}: {ex.Message}");
                    continue;
                }
                using var mine = new Resource();
                mine.Read(new MemoryStream(ours));
                var md = mine.GetBlockByType(BlockType.DATA)!;
                var ourData = ours.AsSpan((int)md.Offset, (int)md.Size);
                if (ourData.SequenceEqual(valveData)) { same++; continue; }
                var why = BitConverter.ToUInt32(valveData, 0) == 0x4B563305
                    ? NodeDiff(Kv3.Kv3Tree.Read(valveData), Kv3.Kv3Tree.Read(ourData.ToArray()))
                    : $"Valve's block is KV3 magic 0x{BitConverter.ToUInt32(valveData, 0):x8}, an older writer";
                var older = BitConverter.ToUInt32(valveData, 0) != 0x4B563305;
                var cause = older ? "older KV3 writer (v3/v4), not comparable" : why[(why.LastIndexOf('/') + 1)..].Split(':')[0];
                causes[cause] = causes.GetValueOrDefault(cause) + 1;
                if (!older && examples.Count < 10)
                    examples.Add($"{path}: first difference at {ourData.CommonPrefixLength(valveData)}, sizes {ourData.Length} vs {valveData.Length}; {why}");
            }
            var compared = files - noText;
            Console.WriteLine($"{ext}: {same}/{compared} DATA blocks identical ({threw} failed to compile, {noText} without KV3 text)");
            foreach (var x in examples) Console.WriteLine("  e.g. " + x);
            foreach (var (c, n) in causes.OrderByDescending(x => x.Value).Take(15)) Console.WriteLine($"  {n,5} x {c}");
            failures += compared - same;
        }
        return failures == 0 ? 0 : 1;
    }

    /// <summary>The first node where the two trees part: its path, and each side's wire type,
    /// flag and value. Falls back to the string tables when the nodes all agree.</summary>
    internal static string NodeDiff(Kv3.Kv3Tree valve, Kv3.Kv3Tree ours)
    {
        string Show(Kv3.Kv3Tree t, Kv3.Kv3Node n) => n.Type switch
        {
            Kv3.Kv3Type.String => $"{n.Type}/{n.Flag} \"{(n.StringId >= 0 ? t.Strings[n.StringId] : "")}\"",
            _ when n.Items is not null => $"{n.Type}/{n.Flag} [{n.Items.Count}]" + (n.IsTypedArray ? $" of {n.ElementType}" : ""),
            _ => $"{n.Type}/{n.Flag} 0x{n.Bits:x}",
        };
        string? Walk(Kv3.Kv3Node a, Kv3.Kv3Node b, string at)
        {
            if (a.Type != b.Type || a.Flag != b.Flag || a.Bits != b.Bits || a.ElementType != b.ElementType
                || (a.Type == Kv3.Kv3Type.String && valve.Strings.ElementAtOrDefault(a.StringId) != ours.Strings.ElementAtOrDefault(b.StringId))
                || (a.Items?.Count ?? -1) != (b.Items?.Count ?? -1))
                return $"{at}: valve {Show(valve, a)}, ours {Show(ours, b)}";
            for (var i = 0; a.Items is not null && i < a.Items.Count; i++)
            {
                var key = a.KeyIds is not null ? valve.Strings[a.KeyIds[i]] : i.ToString();
                if (a.KeyIds is not null && valve.Strings[a.KeyIds[i]] != ours.Strings[b.KeyIds![i]])
                    return $"{at}/{key}: key order, ours {ours.Strings[b.KeyIds[i]]}";
                if (Walk(a.Items[i], b.Items![i], $"{at}/{key}") is { } d)
                    return d;
            }
            return null;
        }
        return Walk(valve.Root, ours.Root, "")
            ?? $"same nodes; strings {valve.Strings.Count} vs {ours.Strings.Count}, first differing "
             + $"#{Enumerable.Range(0, Math.Min(valve.Strings.Count, ours.Strings.Count)).FirstOrDefault(i => valve.Strings[i] != ours.Strings[i], -1)}";
    }
}
