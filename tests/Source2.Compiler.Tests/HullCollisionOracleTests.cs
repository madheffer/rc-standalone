using System.Numerics;
using System.Runtime.InteropServices;
using Source2.Compiler.Physics;
using Source2.Compiler.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Rubikon's hull-hull narrowphase, ported, against vphysics2's own
/// FUN_1802f15e0 and the functions under it, on random hulls marshalled into
/// RnHull_t: manifolds, caches and return values must match byte for byte.
/// </summary>
public unsafe class HullCollisionOracleTests(ITestOutputHelper output)
{
    private const int Hulls = 240;

    /// <summary>{RnHull*, float scale}, as the shape hands it to the narrowphase.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 0x10)]
    internal struct NativeRef
    {
        public byte* Hull;
        public float Scale;
    }

    /// <summary>An RnHull laid out as vphysics2 keeps it (0xf8 bytes and its arrays).</summary>
    internal sealed class NativeHull : IDisposable
    {
        public readonly RnHull Hull;
        public readonly byte* Ptr;

        public NativeHull(RnHull hull)
        {
            Hull = hull;
            var nv = hull.VertexPositions.Length;
            var np = hull.Planes.Length;
            var nve = hull.Vertices.Length;
            var ne = hull.Edges.Length;
            var nf = hull.Faces.Length;
            var verts = 0x100;
            var planes = Align(verts + 12 * nv);
            var vertexEdges = Align(planes + 16 * np);
            var edges = Align(vertexEdges + nve);
            var faces = Align(edges + 4 * ne);
            var size = Align(faces + nf);
            Ptr = (byte*)NativeMemory.AlignedAlloc((nuint)size, 16);
            NativeMemory.Clear(Ptr, (nuint)size);
            *(Vector3*)Ptr = hull.Centroid;
            *(float*)(Ptr + 0x0c) = hull.MaxAngularRadius;
            *(float*)(Ptr + 0x10) = hull.MinCentroidRadius;
            *(Vector3*)(Ptr + 0x14) = hull.BoundsMin;
            *(Vector3*)(Ptr + 0x20) = hull.BoundsMax;
            Vector(0x70, verts, nv);
            for (var i = 0; i < nv; i++)
                *(Vector3*)(Ptr + verts + 12 * i) = hull.VertexPositions[i];
            Vector(0x88, planes, np);
            for (var i = 0; i < np; i++)
            {
                *(Vector3*)(Ptr + planes + 16 * i) = hull.Planes[i].Normal;
                *(float*)(Ptr + planes + 16 * i + 12) = hull.Planes[i].Offset;
            }
            *(uint*)(Ptr + 0xa0) = hull.Flags;
            Vector(0xb0, vertexEdges, nve);
            for (var i = 0; i < nve; i++)
                Ptr[vertexEdges + i] = hull.Vertices[i];
            Vector(0xc8, edges, ne);
            for (var i = 0; i < ne; i++)
            {
                var e = hull.Edges[i];
                Ptr[edges + 4 * i] = e.Next;
                Ptr[edges + 4 * i + 1] = e.Twin;
                Ptr[edges + 4 * i + 2] = e.Origin;
                Ptr[edges + 4 * i + 3] = e.Face;
            }
            Vector(0xe0, faces, nf);
            for (var i = 0; i < nf; i++)
                Ptr[faces + i] = hull.Faces[i];
        }

        private void Vector(int at, int data, int count)
        {
            *(int*)(Ptr + at) = count;
            *(int*)(Ptr + at + 4) = count;
            *(byte**)(Ptr + at + 8) = Ptr + data;
        }

        private static int Align(int x) => (x + 15) & ~15;

        public void Dispose() => NativeMemory.AlignedFree(Ptr);
    }

    /// <summary>Everything one call needs, 16-aligned, in one block.</summary>
    private sealed class Scratch : IDisposable
    {
        public readonly byte* Base = (byte*)NativeMemory.AlignedAlloc(0x4000, 16);
        public RnTransform* XfA => (RnTransform*)Base;
        public RnTransform* XfB => (RnTransform*)(Base + 0x40);
        public NativeRef* RefA => (NativeRef*)(Base + 0x80);
        public NativeRef* RefB => (NativeRef*)(Base + 0x90);
        public SatCache* Cache => (SatCache*)(Base + 0xa0);
        public SatQuery* Query => (SatQuery*)(Base + 0xb0);
        public ClipPlane* Plane => (ClipPlane*)(Base + 0xc0);
        public ClipPlane* Side => (ClipPlane*)(Base + 0xd0);
        public float* Floats => (float*)(Base + 0xe0);
        public CachedManifold* Old => (CachedManifold*)(Base + 0x100);
        public CachedManifold* Out => (CachedManifold*)(Base + 0x200);
        public int* PolyA => (int*)(Base + 0x300);
        public int* PolyB => (int*)(Base + 0x300 + 0x1800);

        public Scratch() => NativeMemory.Clear(Base, 0x4000);

        public void Dispose() => NativeMemory.AlignedFree(Base);
    }

    // ---------------------------------------------------------------- fixtures

    private static NativeHull[]? _pool;

    internal static NativeHull[] Pool()
    {
        if (_pool != null)
            return _pool;
        var random = new Random(1234);
        var list = new List<NativeHull>();
        while (list.Count < Hulls)
        {
            var hull = RandomHull(random, list.Count);
            if (hull != null && hull.Faces.Length > 1 && hull.Edges.Length > 0)
                list.Add(new NativeHull(hull));
        }
        return _pool = list.ToArray();
    }

    private static RnHull? RandomHull(Random r, int k)
    {
        float F(double lo, double hi) => (float)(lo + r.NextDouble() * (hi - lo));
        switch (k % 6)
        {
            case 0:
                return RnHullBuilder.CreateBox(new Vector3(F(0.5, 40), F(0.5, 40), F(0.5, 40)),
                                               new Vector3(F(-2, 2), F(-2, 2), F(-2, 2)));
            case 1:
                return RnHullBuilder.CreateBox(new Vector3(F(4, 32), F(4, 32), F(1, 16)), default);
        }
        var n = 4 + r.Next(57);
        var size = new Vector3(F(0.5, 40), F(0.5, 40), F(0.5, 40));
        var centre = new Vector3(F(-3, 3), F(-3, 3), F(-3, 3));
        var points = new Vector3[n];
        for (var i = 0; i < n; i++)
        {
            Vector3 p;
            if (k % 6 == 2)
            {
                // On a sphere-ish surface.
                p = Vector3.Normalize(new Vector3(Gauss(r), Gauss(r), Gauss(r)));
            }
            else
            {
                p = new Vector3(F(-1, 1), F(-1, 1), F(-1, 1));
            }
            points[i] = centre + p * size;
        }
        try
        {
            return RnHullBuilder.Create(points, RnHullBuilder.Options.Compile, out _);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static float Gauss(Random r)
        => (float)(Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble()));

    private static Quat RandomRotation(Random r)
        => RnMath.Normalize(new Quat(Gauss(r), Gauss(r), Gauss(r), Gauss(r)));

    private static Quat SmallRotation(Random r, float angle)
    {
        var axis = Vector3.Normalize(new Vector3(Gauss(r), Gauss(r), Gauss(r)));
        var half = (float)(r.NextDouble() * angle * 0.5);
        var s = MathF.Sin(half);
        return RnMath.Normalize(new Quat(axis.X * s, axis.Y * s, axis.Z * s, MathF.Cos(half)));
    }

    private static RnTransform Frame(Quat q, Vec3 t) => new() { R = RnMath.Matrix(q), T = t };

    private static float Scale(Random r) => r.Next(3) == 0 ? (float)(0.25 + r.NextDouble() * 2) : 1f;

    /// <summary>The SAT's separation, from the port, to aim placements.</summary>
    private static float Separation(in RnTransform xfA, HullRef a, in RnTransform xfB, HullRef b)
    {
        var s = HullCollision.FaceQuery(xfA, a, xfB, b).Separation;
        s = MathF.Max(s, HullCollision.FaceQuery(xfB, b, xfA, a).Separation);
        return MathF.Max(s, HullCollision.EdgeQuery(xfA, a, xfB, b).Separation);
    }

    private static readonly float[] Targets = [-8f, -1f, -0.3f, -0.05f, 0f, 0.01f, 0.06f, 0.1f, 0.124f, 0.13f, 0.5f, 3f];

    /// <summary>A random pair placed at about a chosen separation.</summary>
    private static (RnTransform A, HullRef HA, int IA, RnTransform B, HullRef HB, int IB) RandomPair(Random r, NativeHull[] pool)
    {
        var ia = r.Next(pool.Length);
        var ib = r.Next(pool.Length);
        var a = new HullRef(pool[ia].Hull, Scale(r));
        var b = new HullRef(pool[ib].Hull, Scale(r));
        var xfA = Frame(RandomRotation(r), new Vec3((float)(r.NextDouble() * 200 - 100), (float)(r.NextDouble() * 200 - 100),
                                                    (float)(r.NextDouble() * 200 - 100)));
        var qb = RandomRotation(r);
        var u = Vector3.Normalize(new Vector3(Gauss(r), Gauss(r), Gauss(r)));
        var reach = a.Hull.MaxAngularRadius * a.Scale + b.Hull.MaxAngularRadius * b.Scale;
        var distance = reach * (float)(0.2 + r.NextDouble());
        var target = Targets[r.Next(Targets.Length)];
        var ca = HullCollision.ToWorld(xfA, a.Scale * a.Hull.Centroid.X, a.Scale * a.Hull.Centroid.Y, a.Scale * a.Hull.Centroid.Z);
        var xfB = default(RnTransform);
        for (var pass = 0; pass < 4; pass++)
        {
            xfB = Frame(qb, new Vec3(ca.X + u.X * distance, ca.Y + u.Y * distance, ca.Z + u.Z * distance));
            if (r.Next(5) == 0)
                break;
            distance += target - Separation(xfA, a, xfB, b);
        }
        return (xfA, a, ia, xfB, b, ib);
    }

    // ---------------------------------------------------------------- oracle calls

    private static nint Fn(nint module, ulong va) => Vphysics2Oracle.At(module, va);

    private static void Setup(Scratch s, in RnTransform xfA, NativeHull a, float sa, in RnTransform xfB, NativeHull b, float sb)
    {
        *s.XfA = xfA;
        *s.XfB = xfB;
        *s.RefA = new NativeRef { Hull = a.Ptr, Scale = sa };
        *s.RefB = new NativeRef { Hull = b.Ptr, Scale = sb };
    }

    private static string Describe(in RnTransform xfA, HullRef a, in RnTransform xfB, HullRef b)
        => $"A: R=({xfA.R.M0:R},{xfA.R.M1:R},{xfA.R.M2:R},{xfA.R.M3:R},{xfA.R.M4:R},{xfA.R.M5:R},{xfA.R.M6:R},{xfA.R.M7:R},{xfA.R.M8:R}) T={xfA.T} s={a.Scale:R} v={a.Hull.VertexPositions.Length} f={a.Hull.Faces.Length}\n"
         + $"B: R=({xfB.R.M0:R},{xfB.R.M1:R},{xfB.R.M2:R},{xfB.R.M3:R},{xfB.R.M4:R},{xfB.R.M5:R},{xfB.R.M6:R},{xfB.R.M7:R},{xfB.R.M8:R}) T={xfB.T} s={b.Scale:R} v={b.Hull.VertexPositions.Length} f={b.Hull.Faces.Length}";

    /// <summary>
    /// Compares two manifolds: the header, then each point up to the count
    /// (bytes 0x00..0x25; 0x26..0x27 are never written).
    /// </summary>
    private static int ManifoldDifference(in CachedManifold x, in CachedManifold y)
    {
        fixed (CachedManifold* px = &x, py = &y)
        {
            var a = new ReadOnlySpan<byte>(px, 0xe0);
            var b = new ReadOnlySpan<byte>(py, 0xe0);
            var header = First(a[..0x40], b[..0x40]);
            if (header >= 0)
                return header;
            var count = Math.Min(x.PointCount, 4);
            for (var k = 0; k < count; k++)
            {
                var at = 0x40 + 0x28 * k;
                var d = First(a.Slice(at, 0x24), b.Slice(at, 0x24));
                if (d >= 0)
                    return at + d;
                if (a[at + 0x24] != b[at + 0x24])
                    return at + 0x24;
                if (a[at + 0x25] != b[at + 0x25])
                    return at + 0x25;
            }
            return -1;
        }
    }

    private static string Dump(in CachedManifold m)
    {
        var s = $"count={m.PointCount} c={m.Centre} n={m.Normal} tw={m.TwistImpulse:R} t1={m.T1} i1={m.Impulse1:R} t2={m.T2} i2={m.Impulse2:R}";
        for (var k = 0; k < Math.Min(m.PointCount, 4); k++)
        {
            var p = m.Points[k];
            s += $"\n  p{k}: a={p.LocalA} b={p.LocalB} imp={p.Impulse:R} f=0x{p.Feature:x8} tri={p.SubShape} r=0x{p.Reserved:x8}";
        }
        return s;
    }

    private static string Dump(in SatCache c) => $"{{{c.Type}, {c.Index1}, {c.Index2}, {c.Separation:R}}}";

    // ---------------------------------------------------------------- sub-function tests

    [Fact]
    public void SupportMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var support = (delegate* unmanaged<byte*, float*, int>)Fn(module, 0x18028b1c0);
        var pool = Pool();
        var random = new Random(11);
        using var s = new Scratch();
        for (var i = 0; i < 20000; i++)
        {
            var h = pool[random.Next(pool.Length)];
            var d = i % 7 == 0
                ? new Vector3(random.Next(3) - 1, random.Next(3) - 1, random.Next(3) - 1)
                : new Vector3(Gauss(random), Gauss(random), Gauss(random));
            s.Floats[0] = d.X;
            s.Floats[1] = d.Y;
            s.Floats[2] = d.Z;
            Assert.Equal(support(h.Ptr, s.Floats), HullCollision.Support(h.Hull, d.X, d.Y, d.Z));
        }
    }

    [Fact]
    public void TheQueriesMatch()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var face = (delegate* unmanaged<SatQuery*, RnTransform*, NativeRef*, RnTransform*, NativeRef*, void>)Fn(module, 0x18028cc00);
        var edge = (delegate* unmanaged<SatQuery*, RnTransform*, NativeRef*, RnTransform*, NativeRef*, void>)Fn(module, 0x18028bbd0);
        var pool = Pool();
        var random = new Random(12);
        using var s = new Scratch();
        for (var i = 0; i < 3000; i++)
        {
            var (xfA, a, ia, xfB, b, ib) = RandomPair(random, pool);
            Setup(s, xfA, pool[ia], a.Scale, xfB, pool[ib], b.Scale);
            face(s.Query, s.XfA, s.RefA, s.XfB, s.RefB);
            AssertQuery(*s.Query, HullCollision.FaceQuery(xfA, a, xfB, b), i, "face A", xfA, a, xfB, b);
            face(s.Query, s.XfB, s.RefB, s.XfA, s.RefA);
            AssertQuery(*s.Query, HullCollision.FaceQuery(xfB, b, xfA, a), i, "face B", xfA, a, xfB, b);
            edge(s.Query, s.XfA, s.RefA, s.XfB, s.RefB);
            AssertQuery(*s.Query, HullCollision.EdgeQuery(xfA, a, xfB, b), i, "edge", xfA, a, xfB, b);
        }
    }

    private static void AssertQuery(SatQuery valve, SatQuery ours, int trial, string what,
                                    in RnTransform xfA, HullRef a, in RnTransform xfB, HullRef b)
    {
        if (Bits(valve.Separation) != Bits(ours.Separation) || valve.Index1 != ours.Index1 || valve.Index2 != ours.Index2)
            Assert.Fail($"trial {trial} {what}: valve {{{valve.Separation:R}, {valve.Index1}, {valve.Index2}}}"
                        + $" ours {{{ours.Separation:R}, {ours.Index1}, {ours.Index2}}}\n{Describe(xfA, a, xfB, b)}");
    }

    [Fact]
    public void TheIncidentFaceAndPolygonMatch()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var incident = (delegate* unmanaged<RnTransform*, NativeRef*, float*, int, byte>)Fn(module, 0x1803372b0);
        var polygon = (delegate* unmanaged<int*, RnTransform*, NativeRef*, int, ClipPlane*, void>)Fn(module, 0x180336c80);
        var pool = Pool();
        var random = new Random(13);
        using var s = new Scratch();
        var ours = new ClipPoint[512];
        for (var i = 0; i < 20000; i++)
        {
            var h = pool[random.Next(pool.Length)];
            var hr = new HullRef(h.Hull, Scale(random));
            var xf = Frame(RandomRotation(random), new Vec3(Gauss(random) * 30, Gauss(random) * 30, Gauss(random) * 30));
            *s.XfA = xf;
            *s.RefA = new NativeRef { Hull = h.Ptr, Scale = hr.Scale };
            var n = Vector3.Normalize(new Vector3(Gauss(random), Gauss(random), Gauss(random)));
            s.Floats[0] = n.X;
            s.Floats[1] = n.Y;
            s.Floats[2] = n.Z;
            var vertex = random.Next(h.Hull.VertexPositions.Length);
            var f = (int)incident(s.XfA, s.RefA, s.Floats, vertex);
            Assert.Equal(f, HullCollision.IncidentFace(xf, hr, n.X, n.Y, n.Z, vertex));

            *s.Plane = new ClipPlane(n.X, n.Y, n.Z, Gauss(random) * 20);
            s.PolyA[0] = 0;
            polygon(s.PolyA, s.XfA, s.RefA, f, s.Plane);
            var count = HullCollision.IncidentPolygon(xf, hr, f, *s.Plane, ours);
            Assert.Equal(s.PolyA[0], count);
            AssertPolygon(s.PolyA, ours, count, i, "polygon");
        }
    }

    private static void AssertPolygon(int* valve, ClipPoint[] ours, int count, int trial, string what)
    {
        fixed (ClipPoint* p = ours)
        {
            var d = First(new ReadOnlySpan<byte>(valve + 1, 20 * count), new ReadOnlySpan<byte>(p, 20 * count));
            if (d >= 0)
            {
                var k = d / 20;
                var v = ((ClipPoint*)(valve + 1))[k];
                Assert.Fail($"trial {trial} {what}: point {k} differs at +0x{d % 20:x}: valve ({v.X:R},{v.Y:R},{v.Z:R},{v.W:R},0x{v.Feature:x8})"
                            + $" ours ({p[k].X:R},{p[k].Y:R},{p[k].Z:R},{p[k].W:R},0x{p[k].Feature:x8})");
            }
        }
    }

    private static ClipPoint RandomPoint(Random r, float spread)
        => new()
        {
            X = Gauss(r) * spread, Y = Gauss(r) * spread, Z = Gauss(r) * spread, W = Gauss(r) * 0.2f,
            Feature = (uint)r.Next() | (uint)r.Next(2) << 31,
        };

    [Fact]
    public void ClippingMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var clip = (delegate* unmanaged<int*, int*, ClipPlane*, byte, ClipPlane*, void>)Fn(module, 0x1803bb310);
        var random = new Random(14);
        using var s = new Scratch();
        var input = new ClipPoint[64];
        var ours = new ClipPoint[512];
        for (var i = 0; i < 30000; i++)
        {
            var count = random.Next(12);
            for (var k = 0; k < count; k++)
                input[k] = RandomPoint(random, 5);
            // Now and then a point exactly on the side plane.
            var n = Vector3.Normalize(new Vector3(Gauss(random), Gauss(random), Gauss(random)));
            var side = new ClipPlane(n.X, n.Y, n.Z, Gauss(random) * 3);
            if (count > 0 && random.Next(4) == 0)
                side.D = ((input[0].Y * side.Y + input[0].Z * side.Z) + input[0].X * side.X);
            var m = Vector3.Normalize(new Vector3(Gauss(random), Gauss(random), Gauss(random)));
            var plane = new ClipPlane(m.X, m.Y, m.Z, Gauss(random) * 3);
            var edge = random.Next(256);
            s.PolyA[0] = count;
            fixed (ClipPoint* p = input)
                Buffer.MemoryCopy(p, s.PolyA + 1, 20 * count, 20 * count);
            *s.Side = side;
            *s.Plane = plane;
            s.PolyB[0] = -1;
            clip(s.PolyB, s.PolyA, s.Side, (byte)edge, s.Plane);
            var got = HullCollision.Clip(ours, input.AsSpan(0, count), side, edge, plane);
            Assert.Equal(s.PolyB[0], got);
            AssertPolygon(s.PolyB, ours, got, i, "clip");
        }
    }

    [Fact]
    public void ReductionMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var reduce = (delegate* unmanaged<int*, ClipPlane*, float, byte, void>)Fn(module, 0x1803bb850);
        var random = new Random(15);
        using var s = new Scratch();
        var ours = new ClipPoint[512];
        for (var i = 0; i < 40000; i++)
        {
            var count = random.Next(1, 14);
            var n = Vector3.Normalize(i % 5 == 0
                ? new Vector3(random.Next(3) - 1, random.Next(3) - 1, 1)
                : new Vector3(Gauss(random), Gauss(random), Gauss(random)));
            var plane = new ClipPlane(n.X, n.Y, n.Z, Gauss(random) * 10);
            var spread = i % 3 == 0 ? 0.03f : 4f;
            for (var k = 0; k < count; k++)
            {
                var p = RandomPoint(random, spread);
                if (k > 0 && random.Next(6) == 0)
                    p = ours[random.Next(k)] with { W = p.W };
                ours[k] = p;
            }
            s.PolyA[0] = count;
            fixed (ClipPoint* p = ours)
                Buffer.MemoryCopy(p, s.PolyA + 1, 20 * count, 20 * count);
            *s.Plane = plane;
            reduce(s.PolyA, s.Plane, 0.125f, 0);
            var got = HullCollision.Reduce(ours, count, plane, 0.125f);
            Assert.True(s.PolyA[0] == got, $"trial {i}: count valve {s.PolyA[0]} ours {got}");
            AssertPolygon(s.PolyA, ours, got, i, "reduce");
        }
    }

    [Fact]
    public void TheSmallHelpersMatch()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var tangents = (delegate* unmanaged<Vec3*, Vec3*, Vec3*, void>)Fn(module, 0x180321cc0);
        var closest = (delegate* unmanaged<float*, Vec3*, Vec3*, Vec3*, Vec3*, float*>)Fn(module, 0x180321270);
        var friction = (delegate* unmanaged<CachedManifold*, CachedManifold*, void>)Fn(module, 0x1802f61a0);
        var minSep = (delegate* unmanaged<RnTransform*, RnTransform*, CachedManifold*, float>)Fn(module, 0x1802f59c0);
        var flip = (delegate* unmanaged<uint, uint>)Fn(module, 0x1803bc9d0);
        var random = new Random(16);
        using var s = new Scratch();
        var v = (Vec3*)s.Floats;
        for (var i = 0; i < 20000; i++)
        {
            var n = Vector3.Normalize(new Vector3(Gauss(random), Gauss(random), Gauss(random)));
            if (i % 9 == 0)
                n = Vector3.Normalize(new Vector3(random.Next(3) - 1, random.Next(3) - 1, random.Next(3) - 1 + 0.001f));
            v[0] = new Vec3(n.X, n.Y, n.Z);
            tangents(&v[0], &v[1], &v[2]);
            var (t1, t2) = HullCollision.Tangents(v[0]);
            Assert.True(Same(v[1], t1) && Same(v[2], t2), $"trial {i}: tangents of {v[0]}: valve {v[1]} {v[2]} ours {t1} {t2}");

            v[0] = RandomVec(random, 20);
            v[1] = RandomVec(random, 5);
            v[2] = RandomVec(random, 20);
            v[3] = i % 4 == 0 ? new Vec3(v[1].X * 2, v[1].Y * 2, v[1].Z * 2) : RandomVec(random, 5);
            var res = closest(s.Floats + 16, &v[0], &v[1], &v[2], &v[3]);
            var (ca, sa, cb, sb) = HullCollision.ClosestPoints(v[0], v[1], v[2], v[3]);
            Assert.True(Same(*(Vec3*)res, ca) && Bits(res[3]) == Bits(sa) && Same(*(Vec3*)(res + 4), cb) && Bits(res[7]) == Bits(sb),
                        $"trial {i}: closest points differ");

            *s.Old = RandomManifold(random);
            *s.Out = RandomManifold(random);
            var ours = *s.Out;
            friction(s.Out, s.Old);
            HullCollision.TransferFriction(ref ours, new ReadOnlySpan<CachedManifold>(s.Old, 1));
            Assert.True(ManifoldDifference(*s.Out, ours) < 0, $"trial {i}: friction transfer differs");
            ours = *s.Out;
            friction(s.Out, null);
            HullCollision.TransferFriction(ref ours, default);
            Assert.True(ManifoldDifference(*s.Out, ours) < 0, $"trial {i}: friction reset differs");

            *s.XfA = Frame(RandomRotation(random), RandomVec(random, 50));
            *s.XfB = Frame(RandomRotation(random), RandomVec(random, 50));
            Assert.Equal(Bits(minSep(s.XfA, s.XfB, s.Out)), Bits(HullCollision.MinSeparation(*s.XfA, *s.XfB, *s.Out)));

            var f = (uint)random.Next() | (uint)random.Next(2) << 31;
            f = (f & 0xfefeffff) | (uint)random.Next(2) | (uint)random.Next(2) << 16;
            Assert.Equal(flip(f), HullCollision.FlipFeature(f));
        }
    }

    private static Vec3 RandomVec(Random r, float s) => new(Gauss(r) * s, Gauss(r) * s, Gauss(r) * s);

    private static bool Same(Vec3 a, Vec3 b) => Bits(a.X) == Bits(b.X) && Bits(a.Y) == Bits(b.Y) && Bits(a.Z) == Bits(b.Z);

    private static CachedManifold RandomManifold(Random r)
    {
        var m = new CachedManifold
        {
            PointCount = r.Next(5),
            Centre = RandomVec(r, 30),
            Normal = Unit(r),
            TwistImpulse = Gauss(r) * 3,
            T1 = Unit(r),
            Impulse1 = Gauss(r) * 3,
            T2 = Unit(r),
            Impulse2 = Gauss(r) * 3,
        };
        for (var k = 0; k < 4; k++)
            m.Points[k] = new CachedPoint
            {
                LocalA = RandomVec(r, 10), LocalB = RandomVec(r, 10), Impulse = Math.Abs(Gauss(r)) * 5,
                Feature = r.Next(), SubShape = -1, Reserved = r.Next(),
            };
        return m;
    }

    private static Vec3 Unit(Random r)
    {
        var n = Vector3.Normalize(new Vector3(Gauss(r), Gauss(r), Gauss(r)));
        return new Vec3(n.X, n.Y, n.Z);
    }

    // ---------------------------------------------------------------- whole-pair tests

    private delegate* unmanaged<CachedManifold*, CachedManifold*, RnTransform*, NativeRef*, RnTransform*, NativeRef*, SatCache*, int, byte> _collide;
    private delegate* unmanaged<CachedManifold*, CachedManifold*, RnTransform*, NativeRef*, RnTransform*, NativeRef*, SatQuery*, int, SatCache*, int, byte> _face;
    private delegate* unmanaged<CachedManifold*, CachedManifold*, RnTransform*, NativeRef*, RnTransform*, NativeRef*, SatQuery*, SatCache*, int, byte> _edge;

    private bool Bind()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return false;
        _collide = (delegate* unmanaged<CachedManifold*, CachedManifold*, RnTransform*, NativeRef*, RnTransform*, NativeRef*, SatCache*, int, byte>)Fn(module, 0x1802f15e0);
        _face = (delegate* unmanaged<CachedManifold*, CachedManifold*, RnTransform*, NativeRef*, RnTransform*, NativeRef*, SatQuery*, int, SatCache*, int, byte>)Fn(module, 0x1802ee580);
        _edge = (delegate* unmanaged<CachedManifold*, CachedManifold*, RnTransform*, NativeRef*, RnTransform*, NativeRef*, SatQuery*, SatCache*, int, byte>)Fn(module, 0x1802ed320);
        return true;
    }

    /// <summary>
    /// One call on both sides from the same state; <paramref name="old"/> null
    /// for none. Returns Valve's result, manifold and cache after checking ours.
    /// </summary>
    private (bool Hit, CachedManifold Out, SatCache Cache) Both(Scratch s, CachedManifold? old, CachedManifold start, SatCache cache,
                                                               NativeHull[] pool, int ia, int ib, int trial, string what, int triangle = -1)
    {
        var xfA = *s.XfA;
        var xfB = *s.XfB;
        var a = new HullRef(pool[ia].Hull, s.RefA->Scale);
        var b = new HullRef(pool[ib].Hull, s.RefB->Scale);
        if (old is { } o)
            *s.Old = o;
        *s.Out = start;
        *s.Cache = cache;
        var hit = _collide(old is null ? null : s.Old, s.Out, s.XfA, s.RefA, s.XfB, s.RefB, s.Cache, triangle) != 0;

        var ours = start;
        var ourCache = cache;
        var oldSpan = old is { } oo ? new ReadOnlySpan<CachedManifold>(in oo) : default;
        var ourHit = HullCollision.Collide(oldSpan, ref ours, xfA, a, xfB, b, ref ourCache, triangle);

        var d = hit ? ManifoldDifference(*s.Out, ours) : First(new ReadOnlySpan<byte>(s.Out, 0xe0), new ReadOnlySpan<byte>(&ours, 0xe0));
        var cacheSame = new ReadOnlySpan<byte>(s.Cache, 16).SequenceEqual(new ReadOnlySpan<byte>(&ourCache, 16));
        if (hit != ourHit || d >= 0 || !cacheSame)
            Assert.Fail($"{what} trial {trial}: hit valve {hit} ours {ourHit}; manifold differs at +0x{d:x}; cache valve {Dump(*s.Cache)} ours {Dump(ourCache)}"
                        + $"\nstart cache {Dump(cache)} old {(old is null ? "none" : Dump(old.Value))}"
                        + $"\nvalve {Dump(*s.Out)}\nours  {Dump(ours)}\n{Describe(xfA, a, xfB, b)}");
        return (hit, *s.Out, *s.Cache);
    }

    [Fact]
    public void RandomPairsMatch()
    {
        if (!Bind())
            return;
        var pool = Pool();
        var random = new Random(21);
        using var s = new Scratch();
        var paths = new Paths();
        var replaced = 0;
        for (var i = 0; i < 8000; i++)
        {
            var (xfA, a, ia, xfB, b, ib) = RandomPair(random, pool);
            Setup(s, xfA, pool[ia], a.Scale, xfB, pool[ib], b.Scale);
            CachedManifold? old = random.Next(3) == 0 ? RandomManifold(random) : null;
            var (hit, m, c) = Both(s, old, default, default, pool, ia, ib, i, "pair", i % 5 == 0 ? i : -1);
            paths.Count(hit, m, c);
            if (hit && c.Type == 3 && FaceContactHolds(xfA, a, xfB, b))
                replaced++;
        }
        output.WriteLine($"random pairs: {paths}; edge replaced a face manifold {replaced}");
    }

    /// <summary>Whether the SAT's chosen face contact succeeds (so a type 3 result replaced it).</summary>
    private static bool FaceContactHolds(in RnTransform xfA, HullRef a, in RnTransform xfB, HullRef b)
    {
        var fa = HullCollision.FaceQuery(xfA, a, xfB, b);
        var fb = HullCollision.FaceQuery(xfB, b, xfA, a);
        var m = default(CachedManifold);
        var c = default(SatCache);
        return fb.Separation - 0.125f > (fa.Separation - 0.125f) * 0.98f + 0.015625f
            ? HullCollision.FaceContact(default, ref m, xfB, b, xfA, a, fb, true, ref c, -1)
            : HullCollision.FaceContact(default, ref m, xfA, a, xfB, b, fa, false, ref c, -1);
    }

    /// <summary>What the calls ended as, to show which paths the cases reach.</summary>
    private sealed class Paths
    {
        private readonly int[] _points = new int[5];
        private readonly int[] _types = new int[4];
        private int _separated;

        public void Count(bool hit, in CachedManifold m, in SatCache c)
        {
            if (hit)
                _points[Math.Min(m.PointCount, 4)]++;
            else
                _separated++;
            _types[Math.Clamp(c.Type, 0, 3)]++;
        }

        public override string ToString()
            => $"separated {_separated}; contacts by point count 0..4: {string.Join(' ', _points)};"
             + $" cache types 0..3: {string.Join(' ', _types)}";
    }

    [Fact]
    public void BoxesRestingOnBoxesMatch()
    {
        if (!Bind())
            return;
        var random = new Random(22);
        using var s = new Scratch();
        var paths = new Paths();
        var boxes = new List<NativeHull>();
        try
        {
            for (var i = 0; i < 4000; i++)
            {
                float F(double lo, double hi) => (float)(lo + random.NextDouble() * (hi - lo));
                var ea = new Vector3(F(2, 64), F(2, 64), F(1, 32));
                var eb = new Vector3(F(1, 32), F(1, 32), F(1, 32));
                var ha = new NativeHull(RnHullBuilder.CreateBox(ea, default));
                var hb = new NativeHull(RnHullBuilder.CreateBox(eb, default));
                boxes.Add(ha);
                boxes.Add(hb);
                var pool = new[] { ha, hb };
                var flat = i % 3 == 0;
                var qa = flat ? Quat.Identity : SmallRotation(random, 0.05f);
                var yaw = MathF.PI * F(-1, 1) * (i % 2);
                var qb = RnMath.Mul(qa, RnMath.Mul(new Quat(0, 0, MathF.Sin(yaw / 2), MathF.Cos(yaw / 2)),
                                                   flat && i % 4 == 0 ? Quat.Identity : SmallRotation(random, 0.08f)));
                var xfA = Frame(qa, new Vec3(F(-500, 500), F(-500, 500), F(-100, 100)));
                var gap = new[] { -0.2f, -0.03f, -0.001f, 0f, 0.01f, 0.05f, 0.09375f, 0.12f, 0.2f }[random.Next(9)];
                var local = new Vector3(F(-ea.X, ea.X), F(-ea.Y, ea.Y), ea.Z + eb.Z + gap);
                var t = HullCollision.ToWorld(xfA, local.X, local.Y, local.Z);
                var xfB = Frame(qb, t);
                Setup(s, xfA, ha, 1f, xfB, hb, 1f);
                var (hit, m, c) = Both(s, null, default, default, pool, 0, 1, i, "box");
                paths.Count(hit, m, c);

                // The next step, warm: same boxes nudged.
                var old = hit ? m : (CachedManifold?)null;
                var cache = *s.Cache;
                xfB.T = new Vec3(xfB.T.X + F(-0.01, 0.01), xfB.T.Y + F(-0.01, 0.01), xfB.T.Z + F(-0.01, 0.01));
                xfB.R = RnMath.Matrix(RnMath.Mul(qb, SmallRotation(random, 0.002f)));
                Setup(s, xfA, ha, 1f, xfB, hb, 1f);
                Both(s, old, hit ? m : default, cache, pool, 0, 1, i, "box step");
            }
        }
        finally
        {
            foreach (var h in boxes)
                h.Dispose();
        }
        output.WriteLine($"boxes: {paths}");
    }

    [Fact]
    public void SteppedPairsWithCacheMatch()
    {
        if (!Bind())
            return;
        var pool = Pool();
        var random = new Random(23);
        using var s = new Scratch();
        var calls = 0;
        var outcomes = new int[3];
        var paths = new Paths();
        for (var i = 0; i < 700; i++)
        {
            var (xfA, a, ia, xfB, b, ib) = RandomPair(random, pool);
            var cache = default(SatCache);
            CachedManifold? old = null;
            var start = default(CachedManifold);
            var dq = SmallRotation(random, 0.01f);
            var dt = RandomVec(random, 0.01f);
            var qb = RandomRotation(random);
            xfB.R = RnMath.Matrix(qb);
            for (var step = 0; step < 16; step++)
            {
                Setup(s, xfA, pool[ia], a.Scale, xfB, pool[ib], b.Scale);
                var probe = start;
                var probeCache = cache;
                var oldSpan = old is { } oo ? new ReadOnlySpan<CachedManifold>(in oo) : default;
                outcomes[HullCollision.Revalidate(oldSpan, ref probe, xfA, a, xfB, b, ref probeCache, -1)]++;
                var (hit, m, c) = Both(s, old, start, cache, pool, ia, ib, i * 100 + step, "step");
                calls++;
                paths.Count(hit, m, c);
                cache = c;
                old = hit ? m : null;
                start = hit ? m : default;
                qb = RnMath.Normalize(RnMath.Mul(qb, dq));
                xfB.R = RnMath.Matrix(qb);
                xfB.T = new Vec3(xfB.T.X + dt.X, xfB.T.Y + dt.Y, xfB.T.Z + dt.Z);
            }
        }
        output.WriteLine($"{calls} stepped calls; cached axis still separating {outcomes[0]}, rebuilt from the cache {outcomes[1]},"
                         + $" full query {outcomes[2]}; {paths}");
    }

    /// <summary>
    /// The two-faced hull FUN_1802f5c20 builds for a mesh triangle {0, e1, e2}
    /// (the hull-vs-mesh path runs the same narrowphase on it).
    /// </summary>
    private static RnHull TriangleHull(Vector3 e1, Vector3 e2)
    {
        var n = Vector3.Normalize(Vector3.Cross(e1, e2));
        return new RnHull
        {
            Centroid = (e1 + e2) * 0.33333334f,
            VertexPositions = [Vector3.Zero, e1, e2],
            Planes = [(n, 0f), (-n, -0f)],
            Vertices = [0, 2, 4],
            Faces = [0, 1],
            Edges = [(2, 1, 0, 0), (5, 0, 1, 1), (4, 3, 1, 0), (1, 2, 2, 1), (0, 5, 2, 0), (3, 4, 0, 1)],
            Flags = 8,
            BoundsMin = Vector3.Min(Vector3.Min(Vector3.Zero, e1), e2),
            BoundsMax = Vector3.Max(Vector3.Max(Vector3.Zero, e1), e2),
        };
    }

    [Fact]
    public void HullsOnTrianglesMatch()
    {
        if (!Bind())
            return;
        var pool = Pool();
        var random = new Random(25);
        using var s = new Scratch();
        var paths = new Paths();
        var triangles = new List<NativeHull>();
        try
        {
            for (var i = 0; i < 2500; i++)
            {
                var e1 = new Vector3(Gauss(random), Gauss(random), Gauss(random)) * 40;
                var e2 = new Vector3(Gauss(random), Gauss(random), Gauss(random)) * 40;
                if (Vector3.Cross(e1, e2).Length() < 1f)
                    continue;
                var tri = new NativeHull(TriangleHull(e1, e2));
                triangles.Add(tri);
                var ia = random.Next(pool.Length);
                var a = new HullRef(pool[ia].Hull, Scale(random));
                var xfB = Frame(RandomRotation(random), RandomVec(random, 100));
                var b = new HullRef(tri.Hull, 1f);
                // Put A over a point of the triangle, along its normal.
                var nl = Vector3.Normalize(Vector3.Cross(e1, e2));
                var u = (float)random.NextDouble();
                var w = (float)random.NextDouble() * (1 - u);
                var at = e1 * u + e2 * w + nl * (a.Hull.MaxAngularRadius * a.Scale * (float)(random.NextDouble() * 1.2));
                var ta = HullCollision.ToWorld(xfB, at.X, at.Y, at.Z);
                var xfA = Frame(RandomRotation(random), ta);
                var cache = default(SatCache);
                var local = new[] { pool[ia], tri };
                var dt = RandomVec(random, 0.02f);
                for (var step = 0; step < 4; step++)
                {
                    Setup(s, xfA, pool[ia], a.Scale, xfB, tri, 1f);
                    var (hit, m, c) = Both(s, null, default, cache, local, 0, 1, i * 10 + step, "triangle", i);
                    paths.Count(hit, m, c);
                    cache = c;
                    xfA.T = new Vec3(xfA.T.X + dt.X, xfA.T.Y + dt.Y, xfA.T.Z + dt.Z);
                }
            }
        }
        finally
        {
            foreach (var t in triangles)
                t.Dispose();
        }
        output.WriteLine($"triangles: {paths}");
    }

    [Fact]
    public void FaceAndEdgeContactsMatch()
    {
        if (!Bind())
            return;
        var pool = Pool();
        var random = new Random(24);
        using var s = new Scratch();
        for (var i = 0; i < 4000; i++)
        {
            var (xfA, a, ia, xfB, b, ib) = RandomPair(random, pool);
            Setup(s, xfA, pool[ia], a.Scale, xfB, pool[ib], b.Scale);
            var flip = random.Next(2) == 1;
            var query = flip ? HullCollision.FaceQuery(xfB, b, xfA, a) : HullCollision.FaceQuery(xfA, a, xfB, b);
            CachedManifold? old = random.Next(2) == 0 ? RandomManifold(random) : null;
            if (old is { } o)
                *s.Old = o;
            var oldSpan = old is { } oo ? new ReadOnlySpan<CachedManifold>(in oo) : default;

            *s.Out = default;
            *s.Cache = default;
            *s.Query = query;
            var hit = flip
                ? _face(old is null ? null : s.Old, s.Out, s.XfB, s.RefB, s.XfA, s.RefA, s.Query, 1, s.Cache, 7)
                : _face(old is null ? null : s.Old, s.Out, s.XfA, s.RefA, s.XfB, s.RefB, s.Query, 0, s.Cache, 7);
            var ours = default(CachedManifold);
            var cache = default(SatCache);
            var ourHit = flip
                ? HullCollision.FaceContact(oldSpan, ref ours, xfB, b, xfA, a, query, true, ref cache, 7)
                : HullCollision.FaceContact(oldSpan, ref ours, xfA, a, xfB, b, query, false, ref cache, 7);
            var d = First(new ReadOnlySpan<byte>(s.Out, 0xe0), new ReadOnlySpan<byte>(&ours, 0xe0));
            if ((hit != 0) != ourHit || d >= 0 || !new ReadOnlySpan<byte>(s.Cache, 16).SequenceEqual(new ReadOnlySpan<byte>(&cache, 16)))
                Assert.Fail($"face trial {i} flip {flip}: hit {hit} ours {ourHit}, differs at +0x{d:x}\nvalve {Dump(*s.Out)}\nours  {Dump(ours)}\n{Describe(xfA, a, xfB, b)}");

            var edge = HullCollision.EdgeQuery(xfA, a, xfB, b);
            *s.Out = default;
            *s.Cache = default;
            *s.Query = edge;
            hit = _edge(old is null ? null : s.Old, s.Out, s.XfA, s.RefA, s.XfB, s.RefB, s.Query, s.Cache, 3);
            ours = default;
            cache = default;
            ourHit = HullCollision.EdgeContact(oldSpan, ref ours, xfA, a, xfB, b, edge, ref cache, 3);
            d = First(new ReadOnlySpan<byte>(s.Out, 0xe0), new ReadOnlySpan<byte>(&ours, 0xe0));
            if ((hit != 0) != ourHit || d >= 0 || !new ReadOnlySpan<byte>(s.Cache, 16).SequenceEqual(new ReadOnlySpan<byte>(&cache, 16)))
                Assert.Fail($"edge trial {i}: hit {hit} ours {ourHit}, differs at +0x{d:x}\nvalve {Dump(*s.Out)}\nours  {Dump(ours)}\n{Describe(xfA, a, xfB, b)}");
        }
    }

    private static int First(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        for (var i = 0; i < a.Length; i += 4)
        {
            var n = Math.Min(4, a.Length - i);
            if (a.Slice(i, n).SequenceEqual(b.Slice(i, n)))
                continue;
            if (n == 4 && float.IsNaN(BitConverter.ToSingle(a[i..])) && float.IsNaN(BitConverter.ToSingle(b[i..])))
                continue;
            return i;
        }
        return -1;
    }

    private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);
}
