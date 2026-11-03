using System.Globalization;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>PHYSMESH=&lt;vpk a&gt;|&lt;vpk b&gt;|&lt;vmdl_c path&gt;</c>): part 0's mesh shapes of a physics
/// model in two packages, vertex by vertex and triangle by triangle, listing where they differ.
/// </summary>
public class PhysMeshDumpProbe(ITestOutputHelper output)
{
    [Fact]
    public void Compare()
    {
        if (Environment.GetEnvironmentVariable("PHYSMESH") is not { Length: > 0 } spec || spec.Split('|') is not [var a, var b, var path])
            return;
        (System.Numerics.Vector3[] V, int[] T)[] Read(string vpk)
        {
            using var pkg = new Package();
            pkg.Read(vpk);
            pkg.ReadEntry(pkg.FindEntry(path)!, out var bytes);
            using var r = new Resource();
            r.Read(new MemoryStream(bytes));
            var phys = ((Model)r.DataBlock!).GetEmbeddedPhys()!;
            return [.. phys.Parts[0].Shape.Meshes.Select(m => (m.Shape.GetVertices().ToArray(), m.Shape.GetTriangles().ToArray().SelectMany(t => new[] { t.X, t.Y, t.Z }).ToArray()))];
        }
        var x = Read(a);
        var y = Read(b);
        for (var m = 0; m < Math.Min(x.Length, y.Length); m++)
        {
            output.WriteLine($"mesh {m}: {x[m].V.Length} / {y[m].V.Length} vertices, {x[m].T.Length / 3} / {y[m].T.Length / 3} triangles");
            var shown = 0;
            for (var i = 0; i < Math.Min(x[m].V.Length, y[m].V.Length) && shown < 12; i++)
                if (x[m].V[i] != y[m].V[i])
                {
                    shown++;
                    output.WriteLine($"  v{i}: {F(x[m].V[i])} | {F(y[m].V[i])}");
                }
            shown = 0;
            for (var i = 0; i < Math.Min(x[m].T.Length, y[m].T.Length) / 3 && shown < 12; i++)
                if (!x[m].T.AsSpan(i * 3, 3).SequenceEqual(y[m].T.AsSpan(i * 3, 3)))
                {
                    shown++;
                    output.WriteLine($"  t{i}: {x[m].T[i * 3]},{x[m].T[(i * 3) + 1]},{x[m].T[(i * 3) + 2]} | {y[m].T[i * 3]},{y[m].T[(i * 3) + 1]},{y[m].T[(i * 3) + 2]}");
                }
        }
        static string F(System.Numerics.Vector3 v) => string.Join(",", new[] { v.X, v.Y, v.Z }.Select(f => f.ToString("R", CultureInfo.InvariantCulture)));
    }
}
