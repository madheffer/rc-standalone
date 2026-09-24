using System.Numerics;
using System.Runtime.InteropServices;
using Source2.Compiler.Physics;
using Source2.Compiler.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The contacts' times of impact and the mesh sweep query, against Valve's
/// functions called on contacts and meshes of a world in this process.
/// </summary>
[Collection(Vphysics2PatchCollection.Name)]
public sealed unsafe class ToiOracleTests(ITestOutputHelper output)
{
    private const ulong SweptQueryVa = 0x180249e90;

    private static float F(Random r, double lo, double hi) => (float)(lo + r.NextDouble() * (hi - lo));

    private static Vec3 V(Random r, double k) => new(F(r, -k, k), F(r, -k, k), F(r, -k, k));

    private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);

    /// <summary>A bumpy mesh: a grid with random heights, some steep.</summary>
    private static byte* Terrain(Vphysics2World w, Random r, int n, float cell)
    {
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
                vertices.Add(new Vector3(i * cell - n * cell / 2, j * cell - n * cell / 2, r.Next(4) == 0 ? F(r, -30, 30) : F(r, -2, 2)));
        for (var i = 0; i + 1 < n; i++)
            for (var j = 0; j + 1 < n; j++)
            {
                var a = i * n + j;
                indices.AddRange([a, a + n, a + 1, a + 1, a + n, a + n + 1]);
            }
        return w.CreateMesh([.. indices], [.. vertices]);
    }

    private static byte* Box(Vphysics2World w, float hx, float hy, float hz)
    {
        var pts = new Vector3[8];
        for (var i = 0; i < 8; i++)
            pts[i] = new Vector3((i & 1) == 0 ? -hx : hx, (i & 2) == 0 ? -hy : hy, (i & 4) == 0 ? -hz : hz);
        return w.CreateHull(pts);
    }

    private static byte* Rock(Vphysics2World w, Random r)
    {
        var pts = new Vector3[12 + r.Next(20)];
        var k = F(r, 3, 15);
        for (var i = 0; i < pts.Length; i++)
            pts[i] = new Vector3(F(r, -k, k), F(r, -k, k), F(r, -k * 0.6, k * 0.6));
        return w.CreateHull(pts);
    }

    /// <summary>FUN_180249e90 on random swept boxes over bumpy meshes, list for list.</summary>
    [Fact]
    public void TheSweptMeshQueryMatches()
    {
        if (Vphysics2World.Create() is not { } w)
            return;
        var module = Vphysics2Oracle.Load()!.Value;
        var query = (delegate* unmanaged<int*, byte*, Vec3*, Vec3*, Vec3*, Vec3*, byte, void>)Vphysics2Oracle.At(module, SweptQueryVa);
        var random = new Random(81);
        var list = (int*)NativeMemory.AllocZeroed(0x20);
        var buffer = (int*)NativeMemory.Alloc(4 * 100000);
        var total = 0;
        var checkedTriangles = 0;
        try
        {
            for (var m = 0; m < 6; m++)
            {
                var mesh = Terrain(w, random, 8 + 4 * m, F(random, 10, 60));
                var floor = w.CreateBody(0, default, Quat.Identity);
                w.AddMesh(floor, mesh);
                var hulls = new Dictionary<nint, RnHull>();
                var meshes = new Dictionary<nint, RnMesh>();
                RnWorldReader.Read(module, w.Rn, hulls, meshes);
                var ours = meshes[(nint)mesh];
                for (var i = 0; i < 5000; i++)
                {
                    var scale = random.Next(3) == 0 ? new Vec3(F(random, 0.5, 2), F(random, 0.5, 2), F(random, 0.5, 2)) : new Vec3(1, 1, 1);
                    var start = new Vec3(F(random, -200, 200), F(random, -200, 200), F(random, -20, 40));
                    var delta = random.Next(8) == 0 ? default : V(random, random.Next(2) == 0 ? 5 : 80);
                    var half = new Vec3(F(random, 1, 20), F(random, 1, 20), F(random, 1, 20));
                    var boxesOnly = (byte)(random.Next(4) == 0 ? 1 : 0);
                    list[0] = 0;
                    *(int**)((byte*)list + 8) = buffer;
                    list[4] = 100000;
                    list[5] = unchecked((int)0x80000000);
                    query(list, mesh, &scale, &start, &delta, &half, boxesOnly);
                    var found = new List<int>();
                    MeshSweep.Query(ours, scale, start, delta, half, boxesOnly != 0, found);
                    var valve = new ReadOnlySpan<int>(buffer, list[0]).ToArray();
                    Assert.True(valve.SequenceEqual(found),
                        $"mesh {m} trial {i}: valve [{string.Join(",", valve)}] ours [{string.Join(",", found)}]");
                    total += found.Count;
                    if (boxesOnly == 0)
                        checkedTriangles++;
                }
            }
        }
        finally
        {
            NativeMemory.Free(list);
            NativeMemory.Free(buffer);
        }
        output.WriteLine($"30000 queries, {total} triangles found, {checkedTriangles} with the triangle test");
    }

    /// <summary>A random sweep for one side of a contact: moving and turning from near where it is.</summary>
    private static Sweep SweepNear(Random r, in RnBodyState b, bool still)
    {
        var s = Continuous.SweepOf(b);
        if (still)
        {
            s.C0 = s.C;
            s.Q0 = s.Q;
            s.Alpha0 = r.Next(2) == 0 ? 0f : F(r, 0, 0.9);
            return s;
        }
        s.Alpha0 = r.Next(2) == 0 ? 0f : F(r, 0, 0.9);
        var back = V(r, r.Next(2) == 0 ? 10 : 60);
        s.C0 = new(s.C.X + back.X, s.C.Y + back.Y, s.C.Z + Math.Abs(back.Z) + F(r, 0, 20));
        s.Q0 = RnMath.Normalize(new Quat(s.Q.X + F(r, -0.3, 0.3), s.Q.Y + F(r, -0.3, 0.3), s.Q.Z + F(r, -0.3, 0.3), s.Q.W + F(r, -0.3, 0.3)));
        if ((s.Q0.X * s.Q.X + s.Q0.Y * s.Q.Y) + (s.Q0.Z * s.Q.Z + s.Q0.W * s.Q.W) < 0f)
            s.Q = new Quat(-s.Q.X, -s.Q.Y, -s.Q.Z, -s.Q.W);
        return s;
    }

    /// <summary>
    /// The contacts' vtable slot 3 (FUN_1803074d0 for hull pairs,
    /// FUN_180302250 for a hull on a mesh) on real contacts with random sweeps.
    /// </summary>
    [Fact]
    public void TheContactTimesOfImpactMatch()
    {
        if (Vphysics2World.Create() is not { } w)
            return;
        var module = Vphysics2Oracle.Load()!.Value;
        var random = new Random(82);
        var floor = w.CreateBody(0, default, Quat.Identity);
        w.AddMesh(floor, Terrain(w, random, 12, 40));
        var shapes = new List<nint>();
        for (var i = 0; i < 40; i++)
        {
            var b = w.CreateBody(2, new Vec3(F(random, -150, 150), F(random, -150, 150), F(random, 0, 30)),
                                 RnMath.Normalize(new Quat(F(random, -1, 1), F(random, -1, 1), F(random, -1, 1), F(random, -1, 1))));
            w.AddHull(b, random.Next(2) == 0 ? Box(w, F(random, 2, 16), F(random, 2, 16), F(random, 2, 16)) : Rock(w, random),
                      random.Next(4) == 0 ? F(random, 0.5, 2) : 1f);
            w.UpdateMass(b);
        }
        for (var s = 0; s < 3; s++)
            w.Step(1f / 90, s == 0);
        var world = RnWorldReader.Read(module, w.Rn, new Dictionary<nint, RnHull>(), new Dictionary<nint, RnMesh>());
        var contacts = world.AllContacts[0];
        output.WriteLine($"{contacts.Count} contacts, +0x1c0 = {*(int*)(w.Rn + 0x1c0)}");
        var sweeps = (Sweep*)NativeMemory.AlignedAlloc(0xa0, 16);
        var counts = new int[2];
        var early = new int[2];
        try
        {
            for (var i = 0; i < 20000; i++)
            {
                var c = contacts[random.Next(contacts.Count)];
                var a = SweepNear(random, c.A.Body.State, c.A.Body.State.BodyType == 0);
                var b = SweepNear(random, c.B.Body.State, c.B.Body.State.BodyType == 0 || random.Next(2) == 0);
                var tMax = random.Next(3) == 0 ? F(random, 0.05, 1) : 1f;
                sweeps[0] = a;
                sweeps[1] = b;
                var vfn = (delegate* unmanaged<nint, Sweep*, Sweep*, float, float>)(*(nint**)c.Native)[3];
                var valve = vfn(c.Native, &sweeps[0], &sweeps[1], tMax);
                var ours = ContinuousSolve.ContactToi(c, a, b, tMax);
                var kind = c.Mesh != null ? 1 : 0;
                Assert.True(Bits(valve) == Bits(ours), $"trial {i} {(kind == 1 ? "mesh" : "hulls")}: valve {valve:R} ours {ours:R} tMax {tMax:R}");
                counts[kind]++;
                if (ours < tMax)
                    early[kind]++;
            }
        }
        finally
        {
            NativeMemory.AlignedFree(sweeps);
        }
        output.WriteLine($"hull pairs {counts[0]} ({early[0]} hit), hull on mesh {counts[1]} ({early[1]} hit)");
    }

    /// <summary>FUN_1802552f0, the fallback of a failed search, on hull shapes with random sweeps.</summary>
    [Fact]
    public void TheFallbackMatches()
    {
        if (Vphysics2World.Create() is not { } w)
            return;
        var module = Vphysics2Oracle.Load()!.Value;
        var fallback = (delegate* unmanaged<nint, Sweep*, float>)Vphysics2Oracle.At(module, 0x1802552f0);
        var random = new Random(83);
        for (var i = 0; i < 30; i++)
        {
            var b = w.CreateBody(2, new Vec3(F(random, -150, 150), F(random, -150, 150), F(random, 0, 30)), Quat.Identity);
            w.AddHull(b, random.Next(2) == 0 ? Box(w, F(random, 2, 16), F(random, 2, 16), F(random, 2, 16)) : Rock(w, random),
                      random.Next(4) == 0 ? F(random, 0.5, 2) : 1f);
            w.UpdateMass(b);
        }
        var world = RnWorldReader.Read(module, w.Rn, new Dictionary<nint, RnHull>(), new Dictionary<nint, RnMesh>());
        var sweep = (Sweep*)NativeMemory.AlignedAlloc(0x50, 16);
        var partial = 0;
        try
        {
            for (var i = 0; i < 50000; i++)
            {
                var body = world.Bodies[random.Next(world.Bodies.Count)];
                if (body.Shapes.Count == 0 || body.Shapes[0].Type != 2)
                    continue;
                var shape = body.Shapes[0];
                var s = SweepNear(random, body.State, false);
                if (random.Next(3) == 0)
                    s.Q0 = s.Q;
                if (random.Next(3) == 0)
                    s.C0 = new(s.C.X + F(random, -2, 2), s.C.Y + F(random, -2, 2), s.C.Z + F(random, -2, 2));
                *sweep = s;
                var valve = fallback(shape.Native, sweep);
                var ours = ContinuousSolve.Fallback(shape, s);
                var sp = (byte*)shape.Native;
                Assert.True(Bits(valve) == Bits(ours), $"trial {i}: valve {valve:R} ours {ours:R} type {*(int*)(sp + 0x18)} mat {*(float*)(sp + 0x30)}/{*(float*)(sp + 0x34)} scale {*(float*)(sp + 0xb8)} inner {*(float*)(*(byte**)(sp + 0xc0) + 0x10)} ours inner {shape.Hull.Hull.MinCentroidRadius} {shape.Hull.Scale} sweep {Convert.ToHexString(new ReadOnlySpan<byte>(sweep, 0x48))}");
                if (ours < 1f)
                    partial++;
            }
        }
        finally
        {
            NativeMemory.AlignedFree(sweep);
        }
        output.WriteLine($"{partial} of 50000 below 1");
    }

    /// <summary>tier0's V_acosf over many arguments, against the port.</summary>
    [Fact]
    public void TheArcCosineMatches()
    {
        if (Vphysics2Oracle.Load() is null)
            return;
        var acos = (delegate* unmanaged<float, float>)Vphysics2Oracle.Tier0("V_acosf");
        var random = new Random(84);
        for (var i = 0; i < 2000000; i++)
        {
            var x = i switch
            {
                < 1000 => BitConverter.UInt32BitsToSingle((uint)random.Next()) ,
                < 500000 => F(random, -1, 1),
                < 1000000 => F(random, -1e-3, 1e-3),
                _ => (random.Next(2) == 0 ? 1f : -1f) * (1f - F(random, 0, 1e-2)),
            };
            if (float.IsNaN(x))
                continue;
            Assert.True(Bits(acos(x)) == Bits(ContinuousSolve.Acos(x)), $"acos({x:R}): valve {acos(x):R} ours {ContinuousSolve.Acos(x):R}");
        }
    }
}
