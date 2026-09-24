using System.Runtime.InteropServices;

namespace Source2.Compiler.Simulation;

/// <summary>
/// A body's motion over the rest of a step, as the TOI code keeps it (0x48
/// bytes, FUN_1801b5390): the start and end orientation (the end flipped to
/// the start's hemisphere), the fraction of the step already done, the scaled
/// mass-centre offset, and the start and end centres of mass.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x50)]
public struct Sweep
{
    [FieldOffset(0x00)] public Quat Q0;
    [FieldOffset(0x10)] public Quat Q;
    [FieldOffset(0x20)] public float Alpha0;
    [FieldOffset(0x24)] public Vec3 LocalCenter;
    [FieldOffset(0x30)] public Vec3 C0;
    [FieldOffset(0x3c)] public Vec3 C;
}

/// <summary>
/// Continuous collision (CRnWorld::SolveContinuous and what it calls), with
/// every float operation in vphysics2's order.
/// </summary>
public static class Continuous
{
    /// <summary>Largest travel and rotation per step (FUN_1801b2570, FUN_1801b2450).</summary>
    private const float MaxTravel = 80f;
    private const float MaxRotation = 1.57079637f;
    private const float MaxRotationSquared = 2.46740127f;

    /// <summary>Below this rotation angle the quaternion step uses a series.</summary>
    private const float SmallAngle = 0.0185813606f;

    /// <summary>The step fraction past which a body's motion counts as done.</summary>
    private const float Done = 0.9999881f;

    /// <summary>The dot product DPPS computes: (xx + yy) + (zz + ww).</summary>
    private static float Dot(Quat a, Quat b) => (a.X * b.X + a.Y * b.Y) + (a.Z * b.Z + a.W * b.W);

    private static Quat Negate(Quat q) => new(0f - q.X, 0f - q.Y, 0f - q.Z, 0f - q.W);

    /// <summary>FUN_1801b5390: the body's sweep.</summary>
    public static Sweep SweepOf(in RnBodyState b)
    {
        var s = b.Scale;
        var sweep = new Sweep
        {
            Alpha0 = b.Cleared1E8,
            C0 = b.PreviousPosition,
            C = b.Position,
            LocalCenter = new(s * b.LocalMassCenter.X, s * b.LocalMassCenter.Y, s * b.LocalMassCenter.Z),
            Q0 = b.PreviousOrientation,
            Q = b.Orientation,
        };
        if (Dot(sweep.Q0, sweep.Q) < 0f)
            sweep.Q = Negate(sweep.Q);
        return sweep;
    }

    /// <summary>
    /// FUN_1801b5430: the body's sweep with its start moved on to step fraction
    /// <paramref name="alpha"/>; <paramref name="t"/> is how far along its own
    /// remaining motion that is (always 1 for a dynamic body not in the TOI
    /// pass). The sweep keeps the body's own alpha0.
    /// </summary>
    public static Sweep SweepAt(in RnBodyState b, float alpha, out float t)
    {
        if (b.BodyType == 2 && (b.Flags249 & 0x40) == 0)
            t = 1f;
        else
        {
            var rest = 1f - b.Cleared1E8;
            if (rest <= 1.1920929e-05f)
                rest = 1.1920929e-05f;
            t = (alpha - b.Cleared1E8) / rest;
            if (t <= 0f)
                t = 0f;
            if (1f <= t)
                t = 1f;
        }
        var sweep = SweepOf(b);
        if (t != 0f)
        {
            var u = 1f - t;
            var q0 = b.PreviousOrientation;
            var q = b.Orientation;
            var a = new Quat(q0.X * u, q0.Y * u, q0.Z * u, q0.W * u);
            var c = new Quat(q.X * t, q.Y * t, q.Z * t, q.W * t);
            var mixed = Dot(q0, q) >= 0f
                ? new Quat(c.X + a.X, c.Y + a.Y, c.Z + a.Z, c.W + a.W)
                : new Quat(a.X - c.X, a.Y - c.Y, a.Z - c.Z, a.W - c.W);
            sweep.Q0 = RnMath.Normalize(mixed);
            sweep.C0 = new(
                t * b.Position.X + u * b.PreviousPosition.X,
                u * b.PreviousPosition.Y + t * b.Position.Y,
                u * b.PreviousPosition.Z + t * b.Position.Z);
            if (Dot(sweep.Q0, sweep.Q) < 0f)
                sweep.Q = Negate(sweep.Q);
        }
        return sweep;
    }

    /// <summary>
    /// FUN_1801b5840: the frame at fraction <paramref name="t"/> of the sweep,
    /// the orientation slerped linearly and renormalised; a sweep that does
    /// not move takes FUN_1801b5620's frame of its start.
    /// </summary>
    public static RnTransform At(in Sweep s, float t)
    {
        if (s.Q0.X == s.Q.X && s.Q0.Y == s.Q.Y && s.Q0.Z == s.Q.Z && s.Q0.W == s.Q.W
            && s.C.X == s.C0.X && s.C.Y == s.C0.Y && s.C.Z == s.C0.Z)
            return Frame(s.Q0, s.LocalCenter, s.C0);
        var u = 1f - t;
        var a = new Quat(u * s.Q0.X, u * s.Q0.Y, u * s.Q0.Z, u * s.Q0.W);
        var b = new Quat(s.Q.X * t, s.Q.Y * t, s.Q.Z * t, s.Q.W * t);
        var q = RnMath.Normalize(Dot(s.Q0, s.Q) >= 0f
            ? new Quat(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.W + b.W)
            : new Quat(a.X - b.X, a.Y - b.Y, a.Z - b.Z, a.W - b.W));
        var r = RnMath.Matrix(q);
        var lc = s.LocalCenter;
        return new RnTransform
        {
            R = r,
            T = new(
                ((u * s.C0.X) + (t * s.C.X)) - (((lc.Y * r.M3) + (lc.X * r.M0)) + (lc.Z * r.M6)),
                ((t * s.C.Y) + (u * s.C0.Y)) - (((lc.Y * r.M4) + (lc.X * r.M1)) + (lc.Z * r.M7)),
                ((t * s.C.Z) + (u * s.C0.Z)) - (((lc.Y * r.M5) + (lc.X * r.M2)) + (lc.Z * r.M8))),
        };
    }

    /// <summary>FUN_1801b5620: the frame of an orientation, mass-centre offset and centre of mass.</summary>
    public static RnTransform Frame(Quat q, Vec3 lc, Vec3 c)
    {
        var r = RnMath.Matrix(q);
        return new RnTransform
        {
            R = r,
            T = new(
                c.X - (((lc.X * r.M0) + (lc.Y * r.M3)) + (lc.Z * r.M6)),
                c.Y - (((lc.Y * r.M4) + (lc.X * r.M1)) + (lc.Z * r.M7)),
                c.Z - (((lc.Y * r.M5) + (lc.X * r.M2)) + (lc.Z * r.M8))),
        };
    }

    /// <summary>
    /// FUN_1801b1250: moves the body's start on to fraction <paramref name="t"/>
    /// of its remaining motion and makes it both start and end; returns whether
    /// the step is done.
    /// </summary>
    public static bool Advance(ref RnBodyState b, float t)
    {
        var alpha = (1f - b.Cleared1E8) * t + b.Cleared1E8;
        if (1f <= alpha)
            alpha = 1f;
        var u = 1f - t;
        b.Cleared1E8 = alpha;
        var c = new Vec3(
            u * b.PreviousPosition.X + t * b.Position.X,
            u * b.PreviousPosition.Y + t * b.Position.Y,
            u * b.PreviousPosition.Z + t * b.Position.Z);
        b.Position = c;
        b.PreviousPosition = c;
        var q0 = b.PreviousOrientation;
        var q = b.Orientation;
        var mixed = Dot(q0, q) >= 0f
            ? new Quat(q.X * t + q0.X * u, q.Y * t + q0.Y * u, q.Z * t + q0.Z * u, q.W * t + q0.W * u)
            : new Quat(u * q0.X - t * q.X, u * q0.Y - t * q.Y, u * q0.Z - t * q.Z, u * q0.W - t * q.W);
        var n = RnMath.Normalize(mixed);
        b.Orientation = n;
        b.PreviousOrientation = n;
        b.WorldInvInertia = WorldInverseInertia(b);
        return b.Cleared1E8 > Done;
    }

    /// <summary>
    /// FUN_1801b31a0: the local inverse inertia scaled by 1 / divisor and by
    /// time scale times inertia scale over (s * s^2) * s^2 (dynamic bodies
    /// only, else zero), turned into the world.
    /// </summary>
    public static Mat3 WorldInverseInertia(in RnBodyState b)
    {
        var k = 0f;
        if (b.BodyType == 2)
        {
            var s = b.Scale;
            var squared = s * s;
            k = (b.TimeScale * b.InertiaScale) / ((s * squared) * squared);
        }
        var inverse = 1f / b.InertiaDivisor;
        var local = new Mat3();
        for (var i = 0; i < 9; i++)
            local[i] = (inverse * b.LocalInvInertia[i]) * k;
        return RnMath.RotateInertia(RnMath.Matrix(b.Orientation), local);
    }

    /// <summary>
    /// FUN_1801b8bd0: the body moves on with its velocities for the part of
    /// the step still left, (1 - alpha0) dt, from its current transform, which
    /// becomes the sweep start.
    /// </summary>
    public static void FinishStep(ref RnBodyState b, float dt)
    {
        b.PreviousPosition = b.Position;
        var h = (1f - b.Cleared1E8) * dt;
        b.PreviousOrientation = b.Orientation;
        b.LinearVelocity = RnMath.Clamp(b.LinearVelocity, h, MaxTravel * MaxTravel, MaxTravel);
        b.AngularVelocity = RnMath.Clamp(b.AngularVelocity, h, MaxRotationSquared, MaxRotation);
        b.Position = new(
            h * b.LinearVelocity.X + b.Position.X,
            b.LinearVelocity.Y * h + b.Position.Y,
            b.LinearVelocity.Z * h + b.Position.Z);
        var ax = h * b.AngularVelocity.X;
        var ay = h * b.AngularVelocity.Y;
        var az = h * b.AngularVelocity.Z;
        var theta = MathF.Sqrt((az * az + ay * ay) + ax * ax);
        Quat dq;
        if (SmallAngle <= theta)
        {
            var inverse = 1f / theta;
            var x = inverse * ax;
            var y = inverse * ay;
            var z = inverse * az;
            dq = Quat.Identity;
            if (!(x == 0f && y == 0f && z == 0f) && theta != 0f)
            {
                var (sin, cos) = RnMath.SinCos(theta * 0.5f);
                dq = new Quat(x * sin, y * sin, z * sin, cos);
            }
        }
        else
        {
            var k = 0.5f - (theta * theta) / 48f;
            dq = new Quat(ax * k, ay * k, az * k, RnMath.Cos(theta * 0.5f));
        }
        b.Orientation = RnMath.Normalize(RnMath.Mul(dq, b.Orientation));
        b.WorldInvInertia = WorldInverseInertia(b);
    }
}
