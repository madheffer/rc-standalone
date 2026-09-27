using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler.Tests;

/// <summary>
/// Two decoded KV3 trees compared the way the engine would read them: key
/// order, value types, flags, integer widths, float bits and blob bytes, with
/// every difference reported by path. Compressed block bytes are encoder
/// defined (docs/RC_PARITY.md), so the decoded tree is what parity means.
/// </summary>
internal static class KvTreeDiff
{
    /// <summary>Every difference, one line each, at most <paramref name="limit"/>.</summary>
    public static List<string> Diff(KVObject valve, KVObject mine, int limit = 200)
    {
        var report = new List<string>();
        Walk("", valve, mine, report, limit);
        return report;
    }

    private static void Walk(string path, KVObject a, KVObject b, List<string> report, int limit)
    {
        if (report.Count >= limit)
            return;
        if (a.ValueType != b.ValueType || a.Flag != b.Flag)
        {
            report.Add($"{path}: type {a.ValueType}/{a.Flag} vs {b.ValueType}/{b.Flag}");
            return;
        }
        switch (a.ValueType)
        {
            case KVValueType.Collection:
            {
                var ak = a.Children.Select(c => c.Key).ToList();
                var bk = b.Children.Select(c => c.Key).ToList();
                if (!ak.SequenceEqual(bk))
                {
                    var missing = ak.Except(bk).ToList();
                    var extra = bk.Except(ak).ToList();
                    report.Add($"{path}: keys differ; missing [{string.Join(", ", missing)}], extra [{string.Join(", ", extra)}]"
                               + (missing.Count == 0 && extra.Count == 0 ? $", order {string.Join(",", ak)} vs {string.Join(",", bk)}" : ""));
                }
                foreach (var key in ak.Intersect(bk))
                    Walk($"{path}.{key}", a[key]!, b[key]!, report, limit);
                break;
            }
            case KVValueType.Array:
            {
                var av = a.Values.ToList();
                var bv = b.Values.ToList();
                if (av.Count != bv.Count)
                    report.Add($"{path}: {av.Count} elements vs {bv.Count}");
                for (var i = 0; i < Math.Min(av.Count, bv.Count); i++)
                    Walk($"{path}[{i}]", av[i], bv[i], report, limit);
                break;
            }
            case KVValueType.BinaryBlob:
            {
                var ab = a.AsBlob();
                var bb = b.AsBlob();
                if (!ab.AsSpan().SequenceEqual(bb))
                {
                    var at = 0;
                    while (at < Math.Min(ab.Length, bb.Length) && ab[at] == bb[at])
                        at++;
                    report.Add($"{path}: blob {ab.Length} vs {bb.Length} bytes, first difference at {at}");
                }
                break;
            }
            case KVValueType.FloatingPoint:
            case KVValueType.FloatingPoint64:
            {
                var x = BitConverter.DoubleToInt64Bits((double)a);
                var y = BitConverter.DoubleToInt64Bits((double)b);
                if (x != y)
                    report.Add($"{path}: {(double)a:R} vs {(double)b:R}");
                break;
            }
            default:
                if (a.ToString() != b.ToString())
                    report.Add($"{path}: {a} vs {b}");
                break;
        }
    }

    /// <summary>The tree's structure with types, arrays past <paramref name="elements"/> elided.</summary>
    public static IEnumerable<string> Typed(KVObject v, string key = "", int depth = 0, int elements = 2)
    {
        var pad = new string(' ', depth * 2);
        var head = $"{pad}{key}: {v.ValueType}{(v.Flag != 0 ? "/" + v.Flag : "")}";
        switch (v.ValueType)
        {
            case KVValueType.Collection:
                yield return head;
                foreach (var c in v.Children)
                    foreach (var line in Typed(c.Value, c.Key, depth + 1, elements))
                        yield return line;
                break;
            case KVValueType.Array:
            {
                var items = v.Values.ToList();
                yield return $"{head} [{items.Count}]";
                for (var i = 0; i < Math.Min(items.Count, elements); i++)
                    foreach (var line in Typed(items[i], $"[{i}]", depth + 1, elements))
                        yield return line;
                break;
            }
            case KVValueType.BinaryBlob:
            {
                var blob = v.AsBlob();
                yield return $"{head} {blob.Length} bytes {Convert.ToHexString(blob.AsSpan(0, Math.Min(32, blob.Length)))}";
                break;
            }
            default:
                yield return $"{head} = {v}";
                break;
        }
    }
}
