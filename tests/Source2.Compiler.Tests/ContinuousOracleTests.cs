using System.Numerics;
using System.Runtime.InteropServices;
using Source2.Compiler.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Continuous collision and the rest of CRnWorld::Step that is neither
/// broadphase nor island solve, against Valve's world in this process.
/// </summary>
[Collection(Vphysics2PatchCollection.Name)]
public unsafe class ContinuousOracleTests(ITestOutputHelper output)
{
    private const ulong SolveContinuousVa = 0x1801ffe80;
    private const ulong ToiBodyVa = 0x1801be330;
    private const ulong ToiSearchVa = 0x1801beac0;

    private static readonly object Gate = new();
    private static nint _solveContinuous, _toiBody, _toiSearch;
    private static byte* _world;
    private static int _queued, _queuedSteps, _toiCalls, _clipped;
    private static readonly int[] Results = new int[3];

    /// <summary>
    /// How often continuous collision fires for props dropped or thrown onto a
    /// mesh floor at up to about 100 u/s, dt 1/90: bodies queued (moved more
    /// than half their inner radius), TOI searches run, and searches that
    /// moved the body back (alpha under 1).
    /// </summary>
    [Fact]
    public void HowOftenContinuousCollisionFires()
    {
        if (Vphysics2World.Create() is null)
            return;
        using var hooks = new Vphysics2Hooks();
        _solveContinuous = hooks.Detour(Vphysics2Oracle.At(Vphysics2Oracle.Load()!.Value, SolveContinuousVa),
                                        (nint)(delegate* unmanaged<byte*, float, void>)&OnSolveContinuous,
                                        Convert.FromHexString("488bc448894808574881ecd0000000"));
        _toiBody = hooks.Detour(Vphysics2Oracle.At(Vphysics2Oracle.Load()!.Value, ToiBodyVa),
                                (nint)(delegate* unmanaged<byte*, byte*, ulong>)&OnToiBody,
                                Convert.FromHexString("48895c240848895424105556574154"));
        foreach (var (name, minHalf, maxHalf, maxSpeed) in new[]
                 {
                     ("props 4-24 u, up to 100 u/s", 4.0, 24.0, 100.0),
                     ("small props 1-4 u, up to 100 u/s", 1.0, 4.0, 100.0),
                     ("props 4-24 u, up to 300 u/s", 4.0, 24.0, 300.0),
                 })
        {
            var w = Vphysics2World.Create()!;
            _world = w.Rn;
            _queued = _queuedSteps = _toiCalls = _clipped = 0;
            Array.Clear(Results);
            var random = new Random(41);
            var floor = w.CreateBody(0, default, Quat.Identity);
            w.AddMesh(floor, Floor(w));
            var bodies = new List<Vphysics2World.Body>();
            for (var i = 0; i < 60; i++)
            {
                var h = (float)(minHalf + random.NextDouble() * (maxHalf - minHalf));
                var speed = random.NextDouble() * maxSpeed;
                var drop = (float)(speed * speed / 720.0);
                var box = w.CreateBody(2, new Vec3(F(random, -250, 250), F(random, -250, 250), h * 1.8f + drop + 2f),
                                       RnMath.Normalize(new Quat(F(random, -1, 1), F(random, -1, 1), F(random, -1, 1), F(random, -1, 1))));
                w.AddHull(box, w.CreateHull(Box(h, random)), 1f);
                w.UpdateMass(box);
                if (random.Next(3) == 0)
                    box.State.LinearVelocity = new Vec3(F(random, -0.6, 0.6) * (float)maxSpeed, F(random, -0.6, 0.6) * (float)maxSpeed, 0f);
                bodies.Add(box);
            }
            const int steps = 900;
            for (var s = 0; s < steps; s++)
                w.Step(1f / 90f, s == 0);
            output.WriteLine($"{name}: {steps} steps, bodies queued {_queued} times on {_queuedSteps} steps, " +
                             $"TOI calls {_toiCalls} (results 0/1/2: {Results[0]}/{Results[1]}/{Results[2]}), moved back {_clipped}");
        }
        _world = null;
    }

    // ------------------------------------------------------------------ step glue

    /// <summary>FUN_1801f5b70 (the applied-force wake test) on random sleeping and awake bodies.</summary>
    [Fact]
    public void TheAppliedForceTestMatches()
    {
        if (Vphysics2World.Create() is not { } w)
            return;
        var test = (delegate* unmanaged<byte*, void>)w.At(0x1801f5b70);
        var random = new Random(52);
        var body = w.CreateBody(2, default, Quat.Identity);
        var woken = 0;
        for (var i = 0; i < 200000; i++)
        {
            ref var s = ref body.State;
            Vec3 V(double k) => random.Next(4) == 0 ? default : new(F(random, -k, k), F(random, -k, k), F(random, -k, k));
            s.Flags249 = (byte)((s.Flags249 & ~4) | (random.Next(2) == 0 ? 4 : 0));
            s.Flags4A = (ushort)(random.Next(8) == 0 ? 0x10 : 0);
            s.Force = V(random.Next(2) == 0 ? 0.02 : 5000);
            s.SleepingForce = random.Next(3) == 0 ? s.Force : V(random.Next(2) == 0 ? 0.02 : 5000);
            s.Torque = V(random.Next(2) == 0 ? 0.02 : 50000);
            s.SleepingTorque = random.Next(3) == 0 ? s.Torque : V(random.Next(2) == 0 ? 0.02 : 50000);
            s.LinearImpulse = V(1);
            s.AngularImpulse = V(0.1);
            s.InvMass = F(random, 0, 0.5);
            for (var k = 0; k < 9; k++)
                s.WorldInvInertia[k] = F(random, 0, 1e-3);
            s.LinearVelocityScale = random.Next(4) == 0 ? F(random, 0.5, 2) : 1f;
            s.AngularVelocityScale = random.Next(4) == 0 ? F(random, 0.5, 2) : 1f;
            s.LinearVelocity = V(10);
            s.AngularVelocity = V(1);
            var ours = s;
            test(body.Rn);
            StepGlue.AppliedForceTest(ref ours);
            var a = new ReadOnlySpan<byte>(body.Rn + 0x40, 0x180);
            var b = new ReadOnlySpan<byte>((byte*)&ours + 0x40, 0x180);
            if (!a.SequenceEqual(b))
                Assert.Fail($"trial {i}: valve {Convert.ToHexString(a)} ours {Convert.ToHexString(b)}");
            if ((ours.Flags4A & 0x10) != 0)
                woken++;
        }
        output.WriteLine($"{woken} of 200000 flagged to wake");
    }

    /// <summary>
    /// The step's clock (CRnWorld+0x1CC/+0x1D0/+0x1D4) and the plain world's
    /// idle parts: no step callbacks, controllers or applied-force bodies, and
    /// no body ever outside the bounds, over Valve's own steps.
    /// </summary>
    [Fact]
    public void TheStepClockMatches()
    {
        if (Vphysics2World.Create() is not { } w)
            return;
        var random = new Random(53);
        var floor = w.CreateBody(0, default, Quat.Identity);
        w.AddMesh(floor, Floor(w));
        for (var i = 0; i < 20; i++)
        {
            var box = w.CreateBody(2, new Vec3(F(random, -200, 200), F(random, -200, 200), F(random, 20, 200)), Quat.Identity);
            w.AddHull(box, w.CreateHull(Box(F(random, 4, 20), random)), 1f);
            w.UpdateMass(box);
        }
        var clock = new StepClock { Time = *(float*)(w.Rn + 0x1cc), PreviousTime = *(float*)(w.Rn + 0x1d0), StepCount = *(int*)(w.Rn + 0x1d4) };
        for (var s = 0; s < 600; s++)
        {
            var dt = s % 50 == 49 ? F(random, 0, 1e-5) : s % 7 == 0 ? F(random, 0.005, 0.03) : 1f / 90f;
            var first = random.Next(3) == 0;
            w.Step(dt, first);
            var ran = 1e-5f < dt && clock.Begin(dt, first);
            Assert.True(*(float*)(w.Rn + 0x1cc) == clock.Time && *(float*)(w.Rn + 0x1d0) == clock.PreviousTime
                        && *(int*)(w.Rn + 0x1d4) == clock.StepCount, $"step {s} ran {ran}");
            Assert.Equal(0, *(int*)(w.Rn + 0x9b0));
            Assert.Equal(0, *(int*)(w.Rn + 0x9c8));
            Assert.Equal(0, *(int*)(w.Rn + 0x698));
            Assert.Equal(0, *(int*)(w.Rn + 0xa58));
        }
    }

    // ------------------------------------------------------------------ sweeps

    /// <summary>
    /// The sweep helpers on random bodies: FUN_1801b5390, FUN_1801b5430,
    /// FUN_1801b5840 (and FUN_1801b5620 through it), FUN_1801b1250,
    /// FUN_1801b31a0 and FUN_1801b8bd0, every output byte compared.
    /// </summary>
    [Fact]
    public void TheSweepHelpersMatch()
    {
        if (Vphysics2World.Create() is not { } w)
            return;
        var sweepOf = (delegate* unmanaged<byte*, Sweep*, Sweep*>)w.At(0x1801b5390);
        var sweepAt = (delegate* unmanaged<byte*, Sweep*, float, float*, Sweep*>)w.At(0x1801b5430);
        var at = (delegate* unmanaged<Sweep*, RnTransform*, float, RnTransform*>)w.At(0x1801b5840);
        var advance = (delegate* unmanaged<byte*, float, byte>)w.At(0x1801b1250);
        var inertia = (delegate* unmanaged<byte*, Mat3*, Mat3*>)w.At(0x1801b31a0);
        var finish = (delegate* unmanaged<byte*, float, void>)w.At(0x1801b8bd0);
        var random = new Random(51);
        var body = w.CreateBody(2, default, Quat.Identity);
        var sweep = (Sweep*)NativeMemory.AlignedAlloc(0x60, 16);
        var xf = (RnTransform*)NativeMemory.AlignedAlloc(0x40, 16);
        var mat = (Mat3*)NativeMemory.AlignedAlloc(0x40, 16);
        var copy = (RnBodyState*)NativeMemory.AlignedAlloc((nuint)sizeof(RnBodyState), 16);
        for (var i = 0; i < 40000; i++)
        {
            RandomMotion(ref body.State, random);
            var state = body.State;

            // Valve writes 0x48 bytes of the 0x50-byte Sweep; the tail is padding.
            NativeMemory.Clear(sweep, 0x60);
            sweepOf(body.Rn, sweep);
            Same(sweep, Continuous.SweepOf(state), $"SweepOf {i}");

            var alpha = random.Next(5) == 0 ? state.Cleared1E8 : F(random, 0, 1);
            float t;
            NativeMemory.Clear(sweep, 0x60);
            sweepAt(body.Rn, sweep, alpha, &t);
            var ours = Continuous.SweepAt(state, alpha, out var tOurs);
            Same(sweep, ours, $"SweepAt {i}");
            Assert.Equal(Bits(t), Bits(tOurs));

            var u = random.Next(6) == 0 ? (random.Next(2) == 0 ? 0f : 1f) : F(random, 0, 1);
            if (random.Next(8) == 0)
            {
                sweep->Q = sweep->Q0;
                sweep->C = sweep->C0;
            }
            var local = *sweep;
            at(sweep, xf, u);
            Same(xf, Continuous.At(local, u), $"At {i}");

            inertia(body.Rn, mat);
            Same(mat, Continuous.WorldInverseInertia(state), $"inertia {i}");

            var d = random.Next(4) == 0 ? 0f : F(random, 0, 1);
            var done = advance(body.Rn, d) != 0;
            var mine = state;
            Assert.Equal(done, Continuous.Advance(ref mine, d));
            SameBody(body.Rn, mine, $"Advance {i}");

            var dt = random.Next(3) == 0 ? F(random, 0, 0.05) : 1f / 90f;
            finish(body.Rn, dt);
            Continuous.FinishStep(ref mine, dt);
            SameBody(body.Rn, mine, $"FinishStep {i}");
        }
    }

    private static void RandomMotion(ref RnBodyState s, Random r)
    {
        Quat Rot() => RnMath.Normalize(new Quat(F(r, -1, 1), F(r, -1, 1), F(r, -1, 1), F(r, -1, 1)));
        Vec3 V(double k) => new(F(r, -k, k), F(r, -k, k), F(r, -k, k));
        s.BodyType = r.Next(4) switch { 0 => 0, 1 => 1, _ => 2 };
        s.Flags249 = (byte)((s.Flags249 & ~0x40) | (r.Next(2) == 0 ? 0x40 : 0));
        s.Orientation = Rot();
        s.PreviousOrientation = r.Next(5) == 0 ? s.Orientation : Rot();
        s.Position = V(2000);
        s.PreviousPosition = r.Next(5) == 0 ? s.Position : new(s.Position.X + F(r, -30, 30), s.Position.Y + F(r, -30, 30), s.Position.Z + F(r, -30, 30));
        s.Cleared1E8 = r.Next(3) == 0 ? 0f : F(r, 0, 1);
        s.Scale = r.Next(3) == 0 ? F(r, 0.5, 2) : 1f;
        s.LocalMassCenter = V(10);
        s.TimeScale = r.Next(4) == 0 ? F(r, 0.5, 1.5) : 1f;
        s.InertiaScale = r.Next(4) == 0 ? F(r, 0.5, 1.5) : 1f;
        s.InertiaDivisor = r.Next(4) == 0 ? F(r, 0.5, 2) : 1f;
        for (var k = 0; k < 9; k++)
            s.LocalInvInertia[k] = F(r, 0, 1e-3);
        s.LinearVelocity = r.Next(8) == 0 ? V(20000) : V(300);
        s.AngularVelocity = r.Next(5) == 0 ? V(0.01) : r.Next(8) == 0 ? V(400) : V(20);
    }

    private static void Same<T>(T* valve, T ours, string what) where T : unmanaged
    {
        var a = new ReadOnlySpan<byte>(valve, sizeof(T));
        var b = new ReadOnlySpan<byte>(&ours, sizeof(T));
        if (!a.SequenceEqual(b))
            Assert.Fail($"{what}: valve {Convert.ToHexString(a)} ours {Convert.ToHexString(b)}");
    }

    /// <summary>The body fields the sweep helpers write: centres, orientations, alpha, inertia, velocities.</summary>
    private static void SameBody(byte* valve, RnBodyState ours, string what)
    {
        foreach (var (at, n) in new[] { (0xd8, 0x24), (0xfc, 0x24), (0x120, 0x20), (0x1dc, 0x10) })
        {
            var a = new ReadOnlySpan<byte>(valve + at, n);
            var b = new ReadOnlySpan<byte>((byte*)&ours + at, n);
            if (!a.SequenceEqual(b))
                Assert.Fail($"{what} at +0x{at:x}: valve {Convert.ToHexString(a)} ours {Convert.ToHexString(b)}");
        }
    }

    private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);

    [UnmanagedCallersOnly]
    private static void OnSolveContinuous(byte* world, float dt)
    {
        if (world == _world)
        {
            var n = *(int*)(world + 0xaa4) + *(int*)(world + 0xadc) + *(int*)(world + 0xb14);
            _queued += n;
            if (n > 0)
                _queuedSteps++;
        }
        ((delegate* unmanaged<byte*, float, void>)_solveContinuous)(world, dt);
    }

    [UnmanagedCallersOnly]
    private static ulong OnToiBody(byte* body, byte* parameters)
    {
        var result = ((delegate* unmanaged<byte*, byte*, ulong>)_toiBody)(body, parameters);
        if (*(byte**)(body + 0x58) == _world)
            lock (Gate)
            {
                _toiCalls++;
                Results[Math.Min((int)(uint)result, 2)]++;
                if (*(float*)(body + 0x1e8) < 1f)
                    _clipped++;
            }
        return result;
    }

    private static float F(Random r, double lo, double hi) => (float)(lo + r.NextDouble() * (hi - lo));

    private static Vector3[] Box(float h, Random r)
    {
        var e = new Vector3(h, h * F(r, 0.4, 1), h * F(r, 0.4, 1));
        var points = new Vector3[8];
        for (var i = 0; i < 8; i++)
            points[i] = new Vector3((i & 1) == 0 ? -e.X : e.X, (i & 2) == 0 ? -e.Y : e.Y, (i & 4) == 0 ? -e.Z : e.Z);
        return points;
    }

    private static byte* Floor(Vphysics2World w)
    {
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        const int n = 11;
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
                vertices.Add(new Vector3(i * 80 - 400, j * 80 - 400, ((i * 7 + j * 3) % 5) * 1.5f));
        for (var i = 0; i + 1 < n; i++)
            for (var j = 0; j + 1 < n; j++)
            {
                var a = i * n + j;
                indices.AddRange([a, a + n, a + 1, a + 1, a + n, a + n + 1]);
            }
        return w.CreateMesh([.. indices], [.. vertices]);
    }
}
