using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Tooling: a copy of a .vmap with one mesh's vertex paint rewritten, for
/// capturing how the blend split reads paint. The paint array is found in the
/// file by its exact bytes (it must occur once) and channel 0 of corner i
/// becomes ((i * 37) mod 101) / 100, so layers alternate and a vertex's
/// corners disagree. With <c>flat</c>, the mesh's subdivision levels are
/// zeroed too (found the same way), making every face a plain polygon.
/// <c>PAINTPATCH=&lt;vmap&gt;|&lt;node id&gt;|&lt;out vmap&gt;[|flat]</c>.
/// </summary>
public class PaintPatch(ITestOutputHelper output)
{
    [Fact]
    public void WriteCopy()
    {
        if (Environment.GetEnvironmentVariable("PAINTPATCH") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var bytes = File.ReadAllBytes(parts[0]);
        var doc = DmxBinary.ReadFile(parts[0]);
        var id = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        var mesh = doc.OfType("CMapMesh").First(m => m.GetValue<int>("nodeID") == id);
        var data = mesh.Get<DmxBinary.Element>("meshData")!;
        var paint = data.Get<DmxBinary.Element>("faceVertexData")!.GetElements("streams")
            .First(s => s.Name.Split(':')[0] == "VertexPaintBlendParams").Get<object?[]>("data")!;
        var old = new byte[4 + (paint.Length * 16)];
        BitConverter.TryWriteBytes(old.AsSpan(0, 4), paint.Length);
        for (var i = 0; i < paint.Length; i++)
        {
            var v = (Vector4)paint[i]!;
            for (var k = 0; k < 4; k++)
                BitConverter.TryWriteBytes(old.AsSpan(4 + (i * 16) + (k * 4), 4), k switch { 0 => v.X, 1 => v.Y, 2 => v.Z, _ => v.W });
        }
        var at = Only(bytes, old, "paint");
        for (var i = 0; i < paint.Length; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(at + 4 + (i * 16), 4), (i * 37 % 101) / 100f);
        if (parts.Length > 3 && parts[3] == "flat")
        {
            var levels = data.Get<DmxBinary.Element>("subdivisionData")!.Get<object?[]>("subdivisionLevels")!;
            var lv = new byte[4 + (levels.Length * 4)];
            BitConverter.TryWriteBytes(lv.AsSpan(0, 4), levels.Length);
            for (var i = 0; i < levels.Length; i++)
                BitConverter.TryWriteBytes(lv.AsSpan(4 + (i * 4), 4), (int)levels[i]!);
            var lat = Only(bytes, lv, "levels");
            Array.Clear(bytes, lat + 4, levels.Length * 4);
        }
        File.WriteAllBytes(parts[2], bytes);
        output.WriteLine($"wrote {parts[2]}: paint of {paint.Length} corners at {at}");
    }

    private static int Only(byte[] haystack, byte[] needle, string what)
    {
        var first = haystack.AsSpan().IndexOf(needle);
        Assert.True(first >= 0, $"{what} not found");
        Assert.True(haystack.AsSpan(first + 1).IndexOf(needle) < 0, $"{what} found twice");
        return first;
    }
}
