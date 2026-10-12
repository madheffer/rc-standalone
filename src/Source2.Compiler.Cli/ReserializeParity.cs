using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler.Cli;

/// <summary>
/// <c>reserialize-parity &lt;pak01_dir.vpk&gt; &lt;mode&gt; &lt;ext&gt;... [--limit N]</c>: reads every stock
/// resource of the given types and writes it back UNCHANGED through one of the write paths a
/// host edits resources with, then compares with Valve's file. An unchanged resource is the
/// easy case: a write path that cannot give it back as it was cannot be trusted with an edit.
/// Modes: <c>serialize</c> (<see cref="ResourceBuilder.Serialize"/>), <c>data-only</c>
/// (<see cref="VrfResourceBuilder.SerializeModelDataOnly"/>), <c>keep-live-ctrl-mdat</c>
/// (<see cref="VrfResourceBuilder.SerializeModelKeepBlocksLive"/> as a draw-call edit calls it),
/// and identity edits: <c>particle-paths</c> (<see cref="ResourceBuilder.RewriteParticlePaths"/>
/// with nothing to rewrite), <c>swap-own-phys</c> (<see cref="ResourceBuilder.SwapPhysBlockBinary"/>
/// with the model's own PHYS), <c>model-raw</c> (<see cref="ResourceBuilder.RebuildModelRaw"/>
/// from the model's own blocks). Per file it reports one of: the
/// bytes are Valve's; every block decodes to Valve's tree (the bytes differ); a block decodes
/// differently or is missing (named); the write threw; the read-back threw.
/// Diagnostics: <c>RESER_NODES=1</c> prints the first differing KV3 node of each block,
/// <c>RESER_DUMP=&lt;dir&gt;</c> saves the first files' Valve and written bytes.
/// </summary>
internal static class ReserializeParity
{
    public static int Run(string[] args)
    {
        if (args.Length < 3) { Console.Error.WriteLine("usage: reserialize-parity <pak01_dir.vpk> <serialize|data-only> <ext>... [--limit N]"); return 2; }
        var mode = args[1];
        var li = Array.IndexOf(args, "--limit");
        var limit = li >= 0 ? int.Parse(args[li + 1]) : int.MaxValue;
        var exts = args.Skip(2).Where((a, i) => !a.StartsWith("--") && i + 2 != li + 1).Select(e => e.TrimStart('.')).ToList();
        using var pak = new Package();
        pak.Read(args[0]);

        var bad = 0;
        foreach (var ext in exts)
        {
            if (!pak.Entries.TryGetValue(ext, out var list)) continue;
            int files = 0, bytesSame = 0, treesSame = 0, unreadable = 0;
            var outcomes = new Dictionary<string, int>();
            var examples = new List<string>();
            void Outcome(string what, string where)
            {
                outcomes[what] = outcomes.GetValueOrDefault(what) + 1;
                if (examples.Count < 10) examples.Add($"{what}: {where}");
            }
            foreach (var e in list)
            {
                if (files >= limit) break;
                pak.ReadEntry(e, out var original);
                var path = e.GetFullPath();
                files++;
                byte[] written;
                try
                {
                    using var res = new Resource { FileName = Path.GetFileName(path) };
                    res.Read(new MemoryStream(original));
                    written = mode switch
                    {
                        "serialize" => VrfResourceBuilder.Serialize(res),
                        "data-only" => VrfResourceBuilder.SerializeModelDataOnly(res, original),
                        "keep-live-ctrl-mdat" => VrfResourceBuilder.SerializeModelKeepBlocksLive(res, original, BlockType.CTRL, BlockType.MDAT),
                        "particle-paths" => ResourceBuilder.RewriteParticlePaths(original, new Dictionary<string, string>()),
                        "swap-own-phys" => ResourceBuilder.ExtractPhysBlock(original) is { } phys
                            ? ResourceBuilder.SwapPhysBlockBinary(original, phys) : original,
                        "model-raw" => ResourceBuilder.RebuildModelRaw(original,
                            res.Blocks.Select(b => (b.Type.ToString(), original.AsSpan((int)b.Offset, (int)b.Size).ToArray())).ToList()),
                        _ => throw new ArgumentException($"unknown mode {mode}"),
                    };
                }
                catch (Exception ex) when (ex is not ArgumentException)
                {
                    // A file VRF itself cannot read is not this write path's fault; count it apart.
                    if (!Readable(original, path)) { unreadable++; continue; }
                    Outcome($"WRITE THREW {ex.GetType().Name}", $"{path}: {ex.Message}");
                    continue;
                }
                if (written.AsSpan().SequenceEqual(original)) { bytesSame++; treesSame++; continue; }
                if (Environment.GetEnvironmentVariable("RESER_DUMP") is { Length: > 0 } dump && files <= 2)
                {
                    Directory.CreateDirectory(dump);
                    File.WriteAllBytes(Path.Combine(dump, Path.GetFileName(path) + ".valve"), original);
                    File.WriteAllBytes(Path.Combine(dump, Path.GetFileName(path) + ".ours"), written);
                }
                string? diff;
                try { diff = BlockDiff(original, written, path); }
                catch (Exception ex) { Outcome($"READ-BACK THREW {ex.GetType().Name}", $"{path}: {ex.Message}"); continue; }
                if (diff is null)
                {
                    treesSame++;
                    Outcome("tree-identical, bytes differ: " + WhyBytes(original, written, path), path);
                }
                else Outcome(diff.Split(':')[0], $"{path}: {diff}");
            }
            var compared = files - unreadable;
            Console.WriteLine($"{ext} [{mode}]: {compared} written; {bytesSame} byte-identical, {treesSame} tree-identical ({unreadable} unreadable by VRF, skipped)");
            foreach (var (k, v) in outcomes.OrderByDescending(x => x.Value)) Console.WriteLine($"  {v,6} x {k}");
            foreach (var x in examples) Console.WriteLine("  e.g. " + x);
            bad += compared - treesSame;
        }
        return bad == 0 ? 0 : 1;
    }

    private static bool Readable(byte[] bytes, string path)
    {
        try
        {
            using var res = new Resource { FileName = Path.GetFileName(path) };
            res.Read(new MemoryStream(bytes));
            _ = res.DataBlock;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Null when every block of <paramref name="written"/> decodes as Valve's does,
    /// else the first block that does not.</summary>
    private static string? BlockDiff(byte[] original, byte[] written, string path)
    {
        using var a = new Resource { FileName = Path.GetFileName(path) };
        a.Read(new MemoryStream(original));
        using var b = new Resource { FileName = Path.GetFileName(path) };
        b.Read(new MemoryStream(written));
        var ta = a.Blocks.Select(x => x.Type).ToList();
        var tb = b.Blocks.Select(x => x.Type).ToList();
        if (!ta.SequenceEqual(tb))
            return $"block list: [{string.Join(' ', ta)}] became [{string.Join(' ', tb)}]";
        for (var i = 0; i < a.Blocks.Count; i++)
        {
            var x = a.Blocks[i];
            var y = b.Blocks[i];
            var rawX = original.AsSpan((int)x.Offset, (int)x.Size);
            var rawY = written.AsSpan((int)y.Offset, (int)y.Size);
            if (rawX.SequenceEqual(rawY)) continue;
            if (Environment.GetEnvironmentVariable("RESER_NODES") == "1"
                && rawX.Length >= 4 && BitConverter.ToUInt32(rawX) == 0x4B563305 && BitConverter.ToUInt32(rawY) == 0x4B563305)
                Console.WriteLine($"    {path} {x.Type}: {RecompileParity.NodeDiff(Kv3.Kv3Tree.Read(rawX), Kv3.Kv3Tree.Read(rawY))}");
            string tx, ty;
            try { tx = Text(x); ty = Text(y); }
            catch (Exception ex) { return $"{x.Type} undecodable: {ex.Message}"; }
            if (tx != ty)
            {
                var at = tx.AsSpan().CommonPrefixLength(ty);
                return $"{x.Type} decodes differently: at char {at}, valve \"{Snip(tx, at)}\" ours \"{Snip(ty, at)}\"";
            }
        }
        return null;
    }

    /// <summary>Which blocks differ in bytes, each with the encoding Valve's copy was in, so a block
    /// Valve wrote with an older KV3 writer (v3/v4) or as NTRO is told apart from a real miss.</summary>
    private static string WhyBytes(byte[] original, byte[] written, string path)
    {
        using var a = new Resource { FileName = Path.GetFileName(path) };
        a.Read(new MemoryStream(original));
        using var b = new Resource { FileName = Path.GetFileName(path) };
        b.Read(new MemoryStream(written));
        var parts = new List<string>();
        for (var i = 0; i < a.Blocks.Count; i++)
        {
            var x = a.Blocks[i];
            var y = b.Blocks[i];
            var rawX = original.AsSpan((int)x.Offset, (int)x.Size);
            if (rawX.SequenceEqual(written.AsSpan((int)y.Offset, (int)y.Size))) continue;
            var magic = rawX.Length >= 4 ? BitConverter.ToUInt32(rawX) : 0;
            parts.Add(magic switch
            {
                0x4B563305 => $"{x.Type} (KV3 v5)",
                0x4B563303 or 0x4B563304 => $"{x.Type} (Valve's is KV3 v{magic & 0xF})",
                _ => $"{x.Type} (not KV3)",
            });
        }
        return parts.Count == 0 ? "layout only" : string.Join(", ", parts);
    }

    private static string Text(Block block) => block switch
    {
        KeyValuesOrNTRO typed => typed.Data.ToKV3String(),
        BinaryKV3 kv3 => kv3.Data.Root.ToKV3String(),
        _ => block.ToString() ?? "",
    };

    private static string Snip(string s, int at) =>
        s.Substring(Math.Max(0, at - 30), Math.Min(70, s.Length - Math.Max(0, at - 30))).Replace("\n", "\\n").Replace("\t", " ");
}
