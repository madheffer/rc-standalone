using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>VTXRAW=&lt;vpk a&gt;|&lt;vpk b&gt;|&lt;model path&gt;</c>): every embedded mesh's
/// vertex buffers field by field, as packed bytes: how many vertices differ in
/// each field, with the first difference's bytes.
/// </summary>
public class VertexRawProbe(ITestOutputHelper output)
{
    [Fact]
    public void Compare()
    {
        if (Environment.GetEnvironmentVariable("VTXRAW") is not { Length: > 0 } spec || spec.Split('|') is not [var a, var b, var path])
            return;
        List<VBIB.OnDiskBufferData[]> Read(string vpk)
        {
            using var pkg = new Package();
            pkg.Read(vpk);
            pkg.ReadEntry(pkg.FindEntry(path)!, out var bytes);
            using var r = new Resource();
            r.Read(new MemoryStream(bytes));
            return [.. ((Model)r.DataBlock!).GetEmbeddedMeshesAndLoD().Select(m => m.Mesh.VBIB.VertexBuffers.ToArray())];
        }
        var x = Read(a);
        var y = Read(b);
        for (var m = 0; m < Math.Min(x.Count, y.Count); m++)
            for (var v = 0; v < Math.Min(x[m].Length, y[m].Length); v++)
            {
                var (p, q) = (x[m][v], y[m][v]);
                if (p.ElementCount != q.ElementCount || p.ElementSizeInBytes != q.ElementSizeInBytes)
                {
                    output.WriteLine($"mesh {m} vb {v}: {p.ElementCount}x{p.ElementSizeInBytes} vs {q.ElementCount}x{q.ElementSizeInBytes}");
                    continue;
                }
                foreach (var f in p.InputLayoutFields)
                {
                    var size = VBIB.GetFormatInfo(f).ElementSize * VBIB.GetFormatInfo(f).ElementCount;
                    int differ = 0;
                    string first = "";
                    for (var e = 0; e < p.ElementCount; e++)
                    {
                        var at = (int)(e * p.ElementSizeInBytes + f.Offset);
                        var s1 = p.Data.AsSpan(at, size);
                        var s2 = q.Data.AsSpan(at, size);
                        if (!s1.SequenceEqual(s2))
                        {
                            if (differ++ == 0)
                                first = $" e.g. vertex {e}: {Convert.ToHexString(s1)} vs {Convert.ToHexString(s2)}";
                        }
                    }
                    if (differ > 0)
                        output.WriteLine($"mesh {m} vb {v} {f.SemanticName}{f.SemanticIndex} ({f.Format}): {differ} of {p.ElementCount} differ{first}");
                }
            }
        output.WriteLine($"{x.Count} vs {y.Count} meshes");
    }
}
