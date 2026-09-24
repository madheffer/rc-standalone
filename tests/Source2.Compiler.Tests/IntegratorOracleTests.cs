using System.Runtime.InteropServices;
using Source2.Compiler.Simulation;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// Rubikon's per-body integration, ported, against vphysics2's own functions on
/// the same random bodies: every output byte must match. The bodies are random
/// but physical (unit orientations, symmetric inertias) with the branches mixed
/// in: damping on both sides of its series cut, drag on and off, spins below and
/// above the small-angle cut, singular inertias.
/// </summary>
public unsafe class IntegratorOracleTests
{
    private const int Trials = 20000;

    private static readonly Vec3 Gravity = new(-0f, -0f, -360f);
    private const float AirDensity = 1.2f;

    [Fact]
    public void TheCrtMathMatchesTier0()
    {
        if (Vphysics2Oracle.Load() is null)
            return;
        var cos = (delegate* unmanaged<float, float>)Vphysics2Oracle.Tier0("V_cosf");
        var exp = (delegate* unmanaged<float, float>)Vphysics2Oracle.Tier0("V_expf");
        var sincos = (delegate* unmanaged<float, float*, float*, void>)Vphysics2Oracle.Tier0("V_sincosf");
        var random = new Random(1);
        for (var i = 0; i < Trials * 10; i++)
        {
            var x = (float)(random.NextDouble() * 4 - 2) * (i % 3 == 0 ? 0.01f : 1f);
            Assert.True(Bits(cos(x)) == Bits(RnMath.Cos(x)), $"cos {x:R}");
            Assert.True(Bits(exp(-x * 4)) == Bits(RnMath.Exp(-x * 4)), $"exp {-x * 4:R}");
            float s, c;
            sincos(x, &s, &c);
            var (ps, pc) = RnMath.SinCos(x);
            Assert.True(Bits(s) == Bits(ps), $"sin {x:R}: {s:R} vs {ps:R}");
            Assert.True(Bits(c) == Bits(pc), $"sincos cos {x:R}");
        }
    }

    [Fact]
    public void BuildingTheSolverBodyMatches()
        => Compare(0x1801b6000, (b, sb, _) => Integrator.Build(*b, ref *sb, touchesDynamic: false),
                   (f, b, sb, _) => ((delegate* unmanaged<RnBodyState*, SolverBody*, void>)f)(b, sb));

    [Fact]
    public void LinearIntegrationMatches()
        => Compare(0x1801b7940, (b, sb, dt) => Integrator.IntegrateLinear(*b, ref *sb, dt, Gravity, AirDensity),
                   (f, b, sb, dt) => ((delegate* unmanaged<RnBodyState*, SolverBody*, float, void>)f)(b, sb, dt));

    [Fact]
    public void AngularIntegrationMatches()
        => Compare(0x1801b7d60, (b, sb, dt) => Integrator.IntegrateAngular(*b, ref *sb, dt, AirDensity),
                   (f, b, sb, dt) => ((delegate* unmanaged<RnBodyState*, SolverBody*, float, int, void>)f)(b, sb, dt, 2));

    [Fact]
    public void PositionIntegrationMatches()
        => Compare(0x1801c0040, (_, sb, dt) => Integrator.IntegratePosition(ref *sb, dt),
                   (f, _, sb, dt) => ((delegate* unmanaged<SolverBody*, float, void>)f)(sb, dt));

    [Fact]
    public void TheSleepTestMatches()
        => Compare(0x1801c02a0, (_, sb, dt) => Integrator.SleepTest(ref *sb, dt),
                   (f, _, sb, dt) => ((delegate* unmanaged<SolverBody*, float, void>)f)(sb, dt));

    private delegate void Port(RnBodyState* b, SolverBody* sb, float dt);

    private delegate void Valve(nint function, RnBodyState* b, SolverBody* sb, float dt);

    /// <summary>
    /// Runs both on the same random body and solver body and asserts the solver
    /// body comes out byte for byte the same.
    /// </summary>
    private static void Compare(ulong va, Port port, Valve valve)
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var function = Vphysics2Oracle.At(module, va);
        // Valve reads the orientation with movaps, so everything is 16-aligned.
        var world = (byte*)NativeMemory.AlignedAlloc(0x300, 16);
        var bodies = (RnBodyState*)NativeMemory.AlignedAlloc((nuint)(2 * sizeof(RnBodyState)), 16);
        var solver = (SolverBody*)NativeMemory.AlignedAlloc((nuint)(2 * sizeof(SolverBody)), 16);
        try
        {
            NativeMemory.Clear(world, 0x300);
            *(Vec3*)(world + 0x190) = Gravity;
            *(float*)(world + 0x1a0) = AirDensity;
            var random = new Random((int)va);
            for (var trial = 0; trial < Trials; trial++)
            {
                var body = RandomBody(random);
                *(nint*)((byte*)&body + 0x58) = (nint)world;
                var sb = default(SolverBody);
                Integrator.Build(body, ref sb, touchesDynamic: false);
                if (random.Next(4) == 0)
                    sb.SleepTimer = (float)random.NextDouble() * 0.6f;
                sb.SleepRequest = (byte)(random.Next(8) == 0 ? 1 : 0);
                var dt = trial % 5 == 0 ? (float)random.NextDouble() * 0.05f : 1f / 90f;

                bodies[0] = body;
                bodies[1] = body;
                solver[0] = sb;
                solver[1] = sb;
                valve(function, &bodies[0], &solver[0], dt);
                port(&bodies[1], &solver[1], dt);

                var a = new ReadOnlySpan<byte>(&solver[0], sizeof(SolverBody));
                var o = new ReadOnlySpan<byte>(&solver[1], sizeof(SolverBody));
                if (!a.SequenceEqual(o))
                    Assert.Fail($"trial {trial}: first difference at sb+0x{First(a, o):x}"
                                + $"\nvalve {Dump(solver[0])}\nours  {Dump(solver[1])}");
            }
        }
        finally
        {
            NativeMemory.AlignedFree(world);
            NativeMemory.AlignedFree(bodies);
            NativeMemory.AlignedFree(solver);
        }
    }

    private static int First(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i])
                return i;
        return -1;
    }

    private static string Dump(SolverBody sb)
        => $"v={sb.V} w={sb.W} q={sb.Q} p={sb.Position} t={sb.SleepTimer:R} ready={sb.ReadyToSleep}";

    private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);

    private static RnBodyState RandomBody(Random r)
    {
        float F(double lo, double hi) => (float)(lo + r.NextDouble() * (hi - lo));
        Vec3 V(double s) => new(F(-s, s), F(-s, s), F(-s, s));

        var b = default(RnBodyState);
        b.BodyType = r.Next(6) == 0 ? 1 : 2;
        b.Scale = r.Next(3) == 0 ? F(0.5, 2) : 1f;
        b.InertiaScale = r.Next(3) == 0 ? F(0.5, 2) : 1f;
        b.TimeScale = r.Next(4) == 0 ? F(0.2, 1.5) : 1f;
        b.InertiaDivisor = r.Next(3) == 0 ? F(0.5, 2) : 1f;
        b.GravityScale = r.Next(4) == 0 ? F(0, 2) : 1f;
        b.FrictionScale = F(0, 1);
        b.LocalInvInertia = r.Next(20) == 0 ? default : Symmetric(r, 1e-4);
        b.WorldInvInertia = r.Next(20) == 0 ? default : Symmetric(r, 1e-4);
        b.LocalMassCenter = V(10);
        b.InvMass = r.Next(20) == 0 ? 0f : F(1e-4, 0.1);
        b.Position = V(3000);
        b.LinearVelocity = r.Next(10) == 0 ? V(6000) : V(300);
        b.AngularVelocity = r.Next(5) == 0 ? V(0.01) : r.Next(10) == 0 ? V(400) : V(20);
        b.Orientation = RandomRotation(r);
        b.LinearDamping = r.Next(3) == 0 ? F(0, 80) : 0f;
        b.AngularDamping = r.Next(3) == 0 ? F(0, 80) : 0f;
        b.LinearDrag = F(0, 2);
        b.AngularDrag = F(0, 2);
        if (r.Next(3) == 0)
        {
            b.Force = V(1e5);
            b.Torque = V(1e6);
            b.LinearImpulse = V(10);
            b.AngularImpulse = V(1);
        }
        b.LinearVelocityScale = r.Next(5) == 0 ? F(0.5, 1) : 1f;
        b.AngularVelocityScale = r.Next(5) == 0 ? F(0.5, 1) : 1f;
        b.LinearDragAxes = new(F(0, 3000), F(0, 3000), F(0, 3000));
        b.AngularDragAxes = new(F(0, 3000), F(0, 3000), F(0, 3000));
        if (r.Next(8) == 0)
            b.GravityOverride = V(500);
        b.SleepTimer = F(0, 0.6);
        b.SolvePriority = (sbyte)r.Next(-2, 3);
        b.Flags4A = (ushort)(r.Next(8) == 0 ? 0x80 : 0);
        b.Flags249 = (byte)((r.Next(2) == 0 ? 0x20 : 0) | (r.Next(4) == 0 ? 0 : 2) | 1);
        return b;
    }

    private static Mat3 Symmetric(Random r, double scale)
    {
        var m = new Mat3();
        for (var i = 0; i < 3; i++)
            for (var j = i; j < 3; j++)
            {
                var v = (float)(r.NextDouble() * scale * (i == j ? 1 : 0.2));
                m[3 * i + j] = v;
                m[3 * j + i] = v;
            }
        return m;
    }

    private static Quat RandomRotation(Random r)
    {
        var q = new Quat((float)(r.NextDouble() * 2 - 1), (float)(r.NextDouble() * 2 - 1),
                         (float)(r.NextDouble() * 2 - 1), (float)(r.NextDouble() * 2 - 1));
        return RnMath.Normalize(q);
    }
}
