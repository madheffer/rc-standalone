using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>PKGDIFF=&lt;our vpk&gt;|&lt;Valve's vpk&gt;[|&lt;path prefix&gt;]</c>): every
/// file of Valve's package beside ours: byte-equal, else equal in every KV3
/// block's tree and in the container facts, else different (first tree line).
/// </summary>
public class PackageDiffProbe(ITestOutputHelper output)
{
    [Fact]
    public void Compare()
    {
        if (Environment.GetEnvironmentVariable("PKGDIFF") is not { Length: > 0 } spec || spec.Split('|') is not [var ours, var valve, .. var rest])
            return;
        var prefix = rest is [var p] ? p : "";
        using var a = new Package();
        a.Read(ours);
        using var b = new Package();
        b.Read(valve);
        var tally = new SortedDictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var e in b.Entries!.SelectMany(kv => kv.Value).OrderBy(e => e.GetFullPath(), StringComparer.Ordinal))
        {
            var path = e.GetFullPath();
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var type = Path.GetExtension(path);
            if (!tally.TryGetValue(type, out var t))
                tally[type] = t = new int[4];
            var mine = a.FindEntry(path);
            if (mine == null)
            {
                t[3]++;
                output.WriteLine($"missing {path}");
                continue;
            }
            b.ReadEntry(e, out var vb);
            a.ReadEntry(mine, out var ob);
            if (vb.AsSpan().SequenceEqual(ob))
            {
                t[0]++;
                continue;
            }
            List<(string, ValveKeyValue.KVObject)> Trees(byte[] bytes)
            {
                using var r = new Resource();
                r.Read(new MemoryStream(bytes));
                return [.. r.Blocks.Select(x => (x.Type.ToString(), x switch
                {
                    BinaryKV3 k => k.Data.Root,
                    ResourceEditInfo2 ed => ed.Data?.Root,
                    _ => null,
                })).Where(x => x.Item2 != null).Select(x => (x.Item1, x.Item2!))];
            }
            var (tv, to) = (Trees(vb), Trees(ob));
            var lines = tv.Count != to.Count ? [$"blocks {string.Join(",", tv.Select(x => x.Item1))} vs {string.Join(",", to.Select(x => x.Item1))}"]
                : tv.Zip(to).SelectMany(z => KvTreeDiff.Diff(z.First.Item2, z.Second.Item2).Select(l => z.First.Item1 + l)).ToList();
            var facts = WorldPhysicsAuthorTests.Facts(vb) == WorldPhysicsAuthorTests.Facts(ob);
            if (lines.Count == 0 && facts)
                t[1]++;
            else
            {
                t[2]++;
                output.WriteLine($"differs {path}: {lines.Count} tree lines{(facts ? "" : ", facts")}{(lines.Count > 0 ? ", " + lines[0] : "")}");
            }
        }
        foreach (var (type, t) in tally)
            output.WriteLine($"{type}: bytes {t[0]}, trees and facts {t[1]}, different {t[2]}, missing {t[3]}");
    }
}
