using System.Runtime.InteropServices;

namespace Source2.Compiler.Simulation;

/// <summary>
/// CRnConicalJoint (type 10, 0x150 bytes, vtable 0x1803e0a70) as vphysics2
/// lays it out: the CRnJoint header, the frames, the motor, the twist and
/// swing limits and the impulses the solver accumulates. A ragdoll joint of a
/// model (VPhysXJoint_t type 4) becomes one (FUN_18005d370).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x150)]
public struct ConicalJointState
{
    /// <summary>+0x08: index in the world's joint list.</summary>
    [FieldOffset(0x08)] public int WorldIndex;

    /// <summary>+0x0c: the joint type (10).</summary>
    [FieldOffset(0x0c)] public int Type;

    /// <summary>+0x28: bit 0 disabled, bit 1 collision between the bodies, bit 2 no linear part, bit 3 no angular part.</summary>
    [FieldOffset(0x28)] public ushort Flags;

    /// <summary>+0x2a: added to the solve priority difference.</summary>
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

    /// <summary>+0xa8: the record size estimate (0x1e8).</summary>
    [FieldOffset(0xa8)] public int Size;

    [FieldOffset(0xe0)] public Quat Target;

    /// <summary>+0xf0: 0 spring to the target, 1 friction, 2 velocity motor, 3 none.</summary>
    [FieldOffset(0xf0)] public int Mode;

    [FieldOffset(0xf4)] public float Frequency;
    [FieldOffset(0xf8)] public float DampingRatio;
    [FieldOffset(0xfc)] public Vec3 TargetVelocity;
    [FieldOffset(0x108)] public float MaxTorque;

    /// <summary>+0x10c: the rate at which the target follows the joint's rotation (FUN_180205bd0).</summary>
    [FieldOffset(0x10c)] public float Plasticity;

    /// <summary>+0x110: the angle (per axis) the joint may stray from the target before the plasticity moves it.</summary>
    [FieldOffset(0x110)] public float PlasticThreshold;

    [FieldOffset(0x114)] public byte TwistLimited;
    [FieldOffset(0x118)] public float TwistMin;
    [FieldOffset(0x11c)] public float TwistMax;
    [FieldOffset(0x120)] public byte SwingLimited;
    [FieldOffset(0x124)] public float SwingMax;
    [FieldOffset(0x128)] public Vec3 MotorImpulse;
    [FieldOffset(0x134)] public float TwistLowImpulse;
    [FieldOffset(0x138)] public float TwistHighImpulse;
    [FieldOffset(0x13c)] public float SwingImpulse;
    [FieldOffset(0x140)] public Vec3 LinearImpulse;
}

/// <summary>The conical joint's solver record (record type 0x0b, 0x1d4 bytes after the header).</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x1d4)]
public struct ConicalRecord
{
    [FieldOffset(0x000)] public AngularBlock Motor;
    [FieldOffset(0x094)] public AngularRow TwistLow;
    [FieldOffset(0x0cc)] public AngularRow TwistHigh;
    [FieldOffset(0x104)] public AngularRow Swing;
    [FieldOffset(0x13c)] public PointBlock Linear;
}

/// <summary>CRnConicalJoint's solver functions, ported from vphysics2 with their float order.</summary>
public static class ConicalJoint
{
    /// <summary>The record type the prepare writes: the velocity pass runs FUN_18022c860, the position pass FUN_18022ab10.</summary>
    public const int RecordType = 0x0b;

    /// <summary>
    /// The prepare (vtable +0x28, FUN_180229870): the joint frames in the
    /// world, the motor or friction block, the twist rows, the swing row and
    /// the point block, then with <paramref name="warm"/> the accumulated
    /// impulses applied to both bodies. Returns the record type.
    /// </summary>
    public static int Prepare(ref ConicalJointState j, in JointHeader h, ref ConicalRecord rec, Span<SolverBody> bodies, float dt, bool warm)
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
        var axisA = RnMath.Rotate(qa, new(0f, 0f, 1f));
        var axisB = RnMath.Rotate(qb, new(0f, 0f, 1f));

        rec.Motor = default;
        if ((j.Mode & ~2) == 0)
        {
            if (j.Frequency > 0f)
            {
                if (j.Plasticity > 0f)
                    j.Target = Plastic(j.Target, rel, j.PlasticThreshold, j.Plasticity, dt);
                Spring(ref rec.Motor, j, ia, ib, qa, rel, dt);
            }
        }
        else if (j.Mode == 1 && j.MaxTorque > 0f)
        {
            JointRows.Angular(ref rec.Motor, ia, ib, new(-j.TargetVelocity.X, -j.TargetVelocity.Y, -j.TargetVelocity.Z),
                              default, j.MotorImpulse, j.MaxTorque * dt);
        }

        rec.TwistLow = default;
        rec.TwistHigh = default;
        if (j.TwistLimited != 0)
        {
            var s = MathF.Sqrt((rel.Y * rel.Y + rel.X * rel.X) / (rel.W * rel.W + rel.Z * rel.Z));
            // a x b, normalised.
            var nx = axisB.Z * axisA.Y - axisB.Y * axisA.Z;
            var ny = axisB.X * axisA.Z - axisB.Z * axisA.X;
            var nz = axisB.Y * axisA.X - axisB.X * axisA.Y;
            var n2 = (ny * ny + nx * nx) + nz * nz;
            if (n2 > 1.17549435e-35f)
            {
                var inv = 1f / MathF.Sqrt(n2);
                nx *= inv;
                ny *= inv;
                nz *= inv;
            }
            else
                nx = ny = nz = 0f;
            var t = new Vec3((axisA.Z * ny - axisA.Y * nz) * s + axisA.X,
                             (axisA.X * nz - nx * axisA.Z) * s + axisA.Y,
                             (nx * axisA.Y - axisA.X * ny) * s + axisA.Z);
            var mid = (j.TwistMin + j.TwistMax) * 0.5f;
            var high = j.TwistMax - mid;
            var low = j.TwistMin - mid;
            var angle = CrtMath.Atan2(rel.Z, rel.W);
            var twist = (angle + angle) - mid;
            if (-3.1415927f > twist)
                twist += 6.2831855f;
            else if (twist > 3.1415927f)
                twist -= 6.2831855f;
            var under = twist - low;
            JointRows.Limit(ref rec.TwistLow, ia, ib, (0f > under ? 0f : under) / dt, t, j.TwistLowImpulse, 0f, float.MaxValue);
            var over = twist - high;
            JointRows.Limit(ref rec.TwistHigh, ia, ib, (over < 0f ? over : 0f) / dt, t, j.TwistHighImpulse, -float.MaxValue, 0f);
        }

        rec.Swing = default;
        if (j.SwingLimited != 0)
        {
            var swing = SwingAngle(rel);
            var nx = axisB.Z * axisA.Y - axisB.Y * axisA.Z;
            var ny = axisB.X * axisA.Z - axisB.Z * axisA.X;
            var nz = axisB.Y * axisA.X - axisB.X * axisA.Y;
            var n2 = (ny * ny + nx * nx) + nz * nz;
            Vec3 axis = default;
            if (n2 > 1.17549435e-35f)
            {
                var inv = 1f / MathF.Sqrt(n2);
                axis = new(nx * inv, ny * inv, nz * inv);
            }
            var over = swing - j.SwingMax;
            JointRows.Limit(ref rec.Swing, ia, ib, (over < 0f ? over : 0f) / dt, axis, j.SwingImpulse, -float.MaxValue, 0f);
        }

        rec.Linear = default;
        if ((j.Flags & 4) == 0)
            JointRows.Point(ref rec.Linear, ma, ia, ra, mb, ib, rb, j.LinearImpulse);

        if (warm)
        {
            var lx = ((j.TwistLowImpulse * rec.TwistLow.Axis.X + j.MotorImpulse.X) + j.TwistHighImpulse * rec.TwistHigh.Axis.X) + j.SwingImpulse * rec.Swing.Axis.X;
            var ly = ((j.TwistLowImpulse * rec.TwistLow.Axis.Y + j.MotorImpulse.Y) + j.TwistHighImpulse * rec.TwistHigh.Axis.Y) + j.SwingImpulse * rec.Swing.Axis.Y;
            var lz = ((j.TwistLowImpulse * rec.TwistLow.Axis.Z + j.MotorImpulse.Z) + j.TwistHighImpulse * rec.TwistHigh.Axis.Z) + j.SwingImpulse * rec.Swing.Axis.Z;
            var p = j.LinearImpulse;
            a.V = new(a.V.X - p.X * ma, a.V.Y - p.Y * ma, a.V.Z - p.Z * ma);
            var ux = (p.Z * ra.Y - p.Y * ra.Z) + lx;
            var uy = (p.X * ra.Z - ra.X * p.Z) + ly;
            var uz = (ra.X * p.Y - p.X * ra.Y) + lz;
            a.W = new(a.W.X - ((uy * ia.M3 + ux * ia.M0) + uz * ia.M6),
                      a.W.Y - ((uy * ia.M4 + ux * ia.M1) + uz * ia.M7),
                      a.W.Z - ((uy * ia.M5 + ux * ia.M2) + uz * ia.M8));
            b.V = new(p.X * mb + b.V.X, p.Y * mb + b.V.Y, p.Z * mb + b.V.Z);
            var vx = (p.Z * rb.Y - p.Y * rb.Z) + lx;
            var vy = (p.X * rb.Z - p.Z * rb.X) + ly;
            var vz = (p.Y * rb.X - p.X * rb.Y) + lz;
            b.W = new(((ib.M3 * vy + ib.M0 * vx) + ib.M6 * vz) + b.W.X,
                      ((ib.M4 * vy + ib.M1 * vx) + ib.M7 * vz) + b.W.Y,
                      ((ib.M5 * vy + ib.M2 * vx) + ib.M8 * vz) + b.W.Z);
        }
        return RecordType;
    }

    /// <summary>
    /// The spring motor block (modes 0 and 2 of FUN_180229870): the rotation
    /// from the joint to its target, in the world, held by one spring per
    /// axis whose mass is that axis's diagonal entry of (IB + IA)^-1. Mode 2
    /// also drives the target velocity, at most max torque dt.
    /// </summary>
    private static void Spring(ref AngularBlock motor, in ConicalJointState j, in Mat3 ia, in Mat3 ib, Quat qa, Quat rel, float dt)
    {
        var e = RnMath.Mul(j.Target, JointRows.Conjugate(rel));
        var sign = float.CopySign(2f, e.W);
        var err = RnMath.Rotate(qa, new(-(sign * e.X), -(sign * e.Y), -(sign * e.Z)));
        float k0 = ib.M0 + ia.M0, k1 = ib.M1 + ia.M1, k2 = ib.M2 + ia.M2, k3 = ib.M3 + ia.M3, k4 = ib.M4 + ia.M4;
        float k5 = ib.M5 + ia.M5, k6 = ib.M6 + ia.M6, k7 = ib.M7 + ia.M7, k8 = ib.M8 + ia.M8;
        var c0 = k4 * k8 - k7 * k5;
        var det = ((k6 * k5 - k3 * k8) * k1 + k0 * c0) + (k7 * k3 - k4 * k6) * k2;
        // The diagonal of the inverse; DAT_18055e880's zeros when singular.
        float m0 = 0f, m4 = 0f, m8 = 0f;
        if (!(1.17549435e-35f > MathF.Abs(det)))
        {
            var inv = 1f / det;
            m0 = inv * c0;
            m4 = (k8 * k0 - k2 * k6) * inv;
            m8 = (k4 * k0 - k3 * k1) * inv;
        }
        var omega = j.Frequency * 6.2831855f;
        var (s0, b0) = Axis(m0, omega, j.DampingRatio, dt, err.X);
        var (s1, b1) = Axis(m4, omega, j.DampingRatio, dt, err.Y);
        var (s2, b2) = Axis(m8, omega, j.DampingRatio, dt, err.Z);
        var max = float.MaxValue;
        if (j.Mode == 2)
        {
            b0 -= j.TargetVelocity.X;
            b1 -= j.TargetVelocity.Y;
            b2 -= j.TargetVelocity.Z;
            max = dt * j.MaxTorque;
        }
        JointRows.Angular(ref motor, ia, ib, new(b0, b1, b2), new(s0, s1, s2), j.MotorImpulse, max);
    }

    /// <summary>One axis of the spring: softness 1 / (2 m zeta omega + k dt) / dt and bias k softness dt error, k = omega m omega.</summary>
    private static (float Softness, float Bias) Axis(float m, float omega, float damping, float dt, float error)
    {
        var k = omega * m * omega;
        var softness = (1f / ((m + m) * damping * omega + k * dt)) / dt;
        return (softness, k * softness * dt * error);
    }

    /// <summary>
    /// The plasticity (FUN_180205bd0): the rotation from the target to the
    /// joint's, as a vector (twice the imaginary part on the positive-w side),
    /// past the threshold per axis, at most rate dt of that, turned back into
    /// a quaternion (sincosf, or the series below 0.0186) that moves the
    /// target; renormalised.
    /// </summary>
    private static Quat Plastic(Quat target, Quat rel, float threshold, float rate, float dt)
    {
        var d = RnMath.Mul(rel, JointRows.Conjugate(target));
        var sign = float.CopySign(2f, d.W);
        var v = new Vec3(d.X * sign, d.Y * sign, d.Z * sign);
        var step = rate * dt;
        var ex = Max0(MathF.Abs(v.X) - threshold);
        var ey = Max0(MathF.Abs(v.Y) - threshold);
        var ez = Max0(MathF.Abs(v.Z) - threshold);
        float mx = ex * step, my = ey * step, mz = ez * step;
        mx = mx < ex ? mx : ex;
        my = my < ey ? my : ey;
        mz = mz < ez ? mz : ez;
        // comiss then jb: negative (or NaN) takes -1.
        var sx = (v.X >= 0f ? 1f : -1f) * mx;
        var sy = (v.Y >= 0f ? 1f : -1f) * my;
        var sz = (v.Z >= 0f ? 1f : -1f) * mz;
        var length = MathF.Sqrt((sy * sy + sz * sz) + sx * sx);
        Quat delta;
        if (0.01858136f > length)
        {
            var f = 0.5f - (length * length) / 48f;
            delta = new(sx * f, sy * f, sz * f, CrtMath.Cos(length * 0.5f));
        }
        else
        {
            var inv = 1f / length;
            float nx = sx * inv, ny = sy * inv, nz = sz * inv;
            if ((nx == 0f && ny == 0f && nz == 0f) || length == 0f)
                delta = Quat.Identity;
            else
            {
                var half = length * 0.5f;
                var sin = CrtMath.Sin(half);
                delta = new(sin * nx, sin * ny, sin * nz, CrtMath.Cos(half));
            }
        }
        return RnMath.Normalize(RnMath.Mul(delta, target));
    }

    /// <summary>
    /// One velocity iteration (record 0x0b, FUN_18022c860): the motor block,
    /// the two twist rows, the swing row and the point block, every part
    /// whether the prepare filled it or left it zero.
    /// </summary>
    public static void SolveVelocity(ref ConicalRecord rec, in JointHeader h, Span<SolverBody> bodies)
    {
        ref var a = ref bodies[h.BodyA];
        ref var b = ref bodies[h.BodyB];
        var va = a.V;
        var vb = b.V;
        var wa = a.W;
        var wb = b.W;

        ref var m = ref rec.Motor;
        var old = m.Impulse;
        var ex = -(((wb.X - wa.X) + m.Bias.X) + old.X * m.Softness.X);
        var ey = -(((wb.Y - wa.Y) + m.Bias.Y) + old.Y * m.Softness.Y);
        var ez = -(((wb.Z - wa.Z) + m.Bias.Z) + old.Z * m.Softness.Z);
        var ix = ((ex * m.Mass.M0 + ey * m.Mass.M3) + ez * m.Mass.M6) + old.X;
        var iy = ((m.Mass.M4 * ey + m.Mass.M1 * ex) + m.Mass.M7 * ez) + old.Y;
        var iz = ((m.Mass.M5 * ey + m.Mass.M2 * ex) + m.Mass.M8 * ez) + old.Z;
        var length2 = (iy * iy + ix * ix) + iz * iz;
        if (m.MaxImpulse * m.MaxImpulse < length2)
        {
            var s = m.MaxImpulse / MathF.Sqrt(length2);
            iy = s * iy;
            iz = s * iz;
            ix = s * ix;
        }
        m.Impulse = new(ix, iy, iz);
        var dy = iy - old.Y;
        var dx = ix - old.X;
        var dz = iz - old.Z;
        wa = new(wa.X - ((dx * m.IA.M0 + dy * m.IA.M3) + dz * m.IA.M6),
                 wa.Y - ((dx * m.IA.M1 + dy * m.IA.M4) + dz * m.IA.M7),
                 wa.Z - ((dx * m.IA.M2 + dy * m.IA.M5) + dz * m.IA.M8));
        wb = new(((dx * m.IB.M0 + dy * m.IB.M3) + dz * m.IB.M6) + wb.X,
                 ((dx * m.IB.M1 + dy * m.IB.M4) + dz * m.IB.M7) + wb.Y,
                 ((dx * m.IB.M2 + dy * m.IB.M5) + dz * m.IB.M8) + wb.Z);

        JointRows.SolveLimit(ref rec.TwistLow, ref wa, ref wb);
        JointRows.SolveLimit(ref rec.TwistHigh, ref wa, ref wb);
        JointRows.SolveLimit(ref rec.Swing, ref wa, ref wb);

        JointRows.SolvePoint(ref rec.Linear, ref a, ref b, va, vb, wa, wb);
    }

    /// <summary>After the last velocity iteration (FUN_18022c390): the record's impulses go back to the joint for the next step's warm start.</summary>
    public static void StoreImpulses(ref ConicalJointState j, in ConicalRecord rec)
    {
        j.MotorImpulse = rec.Motor.Impulse;
        j.TwistLowImpulse = rec.TwistLow.Impulse;
        j.TwistHighImpulse = rec.TwistHigh.Impulse;
        j.SwingImpulse = rec.Swing.Impulse;
        j.LinearImpulse = rec.Linear.Impulse;
    }

    /// <summary>
    /// One position iteration (record 0x0b, FUN_18022ab10), on the solver
    /// bodies' current poses: the twist and swing errors turned back by 0.7
    /// of themselves, at most 0.1396 rad, through the angular part of a
    /// position impulse; then the anchors pulled together, at most 8 units,
    /// through a full position impulse on each body.
    /// </summary>
    public static void SolvePosition(ref ConicalJointState j, in JointHeader h, Span<SolverBody> bodies)
    {
        ref var a = ref bodies[h.BodyA];
        ref var b = ref bodies[h.BodyB];
        if (j.TwistLimited != 0)
        {
            var ia = JointRows.Scaled(h.ScaleA, a.WorldInvInertia);
            var ib = JointRows.Scaled(h.ScaleB, b.WorldInvInertia);
            var (rel, axisA, axisB) = Frames(j, a, b);
            var s = MathF.Sqrt((rel.Y * rel.Y + rel.X * rel.X) / (rel.W * rel.W + rel.Z * rel.Z));
            var n = Normal(axisA, axisB);
            var t = new Vec3((n.Y * axisA.Z - n.Z * axisA.Y) * s + axisA.X,
                             (n.Z * axisA.X - axisA.Z * n.X) * s + axisA.Y,
                             (axisA.Y * n.X - n.Y * axisA.X) * s + axisA.Z);
            var mid = (j.TwistMin + j.TwistMax) * 0.5f;
            var low = j.TwistMin - mid;
            var high = j.TwistMax - mid;
            var angle = CrtMath.Atan2(rel.Z, rel.W);
            var twist = (angle + angle) - mid;
            if (-3.1415927f > twist)
                twist += 6.2831855f;
            else if (twist > 3.1415927f)
                twist -= 6.2831855f;
            // Inside the limits nothing is corrected.
            var c = low > twist ? Min(Max((twist - low) + 0f, -0.13962635f), 0f)
                  : !(twist < high) ? Min(Max((twist - high) - 0f, 0f), 0.13962635f)
                  : 0f;
            if (c != 0f)
            {
                var k = JointRows.Effective(ia, ib, t);
                var lambda = (c * -0.7f) / k;
                var l = new Vec3(t.X * lambda, t.Y * lambda, t.Z * lambda);
                JointRows.RotateBody(ref a, ia, new(-l.X, -l.Y, -l.Z));
                JointRows.RotateBody(ref b, ib, l);
            }
        }
        if (j.SwingLimited != 0)
        {
            var ia = JointRows.Scaled(h.ScaleA, a.WorldInvInertia);
            var ib = JointRows.Scaled(h.ScaleB, b.WorldInvInertia);
            var (rel, axisA, axisB) = Frames(j, a, b);
            var swing = SwingAngle(rel);
            var n = Normal(axisA, axisB);
            if (swing > j.SwingMax && (n.X != 0f || n.Y != 0f || n.Z != 0f))
            {
                var c = Min(Max(((swing - j.SwingMax) - 0f) * 0.7f, 0f), 0.13962635f);
                var k = JointRows.Effective(ia, ib, n);
                var lambda = -c / k;
                var l = new Vec3(n.X * lambda, n.Y * lambda, n.Z * lambda);
                JointRows.RotateBody(ref a, ia, new(-l.X, -l.Y, -l.Z));
                JointRows.RotateBody(ref b, ib, l);
            }
        }
        if ((j.Flags & 4) != 0)
            return;
        JointRows.PointPosition(ref a, ref b, h,
                                JointRows.WorldPoint(a, new(a.Scale * j.Anchor1.X, a.Scale * j.Anchor1.Y, a.Scale * j.Anchor1.Z)),
                                JointRows.WorldPoint(b, new(b.Scale * j.Anchor2.X, b.Scale * j.Anchor2.Y, b.Scale * j.Anchor2.Z)));
    }

    private static float Max(float a, float b) => a > b ? a : b;

    /// <summary>maxps against zero: a NaN becomes zero.</summary>
    private static float Max0(float a) => a > 0f ? a : 0f;

    private static float Min(float a, float b) => a < b ? a : b;

    /// <summary>The joint frames in the world from the bodies' poses: the relative rotation and both twist axes.</summary>
    private static (Quat Rel, Vec3 AxisA, Vec3 AxisB) Frames(in ConicalJointState j, in SolverBody a, in SolverBody b)
    {
        var qa = RnMath.Mul(a.Q, j.Frame1);
        var qb = RnMath.Mul(b.Q, j.Frame2);
        if ((qa.X * qb.X + qa.Y * qb.Y) + (qa.Z * qb.Z + qa.W * qb.W) < 0f)
            qb = new(0f - qb.X, 0f - qb.Y, 0f - qb.Z, 0f - qb.W);
        return (RnMath.Mul(JointRows.Conjugate(qa), qb), RnMath.Rotate(qa, new(0f, 0f, 1f)), RnMath.Rotate(qb, new(0f, 0f, 1f)));
    }

    /// <summary>a x b normalised, or zero when its squared length is not above 1.2e-35.</summary>
    private static Vec3 Normal(Vec3 a, Vec3 b)
    {
        var nx = a.Y * b.Z - a.Z * b.Y;
        var ny = a.Z * b.X - a.X * b.Z;
        var nz = a.X * b.Y - a.Y * b.X;
        var n2 = (ny * ny + nx * nx) + nz * nz;
        if (!(n2 > 1.17549435e-35f))
            return default;
        var inv = 1f / MathF.Sqrt(n2);
        return new(nx * inv, ny * inv, nz * inv);
    }

    /// <summary>The swing angle of a relative rotation (FUN_1802292e0): 2 atan2(|xy|, |zw|).</summary>
    public static float SwingAngle(Quat rel)
    {
        var zw = MathF.Sqrt(rel.W * rel.W + rel.Z * rel.Z);
        var xy = MathF.Sqrt(rel.Y * rel.Y + rel.X * rel.X);
        var angle = CrtMath.Atan2(xy, zw);
        return angle + angle;
    }
}
