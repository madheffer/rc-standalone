using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace Source2.Compiler.Cli;

/// <summary>
/// <c>kv3-read-parity &lt;pak01_dir.vpk&gt; [ext...] [--limit N]</c>: reads every KV3 block (every
/// version) of the stock files with this library's own reader (<see cref="Kv3Binary.Read"/>) and
/// with VRF's, and requires the same tree (types, flags, values by their bits) and format. This is
/// what lets the compiler stop depending on VRF (docs/INDEPENDENCE.md) without changing what a
/// host sees.
/// </summary>
internal static class Kv3ReadParity
{
    public static int Run(string[] args)
    {
        var li = Array.IndexOf(args, "--limit");
        var limit = li >= 0 ? int.Parse(args[li + 1]) : int.MaxValue;
        var only = args.Skip(1).Where((a, i) => !a.StartsWith("--") && i + 1 != li + 1).Select(e => e.TrimStart('.')).ToHashSet();
        using var pak = new Package();
        pak.Read(args[0]);
        long blocks = 0, same = 0;
        var byVersion = new SortedDictionary<int, (long Same, long All)>();
        var examples = new List<string>();
        foreach (var (ext, list) in Source2.Compiler.Io.VpkEntries.ByExtension(pak).OrderBy(x => x.Key))
        {
            if (!ext.EndsWith("_c") || (only.Count > 0 && !only.Contains(ext))) continue;
            var files = 0;
            foreach (var e in list)
            {
                if (files++ >= limit) break;
                pak.ReadEntry(e, out var bytes);
                ResourceFile file;
                try { file = ResourceFile.Read(bytes); } catch (InvalidDataException) { continue; }
                foreach (var block in file.Blocks)
                {
                    if (!Kv3Binary.IsKv3(block.Data)) continue;
                    var version = BitConverter.ToUInt32(block.Data) == 0x03564B56 ? 0 : block.Data[0];
                    blocks++;
                    string? miss = null;
                    try
                    {
                        var ours = Kv3Binary.Read(block.Data);
                        var vrf = new BinaryKV3(BlockType.DATA) { Resource = null!, Size = (uint)block.Data.Length };
                        using (var r = new BinaryReader(new MemoryStream(block.Data))) vrf.Read(r);
                        if (ours.Header!.Format.Id != vrf.Data.Header!.Format.Id) miss = "format";
                        else if (!ResourceBuilder.SameTree(ours.Root, vrf.Data.Root)) miss = "tree";
                    }
                    catch (Exception ex) { miss = "threw " + ex.GetType().Name + ": " + ex.Message; }
                    var (s, a) = byVersion.GetValueOrDefault(version);
                    byVersion[version] = (s + (miss is null ? 1 : 0), a + 1);
                    if (miss is null) same++;
                    else if (examples.Count < 12) examples.Add($"{miss}: {e.GetFullPath()} {block.Type} v{version}");
                }
            }
        }
        Console.WriteLine($"{blocks} KV3 blocks: {same} read identically by both");
        foreach (var (v, (s, a)) in byVersion) Console.WriteLine($"  v{v}: {s}/{a}");
        foreach (var x in examples) Console.WriteLine("  e.g. " + x);
        return same == blocks ? 0 : 1;
    }
}
