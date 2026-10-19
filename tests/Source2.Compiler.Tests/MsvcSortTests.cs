using Xunit;
using MapsSort = Source2.Compiler.Maps.MsvcSort;

namespace Source2.Compiler.Tests;

/// <summary>
/// The compiler's MSVC std::sort (Simulation.MsvcSort and the Maps wrapper) against MSVC's own std::sort: tools/re/msvc_sort_reference.cpp sorts the same tie-heavy
/// records (sizes around the 32 and 40 thresholds and up to 20,000, 1 to 100,000 distinct keys, some
/// 0x7fffffff) and Data/msvc_sort_hashes.txt holds an FNV-1a 64 hash of each resulting id order.
/// </summary>
public class MsvcSortTests
{
    private sealed record Rec(int Id, int Key);

    private readonly struct KeyLess : Source2.Compiler.Simulation.ILess<Rec>
    {
        public bool Less(in Rec a, in Rec b) => a.Key < b.Key;
    }

    // Every MSVC std::sort port in the compiler, on the same records.
    public static TheoryData<string> Ports => new() { "Maps", "Maps (span)", "Simulation" };

    private static void SortWith(string port, List<Rec> recs)
    {
        switch (port)
        {
            case "Maps (span)":
                MapsSort.Sort(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(recs), (x, y) => x.Key < y.Key);
                break;
            case "Maps":
                var a = recs.ToArray();
                MapsSort.Sort(a, (x, y) => x.Key < y.Key);
                recs.Clear();
                recs.AddRange(a);
                break;
            default:
                var b = recs.ToArray();
                Source2.Compiler.Simulation.MsvcSort.Sort(b.AsSpan(), new KeyLess());
                recs.Clear();
                recs.AddRange(b);
                break;
        }
    }

    [Theory]
    [MemberData(nameof(Ports))]
    public void OrderEqualsMsvc(string port)
    {
        var expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Data", "msvc_sort_hashes.txt"))
            .Where(l => l.Length > 0).Select(l => l.Split(' ')).ToList();
        uint s = 12345;
        uint Next() { s = s * 1664525u + 1013904223u; return s >> 8; }
        var i = 0;
        foreach (var n in new[] { 5, 31, 32, 33, 41, 64, 100, 257, 1000, 5000, 20000 })
            foreach (var keys in new[] { 1, 3, 17, 100000 })
            {
                var recs = new List<Rec>();
                for (var k = 0; k < n; k++)
                {
                    var key = (int)(Next() % (uint)keys);
                    if (Next() % 13 == 0)
                        key = 0x7fffffff;
                    recs.Add(new Rec(k, key));
                }
                SortWith(port, recs);
                var line = $"{n} {keys} {string.Join(" ", recs.Select(r => r.Id))}";
                var h = 0xcbf29ce484222325UL;
                foreach (var b in System.Text.Encoding.ASCII.GetBytes(line))
                    h = (h ^ b) * 0x100000001b3UL;
                Assert.Equal($"{expected[i][0]} {expected[i][1]} {expected[i][2]}", $"{n} {keys} {h:x16}");
                i++;
            }
        Assert.Equal(expected.Count, i);
    }
}
