using System.Numerics;
using System.Runtime.InteropServices;
using Source2.Compiler.Physics;
using Source2.Compiler.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Rubikon's hull-vs-mesh contact update, ported, against vphysics2's own
/// FUN_180305460 on a fake CRnMeshContact over meshes built by the DLL's
/// RnMeshCreate: the candidate box, triangle list, per-triangle caches, the
/// manifold block and the size estimate must match after every step.
/// </summary>
public unsafe class MeshCollisionOracleTests(ITestOutputHelper output)
{
    private const ulong RnMeshCreateVa = 0x1801ad8a0;
    private const ulong HullShapeVtable = 0x1803e1140;

    // ---------------------------------------------------------------- native pieces

    /// <summary>A mesh RnMeshCreate built, and the same arrays as an <see cref="RnMesh"/>.</summary>
    private sealed class NativeMesh : IDisposable
    {
        public readonly byte* Ptr;
        public readonly RnMesh Mesh;

        public NativeMesh(nint module, List<int> indices, List<Vector3> vertices)
        {
            var create = (delegate* unmanaged<int, int*, byte*, int, Vector3*, void*, void*, void*>)Vphysics2Oracle.At(module, RnMeshCreateVa);
            Ptr = (byte*)NativeMemory.AllocZeroed(0xc0);
            var ia = indices.ToArray();
            var va = vertices.ToArray();
            void* got;
            fixed (int* pi = ia)
            fixed (Vector3* pv = va)
                got = create(ia.Length / 3, pi, null, va.Length, pv, Ptr, null);
            Assert.True(got != null, "RnMeshCreate built no mesh");
            Mesh = new RnMesh
            {
                Min = *(Vector3*)Ptr,
                Max = *(Vector3*)(Ptr + 0xc),
                Nodes = Read<RnMesh.Node>(0x18),
                Vertices = Read<Vector3>(0x30),
                Triangles = Triangles(),
            };
        }

        private (int, int, int)[] Triangles()
        {
            var raw = Read<int>(0x48, 3);
            var t = new (int, int, int)[raw.Length / 3];
            for (var i = 0; i < t.Length; i++)
                t[i] = (raw[3 * i], raw[3 * i + 1], raw[3 * i + 2]);
            return t;
        }

        private T[] Read<T>(int at, int per = 1) where T : unmanaged
            => new ReadOnlySpan<T>(*(void**)(Ptr + at + 8), *(int*)(Ptr + at) * per).ToArray();

        public void Dispose()
        {
            var alloc = *(nint**)Vphysics2Oracle.Tier0("g_pMemAlloc");
            var free = (delegate* unmanaged<nint*, void*, void>)(*(nint**)alloc)[3];
            foreach (var at in new[] { 0x20, 0x38, 0x50, 0x68, 0x80, 0x98 })
            {
                var p = *(void**)(Ptr + at);
                if (p != null && (*(uint*)(Ptr + at + 0xc) & 0xc0000000u) == 0)
                    free(alloc, p);
            }
            NativeMemory.Free(Ptr);
        }
    }

    /// <summary>A hull shape (0xd0), a mesh shape (0xe8) and a CRnMeshContact (0x100), zeroed and wired.</summary>
    private sealed class FakeContact : IDisposable
    {
        public readonly byte* Block = (byte*)NativeMemory.AlignedAlloc(0x400, 16);
        public byte* HullShape => Block;
        public byte* MeshShape => Block + 0x100;
        public byte* Contact => Block + 0x200;
        public RnTransform* XfA => (RnTransform*)(Block + 0x300);
        public RnTransform* XfB => (RnTransform*)(Block + 0x340);

        public FakeContact(nint module, byte* hull, float hullScale, byte* mesh, Vec3 meshScale)
        {
            NativeMemory.Clear(Block, 0x400);
            *(nint*)HullShape = Vphysics2Oracle.At(module, HullShapeVtable);
            *(int*)(HullShape + 0x18) = 2;
            *(float*)(HullShape + 0xb8) = hullScale;
            *(byte**)(HullShape + 0xc0) = hull;
            *(int*)(MeshShape + 0x18) = 3;
            *(Vec3*)(MeshShape + 0xb8) = meshScale;
            *(byte**)(MeshShape + 0xc8) = mesh;
            *(byte**)(Contact + 0x08) = HullShape;
            *(byte**)(Contact + 0x10) = MeshShape;
            *(int*)(Contact + 0x18) = -1;
            *(int*)(Contact + 0x1c) = -1;
            *(ushort*)(Contact + 0x78) = 1;
            var box = (float*)(Contact + 0xa8);
            box[0] = box[1] = box[2] = float.MaxValue;
            box[3] = box[4] = box[5] = -float.MaxValue;
        }

        public void Dispose() => NativeMemory.AlignedFree(Block);
    }

    // ---------------------------------------------------------------- comparison

    /// <summary>Returns a description of the first difference between Valve's contact and ours, or null.</summary>
    private static string? Compare(byte* contact, MeshContactState ours)
    {
        var box = (Vec3*)(contact + 0xa8);
        if (!Same(box[0], ours.BoxMin) || !Same(box[1], ours.BoxMax))
            return $"box valve {box[0]} {box[1]} ours {ours.BoxMin} {ours.BoxMax}";
        var count = *(int*)(contact + 0xc0);
        if (count != ours.Triangles.Count)
            return $"candidates valve {count} ours {ours.Triangles.Count}";
        var triangles = *(int**)(contact + 0xc8);
        for (var i = 0; i < count; i++)
            if (triangles[i] != ours.Triangles[i])
                return $"candidate {i}: valve {triangles[i]} ours {ours.Triangles[i]}";
        if (*(int*)(contact + 0xd8) != ours.Caches.Count)
            return $"caches valve {*(int*)(contact + 0xd8)} ours {ours.Caches.Count}";
        var caches = *(byte**)(contact + 0xe0);
        for (var i = 0; i < ours.Caches.Count; i++)
        {
            var c = ours.Caches[i];
            if (!new ReadOnlySpan<byte>(caches + 0x3c * i, 0x3c).SequenceEqual(new ReadOnlySpan<byte>(&c, 0x3c)))
            {
                var v = *(SatCache*)(caches + 0x3c * i + 0x2c);
                return $"cache {i} (triangle {ours.Triangles[i]}): valve {Dump(v)} ours {Dump(c.Sat)}";
            }
        }
        var block = *(byte**)(contact + 0x80);
        var manifolds = block == null ? 0 : *(int*)(block + 4);
        if (manifolds != ours.Manifolds.Count)
            return $"manifolds valve {manifolds} ours {ours.Manifolds.Count}";
        for (var k = 0; k < manifolds; k++)
        {
            var valve = *(CachedManifold*)(block + 8 + 0xe0 * k);
            var d = ManifoldDifference(valve, ours.Manifolds[k]);
            if (d >= 0)
                return $"manifold {k} differs at +0x{d:x}\nvalve {Dump(valve)}\nours  {Dump(ours.Manifolds[k])}";
        }
        if (*(int*)(contact + 0x98) != ours.SizeEstimate)
            return $"size valve {*(int*)(contact + 0x98)} ours {ours.SizeEstimate}";
        return null;
    }

    /// <summary>The header and each point to the count, bytes 0x00..0x25 (0x26..0x27 are never written).</summary>
    private static int ManifoldDifference(in CachedManifold x, in CachedManifold y)
    {
        fixed (CachedManifold* px = &x, py = &y)
        {
            var a = new ReadOnlySpan<byte>(px, 0xe0);
            var b = new ReadOnlySpan<byte>(py, 0xe0);
            if (!a[..0x40].SequenceEqual(b[..0x40]))
                return First(a[..0x40], b[..0x40]);
            for (var k = 0; k < Math.Min(x.PointCount, 4); k++)
            {
                var at = 0x40 + 0x28 * k;
                if (!a.Slice(at, 0x26).SequenceEqual(b.Slice(at, 0x26)))
                    return at + First(a.Slice(at, 0x26), b.Slice(at, 0x26));
            }
            return -1;
        }
    }

    private static int First(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i])
                return i;
        return -1;
    }

    private static bool Same(Vec3 a, Vec3 b) => Bits(a.X) == Bits(b.X) && Bits(a.Y) == Bits(b.Y) && Bits(a.Z) == Bits(b.Z);

    private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);

    private static string Dump(in SatCache c) => $"{{{c.Type}, {c.Index1}, {c.Index2}, {c.Separation:R}}}";

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

    // ---------------------------------------------------------------- scenes

    private static float Gauss(Random r)
        => (float)(Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble()));

    private static float F(Random r, double lo, double hi) => (float)(lo + r.NextDouble() * (hi - lo));

    private static Quat RandomRotation(Random r) => RnMath.Normalize(new Quat(Gauss(r), Gauss(r), Gauss(r), Gauss(r)));

    private static Quat SmallRotation(Random r, float angle)
    {
        var axis = Vector3.Normalize(new Vector3(Gauss(r), Gauss(r), Gauss(r)));
        var half = (float)(r.NextDouble() * angle * 0.5);
        var s = MathF.Sin(half);
        return RnMath.Normalize(new Quat(axis.X * s, axis.Y * s, axis.Z * s, MathF.Cos(half)));
    }

    private static RnTransform Frame(Quat q, Vec3 t) => new() { R = RnMath.Matrix(q), T = t };

    private enum Scene { Floor, Ramp, Room, Soup }

    /// <summary>A grid of quads (two triangles each), optionally bumpy, in the z = 0 plane.</summary>
    private static void Grid(Random r, List<int> idx, List<Vector3> v, int nx, int ny, float cell, float bump, Matrix4x4 place)
    {
        var start = v.Count;
        for (var j = 0; j <= ny; j++)
            for (var i = 0; i <= nx; i++)
                v.Add(Vector3.Transform(new Vector3(i * cell, j * cell, bump == 0 ? 0 : F(r, -bump, bump)), place));
        for (var j = 0; j < ny; j++)
            for (var i = 0; i < nx; i++)
            {
                var a = start + j * (nx + 1) + i;
                var b = a + 1;
                var c = a + nx + 1;
                var d = c + 1;
                if (r.Next(2) == 0)
                {
                    idx.AddRange([a, b, d, a, d, c]);
                }
                else
                {
                    idx.AddRange([a, b, c, b, d, c]);
                }
            }
    }

    private static (List<int>, List<Vector3>) Build(Random r, Scene scene)
    {
        var idx = new List<int>();
        var v = new List<Vector3>();
        switch (scene)
        {
            case Scene.Floor:
            {
                var n = 2 + r.Next(10);
                var cell = F(r, 8, 128);
                Grid(r, idx, v, n, n, cell, r.Next(3) == 0 ? F(r, 0, 2) : 0, Matrix4x4.CreateTranslation(-n * cell / 2, -n * cell / 2, 0));
                break;
            }
            case Scene.Ramp:
            {
                var n = 2 + r.Next(6);
                var cell = F(r, 16, 96);
                var tilt = F(r, 0.1, 0.8);
                Grid(r, idx, v, n, n, cell, 0, Matrix4x4.CreateTranslation(-n * cell / 2, -n * cell / 2, 0) * Matrix4x4.CreateRotationX(tilt));
                Grid(r, idx, v, 2, 2, cell, 0, Matrix4x4.CreateTranslation(-cell, -cell, -n * cell * 0.3f));
                break;
            }
            case Scene.Room:
            {
                var s = F(r, 64, 256);
                var cell = s / (1 + r.Next(4));
                var n = (int)MathF.Round(s / cell);
                // Floor and four walls facing in.
                Grid(r, idx, v, n, n, cell, 0, Matrix4x4.CreateTranslation(-s / 2, -s / 2, 0));
                for (var w = 0; w < 4; w++)
                    Grid(r, idx, v, n, n, cell, 0,
                         Matrix4x4.CreateTranslation(-s / 2, 0, 0) * Matrix4x4.CreateRotationX(MathF.PI / 2)
                         * Matrix4x4.CreateTranslation(0, s / 2, 0) * Matrix4x4.CreateRotationZ(w * MathF.PI / 2));
                break;
            }
            default:
            {
                var n = 10 + r.Next(200);
                for (var t = 0; t < n; t++)
                {
                    var c = new Vector3(Gauss(r), Gauss(r), Gauss(r)) * 40;
                    var s = F(r, 4, 60);
                    for (var k = 0; k < 3; k++)
                    {
                        idx.Add(v.Count);
                        v.Add(c + new Vector3(Gauss(r), Gauss(r), Gauss(r)) * s);
                    }
                }
                break;
            }
        }
        return (idx, v);
    }

    // ---------------------------------------------------------------- runs

    [Fact]
    public void MovingHullsMatch() => Run(31, 600, "moving", resting: false);

    [Fact]
    public void RestingAndSlidingHullsMatch() => Run(32, 900, "resting", resting: true);

    private void Run(int seed, int scenes, string what, bool resting)
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var update = (delegate* unmanaged<byte*, RnTransform*, RnTransform*, void>)Vphysics2Oracle.At(module, 0x180305460);
        var random = new Random(seed);
        var hulls = HullPool(random);
        var steps = 0;
        var withContacts = 0;
        var manifoldsSeen = new int[5];
        var pointsSeen = new int[5];
        int warm = 0, reused = 0, cached = 0;
        var failures = new List<string>();
        for (var sc = 0; sc < scenes && failures.Count == 0; sc++)
        {
            var scene = (Scene)(sc % 4);
            var (idx, verts) = Build(random, scene);
            using var mesh = new NativeMesh(module, idx, verts);
            var hullIndex = random.Next(hulls.Length);
            var hull = hulls[hullIndex];
            var hullScale = random.Next(3) == 0 ? F(random, 0.5, 2) : 1f;
            var meshScale = random.Next(4) == 0 ? new Vec3(F(random, 0.5, 2), F(random, 0.5, 2), F(random, 0.5, 2)) : new Vec3(1, 1, 1);
            using var fake = new FakeContact(module, hull.Ptr, hullScale, mesh.Ptr, meshScale);
            var state = new MeshContactState();
            var hr = new HullRef(hull.Hull, hullScale);

            var xfMesh = random.Next(3) == 0 ? Frame(SmallRotation(random, 0.3f), new Vec3(F(random, -50, 50), F(random, -50, 50), F(random, -50, 50)))
                                             : Frame(Quat.Identity, default);
            var q = random.Next(2) == 0 ? RandomRotation(random) : SmallRotation(random, 0.2f);
            var radius = hull.Hull.MaxAngularRadius * hullScale;
            var local = scene == Scene.Soup
                ? new Vector3(Gauss(random), Gauss(random), Gauss(random)) * 30
                : new Vector3(F(random, -40, 40), F(random, -40, 40), radius * F(random, 0.3, 1.1));
            var world = HullCollision.ToWorld(xfMesh, local.X, local.Y, local.Z);
            var xfHull = Frame(q, world);
            var velocity = new Vec3(Gauss(random) * 2, Gauss(random) * 2, Gauss(random) * (scene == Scene.Floor ? 2 : 1));
            var spin = SmallRotation(random, 0.1f);
            if (resting && scene != Scene.Soup)
            {
                // Settle it on the mesh: lowest corner at a small gap above z = 0 (mesh space).
                var gap = new[] { -0.08f, -0.01f, 0f, 0.02f, 0.06f, 0.1f, 0.124f }[random.Next(7)];
                if (random.Next(2) == 0)
                    q = random.Next(2) == 0 ? Quat.Identity : SmallRotation(random, 0.05f);
                var rel = Frame(q, new Vec3(local.X, local.Y, 0));
                var low = float.MaxValue;
                foreach (var pv in hull.Hull.VertexPositions)
                    low = MathF.Min(low, HullCollision.ToWorld(rel, pv.X * hullScale, pv.Y * hullScale, pv.Z * hullScale).Z);
                if (scene == Scene.Ramp)
                    gap += 30;
                world = HullCollision.ToWorld(xfMesh, local.X, local.Y, gap - low);
                xfHull = Frame(q, world);
                velocity = random.Next(2) == 0 ? new Vec3(F(random, -0.02, 0.02), F(random, -0.02, 0.02), F(random, -0.01, 0.01))
                                               : new Vec3(F(random, -0.6, 0.6), F(random, -0.6, 0.6), 0);
                velocity = HullCollision.ToWorld(new RnTransform { R = xfMesh.R }, velocity.X, velocity.Y, velocity.Z);
                spin = SmallRotation(random, 0.004f);
            }
            for (var step = 0; step < 10; step++)
            {
                *fake.XfA = xfHull;
                *fake.XfB = xfMesh;
                var boxBefore = state.BoxMin;
                var cacheTypes = state.Caches.Count(c => c.Sat.Type != 0);
                update(fake.Contact, fake.XfA, fake.XfB);
                MeshCollision.Update(state, xfHull, hr, xfMesh, mesh.Mesh, meshScale);
                steps++;
                if (state.Manifolds.Count > 0)
                    withContacts++;
                manifoldsSeen[Math.Min(state.Manifolds.Count, 4)]++;
                if (step > 0 && Same(boxBefore, state.BoxMin))
                    reused++;
                cached += cacheTypes;
                foreach (var m in state.Manifolds)
                {
                    pointsSeen[m.PointCount]++;
                    for (var k = 0; k < m.PointCount; k++)
                        warm += ((m.Points[k].Reserved >> 8) & 0xff) == 0 ? 1 : 0;
                }
                if (Compare(fake.Contact, state) is { } diff)
                {
                    failures.Add($"{what} scene {sc} ({scene}, hull {hullIndex} v={hull.Hull.VertexPositions.Length}, scale {hullScale}, mesh scale {meshScale}) step {step}: {diff}");
                    break;
                }
                q = RnMath.Normalize(RnMath.Mul(q, spin));
                xfHull = Frame(q, new Vec3(xfHull.T.X + velocity.X, xfHull.T.Y + velocity.Y, xfHull.T.Z + velocity.Z));
                if (random.Next(4) == 0)
                    velocity = new Vec3(velocity.X, velocity.Y, -velocity.Z);
            }
        }
        output.WriteLine($"{what}: {steps} steps, {withContacts} with manifolds; manifold counts 0..4+: {string.Join(' ', manifoldsSeen)};"
                         + $" manifolds by point count 0..4: {string.Join(' ', pointsSeen)}; warm-started points {warm};"
                         + $" steps reusing the candidate box {reused}; triangle caches carried in {cached}");
        foreach (var f in failures)
            output.WriteLine(f);
        Assert.Empty(failures);
    }

    // ---------------------------------------------------------------- sub-functions

    private static Vec3 RandomVec(Random r, float s) => new(Gauss(r) * s, Gauss(r) * s, Gauss(r) * s);

    [Fact]
    public void TriangleBoxTestMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var test = (delegate* unmanaged<Vec3*, Vec3*, Vec3*, Vec3*, Vec3*, byte>)Vphysics2Oracle.At(module, 0x18024b230);
        var random = new Random(41);
        var v = (Vec3*)NativeMemory.AlignedAlloc(0x80, 16);
        var hits = 0;
        try
        {
            for (var i = 0; i < 100000; i++)
            {
                v[0] = RandomVec(random, 10);
                v[1] = new Vec3(MathF.Abs(Gauss(random)) * 8, MathF.Abs(Gauss(random)) * 8, MathF.Abs(Gauss(random)) * 8);
                for (var k = 2; k < 5; k++)
                    v[k] = RandomVec(random, 16);
                if (i % 7 == 0)
                    v[4] = v[3];
                var valve = test(&v[0], &v[1], &v[2], &v[3], &v[4]) != 0;
                var ours = MeshCollision.TriangleOverlapsBox(v[0], v[1], v[2], v[3], v[4]);
                Assert.True(valve == ours, $"trial {i}: valve {valve} ours {ours}");
                hits += valve ? 1 : 0;
            }
        }
        finally
        {
            NativeMemory.AlignedFree(v);
        }
        output.WriteLine($"triangle-box: 100000 trials, {hits} overlapping");
    }

    [Fact]
    public void TheBvhQueryMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var query = (delegate* unmanaged<byte*, byte*, Vec3*, Vec3*, byte, void>)Vphysics2Oracle.At(module, 0x18024a4a0);
        var random = new Random(42);
        var scratch = (byte*)NativeMemory.AlignedAlloc(0x100, 16);
        try
        {
            for (var sc = 0; sc < 300; sc++)
            {
                var (idx, verts) = Build(random, (Scene)(sc % 4));
                using var mesh = new NativeMesh(module, idx, verts);
                for (var i = 0; i < 20; i++)
                {
                    NativeMemory.Clear(scratch, 0x100);
                    var scale = random.Next(3) == 0 ? new Vec3(F(random, 0.5, 2), F(random, 0.5, 2), F(random, 0.5, 2)) : new Vec3(1, 1, 1);
                    var c = RandomVec(random, 60);
                    var h = new Vec3(F(random, 1, 40), F(random, 1, 40), F(random, 1, 40));
                    var box = (Vec3*)(scratch + 0x40);
                    box[0] = new Vec3(c.X - h.X, c.Y - h.Y, c.Z - h.Z);
                    box[1] = new Vec3(c.X + h.X, c.Y + h.Y, c.Z + h.Z);
                    *(Vec3*)(scratch + 0x80) = scale;
                    query(scratch, mesh.Ptr, (Vec3*)(scratch + 0x80), box, 0);
                    var ours = new List<int>();
                    MeshCollision.QueryTriangles(mesh.Mesh, scale, box[0], box[1], ours);
                    var count = *(int*)scratch;
                    var found = *(int**)(scratch + 8);
                    Assert.True(count == ours.Count, $"scene {sc} query {i}: valve {count} ours {ours.Count}");
                    for (var k = 0; k < count; k++)
                        Assert.True(found[k] == ours[k], $"scene {sc} query {i}: entry {k} valve {found[k]} ours {ours[k]}");
                }
            }
        }
        finally
        {
            NativeMemory.AlignedFree(scratch);
        }
    }

    [Fact]
    public void ThePointReductionMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var reduce = (delegate* unmanaged<byte*, RnTransform*, Vec3*, void>)Vphysics2Oracle.At(module, 0x180300d50);
        var random = new Random(43);
        var scratch = (byte*)NativeMemory.AlignedAlloc(0x1000, 16);
        try
        {
            for (var i = 0; i < 40000; i++)
            {
                NativeMemory.Clear(scratch, 0x1000);
                var count = 1 + random.Next(16);
                var points = new CachedPoint[count];
                var spread = i % 3 == 0 ? 0.03f : 20f;
                for (var k = 0; k < count; k++)
                    points[k] = new CachedPoint
                    {
                        LocalA = RandomVec(random, spread), LocalB = RandomVec(random, spread), Impulse = random.Next(4),
                        Feature = random.Next(), SubShape = random.Next(100), Reserved = random.Next(),
                    };
                var xf = (RnTransform*)(scratch + 0x40);
                *xf = Frame(RandomRotation(random), RandomVec(random, 50));
                var n = (Vec3*)(scratch + 0x80);
                var u = Vector3.Normalize(new Vector3(Gauss(random), Gauss(random), Gauss(random)));
                *n = i % 11 == 0 ? default : new Vec3(u.X, u.Y, u.Z);
                var data = (CachedPoint*)(scratch + 0x100);
                for (var k = 0; k < count; k++)
                    data[k] = points[k];
                *(int*)scratch = count;
                *(CachedPoint**)(scratch + 8) = data;
                *(int*)(scratch + 0x10) = count;
                *(uint*)(scratch + 0x14) = 0x80000000u;
                reduce(scratch, xf, n);
                var got = MeshCollision.ReducePoints(points, *xf, *n);
                Assert.True(*(int*)scratch == got, $"trial {i}: count valve {*(int*)scratch} ours {got}");
                for (var k = 0; k < got; k++)
                {
                    var a = data[k];
                    var b = points[k];
                    Assert.True(new ReadOnlySpan<byte>(&a, 0x28).SequenceEqual(new ReadOnlySpan<byte>(&b, 0x28)),
                                $"trial {i}: point {k} differs");
                }
            }
        }
        finally
        {
            NativeMemory.AlignedFree(scratch);
        }
    }

    // ---------------------------------------------------------------- hulls

    private sealed class NativeHull(RnHull hull, byte* ptr)
    {
        public readonly RnHull Hull = hull;
        public readonly byte* Ptr = ptr;
    }

    private static NativeHull[]? _hulls;

    private static NativeHull[] HullPool(Random r)
    {
        if (_hulls != null)
            return _hulls;
        var list = new List<NativeHull>();
        while (list.Count < 60)
        {
            RnHull? hull;
            if (list.Count % 3 == 0)
            {
                hull = RnHullBuilder.CreateBox(new Vector3(F(r, 1, 24), F(r, 1, 24), F(r, 1, 24)), new Vector3(F(r, -1, 1), F(r, -1, 1), F(r, -1, 1)));
            }
            else
            {
                var n = 4 + r.Next(40);
                var size = new Vector3(F(r, 1, 24), F(r, 1, 24), F(r, 1, 24));
                var pts = new Vector3[n];
                for (var i = 0; i < n; i++)
                    pts[i] = new Vector3(F(r, -1, 1), F(r, -1, 1), F(r, -1, 1)) * size;
                try { hull = RnHullBuilder.Create(pts, RnHullBuilder.Options.Compile, out _); }
                catch (Exception) { hull = null; }
            }
            if (hull != null && hull.Faces.Length > 1)
                list.Add(new NativeHull(hull, Marshal(hull)));
        }
        return _hulls = list.ToArray();
    }

    /// <summary>RnHull_t with its arrays, as HullCollisionOracleTests lays it out.</summary>
    private static byte* Marshal(RnHull hull)
    {
        int nv = hull.VertexPositions.Length, np = hull.Planes.Length, nve = hull.Vertices.Length, ne = hull.Edges.Length, nf = hull.Faces.Length;
        static int Align(int x) => (x + 15) & ~15;
        var verts = 0x100;
        var planes = Align(verts + 12 * nv);
        var vertexEdges = Align(planes + 16 * np);
        var edges = Align(vertexEdges + nve);
        var faces = Align(edges + 4 * ne);
        var size = Align(faces + nf);
        var p = (byte*)NativeMemory.AlignedAlloc((nuint)size, 16);
        NativeMemory.Clear(p, (nuint)size);
        *(Vector3*)p = hull.Centroid;
        *(float*)(p + 0x0c) = hull.MaxAngularRadius;
        *(float*)(p + 0x10) = hull.MinCentroidRadius;
        *(Vector3*)(p + 0x14) = hull.BoundsMin;
        *(Vector3*)(p + 0x20) = hull.BoundsMax;
        void Vec(int at, int data, int count)
        {
            *(int*)(p + at) = count;
            *(int*)(p + at + 4) = count;
            *(byte**)(p + at + 8) = p + data;
        }
        Vec(0x70, verts, nv);
        for (var i = 0; i < nv; i++)
            *(Vector3*)(p + verts + 12 * i) = hull.VertexPositions[i];
        Vec(0x88, planes, np);
        for (var i = 0; i < np; i++)
        {
            *(Vector3*)(p + planes + 16 * i) = hull.Planes[i].Normal;
            *(float*)(p + planes + 16 * i + 12) = hull.Planes[i].Offset;
        }
        *(uint*)(p + 0xa0) = hull.Flags;
        Vec(0xb0, vertexEdges, nve);
        for (var i = 0; i < nve; i++)
            p[vertexEdges + i] = hull.Vertices[i];
        Vec(0xc8, edges, ne);
        for (var i = 0; i < ne; i++)
        {
            var e = hull.Edges[i];
            p[edges + 4 * i] = e.Next;
            p[edges + 4 * i + 1] = e.Twin;
            p[edges + 4 * i + 2] = e.Origin;
            p[edges + 4 * i + 3] = e.Face;
        }
        Vec(0xe0, faces, nf);
        for (var i = 0; i < nf; i++)
            p[faces + i] = hull.Faces[i];
        return p;
    }
}
