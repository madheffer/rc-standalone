using System.Runtime.InteropServices;

namespace Source2.Compiler.Simulation;

/// <summary>
/// CRnRevoluteJoint (type 3, 0x130 bytes, vtable 0x1803e07e8) as vphysics2
/// lays it out: the CRnJoint header, the frames, the motor, the hinge limit
/// and the impulses the solver accumulates. A model's VPhysXJoint_t of type 3
/// becomes one (FUN_18005cb80) unless its flags have bit 1, which makes a
/// CRnRevoluteBlockJoint (180213270, record type 6, not ported). The
/// constructor (1802135a0) leaves mode 3, frequency 10, damping ratio 1, the
/// limit at [-pi, pi] and everything else after the frames zero.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x130)]
public struct RevoluteJointState
{
    /// <summary>+0x08: index in the world's joint list.</summary>
    [FieldOffset(0x08)] public int WorldIndex;

    /// <summary>+0x0c: the joint type (3).</summary>
    [FieldOffset(0x0c)] public int Type;

    /// <summary>+0x28: bit 0 disabled, bit 1 collision between the bodies, bit 2 no linear part, bit 3 no angular part.</summary>
    [FieldOffset(0x28)] public ushort Flags;

    [FieldOffset(0x2a)] public sbyte Priority;
    [FieldOffset(0x2b)] public byte IgnoreInfiniteMass;
    [FieldOffset(0x2c)] public byte IgnoreInfiniteInertia;
    [FieldOffset(0x38)] public int BucketIndex;
    [FieldOffset(0x3c)] public int IslandIndex;
    [FieldOffset(0x40)] public byte Colour;
    [FieldOffset(0x41)] public byte InIsland;
    [FieldOffset(0x68)] public int SolverA;
    [FieldOffset(0x6c)] public int SolverB;
    [FieldOffset(0x70)] public Quat Frame1;
    [FieldOffset(0x80)] public Quat Frame2;
    [FieldOffset(0x90)] public Vec3 Anchor1;
    [FieldOffset(0x9c)] public Vec3 Anchor2;

    /// <summary>+0xa8: the record size estimate (0x1ac).</summary>
    [FieldOffset(0xa8)] public int Size;

    /// <summary>+0xe0: 0 spring to the target angle, 1 friction, 2 spring with a velocity motor, 3 none.</summary>
    [FieldOffset(0xe0)] public int Mode;

    /// <summary>+0xe4: the spring's target angle (radians), moved by the plasticity.</summary>
    [FieldOffset(0xe4)] public float TargetAngle;

    [FieldOffset(0xe8)] public float Frequency;
    [FieldOffset(0xec)] public float DampingRatio;

    /// <summary>+0xf0: the motor's target speed (radians per second).</summary>
    [FieldOffset(0xf0)] public float TargetSpeed;

    [FieldOffset(0xf4)] public float MaxTorque;

    /// <summary>+0xf8: the rate at which the target angle follows the hinge angle (FUN_180205e70).</summary>
    [FieldOffset(0xf8)] public float Plasticity;

    /// <summary>+0xfc: the angle the hinge may stray from the target before the plasticity moves it.</summary>
    [FieldOffset(0xfc)] public float PlasticThreshold;

    [FieldOffset(0x100)] public byte Limited;
    [FieldOffset(0x104)] public float Min;
    [FieldOffset(0x108)] public float Max;
    [FieldOffset(0x10c)] public float MotorImpulse;
    [FieldOffset(0x110)] public float LowImpulse;
    [FieldOffset(0x114)] public float HighImpulse;
    [FieldOffset(0x118)] public float HingeImpulse1;
    [FieldOffset(0x11c)] public float HingeImpulse2;
    [FieldOffset(0x120)] public Vec3 LinearImpulse;
}

/// <summary>
/// The revolute motor row (FUN_180213d10, 0x38 bytes): one angular axis with a
/// bias, a softness, the effective mass 1 / (a . (IA + IB) a + softness), the
/// accumulated impulse, its symmetric bound and the axis through each inertia.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x38)]
public struct MotorRow
{
    [FieldOffset(0x00)] public Vec3 Axis;
    [FieldOffset(0x0c)] public float Bias;
    [FieldOffset(0x10)] public float Softness;
    [FieldOffset(0x14)] public float Mass;
    [FieldOffset(0x18)] public float Impulse;
    [FieldOffset(0x1c)] public float MaxImpulse;
    [FieldOffset(0x20)] public Vec3 IA;
    [FieldOffset(0x2c)] public Vec3 IB;
}

/// <summary>
/// One of the two angular rows that keep the hinge axes aligned (FUN_180213870,
/// 0x2c bytes): the axis, the effective mass, the accumulated impulse and the
/// axis through each inertia.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x2c)]
public struct HingeRow
{
    [FieldOffset(0x00)] public Vec3 Axis;
    [FieldOffset(0x0c)] public float Mass;
    [FieldOffset(0x10)] public float Impulse;
    [FieldOffset(0x14)] public Vec3 IA;
    [FieldOffset(0x20)] public Vec3 IB;
}

/// <summary>The revolute joint's solver record (record type 5, 0x198 bytes after the header).</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x198)]
public struct RevoluteRecord
{
    [FieldOffset(0x000)] public MotorRow Motor;
    [FieldOffset(0x038)] public AngularRow Low;
    [FieldOffset(0x070)] public AngularRow High;
    [FieldOffset(0x0a8)] public HingeRow Hinge1;
    [FieldOffset(0x0d4)] public HingeRow Hinge2;
    [FieldOffset(0x100)] public PointBlock Linear;
}

/// <summary>CRnRevoluteJoint's solver functions, ported from vphysics2 with their float order.</summary>
public static class RevoluteJoint
{
    /// <summary>The record type the prepare writes: the velocity pass runs FUN_18021bcd0, the position pass FUN_180219190.</summary>
    public const int RecordType = 5;

    private const float Tiny = 1.17549435e-35f;

    /// <summary>
    /// The prepare (vtable +0x28, FUN_180216240): the joint frames in the
    /// world, the motor row (spring, friction or none), the two limit rows,
    /// the two hinge rows and the point block, then with
    /// <paramref name="warm"/> the accumulated impulses applied to both
    /// bodies. A spring with plasticity moves the target angle. Returns the
    /// record type.
    /// </summary>
    public static int Prepare(ref RevoluteJointState j, in JointHeader h, ref RevoluteRecord rec, Span<SolverBody> bodies, float dt, bool warm)
    {
        ref var a = ref bodies[h.BodyA];
        ref var b = ref bodies[h.BodyB];
        var ma = h.ScaleA * a.InvMass;
        var ia = JointRows.Scaled(h.ScaleA, a.WorldInvInertia);
        var mb = h.ScaleB * b.InvMass;
        var ib = JointRows.Scaled(h.ScaleB, b.WorldInvInertia);

        var qa = RnMath.Mul(a.Q, j.Frame1);
        var pa = JointRows.WorldPoint(a, new(a.Scale * j.Anchor1.X, a.Scale * j.Anchor1.Y, a.Scale * j.Anchor1.Z));
        var ra = new Vec3(pa.X - a.Position.X, pa.Y - a.Position.Y, pa.Z - a.Position.Z);
        var qb = RnMath.Mul(b.Q, j.Frame2);
        var pb = JointRows.WorldPoint(b, new(b.Scale * j.Anchor2.X, b.Scale * j.Anchor2.Y, b.Scale * j.Anchor2.Z));
        var rb = new Vec3(pb.X - b.Position.X, pb.Y - b.Position.Y, pb.Z - b.Position.Z);
        if ((qa.X * qb.X + qa.Y * qb.Y) + (qa.Z * qb.Z + qa.W * qb.W) < 0f)
            qb = new(0f - qb.X, 0f - qb.Y, 0f - qb.Z, 0f - qb.W);
        var rel = RnMath.Mul(JointRows.Conjugate(qa), qb);
        var t = TwistAxis(rel, RnMath.Rotate(qa, new(0f, 0f, 1f)), RnMath.Rotate(qb, new(0f, 0f, 1f)));
        var axis1 = HingeAxis(qa, First(rel));
        var axis2 = HingeAxis(qa, Second(rel));

        rec.Motor = default;
        if ((j.Mode & ~2) == 0)
        {
            if (j.Frequency > 0f)
            {
                var angle = CrtMath.Atan2(rel.Z, rel.W);
                var hinge = angle + angle;
                if (j.Plasticity > 0f)
                    j.TargetAngle = Plastic(j.TargetAngle, hinge, j.PlasticThreshold, j.Plasticity, dt);
                var k = Quadratic(ia, ib, t);
                var mass = k * k <= Tiny ? 0f : 1f / k;
                var omega = j.Frequency * 6.2831855f;
                var stiffness = omega * mass * omega;
                var softness = (1f / ((mass + mass) * j.DampingRatio * omega + stiffness * dt)) / dt;
                var bias = stiffness * softness * dt * (hinge - j.TargetAngle);
                var max = float.MaxValue;
                if (j.Mode == 2)
                {
                    bias -= j.TargetSpeed;
                    max = dt * j.MaxTorque;
                }
                Motor(ref rec.Motor, ia, ib, t, bias, softness, j.MotorImpulse, max);
            }
        }
        else if (j.Mode == 1 && j.MaxTorque > 0f)
            Motor(ref rec.Motor, ia, ib, t, -j.TargetSpeed, 0f, j.MotorImpulse, j.MaxTorque * dt);

        rec.Low = default;
        rec.High = default;
        if (j.Limited != 0)
        {
            var mid = (j.Min + j.Max) * 0.5f;
            var angle = CrtMath.Atan2(rel.Z, rel.W);
            var hinge = (angle + angle) - mid;
            if (-3.1415927f > hinge)
                hinge += 6.2831855f;
            else if (hinge > 3.1415927f)
                hinge -= 6.2831855f;
            var under = hinge - (j.Min - mid);
            Limit(ref rec.Low, ia, ib, (0f > under ? 0f : under) / dt, t, j.LowImpulse, 0f, float.MaxValue);
            var over = hinge - (j.Max - mid);
            Limit(ref rec.High, ia, ib, (over < 0f ? over : 0f) / dt, t, j.HighImpulse, -float.MaxValue, 0f);
        }

        rec.Hinge1 = default;
        rec.Hinge2 = default;
        if ((j.Flags & 8) == 0)
        {
            Hinge(ref rec.Hinge1, ia, ib, axis1, j.HingeImpulse1);
            Hinge(ref rec.Hinge2, ia, ib, axis2, j.HingeImpulse2);
        }

        rec.Linear = default;
        if ((j.Flags & 4) == 0)
            JointRows.Point(ref rec.Linear, ma, ia, ra, mb, ib, rb, j.LinearImpulse);

        if (warm)
        {
            var s = (j.MotorImpulse + j.LowImpulse) + j.HighImpulse;
            var lx = (t.X * s + j.HingeImpulse1 * axis1.X) + j.HingeImpulse2 * axis2.X;
            var ly = (s * t.Y + j.HingeImpulse1 * axis1.Y) + j.HingeImpulse2 * axis2.Y;
            var lz = (s * t.Z + j.HingeImpulse1 * axis1.Z) + j.HingeImpulse2 * axis2.Z;
            var p = j.LinearImpulse;
            a.V = new(a.V.X - p.X * ma, a.V.Y - p.Y * ma, a.V.Z - p.Z * ma);
            var ux = (p.Z * ra.Y - p.Y * ra.Z) + lx;
            var uy = (p.X * ra.Z - p.Z * ra.X) + ly;
            var uz = (p.Y * ra.X - p.X * ra.Y) + lz;
            a.W = new(a.W.X - ((uy * ia.M3 + ux * ia.M0) + uz * ia.M6),
                      a.W.Y - ((uy * ia.M4 + ux * ia.M1) + uz * ia.M7),
                      a.W.Z - ((uy * ia.M5 + ux * ia.M2) + uz * ia.M8));
            b.V = new(p.X * mb + b.V.X, p.Y * mb + b.V.Y, p.Z * mb + b.V.Z);
            var vx = (p.Z * rb.Y - p.Y * rb.Z) + lx;
            var vy = (p.X * rb.Z - p.Z * rb.X) + ly;
            var vz = (p.Y * rb.X - p.X * rb.Y) + lz;
            b.W = new(((vy * ib.M3 + vx * ib.M0) + vz * ib.M6) + b.W.X,
                      ((vy * ib.M4 + vx * ib.M1) + vz * ib.M7) + b.W.Y,
                      ((vy * ib.M5 + vx * ib.M2) + vz * ib.M8) + b.W.Z);
        }
        return RecordType;
    }

    /// <summary>
    /// One velocity iteration (record 5, FUN_18021bcd0): the motor row clamped
    /// to plus or minus its bound, the two limit rows, the two hinge rows and
    /// the point block, every part whether the prepare filled it or left it zero.
    /// </summary>
    public static void SolveVelocity(ref RevoluteRecord rec, in JointHeader h, Span<SolverBody> bodies)
    {
        ref var a = ref bodies[h.BodyA];
        ref var b = ref bodies[h.BodyB];
        var va = a.V;
        var vb = b.V;
        var wa = a.W;
        var wb = b.W;

        ref var m = ref rec.Motor;
        var old = m.Impulse;
        var lambda = -(((((wb.Y - wa.Y) * m.Axis.Y + (wb.X - wa.X) * m.Axis.X) + (wb.Z - wa.Z) * m.Axis.Z) + m.Bias) + old * m.Softness) * m.Mass + old;
        // comiss then minss: a NaN passes through.
        var clamped = -m.MaxImpulse > lambda ? -m.MaxImpulse : m.MaxImpulse < lambda ? m.MaxImpulse : lambda;
        m.Impulse = clamped;
        var d = clamped - old;
        wa = new(wa.X - d * m.IA.X, wa.Y - d * m.IA.Y, wa.Z - d * m.IA.Z);
        wb = new(d * m.IB.X + wb.X, d * m.IB.Y + wb.Y, d * m.IB.Z + wb.Z);

        JointRows.SolveLimit(ref rec.Low, ref wa, ref wb);
        JointRows.SolveLimit(ref rec.High, ref wa, ref wb);
        SolveHinge(ref rec.Hinge1, ref wa, ref wb);
        SolveHinge(ref rec.Hinge2, ref wa, ref wb);
        JointRows.SolvePoint(ref rec.Linear, ref a, ref b, va, vb, wa, wb);
    }

    /// <summary>A hinge row in a velocity iteration: an unbounded impulse change applied to both bodies.</summary>
    private static void SolveHinge(ref HingeRow r, ref Vec3 wa, ref Vec3 wb)
    {
        var lambda = -(((wb.Y - wa.Y) * r.Axis.Y + (wb.X - wa.X) * r.Axis.X) + (wb.Z - wa.Z) * r.Axis.Z) * r.Mass;
        r.Impulse = lambda + r.Impulse;
        wa = new(wa.X - lambda * r.IA.X, wa.Y - lambda * r.IA.Y, wa.Z - lambda * r.IA.Z);
        wb = new(lambda * r.IB.X + wb.X, lambda * r.IB.Y + wb.Y, lambda * r.IB.Z + wb.Z);
    }

    /// <summary>After the last velocity iteration (FUN_18021ac10): the record's impulses go back to the joint for the next step's warm start.</summary>
    public static void StoreImpulses(ref RevoluteJointState j, in RevoluteRecord rec)
    {
        j.MotorImpulse = rec.Motor.Impulse;
        j.LowImpulse = rec.Low.Impulse;
        j.HighImpulse = rec.High.Impulse;
        j.HingeImpulse1 = rec.Hinge1.Impulse;
        j.HingeImpulse2 = rec.Hinge2.Impulse;
        j.LinearImpulse = rec.Linear.Impulse;
    }

    /// <summary>The accumulated impulses times a factor (vtable +0x18, FUN_18021ac80; zero when a body turns dynamic).</summary>
    public static void ScaleImpulses(ref RevoluteJointState j, float s)
    {
        j.MotorImpulse = s * j.MotorImpulse;
        j.LowImpulse = s * j.LowImpulse;
        j.HingeImpulse1 = s * j.HingeImpulse1;
        j.HighImpulse = s * j.HighImpulse;
        j.HingeImpulse2 = j.HingeImpulse2 * s;
        j.LinearImpulse = new(j.LinearImpulse.X * s, j.LinearImpulse.Y * s, j.LinearImpulse.Z * s);
    }

    /// <summary>
    /// One position iteration (record 5, FUN_180219190), on the solver bodies'
    /// current poses: the hinge angle outside its limit turned back by 0.7 of
    /// the error, at most 0.1396 rad; the two hinge axes realigned (the
    /// relative rotation's x, then its y, to zero); then the anchors pulled
    /// together as the conical joint does.
    /// </summary>
    public static void SolvePosition(ref RevoluteJointState j, in JointHeader h, Span<SolverBody> bodies)
    {
        ref var a = ref bodies[h.BodyA];
        ref var b = ref bodies[h.BodyB];
        if (j.Limited != 0)
        {
            var ia = JointRows.Scaled(h.ScaleA, a.WorldInvInertia);
            var ib = JointRows.Scaled(h.ScaleB, b.WorldInvInertia);
            var qa = RnMath.Mul(a.Q, j.Frame1);
            var qb = RnMath.Mul(b.Q, j.Frame2);
            if ((qa.X * qb.X + qa.Y * qb.Y) + (qa.Z * qb.Z + qa.W * qb.W) < 0f)
                qb = new(0f - qb.X, 0f - qb.Y, 0f - qb.Z, 0f - qb.W);
            var rel = RnMath.Mul(JointRows.Conjugate(qa), qb);
            var t = TwistAxis(rel, RnMath.Rotate(qa, new(0f, 0f, 1f)), RnMath.Rotate(qb, new(0f, 0f, 1f)));
            var mid = (j.Min + j.Max) * 0.5f;
            var low = j.Min - mid;
            var high = j.Max - mid;
            var angle = CrtMath.Atan2(rel.Z, rel.W);
            var hinge = (angle + angle) - mid;
            if (-3.1415927f > hinge)
                hinge += 6.2831855f;
            else if (hinge > 3.1415927f)
                hinge -= 6.2831855f;
            // Inside the limits nothing is corrected.
            var c = 0f;
            if (low <= hinge)
            {
                if (high <= hinge)
                {
                    c = (hinge - high) - 0f;
                    if (c <= 0f)
                        c = 0f;
                    if (0.13962635f <= c)
                        c = 0.13962635f;
                }
            }
            else
            {
                c = (hinge - low) + 0f;
                if (c <= -0.13962635f)
                    c = -0.13962635f;
                if (0f <= c)
                    c = 0f;
            }
            var lambda = (c * -0.7f) / JointRows.Effective(ia, ib, t);
            var l = new Vec3(t.X * lambda, t.Y * lambda, t.Z * lambda);
            JointRows.RotateBody(ref a, ia, new(-l.X, -l.Y, -l.Z));
            JointRows.RotateBody(ref b, ib, l);
        }
        if ((j.Flags & 8) == 0)
        {
            AlignHinge(ref a, ref b, j, h, true);
            AlignHinge(ref a, ref b, j, h, false);
        }
        if ((j.Flags & 4) != 0)
            return;
        JointRows.PointPosition(ref a, ref b, h,
                                JointRows.WorldPoint(a, new(a.Scale * j.Anchor1.X, a.Scale * j.Anchor1.Y, a.Scale * j.Anchor1.Z)),
                                JointRows.WorldPoint(b, new(b.Scale * j.Anchor2.X, b.Scale * j.Anchor2.Y, b.Scale * j.Anchor2.Z)));
    }

    /// <summary>
    /// One hinge axis realigned in the position pass: the relative rotation's
    /// x (<paramref name="first"/>) or y component driven to zero along that
    /// hinge row's axis, on the current poses (no hemisphere flip here).
    /// </summary>
    private static void AlignHinge(ref SolverBody a, ref SolverBody b, in RevoluteJointState j, in JointHeader h, bool first)
    {
        var ia = JointRows.Scaled(h.ScaleA, a.WorldInvInertia);
        var ib = JointRows.Scaled(h.ScaleB, b.WorldInvInertia);
        var qa = RnMath.Mul(a.Q, j.Frame1);
        var qb = RnMath.Mul(b.Q, j.Frame2);
        var rel = RnMath.Mul(JointRows.Conjugate(qa), qb);
        var axis = HingeAxis(qa, first ? First(rel) : Second(rel));
        var lambda = -(first ? rel.X : rel.Y) / JointRows.Effective(ia, ib, axis);
        var l = new Vec3(axis.X * lambda, axis.Y * lambda, axis.Z * lambda);
        JointRows.RotateBody(ref a, ia, new(-l.X, -l.Y, -l.Z));
        JointRows.RotateBody(ref b, ib, l);
    }

    /// <summary>
    /// The hinge axis with the swing taken out, as the conical joint's twist
    /// axis: a + ((b x a) normalised x a) times sqrt((x^2 + y^2) / (z^2 + w^2)).
    /// </summary>
    private static Vec3 TwistAxis(Quat rel, Vec3 axisA, Vec3 axisB)
    {
        var s = MathF.Sqrt((rel.Y * rel.Y + rel.X * rel.X) / (rel.W * rel.W + rel.Z * rel.Z));
        var nx = axisB.Z * axisA.Y - axisB.Y * axisA.Z;
        var ny = axisB.X * axisA.Z - axisB.Z * axisA.X;
        var nz = axisB.Y * axisA.X - axisB.X * axisA.Y;
        var n2 = (ny * ny + nx * nx) + nz * nz;
        if (n2 > Tiny)
        {
            var inv = 1f / MathF.Sqrt(n2);
            nx = inv * nx;
            ny = inv * ny;
            nz = inv * nz;
        }
        else
            nx = ny = nz = 0f;
        return new((ny * axisA.Z - nz * axisA.Y) * s + axisA.X,
                   (nz * axisA.X - nx * axisA.Z) * s + axisA.Y,
                   (nx * axisA.Y - ny * axisA.X) * s + axisA.Z);
    }

    /// <summary>The vector the first hinge axis rotates, from the relative rotation: (w, z, -y) with vphysics2's zero terms.</summary>
    private static Vec3 First(Quat r)
        => new((r.Y * 0f - r.Z * 0f) + r.W, (r.Z - r.X * 0f) + r.W * 0f, (r.X * 0f - r.Y) + r.W * 0f);

    /// <summary>The vector the second hinge axis rotates: (-z, w, x) with vphysics2's zero terms.</summary>
    private static Vec3 Second(Quat r)
        => new((r.Y * 0f - r.Z) + r.W * 0f, (r.Z * 0f - r.X * 0f) + r.W, (r.X - r.Y * 0f) + r.W * 0f);

    /// <summary>v rotated by q, halved: t = 2 (v x q), ((q x t) + v + w t) / 2.</summary>
    private static Vec3 HingeAxis(Quat q, Vec3 v)
    {
        var cx = v.Z * q.Y - v.Y * q.Z;
        var cy = v.X * q.Z - v.Z * q.X;
        var cz = v.Y * q.X - v.X * q.Y;
        var tx = cx + cx;
        var ty = cy + cy;
        var tz = cz + cz;
        return new(((tz * q.Y - ty * q.Z) + (v.X + q.W * tx)) * 0.5f,
                   ((tx * q.Z - tz * q.X) + (v.Y + q.W * ty)) * 0.5f,
                   ((ty * q.X - tx * q.Y) + (v.Z + q.W * tz)) * 0.5f);
    }

    /// <summary>
    /// The plasticity (FUN_180205e70): the target angle moves towards the hinge
    /// angle by rate dt of the error past the threshold, never past it.
    /// </summary>
    private static float Plastic(float target, float current, float threshold, float rate, float dt)
    {
        var step = rate * dt;
        var d = current - target;
        var e = MathF.Abs(d) - threshold;
        e = e > 0f ? e : 0f;
        var moved = e * step;
        moved = moved < e ? moved : e;
        return (d < 0f ? -1f : 1f) * moved + target;
    }

    /// <summary>a . (IA + IB) a as the revolute rows sum it.</summary>
    private static float Quadratic(in Mat3 ia, in Mat3 ib, Vec3 a)
    {
        var x = ((ib.M0 + ia.M0) * a.X + (ib.M3 + ia.M3) * a.Y) + (ib.M6 + ia.M6) * a.Z;
        var y = ((ib.M4 + ia.M4) * a.Y + (ib.M1 + ia.M1) * a.X) + (ib.M7 + ia.M7) * a.Z;
        var z = ((ib.M5 + ia.M5) * a.Y + (ib.M2 + ia.M2) * a.X) + (ib.M8 + ia.M8) * a.Z;
        return (x * a.X + y * a.Y) + z * a.Z;
    }

    private static Vec3 Through(in Mat3 i, Vec3 a)
        => new((a.X * i.M0 + a.Y * i.M3) + a.Z * i.M6, (a.X * i.M1 + a.Y * i.M4) + a.Z * i.M7, (a.X * i.M2 + a.Y * i.M5) + a.Z * i.M8);

    /// <summary>The motor row (FUN_180213d10): mass 1 / (a . (IA + IB) a + softness), zero when its square is not above 1.2e-35.</summary>
    private static void Motor(ref MotorRow row, in Mat3 ia, in Mat3 ib, Vec3 a, float bias, float softness, float impulse, float max)
    {
        var k = Quadratic(ia, ib, a) + softness;
        row.Axis = a;
        row.Bias = bias;
        row.Mass = k * k <= Tiny ? 0f : 1f / k;
        row.Impulse = impulse;
        row.MaxImpulse = max;
        row.Softness = softness;
        row.IA = Through(ia, a);
        row.IB = Through(ib, a);
    }

    /// <summary>A limit row (FUN_180213aa0): as <see cref="JointRows.Limit"/> but zero mass when k squared is not above 1.2e-35.</summary>
    private static void Limit(ref AngularRow row, in Mat3 ia, in Mat3 ib, float bias, Vec3 a, float impulse, float min, float max)
    {
        var k = Quadratic(ia, ib, a);
        row.Bias = bias;
        row.Axis = a;
        row.Impulse = impulse;
        row.Mass = k * k <= Tiny ? 0f : 1f / k;
        row.Max = max;
        row.Min = min;
        row.IA = Through(ia, a);
        row.IB = Through(ib, a);
    }

    /// <summary>A hinge row (FUN_180213870).</summary>
    private static void Hinge(ref HingeRow row, in Mat3 ia, in Mat3 ib, Vec3 a, float impulse)
    {
        var k = Quadratic(ia, ib, a);
        row.Axis = a;
        row.Impulse = impulse;
        row.Mass = k * k <= Tiny ? 0f : 1f / k;
        row.IA = Through(ia, a);
        row.IB = Through(ib, a);
    }
}
