using ValvePak;

namespace Source2.Compiler.Cli;

/// <summary>
/// <c>container-parity &lt;pak01_dir.vpk&gt; [ext...] [--limit N]</c>: reads every stock compiled file
/// (of the given types, or all) with <see cref="ResourceFile"/>, VRF-free, writes it back, and
/// requires Valve's bytes. Misses are grouped by the region the first difference falls in.
/// </summary>
internal static class ContainerParity
{
    public static int Run(string[] args)
    {
        var li = Array.IndexOf(args, "--limit");
        var limit = li >= 0 ? int.Parse(args[li + 1]) : int.MaxValue;
        var only = args.Skip(1).Where((a, i) => !a.StartsWith("--") && i + 1 != li + 1).Select(e => e.TrimStart('.')).ToHashSet();
        using var pak = new Package();
        pak.Read(args[0]);
        var bad = 0;
        foreach (var (ext, list) in Source2.Compiler.Io.VpkEntries.ByExtension(pak).OrderBy(x => x.Key))
        {
            if (!ext.EndsWith("_c") || (only.Count > 0 && !only.Contains(ext))) continue;
            int files = 0, same = 0;
            var why = new Dictionary<string, int>();
            var examples = new List<string>();
            foreach (var e in list)
            {
                if (files >= limit) break;
                files++;
                pak.ReadEntry(e, out var bytes);
                string? miss;
                try
                {
                    var written = ResourceFile.Read(bytes).Write();
                    if (written.AsSpan().SequenceEqual(bytes)) { same++; continue; }
                    miss = Region(bytes, written);
                }
                catch (InvalidDataException ex) { miss = "unreadable: " + ex.Message.Split(':')[0]; }
                why[miss] = why.GetValueOrDefault(miss) + 1;
                if (examples.Count < 4) examples.Add($"{miss}: {e.GetFullPath()}");
            }
            Console.WriteLine($"{ext}: {same}/{files} byte-identical");
            foreach (var (k, v) in why.OrderByDescending(x => x.Value).Take(6)) Console.WriteLine($"  {v,6} x {k}");
            foreach (var x in examples) Console.WriteLine("    e.g. " + x);
            bad += files - same;
        }
        return bad == 0 ? 0 : 1;
    }

    private static string Region(byte[] valve, byte[] ours)
    {
        var at = ours.AsSpan().CommonPrefixLength(valve);
        if (at < 16) return $"header byte {at}";
        var count = BitConverter.ToInt32(valve, 12);
        if (at < 16 + 12 * count) return "block table";
        for (var i = 0; i < count; i++)
        {
            var entry = 16 + 12 * i;
            var start = entry + 4 + BitConverter.ToInt32(valve, entry + 4);
            var end = start + BitConverter.ToInt32(valve, entry + 8);
            if (at < start) return $"padding before {System.Text.Encoding.ASCII.GetString(valve, entry, 4)}";
            if (at < end) return $"inside {System.Text.Encoding.ASCII.GetString(valve, entry, 4)}";
        }
        return at < BitConverter.ToInt32(valve, 0) ? "after the last block" : "tail";
    }
}
