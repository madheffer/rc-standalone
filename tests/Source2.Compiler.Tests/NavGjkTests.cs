using System.Text.Json;
using Source2.Compiler.Maps.NavGen;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// IslandRemoval's distances: navsystem's GJK (<see cref="NavGjk"/>) on shapes with known answers, and
/// against navsystem's 1800a3060 and vphysics2's CVPhysics2Interface vfn 0x190 called directly by
/// tools/re/call_navsystem.py gjk (<c>NAVGEN_GJK=&lt;file&gt;</c>, default %TEMP%/navgen_capture/gjk.json).
/// </summary>
public sealed class NavGjkTests(ITestOutputHelper output)
{
    private static (float X, float Y, float Z)[] Square(float x, float y, float z, float s)
        => [(x, y, z), (x + s, y, z), (x + s, y + s, z), (x, y + s, z)];

    [Fact]
    public void KnownDistances()
    {
        Assert.Equal(4f, NavGjk.Distance(Square(0, 0, 0, 32), Square(36, 0, 0, 32)));
        Assert.Equal(3f, NavGjk.Distance(Square(0, 0, 0, 32), Square(8, 8, 3, 32)));
        Assert.Equal(0f, NavGjk.Distance(Square(0, 0, 0, 32), Square(32, 0, 0, 32)));
        Assert.Equal(0f, NavGjk.Distance(Square(0, 0, 0, 32), Square(16, 16, 0, 32)));
        // A point over a triangle's interior, and past its corner.
        Assert.Equal(5f, NavGjk.Distance([(10, 10, 5)], [(0, 0, 0), (100, 0, 0), (0, 100, 0)]));
        Assert.Equal(5f, NavGjk.Distance([(-3, -4, 0)], [(0, 0, 0), (100, 0, 0), (0, 100, 0)]));
        // A box round a polygon touches it.
        (float X, float Y, float Z)[] box = [(-40, -40, -40), (40, -40, -40), (-40, 40, -40), (40, 40, -40),
                                             (-40, -40, 40), (40, -40, 40), (-40, 40, 40), (40, 40, 40)];
        Assert.Equal(0f, NavGjk.Distance(box, Square(-10, -10, 0, 20)));
        Assert.Equal(10f, NavGjk.Distance(box, Square(50, -10, 0, 20)));
    }

    [Fact]
    public void DistancesMatchNavsystem()
    {
        var path = Environment.GetEnvironmentVariable("NAVGEN_GJK") ?? Path.Combine(Path.GetTempPath(), "navgen_capture", "gjk.json");
        if (!File.Exists(path))
            return;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        static (float X, float Y, float Z)[] Points(JsonElement e)
            => [.. e.EnumerateArray().Select(p => ((float)p[0].GetDouble(), (float)p[1].GetDouble(), (float)p[2].GetDouble()))];
        int cases = 0, bad = 0, physBad = 0, physDiffer = 0;
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            cases++;
            var a = Points(c.GetProperty("a"));
            var b = Points(c.GetProperty("b"));
            var valve = c.GetProperty("valve");
            var nav = (float)valve.GetProperty("nav").GetDouble();
            var phys = (float)valve.GetProperty("phys").GetDouble();
            var ours = NavGjk.Distance(a, b);
            if (BitConverter.SingleToInt32Bits(ours) != BitConverter.SingleToInt32Bits(nav))
            {
                if (bad++ < 5)
                    output.WriteLine($"case {cases - 1} ({c.GetProperty("kind").GetString()}): ours {ours:R}, navsystem {nav:R}");
            }
            // vphysics2's own GJK: the seed and link test only reads whether it is at most 0.125.
            if ((ours <= 0.125f) != (phys <= 0f))
            {
                if (physBad++ < 5)
                    output.WriteLine($"case {cases - 1} ({c.GetProperty("kind").GetString()}): ours {ours:R}, vphysics2 {phys:R}");
            }
            if (phys > 0f && phys != ours)
                physDiffer++;
        }
        output.WriteLine($"{cases} cases, {bad} differ from navsystem, {physBad} touch tests differ from vphysics2 ({physDiffer} vphysics2 distances differ)");
        Assert.Equal(0, bad);
        Assert.Equal(0, physBad);
    }
}
