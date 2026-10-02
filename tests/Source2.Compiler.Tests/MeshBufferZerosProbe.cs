using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>MESHBUF=&lt;dir&gt;</c> from tools/hulls/dump_meshbuf.py,
/// <c>MESHBUF_NEAR=x,y,z</c>): the normal and tangent values of the dump whose
/// first position is nearest the point, with each zero's sign, and how many
/// corners index each value (ledger 37).
/// </summary>
public class MeshBufferZerosProbe(ITestOutputHelper output)
{
    [Fact]
    public void Zeros()
    {
        if (Environment.GetEnvironmentVariable("MESHBUF") is not { Length: > 0 } dir || Environment.GetEnvironmentVariable("MESHBUF_NEAR") is not { } near)
            return;
        var p = near.Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var target = new Vector3(p[0], p[1], p[2]);
        static string Z(float x) => BitConverter.SingleToInt32Bits(x) == int.MinValue ? "-0" : x.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var best = (Dist: float.MaxValue, File: "");
        foreach (var file in Directory.GetFiles(dir, "*.dmx"))
        {
            var doc = DmxBinary.ReadFile(file);
            foreach (var data in doc.Elements.Where(e => e.Type == "DmeVertexData"))
                if (data.Get<object?[]>("position$0") is { Length: > 0 } pos && pos[0] is Vector3 first)
                {
                    var d = Vector3.Distance(first, target);
                    if (d < best.Dist)
                        best = (d, file);
                }
        }
        if (Environment.GetEnvironmentVariable("MESHBUF_FILE") is { } chosen)
            best = (0, Path.Combine(dir, chosen));
        output.WriteLine($"nearest {best.File} ({best.Dist})");
        // Every dump holding a tangent with these x and y.
        foreach (var file in Directory.GetFiles(dir, "*.dmx"))
            foreach (var data in DmxBinary.ReadFile(file).Elements.Where(e => e.Type == "DmeVertexData"))
                foreach (var name in (data.Get<object?[]>("vertexFormat") ?? []).Select(x => (string)x!).Where(n => n.StartsWith("tangent")))
                    foreach (var t in (data.Get<object?[]>(name) ?? []).OfType<Vector4>())
                        if (t.X == 1f && t.Y == -7.7e-9f && t.Z == 0f)
                            output.WriteLine($"FOUND {Path.GetFileName(file)} {name}: {Z(t.X)},{Z(t.Y)},{Z(t.Z)},{Z(t.W)}");
        var vd = DmxBinary.ReadFile(best.File).Elements.Where(e => e.Type == "DmeVertexData").ToList();
        foreach (var data in vd)
        {
            var names = (data.Get<object?[]>("vertexFormat") ?? []).Select(x => (string)x!).ToList();
            output.WriteLine($"vertex data {data.Name}: {string.Join(" ", names)}");
            foreach (var name in names.Where(n => n.StartsWith("normal") || n.StartsWith("tangent")))
            {
                var values = data.Get<object?[]>(name) ?? [];
                var indices = (data.Get<object?[]>(name + "Indices") ?? []).Select(x => (int)x!).ToList();
                output.WriteLine($"  {name}: {values.Length} values, {indices.Count} corners");
                for (var i = 0; i < values.Length; i++)
                {
                    var comps = values[i] switch { Vector3 v => new[] { v.X, v.Y, v.Z }, Vector4 v => new[] { v.X, v.Y, v.Z, v.W }, _ => [] };
                    if (comps.Any(c => c == 0f))
                        output.WriteLine($"    {i}: {string.Join(",", comps.Select(Z))} used by corners {string.Join(" ", indices.Select((x, k) => (x, k)).Where(t => t.x == i).Select(t => t.k).Take(8))}");
                }
            }
        }
    }
}
