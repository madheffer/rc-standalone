using System.Numerics;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>NAVGEN_CAPTURE=&lt;capture dir&gt;</c>, <c>NAVGEN_PKG=&lt;full vpk&gt;|&lt;map&gt;</c>): the
/// navsystem stage dumps of tools/re/capture_navgen.py against the package's world collision.
/// </summary>
public sealed class NavGenProbe(ITestOutputHelper output)
{
    internal static (List<int[]> Polys, List<Vector3d> Verts) Load(string path)
    {
        var b = File.ReadAllBytes(path);
        int n = BitConverter.ToInt32(b, 0), v = BitConverter.ToInt32(b, 4);
        var polys = new List<int[]>();
        for (var i = 0; i < n; i++)
        {
            var at = 8 + i * 0x88;
            var c = BitConverter.ToInt32(b, at);
            polys.Add([.. Enumerable.Range(0, c).Select(k => BitConverter.ToInt32(b, at + 0x68 + k * 4))]);
        }
        var verts = new List<Vector3d>();
        var off = 8 + n * 0x88;
        for (var i = 0; i < v; i++)
            verts.Add(new Vector3d(BitConverter.ToDouble(b, off + i * 24), BitConverter.ToDouble(b, off + i * 24 + 8), BitConverter.ToDouble(b, off + i * 24 + 16)));
        return (polys, verts);
    }

    internal readonly record struct Vector3d(double X, double Y, double Z);

    [Fact]
    public void InputAgainstWorldPhysics()
    {
        if (Environment.GetEnvironmentVariable("NAVGEN_CAPTURE") is not { } dir || Environment.GetEnvironmentVariable("NAVGEN_PKG") is not { } spec)
            return;
        var (polys, verts) = Load(Path.Combine(dir, "000_ValidateVertices_out.bin"));
        var p = spec.Split('|');
        using var package = new ValvePak.Package();
        package.Read(p[0]);
        package.ReadEntry(package.FindEntry($"maps/{p[1]}/world_physics.vmdl_c")!, out var bytes);
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        var phys = ((Model)resource.DataBlock!).GetEmbeddedPhys()!;
        foreach (var part in phys.Parts)
        {
            foreach (var m in part.Shape.Meshes)
            {
                var tris = m.Shape.GetTriangles().ToArray();
                var vs = m.Shape.GetVertices().ToArray();
                output.WriteLine($"mesh: {tris.Length} triangles, {vs.Length} vertices, collision attribute {m.CollisionAttributeIndex}, first {vs[tris[0].X]} {vs[tris[0].Y]} {vs[tris[0].Z]}");
            }
            output.WriteLine($"hulls {part.Shape.Hulls.Length}");
        }
        var mesh0 = phys.Parts[0].Shape.Meshes[0].Shape;
        var mt = mesh0.GetTriangles().ToArray();
        var mv = mesh0.GetVertices().ToArray();
        string Key(Vector3 a) => $"{a.X:R},{a.Y:R},{a.Z:R}";
        string KeyD(Vector3d a) => $"{(float)a.X:R},{(float)a.Y:R},{(float)a.Z:R}";
        var byKey = new Dictionary<string, List<(int Tri, int Rot)>>();
        for (var t = 0; t < mt.Length; t++)
        {
            var c = new[] { mv[mt[t].X], mv[mt[t].Y], mv[mt[t].Z] };
            for (var r = 0; r < 3; r++)
            {
                var k = Key(c[r]) + "|" + Key(c[(r + 1) % 3]) + "|" + Key(c[(r + 2) % 3]);
                if (!byKey.TryGetValue(k, out var l))
                    byKey[k] = l = [];
                l.Add((t, r));
            }
        }
        var map = polys.Select(pp => byKey.TryGetValue(KeyD(verts[pp[0]]) + "|" + KeyD(verts[pp[1]]) + "|" + KeyD(verts[pp[2]]), out var l) ? $"{l[0].Tri}r{l[0].Rot}" : "?").ToList();
        output.WriteLine($"input -> mesh triangle: {string.Join(" ", map.Take(60))}");
        output.WriteLine($"unmatched {map.Count(m => m == "?")}, exact float {verts.All(v => (double)(float)v.X == v.X && (double)(float)v.Y == v.Y && (double)(float)v.Z == v.Z)}");
        output.WriteLine($"nav input: {polys.Count} polys, {verts.Count} vertices; first {string.Join(" ", polys[0].Select(i => verts[i]))}");
        var zs = verts.Select(v => v.Z).Distinct().OrderBy(z => z).ToList();
        output.WriteLine($"z values: {string.Join(", ", zs.Take(20))}");
    }
}
