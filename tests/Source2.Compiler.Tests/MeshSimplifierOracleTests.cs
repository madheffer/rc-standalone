using System.Runtime.InteropServices;
using Source2.Compiler.Physics;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// physicsbuilder's mesh simplifier (the 18060xxxx edge collapse under
/// CMesh_Simplify), called in the DLL on random meshes and compared bit for
/// bit with <see cref="MeshSimplifier"/>: every output position as a double and
/// every index.
/// </summary>
public unsafe class MeshSimplifierOracleTests(ITestOutputHelper output)
{
    private const ulong InputInitVa = 0x18060aa30;
    private const ulong InputTrianglesVa = 0x18060aa70;
    private const ulong InputPositionsVa = 0x18060ab00;
    private const ulong SimplifierInitVa = 0x18060b810;
    private const ulong SimplifyVa = 0x18060fdf0;
    private const ulong OutputVa = 0x18060c990;
    private const ulong StaticsVa = 0x180e2a018;

    private static (double[] Positions, int[] Indices) Valve(nint pb, float[] positions, int[] indices, double error, int collapses)
    {
        var input = (byte*)NativeMemory.AllocZeroed(0x40);
        var simplifier = (byte*)NativeMemory.AllocZeroed(0x100);
        var result = (byte*)NativeMemory.AllocZeroed(0x40);
        try
        {
            ((delegate* unmanaged<byte*, byte*>)PhysicsBuilderOracle.At(pb, InputInitVa))(input);
            fixed (int* ip = indices)
                ((delegate* unmanaged<byte*, int, int*, void>)PhysicsBuilderOracle.At(pb, InputTrianglesVa))(input, indices.Length / 3, ip);
            fixed (float* pp = positions)
                ((delegate* unmanaged<byte*, int, float*, int, void>)PhysicsBuilderOracle.At(pb, InputPositionsVa))(input, positions.Length / 3, pp, 12);
            ((delegate* unmanaged<byte*, byte*, byte*>)PhysicsBuilderOracle.At(pb, SimplifierInitVa))(simplifier, input);
            ((delegate* unmanaged<byte*, double, int, void>)PhysicsBuilderOracle.At(pb, SimplifyVa))(simplifier, error, collapses);
            ((delegate* unmanaged<byte*, byte*, byte*>)PhysicsBuilderOracle.At(pb, OutputVa))(simplifier, result);
            var vertexCount = *(int*)result;
            var points = new double[vertexCount * 3];
            new ReadOnlySpan<double>(*(double**)(result + 8), points.Length).CopyTo(points);
            var triangleCount = *(int*)(result + 0x10);
            var tris = new int[triangleCount * 3];
            new ReadOnlySpan<int>(*(int**)(result + 0x18), tris.Length).CopyTo(tris);
            return (points, tris);
        }
        finally
        {
            // The DLL's buffers are left to the process; the test allocates little.
            NativeMemory.Free(input);
            NativeMemory.Free(simplifier);
            NativeMemory.Free(result);
        }
    }

    /// <summary>A grid of quads, each split on a random diagonal, its heights jittered.</summary>
    private static (float[], int[]) Grid(Random r, int nx, int ny, float bump)
    {
        var p = new List<float>();
        for (var y = 0; y <= ny; y++)
            for (var x = 0; x <= nx; x++)
                p.AddRange([x * 16f, y * 16f, (float)(r.NextDouble() * bump)]);
        var t = new List<int>();
        for (var y = 0; y < ny; y++)
            for (var x = 0; x < nx; x++)
            {
                int a = (y * (nx + 1)) + x, b = a + 1, c = a + nx + 1, d = c + 1;
                if (r.Next(2) == 0)
                    t.AddRange([a, b, d, a, d, c]);
                else
                    t.AddRange([a, b, c, b, d, c]);
            }
        return ([.. p], [.. t]);
    }

    /// <summary>A closed box, each side a grid, corners shared, optionally dented.</summary>
    private static (float[], int[]) Box(Random r, int n, float dent)
    {
        var index = new Dictionary<(int, int, int), int>();
        var p = new List<float>();
        var t = new List<int>();
        int V(int x, int y, int z)
        {
            if (!index.TryGetValue((x, y, z), out var i))
            {
                i = index.Count;
                index[(x, y, z)] = i;
                var j = x > 0 && x < n && y > 0 && y < n && z > 0 && z < n ? 0f : (float)(r.NextDouble() * dent);
                p.AddRange([(x * 8f) + j, (y * 8f) - j, (z * 8f) + (j * 0.5f)]);
            }
            return i;
        }
        for (var axis = 0; axis < 3; axis++)
            for (var side = 0; side <= 1; side++)
                for (var u = 0; u < n; u++)
                    for (var v = 0; v < n; v++)
                    {
                        int[] Corner(int du, int dv)
                        {
                            var c = new int[3];
                            c[axis] = side * n;
                            c[(axis + 1) % 3] = u + du;
                            c[(axis + 2) % 3] = v + dv;
                            return c;
                        }
                        var q = new[] { Corner(0, 0), Corner(1, 0), Corner(1, 1), Corner(0, 1) }.Select(c => V(c[0], c[1], c[2])).ToArray();
                        if (side == 1)
                            t.AddRange([q[0], q[1], q[2], q[0], q[2], q[3]]);
                        else
                            t.AddRange([q[0], q[2], q[1], q[0], q[3], q[2]]);
                    }
        return ([.. p], [.. t]);
    }

    /// <summary>Random triangles over few points: non-manifold edges, repeats and degenerate corners.</summary>
    private static (float[], int[]) Soup(Random r, int points, int triangles)
    {
        var p = new float[points * 3];
        for (var i = 0; i < p.Length; i++)
            p[i] = (float)Math.Round(r.NextDouble() * 64, r.Next(3));
        var t = new int[triangles * 3];
        for (var i = 0; i < t.Length; i++)
            t[i] = r.Next(points);
        return (p, t);
    }

    /// <summary>Fans around a few hubs: vertices with more than 32 edges reach std::sort's partition.</summary>
    private static (float[], int[]) Fans(Random r, int hubs, int spokes, float bump)
    {
        var p = new List<float>();
        var t = new List<int>();
        for (var h = 0; h < hubs; h++)
        {
            var hub = p.Count / 3;
            p.AddRange([h * 200f, 0f, (float)(r.NextDouble() * bump)]);
            var first = p.Count / 3;
            for (var s = 0; s < spokes; s++)
            {
                var a = s * 2 * Math.PI / spokes;
                var radius = 40 + (r.NextDouble() * 20);
                p.AddRange([(h * 200f) + (float)(Math.Cos(a) * radius), (float)(Math.Sin(a) * radius), (float)(r.NextDouble() * bump)]);
            }
            for (var s = 0; s < spokes; s++)
                t.AddRange([hub, first + s, first + ((s + 1) % spokes)]);
        }
        return ([.. p], [.. t]);
    }

    [Fact]
    public void SimplifiesAsTheDll()
    {
        if (PhysicsBuilderOracle.Load() is not { } pb)
            return;
        var statics = new ReadOnlySpan<double>((void*)PhysicsBuilderOracle.At(pb, StaticsVa), 13).ToArray();
        output.WriteLine("statics: " + string.Join(" ", statics));
        Assert.All(statics[3..], s => Assert.Equal(0.0, s));
        var r = new Random(20261023);
        var cases = 0;
        var collapsed = 0;
        var runs = int.TryParse(Environment.GetEnvironmentVariable("SIMPLIFY_CASES"), out var n) ? n : 1000;
        for (var i = 0; i < runs; i++)
        {
            var (positions, indices) = (i % 5) switch
            {
                0 => Grid(r, 2 + r.Next(10), 2 + r.Next(10), r.Next(3) == 0 ? 0f : (float)(r.NextDouble() * 8)),
                1 => Box(r, 1 + r.Next(5), r.Next(2) == 0 ? 0f : (float)(r.NextDouble() * 4)),
                2 => Soup(r, 4 + r.Next(12), 2 + r.Next(20)),
                3 => Fans(r, 1 + r.Next(3), 20 + r.Next(60), (float)(r.NextDouble() * 4)),
                _ => Grid(r, 1 + r.Next(30), 1 + r.Next(3), (float)(r.NextDouble() * 64)),
            };
            var error = Math.Pow(10, (r.NextDouble() * 8) - 3);
            var collapses = r.Next(4) == 0 ? r.Next(1, 20) : -1;
            var want = Valve(pb, positions, indices, error, collapses);
            var got = MeshSimplifier.Run(positions, indices, error, collapses);
            cases++;
            if (want.Indices.Length < indices.Length)
                collapsed++;
            if (!Same(want, got))
            {
                // The first collapse after which the two differ, and the mesh.
                var k = 1;
                while (k < 10000 && Same(Valve(pb, positions, indices, error, k), MeshSimplifier.Run(positions, indices, error, k)))
                    k++;
                output.WriteLine($"case {i}: first difference after collapse {k}");
                output.WriteLine("positions " + string.Join(",", positions.Select(x => x.ToString("R"))));
                output.WriteLine("indices " + string.Join(",", indices));
                output.WriteLine("valve " + string.Join(",", Valve(pb, positions, indices, error, k).Indices));
                output.WriteLine("port  " + string.Join(",", MeshSimplifier.Run(positions, indices, error, k).Indices));
            }
            Assert.True(want.Indices.AsSpan().SequenceEqual(got.Indices), $"case {i} ({i % 5}) error {error}: indices {want.Indices.Length} vs {got.Indices.Length}");
            Assert.Equal(want.Positions.Length, got.Positions.Length);
            for (var k = 0; k < want.Positions.Length; k++)
                Assert.True(BitConverter.DoubleToInt64Bits(want.Positions[k]) == BitConverter.DoubleToInt64Bits(got.Positions[k]),
                    $"case {i} ({i % 5}) error {error}: position {k} {want.Positions[k]:R} vs {got.Positions[k]:R}");
        }
        output.WriteLine($"{cases} meshes, {collapsed} simplified");
    }

    private static bool Same((double[] Positions, int[] Indices) a, (double[] Positions, int[] Indices) b)
        => a.Indices.AsSpan().SequenceEqual(b.Indices) && a.Positions.Length == b.Positions.Length
           && a.Positions.Select(BitConverter.DoubleToInt64Bits).SequenceEqual(b.Positions.Select(BitConverter.DoubleToInt64Bits));
}

public unsafe class MeshSimplifierDebugTests(ITestOutputHelper output)
{
    [Fact]
    public void DumpCase()
    {
        if (Environment.GetEnvironmentVariable("SIMPLIFY_DUMP") is not { } spec || PhysicsBuilderOracle.Load() is not { } pb)
            return;
        var parts = spec.Split('|');
        var positions = parts[0].Split(',').Select(float.Parse).ToArray();
        var indices = parts[1].Split(',').Select(int.Parse).ToArray();
        var error = double.Parse(parts[2]);
        var input = (byte*)NativeMemory.AllocZeroed(0x40);
        var s = (byte*)NativeMemory.AllocZeroed(0x100);
        ((delegate* unmanaged<byte*, byte*>)PhysicsBuilderOracle.At(pb, 0x18060aa30))(input);
        fixed (int* ip = indices)
            ((delegate* unmanaged<byte*, int, int*, void>)PhysicsBuilderOracle.At(pb, 0x18060aa70))(input, indices.Length / 3, ip);
        fixed (float* pp = positions)
            ((delegate* unmanaged<byte*, int, float*, int, void>)PhysicsBuilderOracle.At(pb, 0x18060ab00))(input, positions.Length / 3, pp, 12);
        ((delegate* unmanaged<byte*, byte*, byte*>)PhysicsBuilderOracle.At(pb, 0x18060b810))(s, input);
        ((delegate* unmanaged<byte*, double, int, void>)PhysicsBuilderOracle.At(pb, 0x18060fdf0))(s, error, 0);
        var verts = *(byte**)(s + 8);
        var e0 = *(byte**)(s + 0x38);
        var e1 = *(byte**)(s + 0x40);
        var mine = Source2.Compiler.Physics.MeshSimplifier.Edges(positions, indices, error);
        var i = 0;
        for (var e = e0; e < e1; e += 0x68, i++)
        {
            var lo = (*(byte**)(e + 0x10) - verts) / 0x128;
            var hi = (*(byte**)(e + 0x18) - verts) / 0x128;
            var faces = (*(byte**)(e + 0x38) - *(byte**)(e + 0x30)) / 8;
            var m = i < mine.Count ? mine[i] : default;
            output.WriteLine($"{i}: valve {lo}-{hi} f{faces} cost {*(double*)e:R} heap {*(int*)(e + 8)} | port {m.Lo}-{m.Hi} f{m.Faces} cost {m.Cost:R} heap {m.Heap}");
        }
        for (var v = 0; v < positions.Length / 3; v++)
        {
            var q = (double*)(verts + (v * 0x128) + 0xc8);
            output.WriteLine($"v{v}: valve Q " + string.Join(" ", Enumerable.Range(0, 11).Select(k => q[k].ToString("R"))));
        }
    }
}
