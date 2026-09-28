using System.Collections;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="DmxText"/> against <see cref="DmxBinary"/>: a binary map and the
/// keyvalues2 text Valve's dmxconvert makes of it read to the same document,
/// element for element (order, type, name, id) and attribute for attribute,
/// references compared by the element's id.
/// </summary>
public class DmxTextTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe")]
    [InlineData("s2c_rc_probe", "cardtest")]
    [InlineData("s2c_rc_probe", "probe_classes")]
    [InlineData("s2c_rc_probe", "c2m2_fairgrounds_csgo_gameplay")]
    [InlineData("s2c_big", "ze_ffvii_mako_reactor_v6_p")]
    public void TextReadsAsBinary(string addon, string map)
    {
        var source = MapFixtures.VmapSource(addon, map);
        if (source is null || CS2Fixtures.StockPak() is not { } pak)
        {
            MapFixtures.Skip($"{map}'s source");
            return;
        }
        var dmxconvert = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, "..", "bin", "win64", "dmxconvert.exe"));
        var text = Path.Combine(Path.GetTempPath(), $"s2c_dmxtext_{map}.vmap");
        var run = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dmxconvert)
        {
            ArgumentList = { "-i", source, "-o", text, "-oe", "keyvalues2" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        run.StandardOutput.ReadToEnd();
        run.WaitForExit();
        var binary = DmxBinary.ReadFile(source);
        var kv2 = DmxBinary.ReadFile(text);
        Assert.Equal(binary.Format, kv2.Format);
        // dmxconvert writes the newest vmap version (atixref's 37 comes back 40).
        output.WriteLine($"format {binary.FormatVersion} -> {kv2.FormatVersion}");
        // Element order is the writer's: Hammer's binary saves its own order,
        // dmxconvert's text the tree's. The prefix element gets a new id.
        static bool Prefix(DmxBinary.Element e) => e.Type == "$prefix_element$";
        var textById = kv2.Elements.Where(e => !Prefix(e)).ToDictionary(e => e.Id);
        // An element nothing references (c2m2's gameplay map has one DmElement)
        // is not written to the text.
        var referenced = new HashSet<DmxBinary.Element>(ReferenceEqualityComparer.Instance) { binary.Elements[0] };
        foreach (var e in binary.Elements)
            foreach (var v in e.Attributes.Values)
                foreach (var r in v is object?[] arr ? arr : [v])
                    if (r is DmxBinary.Element x)
                        referenced.Add(x);
        var kept = binary.Elements.Where(e => !Prefix(e) && referenced.Contains(e)).ToList();
        Assert.Equal(kept.Count, textById.Count);
        Assert.Equal(binary.Prefix.Count, kv2.Prefix.Count);
        Assert.Equal(binary.Elements[0].Id, kv2.Elements[0].Id);
        var problems = new List<string>();
        foreach (var a in kept)
        {
            if (problems.Count >= 20)
                break;
            if (!textById.TryGetValue(a.Id, out var b) || a.Type != b.Type || a.Name != b.Name)
            {
                problems.Add($"{a.Type} '{a.Name}' {a.Id}: {(b == null ? "missing" : $"{b.Type} '{b.Name}'")}");
                continue;
            }
            if (!a.Attributes.Keys.SequenceEqual(b.Attributes.Keys))
                problems.Add($"{a.Type} {a.Id}: attributes {string.Join(",", a.Attributes.Keys)} vs {string.Join(",", b.Attributes.Keys)}");
            foreach (var (key, value) in a.Attributes)
                if (b.Attributes.TryGetValue(key, out var other) && !Same(value, other))
                    problems.Add($"{a.Type} {a.Id}.{key}: {Show(value)} vs {Show(other)}");
        }
        foreach (var p in problems)
            output.WriteLine(p);
        output.WriteLine($"{map}: {binary.Elements.Count} elements");
        Assert.Empty(problems);
        File.Delete(text);
    }

    private static int IndexOf(DmxBinary.Document d, DmxBinary.Element e)
    {
        for (var i = 0; i < d.Elements.Count; i++)
            if (ReferenceEquals(d.Elements[i], e))
                return i;
        return -1;
    }

    private static bool Same(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (DmxBinary.Element x, DmxBinary.Element y) => x.Id == y.Id,
        (byte[] x, byte[] y) => x.AsSpan().SequenceEqual(y),
        (object?[] x, object?[] y) => x.Length == y.Length && x.Zip(y).All(t => Same(t.First, t.Second)),
        (float x, float y) => Near(x, y),
        (System.Numerics.Vector2 x, System.Numerics.Vector2 y) => Near(x.X, y.X) && Near(x.Y, y.Y),
        (System.Numerics.Vector3 x, System.Numerics.Vector3 y) => Near(x.X, y.X) && Near(x.Y, y.Y) && Near(x.Z, y.Z),
        (System.Numerics.Vector4 x, System.Numerics.Vector4 y) => Near(x.X, y.X) && Near(x.Y, y.Y) && Near(x.Z, y.Z) && Near(x.W, y.W),
        (System.Numerics.Quaternion x, System.Numerics.Quaternion y) => Near(x.X, y.X) && Near(x.Y, y.Y) && Near(x.Z, y.Z) && Near(x.W, y.W),
        _ => Equals(a, b),
    };

    // dmxconvert writes a float with seven significant digits, so the text's
    // value is the binary's rounded there (0.00048828125 is "0.0004882812";
    // 3.973472e-08 is "3.97e-08", ten decimals).
    private static bool Near(float x, float y)
        => BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits(y)
           || MathF.Abs(x - y) <= (1e-6f * MathF.Max(MathF.Abs(x), MathF.Abs(y))) + 1e-9f;

    private static string Show(object? v) => v switch
    {
        null => "null",
        DmxBinary.Element e => $"<{e.Type} {e.Id}>",
        IEnumerable e and not string => $"[{string.Join(",", e.Cast<object?>().Take(4).Select(Show))}...]",
        _ => $"{v} ({v.GetType().Name})",
    };
}
