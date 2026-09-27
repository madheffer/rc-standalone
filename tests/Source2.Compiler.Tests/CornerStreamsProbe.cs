using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: a mesh piece's corners (every stream, before the weld) near
/// a point. <c>CORNERS=&lt;.vmap&gt;|&lt;node id&gt;|&lt;material&gt;|x,y|radius[|noshift]</c>.
/// </summary>
public class CornerStreamsProbe(ITestOutputHelper output)
{
    [Fact]
    public void Near()
    {
        if (Environment.GetEnvironmentVariable("CORNERS") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        var doc = DmxBinary.ReadFile(p[0]);
        var mesh = MapMeshes.Read(doc).First(m => m.NodeId == int.Parse(p[1]));
        var material = int.Parse(p[2]);
        var at = p[3].Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var radius = float.Parse(p[4], System.Globalization.CultureInfo.InvariantCulture);
        var world = MapMeshes.Local(mesh.Element!);
        foreach (var piece in MapMeshCorners.Build(mesh.Element!, !(p.Length > 5 && p[5] == "noshift")).Where(x => x.Material == material))
        {
            output.WriteLine("streams: " + string.Join(", ", piece.Streams.Select(s => $"{s.Name}@{s.First}x{s.Count}{(s.Ignored ? " ignored" : "")}")));
            for (var c = 0; c * piece.Stride < piece.Vertices.Length; c++)
            {
                var v = piece.Vertices.AsSpan(c * piece.Stride, piece.Stride).ToArray();
                var w = MapMeshes.Transform(world, new Vector3(v[0], v[1], v[2]));
                if (MathF.Abs(w.X - at[0]) > radius || MathF.Abs(w.Y - at[1]) > radius)
                    continue;
                output.WriteLine($"  corner {c} world {w.X:R},{w.Y:R},{w.Z:R}: {string.Join(" ", v.Select(x => x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))}");
            }
        }
    }
}
