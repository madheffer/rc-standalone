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
    [FieldOffset(0x10c)] public float TargetSpeed;
    [FieldOffset(0x110)] public float TargetStep;
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

    private static Mat3 Scaled(float s, in Mat3 m) => new()
    {
        M0 = s * m.M0, M1 = s * m.M1, M2 = s * m.M2, M3 = s * m.M3, M4 = s * m.M4,
        M5 = s * m.M5, M6 = s * m.M6, M7 = s * m.M7, M8 = s * m.M8,
    };

    private static Quat Conjugate(Quat q) => new(-q.X, -q.Y, -q.Z, q.W);

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
        var ia = Scaled(h.ScaleA, a.WorldInvInertia);
        var mb = h.ScaleB * b.InvMass;
        var ib = Scaled(h.ScaleB, b.WorldInvInertia);

        var qa = RnMath.Mul(a.Q, j.Frame1);
        var pa = JointRows.WorldPoint(a, new(a.Scale * j.Anchor1.X, a.Scale * j.Anchor1.Y, a.Scale * j.Anchor1.Z));
        var ra = new Vec3(pa.X - a.Position.X, pa.Y - a.Position.Y, pa.Z - a.Position.Z);
        var qb = RnMath.Mul(b.Q, j.Frame2);
        var pb = JointRows.WorldPoint(b, new(b.Scale * j.Anchor2.X, b.Scale * j.Anchor2.Y, b.Scale * j.Anchor2.Z));
        var rb = new Vec3(pb.X - b.Position.X, pb.Y - b.Position.Y, pb.Z - b.Position.Z);
        if ((qa.X * qb.X + qa.Y * qb.Y) + (qa.Z * qb.Z + qa.W * qb.W) < 0f)
            qb = new(0f - qb.X, 0f - qb.Y, 0f - qb.Z, 0f - qb.W);
        var rel = RnMath.Mul(Conjugate(qa), qb);
        var axisA = RnMath.Rotate(qa, new(0f, 0f, 1f));
        var axisB = RnMath.Rotate(qb, new(0f, 0f, 1f));

        rec.Motor = default;
        if ((j.Mode & ~2) == 0)
        {
            if (j.Frequency > 0f)
                throw new NotSupportedException("the conical joint's spring and velocity motor (modes 0 and 2) are not ported yet");
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

        SolveLimit(ref rec.TwistLow, ref wa, ref wb);
        SolveLimit(ref rec.TwistHigh, ref wa, ref wb);
        SolveLimit(ref rec.Swing, ref wa, ref wb);

        ref var p = ref rec.Linear;
        var cx = -(((wb.Y * p.RB.Z - p.RB.Y * wb.Z) + vb.X) - ((wa.Y * p.RA.Z - wa.Z * p.RA.Y) + va.X));
        var cy = -(((p.RB.X * wb.Z - wb.X * p.RB.Z) + vb.Y) - ((wa.Z * p.RA.X - wa.X * p.RA.Z) + va.Y));
        var cz = -(((wb.X * p.RB.Y - wb.Y * p.RB.X) + vb.Z) - ((wa.X * p.RA.Y - wa.Y * p.RA.X) + va.Z));
        var lx = (cy * p.Mass.M3 + cx * p.Mass.M0) + cz * p.Mass.M6;
        var ly = (cy * p.Mass.M4 + cx * p.Mass.M1) + cz * p.Mass.M7;
        var lz = (cy * p.Mass.M5 + cx * p.Mass.M2) + cz * p.Mass.M8;
        p.Impulse = new(lx + p.Impulse.X, ly + p.Impulse.Y, lz + p.Impulse.Z);
        var ax = lz * p.RA.Y - ly * p.RA.Z;
        var ay = lx * p.RA.Z - lz * p.RA.X;
        var az = ly * p.RA.X - lx * p.RA.Y;
        var bx = lz * p.RB.Y - ly * p.RB.Z;
        var by = lx * p.RB.Z - lz * p.RB.X;
        var bz = ly * p.RB.X - lx * p.RB.Y;
        a.V = new(va.X - lx * p.MA, va.Y - ly * p.MA, va.Z - lz * p.MA);
        a.W = new(wa.X - ((ay * p.IA.M3 + ax * p.IA.M0) + az * p.IA.M6),
                  wa.Y - ((ax * p.IA.M1 + ay * p.IA.M4) + az * p.IA.M7),
                  wa.Z - ((ax * p.IA.M2 + ay * p.IA.M5) + p.IA.M8 * az));
        b.V = new(lx * p.MB + vb.X, vb.Y + ly * p.MB, vb.Z + lz * p.MB);
        b.W = new(((by * p.IB.M3 + bx * p.IB.M0) + bz * p.IB.M6) + wb.X,
                  ((by * p.IB.M4 + bx * p.IB.M1) + bz * p.IB.M7) + wb.Y,
                  ((by * p.IB.M5 + bx * p.IB.M2) + bz * p.IB.M8) + wb.Z);
    }

    /// <summary>One limited angular axis in a velocity iteration: the clamped impulse change applied to both bodies.</summary>
    private static void SolveLimit(ref AngularRow r, ref Vec3 wa, ref Vec3 wb)
    {
        var old = r.Impulse;
        var lambda = -((((wb.Y - wa.Y) * r.Axis.Y + (wb.X - wa.X) * r.Axis.X) + (wb.Z - wa.Z) * r.Axis.Z) + r.Bias) * r.Mass + old;
        if (lambda <= r.Min)
            lambda = r.Min;
        if (r.Max <= lambda)
            lambda = r.Max;
        r.Impulse = lambda;
        var d = lambda - old;
        wa = new(wa.X - d * r.IA.X, wa.Y - d * r.IA.Y, wa.Z - d * r.IA.Z);
        wb = new(d * r.IB.X + wb.X, d * r.IB.Y + wb.Y, d * r.IB.Z + wb.Z);
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
            var ia = Scaled(h.ScaleA, a.WorldInvInertia);
            var ib = Scaled(h.ScaleB, b.WorldInvInertia);
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
                var k = Effective(ia, ib, t);
                var lambda = (c * -0.7f) / k;
                var l = new Vec3(t.X * lambda, t.Y * lambda, t.Z * lambda);
                Rotate(ref a, ia, new(-l.X, -l.Y, -l.Z));
                Rotate(ref b, ib, l);
            }
        }
        if (j.SwingLimited != 0)
        {
            var ia = Scaled(h.ScaleA, a.WorldInvInertia);
            var ib = Scaled(h.ScaleB, b.WorldInvInertia);
            var (rel, axisA, axisB) = Frames(j, a, b);
            var swing = SwingAngle(rel);
            var n = Normal(axisA, axisB);
            if (swing > j.SwingMax && (n.X != 0f || n.Y != 0f || n.Z != 0f))
            {
                var c = Min(Max(((swing - j.SwingMax) - 0f) * 0.7f, 0f), 0.13962635f);
                var k = Effective(ia, ib, n);
                var lambda = -c / k;
                var l = new Vec3(n.X * lambda, n.Y * lambda, n.Z * lambda);
                Rotate(ref a, ia, new(-l.X, -l.Y, -l.Z));
                Rotate(ref b, ib, l);
            }
        }
        if ((j.Flags & 4) != 0)
            return;
        {
            var ia = Scaled(h.ScaleA, a.WorldInvInertia);
            var ib = Scaled(h.ScaleB, b.WorldInvInertia);
            var pa = JointRows.WorldPoint(a, new(a.Scale * j.Anchor1.X, a.Scale * j.Anchor1.Y, a.Scale * j.Anchor1.Z));
            var pb = JointRows.WorldPoint(b, new(b.Scale * j.Anchor2.X, b.Scale * j.Anchor2.Y, b.Scale * j.Anchor2.Z));
            // The 3x3 effective mass, its inverse and the impulse, in the
            // order the compiled function has them (traced by
            // tools/settle/lanesym.py over 18022b6e0-18022c337).
        var t1 = (h.ScaleA * a.InvMass);
        var bm80 = t1;
        var t2 = (h.ScaleB * b.InvMass);
        var bm7c = t2;
        var t3 = (pa.Y - a.Position.Y);
        var t4 = (pa.X - a.Position.X);
        var t5 = (pa.Z - a.Position.Z);
        var bbc = t3;
        var bb8 = t4;
        var bc0 = t5;
        var b138 = 0f;
        var b13c = t5;
        var b14c = t4;
        var b150 = t3;
        var b154 = -(t4);
        var b460 = -(t3);
        var b450 = -(t5);
        var s3c = -(t4);
        var t6 = (pb.X - b.Position.X);
        var t7 = (pb.Y - b.Position.Y);
        var t8 = (pb.Z - b.Position.Z);
        var s34 = t6;
        var bd8 = t6;
        var s64 = t7;
        var bdc = t7;
        var s38 = t8;
        var be0 = t8;
        var b468 = -(t8);
        var b458 = -(t7);
        var t9 = (pb.X - pa.X);
        var s40 = -(t6);
        var t10 = (pb.Y - pa.Y);
        var t11 = (pb.Z - pa.Z);
        var t12 = (t9 * t9);
        var t13 = (t10 * t10);
        var t14 = (t11 * t11);
        var t15 = (t13 + t12);
        var t16 = (t15 + t14);
        float dx = t9, dy = t10, dz = t11;
        if (t16 > 64f)
        {
            var clamp = 8f / MathF.Sqrt(t16);
            dx *= clamp;
            dy *= clamp;
            dz *= clamp;
        }
        var t17 = (bm7c + bm80);
        var s7c = dz;
        var s70 = dz;
        var s48 = t17;
        var t18 = (-(t7) * ib.M0);
        var t19 = (ib.M0 * t8);
        var t20 = (t6 * ib.M1);
        var t21 = (ib.M1 * 0f);
        var t22 = (t20 + t18);
        var t23 = (ib.M0 * 0f);
        var t24 = (t21 + t19);
        var t25 = (s40 * ib.M2);
        var s78 = dy;
        var t26 = (b468 * ib.M1);
        var t27 = (t25 + t24);
        var s6c = dy;
        var t28 = (t26 + t23);
        var t29 = (s64 * ib.M2);
        var t30 = (ib.M4 * 0f);
        var t31 = (t28 + t29);
        var t32 = (ib.M2 * 0f);
        var t33 = (b458 * ib.M3);
        var t34 = (t32 + t22);
        var s74 = dx;
        var s44 = t31;
        var t35 = (ib.M3 * s38);
        var t36 = (ib.M3 * 0f);
        var t37 = (t30 + t35);
        var s68 = dx;
        var t38 = (b468 * ib.M4);
        var t39 = (s34 * ib.M4);
        var t40 = (t38 + t36);
        var t41 = (t39 + t33);
        var t42 = (s40 * ib.M5);
        var t43 = (s64 * ib.M5);
        var t44 = (t42 + t37);
        var t45 = (ib.M5 * 0f);
        var t46 = (t40 + t43);
        var t47 = (t45 + t41);
        var t48 = (b458 * ib.M6);
        var t49 = (ib.M7 * 0f);
        var s60 = t46;
        var t50 = (ib.M6 * s38);
        var t51 = (s34 * ib.M7);
        var t52 = (t49 + t50);
        var t53 = (ib.M6 * 0f);
        var t54 = (t51 + t48);
        var t55 = (b468 * ib.M7);
        var t56 = (t55 + t53);
        var t57 = (s64 * ib.M8);
        var t58 = (s40 * ib.M8);
        var t59 = (t56 + t57);
        var t60 = (ib.M8 * 0f);
        var t61 = (t58 + t52);
        var t62 = (t34 * 0f);
        var bm24 = t59;
        var t63 = (t60 + t54);
        var bm20 = t61;
        var t64 = (t47 * s38);
        var t65 = (t27 * 0f);
        var t66 = (t64 + t62);
        var bm38 = t63;
        var t67 = (t44 * s38);
        var s4c = t66;
        var t68 = (t67 + t65);
        var t69 = (t27 * b468);
        var b10 = t68;
        var t70 = (b468 * t34);
        var t71 = (t47 * 0f);
        var t72 = (t71 + t70);
        var t73 = (t44 * 0f);
        var t74 = (t73 + t69);
        var bm30 = t72;
        var t75 = (s64 * t27);
        var bm34 = t74;
        var t76 = (s64 * t34);
        var t77 = (s40 * t47);
        var t78 = (t77 + t76);
        var t79 = (s40 * t44);
        var t80 = (-(t4) * ia.M2);
        var t81 = (t79 + t75);
        var bm28 = t78;
        var t82 = (b13c * ia.M0);
        var t83 = (b460 * ia.M0);
        var bm2c = t81;
        var t84 = (b14c * ia.M1);
        var t85 = (ia.M1 * 0f);
        var t86 = (ia.M0 * 0f);
        var t87 = (t84 + t83);
        var t88 = (t85 + t82);
        var t89 = (-(t5) * ia.M1);
        var t90 = (b150 * ia.M2);
        var t91 = (t89 + t86);
        var t92 = (ia.M2 * 0f);
        var t93 = (t80 + t88);
        var t94 = (t92 + t87);
        var t95 = (t91 + t90);
        var s30 = t95;
        var t96 = (b460 * ia.M3);
        var t97 = (ia.M4 * b14c);
        var t98 = (ia.M3 * b13c);
        var t99 = (t97 + t96);
        var t100 = (ia.M4 * 0f);
        var t101 = (ia.M3 * 0f);
        var t102 = (t100 + t98);
        var t103 = (ia.M5 * b150);
        var t104 = (-(t4) * ia.M5);
        var t105 = (ia.M5 * 0f);
        var t106 = (t104 + t102);
        var t107 = (-(t5) * ia.M4);
        var t108 = (t105 + t99);
        var t109 = (ia.M7 * b14c);
        var t110 = (t107 + t101);
        var t111 = (ia.M6 * b13c);
        var t112 = (t110 + t103);
        var t113 = (ia.M7 * 0f);
        var t114 = (b460 * ia.M6);
        var t115 = (t113 + t111);
        var t116 = (ia.M6 * 0f);
        var t117 = (t109 + t114);
        var t118 = (b450 * ia.M7);
        var t119 = (s3c * ia.M8);
        var t120 = (t93 * 0f);
        var t121 = (ia.M8 * b150);
        var t122 = (t118 + t116);
        var t123 = (ia.M8 * 0f);
        var t124 = (t108 * b13c);
        var t125 = (t122 + t121);
        var t126 = (t108 * 0f);
        var t127 = (t119 + t115);
        var t128 = (t94 * 0f);
        var t129 = (t123 + t117);
        var t130 = (t106 * b13c);
        var t131 = (t124 + t128);
        var t132 = (t106 * 0f);
        var t133 = (t130 + t120);
        var t134 = (b450 * t93);
        var t135 = (b450 * t94);
        var t136 = (t132 + t134);
        var t137 = (t126 + t135);
        var t138 = (t94 * b150);
        var t139 = (b150 * t93);
        var t140 = (s3c * t108);
        var t141 = (s3c * t106);
        var t142 = (t140 + t138);
        var t143 = (t112 * b13c);
        var t144 = (t141 + t139);
        var t145 = (s30 * 0f);
        var t146 = (s60 * s38);
        var t147 = (t143 + t145);
        var t148 = (b460 * t125);
        var t149 = (t147 + t148);
        var t150 = (s48 - t149);
        var s30_1 = t150;
        var t151 = (s44 * 0f);
        var t152 = (t146 + t151);
        var t153 = (b458 * bm24);
        var t154 = (t152 + t153);
        var t155 = (s30_1 - t154);
        var s30_2 = t155;
        var t156 = (b460 * t127);
        var t157 = (t156 + t133);
        var t158 = (b458 * bm20);
        var t159 = (t158 + b10);
        var t160 = (0f - t157);
        var t161 = (t129 * b460);
        var t162 = (t161 + t131);
        var t163 = (t160 - t159);
        var t164 = (0f - t162);
        var t165 = (b458 * bm38);
        var b460_1 = t163;
        var t166 = (t165 + s4c);
        var t167 = (t164 - t166);
        var t168 = (t112 * 0f);
        var t169 = (s44 * b468);
        var s4c_1 = t167;
        var t170 = (b450 * s30);
        var t171 = (t170 + t168);
        var b450_1 = t171;
        var t172 = (t125 * b14c);
        var t173 = (b450_1 + t172);
        var t174 = (0f - t173);
        var b450_2 = t173;
        var b458_1 = t174;
        var t175 = (s60 * 0f);
        var t176 = (t169 + t175);
        var t177 = (s34 * bm24);
        var t178 = (t176 + t177);
        var t179 = (b458_1 - t178);
        var b458_2 = t179;
        var t180 = (b14c * t127);
        var t181 = (t180 + t136);
        var t182 = (s48 - t181);
        var b450_3 = t182;
        var t183 = (s30 * b150);
        var t184 = (s34 * bm20);
        var t185 = (s34 * bm38);
        var t186 = (t184 + bm34);
        var t187 = (t185 + bm30);
        var t188 = (b450_3 - t186);
        var t189 = (bm20 * 0f);
        var t190 = (t125 * 0f);
        var t191 = (t189 + bm2c);
        var b450_4 = t188;
        var t192 = (t129 * b14c);
        var t193 = (s40 * s60);
        var t194 = (t192 + t137);
        var t195 = (t127 * 0f);
        var t196 = (bm24 * 0f);
        var t197 = (t195 + t144);
        var t198 = (t129 * 0f);
        var t199 = (0f - t194);
        var t200 = (s3c * t112);
        var t201 = (t198 + t142);
        var t202 = (bm38 * 0f);
        var t203 = (t199 - t187);
        var t204 = (t200 + t183);
        var t205 = (s48 - t201);
        var t206 = (t202 + bm28);
        var b468_1 = t203;
        var t207 = (t204 + t190);
        var t208 = (t205 - t206);
        var t209 = (0f - t207);
        var t210 = (s64 * s44);
        var t211 = (t208 * b450_4);
        var t212 = (t193 + t210);
        var t213 = (t212 + t196);
        var t214 = (t209 - t213);
        var t215 = (0f - t197);
        var t216 = (t203 * t214);
        var t217 = (t215 - t191);
        var t218 = (t217 * t203);
        var t219 = (t211 - t218);
        var t220 = (b458_2 * t217);
        var t221 = (b458_2 * t208);
        var t222 = (s30_2 * t219);
        var t223 = (t216 - t221);
        var t224 = (b450_4 * t214);
        var t225 = (t220 - t224);
        var t226 = (b460_1 * t223);
        var t227 = (t222 + t226);
        var t228 = (t225 * s4c_1);
        var t229 = (t227 + t228);
        float bm60 = 0f, bm5c = 0f, bm58 = 0f, bm54 = 0f, bm50 = 0f, bm4c = 0f, bm48 = 0f, bm44 = 0f, bm40 = 0f;
        if (!(1.17549435e-35f > MathF.Abs(t229)))
        {
            var t230 = (1f / t229);
            var t231 = (t208 * b460_1);
            var t232 = (b460_1 * b468_1);
            var t233 = (s4c_1 * t217);
            var t234 = (t208 * s30_2);
            var t235 = (t233 - t231);
            var t236 = (b460_1 * t214);
            var t237 = (t219 * t230);
            var t238 = (s4c_1 * t214);
            var t239 = (t235 * t230);
            var t240 = (t234 - t238);
            var t241 = (t223 * t230);
            var t242 = (t225 * t230);
            var t243 = (s30_2 * t217);
            var t244 = (b458_2 * b460_1);
            var t245 = (t236 - t243);
            var t246 = (b458_2 * s4c_1);
            var t247 = (b450_4 * s4c_1);
            var t248 = (t240 * t230);
            var t249 = (t232 - t247);
            var t250 = (t245 * t230);
            var t251 = (s30_2 * b468_1);
            var t252 = (s30_2 * b450_4);
            var t253 = (t249 * t230);
            var t254 = (t246 - t251);
            var t255 = (t252 - t244);
            var t256 = (t254 * t230);
            var t257 = (t255 * t230);
            bm48 = t253;
            bm44 = t256;
            bm58 = t242;
            bm4c = t250;
            bm40 = t257;
            bm60 = t237;
            bm5c = t241;
            bm54 = t239;
            bm50 = t248;
        }
        var t258 = (bm4c * -(s78));
        var t259 = (bm5c * -(s74));
        var t260 = (bm58 * -(s74));
        var t261 = (bm40 * -(s7c));
        var t262 = (t258 + t260);
        var t263 = (bm44 * -(s7c));
        var t264 = (bm50 * -(s78));
        var t265 = (t261 + t262);
        var t266 = (bm60 * -(s74));
        var t267 = (t264 + t259);
        var t268 = (bm54 * -(s78));
        var t269 = (bm48 * -(s7c));
        var t270 = (t263 + t267);
        var bf0 = t265;
        var t271 = (t268 + t266);
        var bd0 = -(t265);
        var bec = t270;
        var t272 = (t271 + t269);
        var bcc = -(t270);
        var be8 = t272;
        var bc8 = -(t272);

            ContactSolver.ApplyPositionImpulse(ref a, bm80, ia, new(bc8, bcc, bd0), new(bb8, bbc, bc0));
            ContactSolver.ApplyPositionImpulse(ref b, bm7c, ib, new(be8, bec, bf0), new(bd8, bdc, be0));
        }
    }

    private static float Max(float a, float b) => a > b ? a : b;

    private static float Min(float a, float b) => a < b ? a : b;

    /// <summary>The joint frames in the world from the bodies' poses: the relative rotation and both twist axes.</summary>
    private static (Quat Rel, Vec3 AxisA, Vec3 AxisB) Frames(in ConicalJointState j, in SolverBody a, in SolverBody b)
    {
        var qa = RnMath.Mul(a.Q, j.Frame1);
        var qb = RnMath.Mul(b.Q, j.Frame2);
        if ((qa.X * qb.X + qa.Y * qb.Y) + (qa.Z * qb.Z + qa.W * qb.W) < 0f)
            qb = new(0f - qb.X, 0f - qb.Y, 0f - qb.Z, 0f - qb.W);
        return (RnMath.Mul(Conjugate(qa), qb), RnMath.Rotate(qa, new(0f, 0f, 1f)), RnMath.Rotate(qb, new(0f, 0f, 1f)));
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

    /// <summary>a . (IA + IB) a as the position pass sums it.</summary>
    private static float Effective(in Mat3 ia, in Mat3 ib, Vec3 t)
    {
        var x = (((ia.M0 + ib.M0) * t.X + (ia.M3 + ib.M3) * t.Y) + (ia.M6 + ib.M6) * t.Z);
        var y = (((ia.M4 + ib.M4) * t.Y + (ia.M1 + ib.M1) * t.X) + (ia.M7 + ib.M7) * t.Z);
        var z = (((ia.M5 + ib.M5) * t.Y + (ia.M2 + ib.M2) * t.X) + (ia.M8 + ib.M8) * t.Z);
        return (x * t.X + t.Y * y) + t.Z * z;
    }

    /// <summary>
    /// The angular half of a position impulse (FUN_1801b1570): q += ((I L) /
    /// 2, 0) q, renormalised, unless the body's inertia is infinite.
    /// </summary>
    private static void Rotate(ref SolverBody sb, in Mat3 inertia, Vec3 l)
    {
        if (sb.InfiniteInertia != 0)
            return;
        var theta = ContactSolver.Apply(inertia, l);
        var dq = RnMath.Mul(new Quat(theta.X * 0.5f, theta.Y * 0.5f, theta.Z * 0.5f, 0f * 0.5f), sb.Q);
        var q = new Quat(sb.Q.X + dq.X * 1f, sb.Q.Y + dq.Y * 1f, sb.Q.Z + dq.Z * 1f, sb.Q.W + dq.W * 1f);
        Integrator.SetOrientation(ref sb, RnMath.Normalize(q));
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
