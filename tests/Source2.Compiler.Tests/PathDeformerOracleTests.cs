using System.Numerics;
using System.Runtime.InteropServices;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// CMapDeformerPath's fit (<see cref="PathDeformer"/>) against
/// resourcecompiler.dll's own functions, called in this process on random
/// paths: each piece of the curve fit, then the whole lattice fit (18128dc90).
/// </summary>
public sealed unsafe class PathDeformerOracleTests(ITestOutputHelper output)
{
    private static float F(Random r, double lo, double hi) => (float)(lo + r.NextDouble() * (hi - lo));

    private static string Hex(IEnumerable<Vector3> v) => string.Join(' ', v.SelectMany(p => new[] { p.X, p.Y, p.Z }).Select(f => BitConverter.SingleToUInt32Bits(f).ToString("x8")));

    private static float* Floats(IEnumerable<Vector3> points)
    {
        var list = points.ToArray();
        var data = (float*)NativeMemory.AllocZeroed((nuint)(12 * list.Length + 16));
        for (var i = 0; i < list.Length; i++)
            (data[i * 3], data[i * 3 + 1], data[i * 3 + 2]) = (list[i].X, list[i].Y, list[i].Z);
        return data;
    }

    private static Vector3[] Read(float* data, int count)
        => [.. Enumerable.Range(0, count).Select(i => new Vector3(data[i * 3], data[i * 3 + 1], data[i * 3 + 2]))];

    // A random path's Bezier polygon: each node, it plus its out handle, the next node plus its in handle, the last node.
    private static Vector3[] RandomPolygon(Random r)
    {
        var nodes = 2 + r.Next(5);
        var scale = r.Next(3) switch { 0 => 30.0, 1 => 300.0, _ => 2000.0 };
        var flat = r.Next(2) == 0;
        Vector3 Step() => new(F(r, -scale, scale), F(r, -scale, scale), flat ? 0f : F(r, -scale, scale));
        Vector3 Handle() => r.Next(4) == 0 ? Vector3.Zero : Step() * (float)(r.NextDouble() * 0.5);
        var p = new Vector3(F(r, -3000, 3000), F(r, -3000, 3000), F(r, -500, 500));
        var polygon = new List<Vector3> { p };
        for (var k = 1; k < nodes; k++)
        {
            polygon.Add(p + Handle());
            p += Step();
            polygon.Add(p + Handle());
            polygon.Add(p);
        }
        return [.. polygon];
    }

    /// <summary>The single-cubic pieces: chord parameters, the least-squares fit, the worst distance, the reparameterisation and FitOne.</summary>
    [Fact]
    public void TheCubicFitMatches()
    {
        if (ResourceCompilerOracle.Load() is not { } module)
            return;
        var chords = (delegate* unmanaged<float*, int, float*, float>)ResourceCompilerOracle.At(module, 0x181280a60);
        var leastSquares = (delegate* unmanaged<float*, float*, float*, int, float*, float*, void>)ResourceCompilerOracle.At(module, 0x181280e20);
        var worst = (delegate* unmanaged<float*, float*, float*, int, float>)ResourceCompilerOracle.At(module, 0x181281360);
        var reparameterise = (delegate* unmanaged<float*, float*, float*, int, void>)ResourceCompilerOracle.At(module, 0x181285e60);
        var fitOne = (delegate* unmanaged<float*, float*, int, int, float*, float*, float>)ResourceCompilerOracle.At(module, 0x1812858b0);
        var random = new Random(7);
        var trials = int.TryParse(Environment.GetEnvironmentVariable("PATHDEFORM_TRIALS"), out var many) ? many : 400;
        for (var trial = 0; trial < trials; trial++)
        {
            var polygon = RandomPolygon(random);
            var curve = PathDeformer.Curve.FromPolygon(polygon);
            var (points, _) = PathDeformer.Resample(curve);
            var from = random.Next(points.Length / 2);
            var to = Math.Min(points.Length - 1, from + 2 + random.Next(150));
            var n = to - from + 1;
            var span = points[from..(to + 1)];
            var p = Floats(span);
            var u = (float*)NativeMemory.AllocZeroed((nuint)(4 * n + 16));
            var valveLength = chords(p, n, u);
            var ours = new float[n];
            var length = PathDeformer.Chords(span, ours);
            Assert.True(BitConverter.SingleToUInt32Bits(valveLength) == BitConverter.SingleToUInt32Bits(length), $"trial {trial}: chord length {valveLength} vs {length}");
            for (var i = 0; i < n; i++)
                Assert.True(BitConverter.SingleToUInt32Bits(u[i]) == BitConverter.SingleToUInt32Bits(ours[i]), $"trial {trial}: chord {i} {u[i]} vs {ours[i]}");

            var t0 = Vector3.Normalize(new Vector3(F(random, -1, 1), F(random, -1, 1), F(random, -1, 1)));
            var t1 = Vector3.Normalize(new Vector3(F(random, -1, 1), F(random, -1, 1), F(random, -1, 1)));
            var pt0 = Floats([t0]);
            var pt1 = Floats([t1]);
            var bez = (float*)NativeMemory.AllocZeroed(64);
            leastSquares(bez, p, u, n, pt0, pt1);
            var mine = PathDeformer.LeastSquares(span, ours, t0, t1);
            Assert.True(Hex(Read(bez, 4)) == Hex(mine), $"trial {trial}: least squares\n valve {Hex(Read(bez, 4))}\n ours  {Hex(mine)}");

            var valveWorst = worst(bez, p, u, n);
            var myWorst = PathDeformer.Worst(mine, span, ours);
            Assert.True(BitConverter.SingleToUInt32Bits(valveWorst) == BitConverter.SingleToUInt32Bits(myWorst), $"trial {trial}: worst {valveWorst} vs {myWorst}");

            reparameterise(bez, p, u, n);
            PathDeformer.Reparameterise(mine, span, ours);
            for (var i = 0; i < n; i++)
                Assert.True(BitConverter.SingleToUInt32Bits(u[i]) == BitConverter.SingleToUInt32Bits(ours[i]), $"trial {trial}: reparameterised {i} {u[i]} vs {ours[i]}");

            if (Environment.GetEnvironmentVariable("PATHDEFORM_DEBUG") == "1")
            {
                // Round by round from the chord parameters.
                var uv = (float*)NativeMemory.AllocZeroed((nuint)(4 * n + 16));
                var uo = new float[n];
                chords(p, n, uv);
                PathDeformer.Chords(span, uo);
                var bv = (float*)NativeMemory.AllocZeroed(64);
                Vector3[] bo = [];
                for (var round = 0; round < 10; round++)
                {
                    if (round > 0)
                    {
                        reparameterise(bv, p, uv, n);
                        PathDeformer.Reparameterise(bo, span, uo);
                        for (var i = 0; i < n; i++)
                            if (BitConverter.SingleToUInt32Bits(uv[i]) != BitConverter.SingleToUInt32Bits(uo[i]))
                                output.WriteLine($"trial {trial} round {round}: u {i} {uv[i]:R} vs {uo[i]:R}");
                    }
                    leastSquares(bv, p, uv, n, pt0, pt1);
                    bo = PathDeformer.LeastSquares(span, uo, t0, t1);
                    output.WriteLine($"trial {trial} round {round}: {(Hex(Read(bv, 4)) == Hex(bo) ? "same" : "DIFF")} worst {worst(bv, p, uv, n):R} {PathDeformer.Worst(bo, span, uo):R}");
                }
            }
            var all = Floats(points);
            var valveError = fitOne(bez, all, from, to, pt0, pt1);
            var (fitted, error) = PathDeformer.FitOne(points, from, to, t0, t1);
            Assert.True(Hex(Read(bez, 4)) == Hex(fitted), $"trial {trial}: fit one\n valve {Hex(Read(bez, 4))}\n ours  {Hex(fitted)}");
            Assert.True(BitConverter.SingleToUInt32Bits(valveError) == BitConverter.SingleToUInt32Bits(error), $"trial {trial}: fit error {valveError} vs {error}");
            NativeMemory.Free(p);
            NativeMemory.Free(u);
            NativeMemory.Free(pt0);
            NativeMemory.Free(pt1);
            NativeMemory.Free(bez);
            NativeMemory.Free(all);
        }
        output.WriteLine($"{trials} cubic fits exactly");
    }

    /// <summary>181281aa0: the normals along given tangents from a given up.</summary>
    [Fact]
    public void TheFramesMatch()
    {
        if (ResourceCompilerOracle.Load() is not { } module)
            return;
        var frames = (delegate* unmanaged<float*, int, float*, float*, float*, void>)ResourceCompilerOracle.At(module, 0x181281aa0);
        var random = new Random(11);
        var trials = int.TryParse(Environment.GetEnvironmentVariable("PATHDEFORM_TRIALS"), out var many) ? many : 300;
        for (var trial = 0; trial < trials; trial++)
        {
            var curve = PathDeformer.Curve.FromPolygon(RandomPolygon(random));
            var (points, _) = PathDeformer.Resample(curve);
            var (_, tangents) = Physics.CableCollision.Frames(points);
            var up = new Vector3(F(random, -1, 1), F(random, -1, 1), F(random, -1, 1));
            if (trial % 5 == 0)
                up = tangents[0] * 3f;
            var pu = Floats([up]);
            var pp = Floats(points);
            var pt = Floats(tangents);
            var pn = (float*)NativeMemory.AllocZeroed((nuint)(12 * points.Length + 16));
            frames(pu, points.Length, pp, pt, pn);
            var mine = PathDeformer.Frames(up, tangents);
            Assert.True(Hex(Read(pn, points.Length)) == Hex(mine), $"trial {trial}: frames differ");
            NativeMemory.Free(pu);
            NativeMemory.Free(pp);
            NativeMemory.Free(pt);
            NativeMemory.Free(pn);
        }
        output.WriteLine($"{trials} frame runs exactly");
    }

    /// <summary>181285470: the multi-segment fit, read back as the curve's nodes, shares and total.</summary>
    [Fact]
    public void TheCurveFitMatches()
    {
        if (ResourceCompilerOracle.Load() is not { } module)
            return;
        var fit = (delegate* unmanaged<int*, float*, int, float*, float*, int, float, void>)ResourceCompilerOracle.At(module, 0x181285470);
        var random = new Random(13);
        var trials = int.TryParse(Environment.GetEnvironmentVariable("PATHDEFORM_TRIALS"), out var many) ? many : 300;
        for (var trial = 0; trial < trials; trial++)
        {
            var curve = PathDeformer.Curve.FromPolygon(RandomPolygon(random));
            var (points, _) = PathDeformer.Resample(curve);
            var (_, tangents) = Physics.CableCollision.Frames(points);
            var t0 = tangents[0];
            var t1 = -tangents[^1];
            var segments = 1 + random.Next(16);
            var error = random.Next(4) == 0 ? 0.01f : F(random, 0.05, 3);
            var c = (int*)NativeMemory.AllocZeroed(0x80);
            var pp = Floats(points);
            var pt0 = Floats([t0]);
            var pt1 = Floats([t1]);
            fit(c, pp, points.Length, pt0, pt1, segments, error);
            var mine = PathDeformer.Fit(points, t0, t1, segments, error);
            var count = c[0];
            var nodes = (float*)*(long*)(c + 2);
            var valve = Enumerable.Range(0, count).SelectMany(i => new[]
            {
                new Vector3(nodes[i * 9], nodes[i * 9 + 1], nodes[i * 9 + 2]),
                new Vector3(nodes[i * 9 + 3], nodes[i * 9 + 4], nodes[i * 9 + 5]),
                new Vector3(nodes[i * 9 + 6], nodes[i * 9 + 7], nodes[i * 9 + 8]),
            });
            var ours = mine.Nodes.SelectMany(nd => new[] { nd.Position, nd.In, nd.Out });
            Assert.True(Hex(valve) == Hex(ours), $"trial {trial}: {count} vs {mine.Nodes.Count} nodes\n valve {Hex(valve)}\n ours  {Hex(ours)}");
            var shares = (float*)*(long*)(c + 8);
            for (var i = 0; i < count; i++)
                Assert.True(BitConverter.SingleToUInt32Bits(shares[i]) == BitConverter.SingleToUInt32Bits(mine.Shares[i]), $"trial {trial}: share {i}");
            Assert.True(BitConverter.SingleToUInt32Bits(*(float*)(c + 0x12)) == BitConverter.SingleToUInt32Bits(mine.Total), $"trial {trial}: total");
            NativeMemory.Free(pp);
            NativeMemory.Free(pt0);
            NativeMemory.Free(pt1);
        }
        output.WriteLine($"{trials} curve fits exactly");
    }

    /// <summary>18128dc90: the whole lattice fit, every control point and handle.</summary>
    [Fact]
    public void TheLatticeFitMatches()
    {
        if (ResourceCompilerOracle.Load() is not { } module)
            return;
        var fit = (delegate* unmanaged<byte*, int*, float, float, float, void>)ResourceCompilerOracle.At(module, 0x18128dc90);
        var random = new Random(17);
        var trials = int.TryParse(Environment.GetEnvironmentVariable("PATHDEFORM_TRIALS"), out var many) ? many : 300;
        for (var trial = 0; trial < trials; trial++)
        {
            var polygon = RandomPolygon(random);
            var size = new Vector3(F(random, 1, 2000), F(random, 1, 400), F(random, 1, 400));
            var start = random.Next(3) == 0 ? 0f : F(random, 0, 0.6);
            var end = random.Next(3) == 0 ? 1f : F(random, start + 0.01, 1);
            var maxError = random.Next(3) == 0 ? 0.5f : F(random, 0.02, 4);
            var lattice = (byte*)NativeMemory.AllocZeroed(0x100);
            *(float*)(lattice + 0x0c) = 1f; // CTransform: scale 1, identity rotation
            *(float*)(lattice + 0x1c) = 1f;
            *(Vector3*)(lattice + 0x20) = size;
            *(int*)(lattice + 0x38) = 1;
            var vector = (int*)NativeMemory.AllocZeroed(0x20);
            var pp = Floats(polygon);
            vector[0] = polygon.Length;
            *(long*)(vector + 2) = (long)pp;
            vector[4] = polygon.Length;
            fit(lattice, vector, start, end, maxError);
            var mine = new LatticeDeformer { Size = size, Mode = 1 };
            PathDeformer.FitLattice(mine, polygon, new PathDeformer.Settings(start, end, maxError));
            int segs = *(int*)(lattice + 0x2c), dy = *(int*)(lattice + 0x30), dz = *(int*)(lattice + 0x34), mode = *(int*)(lattice + 0x38);
            Assert.True((segs, dy, dz, mode) == (mine.Segments, mine.DivisionsY, mine.DivisionsZ, mine.Mode), $"trial {trial}: dims {(segs, dy, dz, mode)} vs {(mine.Segments, mine.DivisionsY, mine.DivisionsZ, mine.Mode)}");
            var n = (segs + 1) * (dy + 1) * (dz + 1);
            var points = Read((float*)*(long*)(lattice + 0x48), n);
            var handles = Read((float*)*(long*)(lattice + 0x60), segs * 4 * 2);
            Assert.True(Hex(points) == Hex(mine.Points), $"trial {trial}: points\n valve {Hex(points)}\n ours  {Hex(mine.Points)}");
            Assert.True(Hex(handles) == Hex(mine.Handles), $"trial {trial}: handles\n valve {Hex(handles)}\n ours  {Hex(mine.Handles)}");
            NativeMemory.Free(pp);
            NativeMemory.Free(vector);
        }
        output.WriteLine($"{trials} lattice fits exactly");
    }
}
