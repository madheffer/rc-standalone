using System.Text.Json;
using Source2.Compiler.Maps.NavGen;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The fan split's fallback triangulation against navsystem's own 1800f3780, called directly by
/// tools/re/call_navsystem.py triangulate (<c>NAVGEN_TRI=&lt;file&gt;</c>, default
/// %TEMP%/navgen_capture/triangulate.json).
/// </summary>
public sealed class NavGenTriangulateTests(ITestOutputHelper output)
{
    [Fact]
    public void TriangulationMatchesNavsystem()
    {
        var path = Environment.GetEnvironmentVariable("NAVGEN_TRI") ?? Path.Combine(Path.GetTempPath(), "navgen_capture", "triangulate.json");
        if (!File.Exists(path))
            return;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        int cases = 0, bad = 0;
        var kinds = new Dictionary<string, int>();
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            cases++;
            var corners = c.GetProperty("corners").EnumerateArray()
                .Select(p => ((float)p[0].GetDouble(), (float)p[1].GetDouble(), (float)p[2].GetDouble())).ToList();
            var n = c.GetProperty("normal");
            var normal = ((float)n[0].GetDouble(), (float)n[1].GetDouble(), (float)n[2].GetDouble());
            var cap = c.GetProperty("cap").GetInt32();
            var valve = c.GetProperty("valve");
            var want = valve.GetProperty("tris").EnumerateArray().Select(e => e.GetInt32()).ToList();
            var tris = new List<int>();
            var count = NavGenTriangulate.Triangulate(corners, normal, tris, cap);
            var got = tris.Take(count).ToList();
            if (count != valve.GetProperty("count").GetInt32() || !got.SequenceEqual(want))
            {
                var kind = c.GetProperty("kind").GetString()!;
                kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                if (bad++ < 5)
                    output.WriteLine($"case {cases - 1} ({kind}, {corners.Count} corners): ours {count} [{string.Join(",", got)}], Valve [{string.Join(",", want)}]");
            }
        }
        output.WriteLine($"{cases} cases, {bad} differ ({string.Join(", ", kinds.Select(k => $"{k.Key} {k.Value}"))})");
        Assert.Equal(0, bad);
    }
}
