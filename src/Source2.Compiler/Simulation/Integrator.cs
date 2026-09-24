namespace Source2.Compiler.Simulation;

/// <summary>
/// Rubikon's per-body steps around the constraint solve: building the solver
/// body, integrating velocities (gravity, damping, drag, the implicit
/// gyroscopic term), integrating the transform, and the sleep test.
///
/// <para>Each routine is vphysics2's, read from the disassembly with every float
/// operation in its original order (see tools/settle/lanesym.py), and checked
/// bit for bit against the DLL's own function in the tests.</para>
/// </summary>
public static class Integrator
{
    /// <summary>Largest travel per step (FUN_1801b2570).</summary>
    private const float MaxTravel = 80f;

    /// <summary>Largest rotation per step, pi/2 (FUN_1801b2450).</summary>
    private const float MaxRotation = 1.57079637f;
    private const float MaxRotationSquared = 2.46740127f;

    /// <summary>Below this rotation angle the quaternion step uses a series.</summary>
    private const float SmallAngle = 0.0185813606f;

    /// <summary>Converts the drag constants to Rubikon's units (cubic inches per litre).</summary>
    private const float DragUnits = 1.63870627e-05f;

    /// <summary>A determinant below this makes a 3x3 inverse the zero matrix.</summary>
    private const float SingularDeterminant = 1.17549435e-35f;

    /// <summary>Squared speed and spin under which a body counts toward sleep.</summary>
    private const float SleepSpeedSquared = 0.160000011f;
    private const float SleepSpinSquared = 0.00487387972f;

    /// <summary>Seconds a body must stay slow before it is ready to sleep.</summary>
    private const float SleepTime = 0.5f;

    /// <summary>
    /// Copies a body into its solver body (FUN_1801b6000). The joint edge walk
    /// (body +0x70) that decides <see cref="SolverBody.NoDynamicContact"/> is the caller's.
    /// </summary>
    public static void Build(in RnBodyState b, ref SolverBody sb, bool touchesDynamic)
    {
        var ts = b.TimeScale;
        sb.V = new(b.LinearVelocity.X * ts, b.LinearVelocity.Y * ts, b.LinearVelocity.Z * ts);
        sb.W = new(b.AngularVelocity.X * ts, b.AngularVelocity.Y * ts, b.AngularVelocity.Z * ts);
        sb.WorldInvInertia = b.WorldInvInertia;

        var s = 0f;
        if (b.BodyType == 2)
        {
            var scale = b.Scale;
            var squared = scale * scale;
            s = (b.InertiaScale * b.TimeScale) / ((squared * scale) * squared);
        }
        var inverse = 1f / b.InertiaDivisor;
        for (var i = 0; i < 9; i++)
            sb.LocalInvInertia[i] = (inverse * b.LocalInvInertia[i]) * s;

        sb.Q = b.Orientation;
        sb.Scale = b.Scale;
        sb.LocalMassCenter = new(
            b.Scale * b.LocalMassCenter.X, b.Scale * b.LocalMassCenter.Y, b.Scale * b.LocalMassCenter.Z);
        sb.Position = b.Position;
        sb.V0 = sb.V;
        sb.W0 = sb.W;
        sb.InvMass = b.InvMass;
        sb.SleepTimer = b.SleepTimer;
        sb.TimeScale = b.TimeScale;
        sb.SolvePriority = b.SolvePriority;
        sb.FrictionScale = b.FrictionScale;
        sb.SleepRequest = (byte)((b.Flags4A >> 7) & 1);
        sb.SleepAllowed = (byte)((b.Flags249 >> 1) & 1);
        sb.BodyType = (byte)b.BodyType;
        sb.InfiniteMass = (byte)(b.InvMass == 0f ? 1 : 0);
        sb.InfiniteInertia = (byte)(IsZero(sb.WorldInvInertia) ? 1 : 0);
        sb.ReadyToSleep = 0;
        sb.NoDynamicContact = (byte)(b.BodyType == 2 && touchesDynamic ? 0 : 1);
    }

    private static bool IsZero(Mat3 m)
    {
        for (var i = 0; i < 9; i++)
            if (m[i] != 0f)
                return false;
        return true;
    }

    /// <summary>
    /// Integrates the linear velocity (FUN_1801b7940): gravity and force, then
    /// damping, then air drag in the body frame.
    /// </summary>
    /// <param name="b">The body.</param>
    /// <param name="sb">Its solver body.</param>
    /// <param name="dt">The step.</param>
    /// <param name="gravity">The world's gravity.</param>
    /// <param name="airDensity">The world's air density.</param>
    public static void IntegrateLinear(in RnBodyState b, ref SolverBody sb, float dt, Vec3 gravity, float airDensity)
    {
        var h = dt * b.TimeScale;
        var o = b.GravityOverride;
        var g = o.X != 0f || o.Y != 0f || o.Z != 0f ? o : gravity;
        var gs = b.GravityScale;
        var im = sb.InvMass;
        var k = b.LinearVelocityScale;
        var vx = ((((g.X * gs) + (im * b.Force.X)) * h) + sb.V.X + b.LinearImpulse.X) * k;
        var vy = ((((g.Y * gs) + (im * b.Force.Y)) * h) + sb.V.Y + b.LinearImpulse.Y) * k;
        var vz = ((((g.Z * gs) + (im * b.Force.Z)) * h) + sb.V.Z + b.LinearImpulse.Z) * k;

        var f = Damping(h * b.LinearDamping);
        vx *= f;
        vy *= f;
        vz *= f;

        if ((b.Flags249 & 0x20) != 0)
        {
            var local = ConjugateRotate(sb.Q, new Vec3(vx, vy, vz));
            var s = (MathF.Abs(local.X * b.LinearDragAxes.X) + MathF.Abs(local.Y * b.LinearDragAxes.Y))
                    + MathF.Abs(local.Z * b.LinearDragAxes.Z);
            var d = Drag(((s * b.LinearDrag) * -0.5f) * (airDensity * DragUnits) * h);
            if (d < 0f)
            {
                vx *= d + 1f;
                vy *= d + 1f;
                vz *= d + 1f;
            }
        }
        sb.V = new(vx, vy, vz);
    }

    /// <summary>
    /// Integrates the angular velocity (FUN_1801b7d60): torque, the implicit
    /// gyroscopic term solved by Newton's method in the body frame, damping and
    /// drag, then back to world space at the step's predicted end orientation.
    /// </summary>
    public static void IntegrateAngular(in RnBodyState b, ref SolverBody sb, float dt, float airDensity, int iterations = 2)
    {
        var h = dt * b.TimeScale;
        var inertia = Inverse(sb.LocalInvInertia);

        var iw = sb.WorldInvInertia;
        var t = b.Torque;
        var w1 = new Vec3(
            ((((t.Y * iw.M3) + (t.X * iw.M0)) + (t.Z * iw.M6)) * h) + sb.W.X + b.AngularImpulse.X,
            ((((t.Y * iw.M4) + (t.X * iw.M1)) + (t.Z * iw.M7)) * h) + sb.W.Y + b.AngularImpulse.Y,
            ((((t.Y * iw.M5) + (t.X * iw.M2)) + (t.Z * iw.M8)) * h) + sb.W.Z + b.AngularImpulse.Z);
        var local = ConjugateRotate(sb.Q, w1);
        var scale = b.AngularVelocityScale;
        var wb = new Vec3(local.X * scale, local.Y * scale, local.Z * scale);

        var w = wb;
        for (var i = 0; i < iterations; i++)
            w = NewtonStep(inertia, w, wb, h);

        var f = Damping(h * b.AngularDamping);
        w = new(w.X * f, w.Y * f, w.Z * f);

        if ((b.Flags249 & 0x20) != 0)
        {
            var s = (MathF.Abs(w.Y * b.AngularDragAxes.Y) + MathF.Abs(w.X * b.AngularDragAxes.X))
                    + MathF.Abs(w.Z * b.AngularDragAxes.Z);
            var d = Drag((-(s * b.AngularDrag) * (airDensity * DragUnits)) * h);
            if (d < 0f)
                w = new((d + 1f) * w.X, (d + 1f) * w.Y, (d + 1f) * w.Z);
        }

        var step = new Vec3(w.X * h, w.Y * h, w.Z * h);
        var end = RnMath.Mul(sb.Q, Rotation(step, (step.Z * step.Z + step.Y * step.Y) + step.X * step.X));
        sb.W = RnMath.Rotate(end, w);
    }

    /// <summary>
    /// One Newton step on I (w - wb) + h (w x I^T w) = 0, with the Jacobian
    /// (I S(w) - S(I^T w)) h + I and S(u) = [[0, uz, -uy], [-uz, 0, ux], [uy, -ux, 0]].
    /// </summary>
    private static Vec3 NewtonStep(Mat3 i, Vec3 w, Vec3 wb, float h)
    {
        var v = new Vec3(
            ((w.X * i.M0) + (w.Y * i.M3)) + (w.Z * i.M6),
            ((w.X * i.M1) + (w.Y * i.M4)) + (w.Z * i.M7),
            ((w.X * i.M2) + (w.Y * i.M5)) + (w.Z * i.M8));
        var sw = Skew(w);
        var sv = Skew(v);
        var j = new Mat3();
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
            {
                var m = ((i[3 * r] * sw[c]) + (i[3 * r + 1] * sw[3 + c])) + (i[3 * r + 2] * sw[6 + c]);
                j[3 * r + c] = ((m - sv[3 * r + c]) * h) + i[3 * r + c];
            }

        var dx = w.X - wb.X;
        var dy = w.Y - wb.Y;
        var dz = w.Z - wb.Z;
        var gx = -((((dx * i.M0) + (dy * i.M3)) + (dz * i.M6)) + (((w.Y * v.Z) - (w.Z * v.Y)) * h));
        var gy = -((((dx * i.M1) + (dy * i.M4)) + (dz * i.M7)) + (((w.Z * v.X) - (w.X * v.Z)) * h));
        var gz = -((((dx * i.M2) + (dy * i.M5)) + (dz * i.M8)) + (((w.X * v.Y) - (w.Y * v.X)) * h));

        var k = Inverse(j);
        return new(
            w.X + (((k.M0 * gx) + (k.M3 * gy)) + (k.M6 * gz)),
            w.Y + (((k.M1 * gx) + (k.M4 * gy)) + (k.M7 * gz)),
            w.Z + (((k.M2 * gx) + (k.M5 * gy)) + (k.M8 * gz)));
    }

    private static Mat3 Skew(Vec3 u) => new()
    {
        M0 = 0f, M1 = u.Z, M2 = -u.Y,
        M3 = -u.Z, M4 = 0f, M5 = u.X,
        M6 = u.Y, M7 = -u.X, M8 = 0f,
    };

    /// <summary>
    /// A 3x3 inverse by cofactors with one reciprocal of the determinant, or
    /// the zero matrix when the determinant is too small (both integrators).
    /// </summary>
    public static Mat3 Inverse(Mat3 m)
    {
        var c0 = (m.M4 * m.M8) - (m.M7 * m.M5);
        var c1 = (m.M6 * m.M5) - (m.M3 * m.M8);
        var c2 = (m.M3 * m.M7) - (m.M6 * m.M4);
        var det = ((m.M0 * c0) + (m.M1 * c1)) + (m.M2 * c2);
        if (SingularDeterminant > MathF.Abs(det))
            return new Mat3();
        var r = 1f / det;
        return new Mat3
        {
            M0 = c0 * r,
            M1 = ((m.M7 * m.M2) - (m.M8 * m.M1)) * r,
            M2 = ((m.M1 * m.M5) - (m.M4 * m.M2)) * r,
            M3 = c1 * r,
            M4 = ((m.M0 * m.M8) - (m.M6 * m.M2)) * r,
            M5 = ((m.M3 * m.M2) - (m.M0 * m.M5)) * r,
            M6 = c2 * r,
            M7 = ((m.M6 * m.M1) - (m.M0 * m.M7)) * r,
            M8 = ((m.M0 * m.M4) - (m.M3 * m.M1)) * r,
        };
    }

    /// <summary>
    /// Integrates the transform (FUN_1801c0040): clamps both velocities, moves
    /// the centre of mass, turns the orientation by w dt and renormalises it.
    /// </summary>
    public static void IntegratePosition(ref SolverBody sb, float dt)
    {
        sb.V = RnMath.Clamp(sb.V, dt, MaxTravel * MaxTravel, MaxTravel);
        sb.Position = new(
            dt * sb.V.X + sb.Position.X, sb.V.Y * dt + sb.Position.Y, sb.V.Z * dt + sb.Position.Z);
        sb.W = RnMath.Clamp(sb.W, dt, MaxRotationSquared, MaxRotation);

        var a = new Vec3(dt * sb.W.X, dt * sb.W.Y, dt * sb.W.Z);
        if (a.X == 0f && a.Y == 0f && a.Z == 0f)
            return;
        var dq = Rotation(a, (a.Y * a.Y + a.Z * a.Z) + a.X * a.X);
        SetOrientation(ref sb, RnMath.Normalize(RnMath.Mul(dq, sb.Q)));
    }

    /// <summary>
    /// The quaternion of a rotation vector, from its squared length: a series
    /// below <see cref="SmallAngle"/>, sincos of the half angle above.
    /// </summary>
    private static Quat Rotation(Vec3 a, float lengthSquared)
    {
        var theta = MathF.Sqrt(lengthSquared);
        if (SmallAngle > theta)
        {
            var s = 0.5f - (theta * theta) / 48f;
            return new(s * a.X, s * a.Y, s * a.Z, RnMath.Cos(theta * 0.5f));
        }
        var inverse = 1f / theta;
        var axis = new Vec3(a.X * inverse, a.Y * inverse, a.Z * inverse);
        if ((axis.X == 0f && axis.Y == 0f && axis.Z == 0f) || theta == 0f)
            return Quat.Identity;
        var (sin, cos) = RnMath.SinCos(theta * 0.5f);
        return new(sin * axis.X, sin * axis.Y, sin * axis.Z, cos);
    }

    /// <summary>Stores q and rebuilds the world inverse inertia from it (FUN_1801bc710).</summary>
    public static void SetOrientation(ref SolverBody sb, Quat q)
    {
        sb.Q = q;
        sb.WorldInvInertia = RnMath.RotateInertia(RnMath.Matrix(q), sb.LocalInvInertia);
    }

    /// <summary>
    /// The sleep test (FUN_1801c02a0): a slow body accumulates time, and one that
    /// has been slow long enough, or was asked to, is ready to sleep.
    /// </summary>
    public static void SleepTest(ref SolverBody sb, float dt)
    {
        var v = sb.V;
        var w = sb.W;
        if (sb.SleepAllowed == 0
            || (v.X * v.X + v.Y * v.Y) + v.Z * v.Z > SleepSpeedSquared
            || (w.X * w.X + w.Y * w.Y) + w.Z * w.Z > SleepSpinSquared)
        {
            sb.SleepTimer = 0f;
            sb.ReadyToSleep = sb.SleepRequest;
            return;
        }
        sb.SleepTimer = dt + sb.SleepTimer;
        sb.ReadyToSleep = (byte)(sb.SleepTimer > SleepTime || sb.SleepRequest != 0 ? 1 : 0);
    }

    /// <summary>1 / (1 + c) for a small c, exp(-c) otherwise.</summary>
    private static float Damping(float c) => 0.5f > c ? 1f / (c + 1f) : RnMath.Exp(-c);

    /// <summary>The drag factor minus one, never below -1 (maxss(-1, d)).</summary>
    private static float Drag(float d) => -1f > d ? -1f : d;

    /// <summary>Rotates v by the conjugate of q, into the body frame.</summary>
    private static Vec3 ConjugateRotate(Quat q, Vec3 v)
    {
        var tx = v.X * q.W - (v.Z * q.Y - v.Y * q.Z);
        var ty = v.Y * q.W - (v.X * q.Z - v.Z * q.X);
        var tz = v.Z * q.W - (v.Y * q.X - v.X * q.Y);
        var ux = q.Y * tz - q.Z * ty;
        var uy = q.Z * tx - q.X * tz;
        var uz = q.X * ty - q.Y * tx;
        return new(v.X - (ux + ux), v.Y - (uy + uy), v.Z - (uz + uz));
    }
}
