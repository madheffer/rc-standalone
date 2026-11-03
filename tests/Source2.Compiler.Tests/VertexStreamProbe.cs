using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>VTXSTREAM=&lt;vpk a&gt;|&lt;vpk b&gt;|&lt;model path&gt;|&lt;semantic&gt;[|&lt;index&gt;]</c>):
/// one layout field of a model's first mesh, decoded from both packages side
/// by side, with how many vertices agree.
/// </summary>
public class VertexStreamProbe(ITestOutputHelper output)
{
    [Fact]
    public void Compare()
    {
        if (Environment.GetEnvironmentVariable("VTXSTREAM") is not { Length: > 0 } spec || spec.Split('|') is not [var a, var b, var path, var semantic, .. var rest])
            return;
        var index = rest is [var i] ? int.Parse(i) : 0;
        float[][] Read(string vpk)
        {
            using var pkg = new Package();
            pkg.Read(vpk);
            pkg.ReadEntry(pkg.FindEntry(path)!, out var bytes);
            using var r = new Resource();
            r.Read(new MemoryStream(bytes));
            var mesh = ((Model)r.DataBlock!).GetEmbeddedMeshesAndLoD().First().Mesh;
            foreach (var vb in mesh.VBIB.VertexBuffers)
                foreach (var f in vb.InputLayoutFields.Where(f => f.SemanticName.Equals(semantic, StringComparison.OrdinalIgnoreCase) && f.SemanticIndex == index))
                {
                    output.WriteLine($"{Path.GetFileName(vpk)}: {vb.ElementCount} vertices, {f.Format} at {f.Offset}");
                    var (_, count) = VBIB.GetFormatInfo(f);
                    return count switch
                    {
                        2 => [.. VBIB.GetVector2AttributeArray(vb, f).Select(v => new[] { v.X, v.Y })],
                        3 => [.. VBIB.GetVector3AttributeArray(vb, f).Select(v => new[] { v.X, v.Y, v.Z })],
                        _ => [.. VBIB.GetVector4AttributeArray(vb, f).Select(v => new[] { v.X, v.Y, v.Z, v.W })],
                    };
                }
            return [];
        }
        var x = Read(a);
        var y = Read(b);
        var same = x.Zip(y).Count(p => p.First.SequenceEqual(p.Second));
        output.WriteLine($"{same} of {Math.Min(x.Length, y.Length)} equal");
        // Per-vertex differences, rounded to 1e-3 and counted (a uniform shift shows as one line).
        foreach (var g in x.Zip(y).GroupBy(p => string.Join(",", p.First.Zip(p.Second).Select(q => Math.Round(q.First - q.Second, 3)))).OrderByDescending(g => g.Count()).Take(12))
            output.WriteLine($"  a - b = {g.Key}: {g.Count()} vertices");
        var shown = 0;
        var all = Environment.GetEnvironmentVariable("VTXALL") == "1";
        for (var v = 0; v < Math.Min(x.Length, y.Length) && (all || shown < 8); v++)
            if (!x[v].SequenceEqual(y[v]))
            {
                shown++;
                output.WriteLine($"  {v}: {string.Join(",", x[v].Select(z => z.ToString("R")))} | {string.Join(",", y[v].Select(z => z.ToString("R")))}");
            }
    }
}
