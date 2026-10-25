using System.Runtime.InteropServices;

namespace Source2.Compiler.Simulation;

/// <summary>
/// The header vphysics2's joint wrapper (FUN_18030f0b0) writes before each
/// joint's record in the solver stream: the record type, the two solver
/// body indices and the two mass scales.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x14)]
public struct JointHeader
{
    [FieldOffset(0x00)] public int Type;
    [FieldOffset(0x04)] public int BodyA;
    [FieldOffset(0x08)] public int BodyB;
    [FieldOffset(0x0c)] public float ScaleA;
    [FieldOffset(0x10)] public float ScaleB;
}

/// <summary>
/// One angular axis with a one-sided limit (FUN_180229380, 0x38 bytes): the
/// world axis, the bias, the effective mass, the accumulated impulse, its
/// bounds and the axis through each body's inverse inertia.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x38)]
public struct AngularRow
{
    [FieldOffset(0x00)] public Vec3 Axis;
    [FieldOffset(0x0c)] public float Bias;
    [FieldOffset(0x10)] public float Mass;
    [FieldOffset(0x14)] public float Impulse;
    [FieldOffset(0x18)] public float Min;
    [FieldOffset(0x1c)] public float Max;
    [FieldOffset(0x20)] public Vec3 IA;
    [FieldOffset(0x2c)] public Vec3 IB;
}

/// <summary>
/// Three angular axes solved as one block (FUN_180206120, 0x94 bytes): the
/// target velocity, the softness on the diagonal, the inverse of (IA + IB +
/// softness), the accumulated impulse, its length limit and the two inverse
/// inertias.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x94)]
public struct AngularBlock
{
    [FieldOffset(0x00)] public Vec3 Bias;
    [FieldOffset(0x0c)] public Vec3 Softness;
    [FieldOffset(0x18)] public Mat3 Mass;
    [FieldOffset(0x3c)] public Vec3 Impulse;
    [FieldOffset(0x48)] public float MaxImpulse;
    [FieldOffset(0x4c)] public Mat3 IA;
    [FieldOffset(0x70)] public Mat3 IB;
}

/// <summary>
/// The point-to-point block (FUN_1802064a0, 0x98 bytes): both lever arms,
/// the inverse of the 3x3 effective mass, the accumulated impulse, the
/// inverse masses and inertias.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x98)]
public struct PointBlock
{
    [FieldOffset(0x00)] public Vec3 RA;
    [FieldOffset(0x0c)] public Vec3 RB;
    [FieldOffset(0x18)] public Mat3 Mass;
    [FieldOffset(0x3c)] public Vec3 Impulse;
    [FieldOffset(0x48)] public float MA;
    [FieldOffset(0x4c)] public float MB;
    [FieldOffset(0x50)] public Mat3 IA;
    [FieldOffset(0x74)] public Mat3 IB;
}

/// <summary>
/// The pieces vphysics2's joints build their solver records from, each with
/// the float operations in vphysics2's order.
/// </summary>
public static class JointRows
{
    private const float Tiny = 1.17549435e-35f;

    /// <summary>
    /// A body-local point in the world (FUN_180209820): the mass centre's
    /// position less the rotated local mass centre, plus the point rotated
    /// as p + 2 (q x (q x p + w p)).
    /// </summary>
    public static Vec3 WorldPoint(in SolverBody b, Vec3 p)
    {
        var q = b.Q;
        var cx = (q.Y * p.Z - q.Z * p.Y) + q.W * p.X;
        var cy = (q.Z * p.X - q.X * p.Z) + q.W * p.Y;
        var cz = (q.X * p.Y - q.Y * p.X) + q.W * p.Z;
        var dx = cz * q.Y - cy * q.Z;
        var dy = cx * q.Z - cz * q.X;
        var dz = cy * q.X - cx * q.Y;
        var m = RnMath.Rotate(q, b.LocalMassCenter);
        return new(((dx + dx) + p.X) + (b.Position.X - m.X),
                   ((dy + dy) + p.Y) + (b.Position.Y - m.Y),
                   ((dz + dz) + p.Z) + (b.Position.Z - m.Z));
    }

    /// <summary>The 3x3 inverse by cofactors; a determinant below 1.2e-35 in size gives zero (DAT_18055e880).</summary>
    private static Mat3 Inverse(float k0, float k1, float k2, float k3, float k4, float k5, float k6, float k7, float k8)
    {
        var c0 = k4 * k8 - k5 * k7;
        var c1 = k5 * k6 - k3 * k8;
        var c2 = k3 * k7 - k4 * k6;
        var det = c0 * k0 + c1 * k1 + c2 * k2;
        // comiss then jbe: a NaN determinant still inverts.
        if (Tiny > MathF.Abs(det))
            return default;
        var inv = 1f / det;
        return new Mat3
        {
            M0 = c0 * inv, M1 = c1 * inv, M2 = c2 * inv,
            M3 = (k2 * k7 - k1 * k8) * inv, M4 = (k0 * k8 - k6 * k2) * inv, M5 = (k1 * k6 - k0 * k7) * inv,
            M6 = (k5 * k1 - k4 * k2) * inv, M7 = (k2 * k3 - k5 * k0) * inv, M8 = (k4 * k0 - k1 * k3) * inv,
        };
    }

    /// <summary>The angular block (FUN_180206120): K = (IB + IA) + softness on the diagonal, zero off it.</summary>
    public static void Angular(ref AngularBlock row, in Mat3 ia, in Mat3 ib, Vec3 bias, Vec3 softness, Vec3 impulse, float maxImpulse)
    {
        row.Mass = Inverse((ib.M0 + ia.M0) + softness.X, (ib.M1 + ia.M1) + 0f, (ib.M2 + ia.M2) + 0f,
                           (ib.M3 + ia.M3) + 0f, (ib.M4 + ia.M4) + softness.Y, (ib.M5 + ia.M5) + 0f,
                           (ib.M6 + ia.M6) + 0f, (ib.M7 + ia.M7) + 0f, (ib.M8 + ia.M8) + softness.Z);
        row.Bias = bias;
        row.Softness = softness;
        row.Impulse = impulse;
        row.MaxImpulse = maxImpulse;
        row.IA = ia;
        row.IB = ib;
    }

    /// <summary>
    /// One limited angular axis (FUN_180229380): the effective mass 1 / (a .
    /// (IA + IB) a), zero when that is not above 1.2e-35, and the axis
    /// through each inertia.
    /// </summary>
    public static void Limit(ref AngularRow row, in Mat3 ia, in Mat3 ib, float bias, Vec3 a, float impulse, float min, float max)
    {
        var k = (((ib.M1 + ia.M1) * a.X + (ib.M4 + ia.M4) * a.Y) + (ib.M7 + ia.M7) * a.Z) * a.Y
              + (((ib.M0 + ia.M0) * a.X + (ib.M3 + ia.M3) * a.Y) + (ib.M6 + ia.M6) * a.Z) * a.X
              + (((ib.M5 + ia.M5) * a.Y + (ib.M2 + ia.M2) * a.X) + (ib.M8 + ia.M8) * a.Z) * a.Z;
        row.Bias = bias;
        row.Axis = a;
        row.Impulse = impulse;
        row.Mass = k <= Tiny ? 0f : 1f / k;
        row.Max = max;
        row.Min = min;
        row.IA = new((a.X * ia.M0 + a.Y * ia.M3) + a.Z * ia.M6,
                     (a.X * ia.M1 + a.Y * ia.M4) + a.Z * ia.M7,
                     (a.Y * ia.M5 + a.X * ia.M2) + a.Z * ia.M8);
        row.IB = new((a.Y * ib.M3 + a.X * ib.M0) + a.Z * ib.M6,
                     (a.X * ib.M1 + a.Y * ib.M4) + a.Z * ib.M7,
                     (a.X * ib.M2 + a.Y * ib.M5) + a.Z * ib.M8);
    }

    /// <summary>
    /// The point-to-point block (FUN_1802064a0): K = (mA + mB) I - [rA] IA
    /// [rA] - [rB] IB [rB] with the skew products in vphysics2's order, and
    /// its inverse (zero when singular).
    /// </summary>
    public static void Point(ref PointBlock row, float ma, in Mat3 ia, Vec3 ra, float mb, in Mat3 ib, Vec3 rb, Vec3 impulse)
    {
        var m = ma + mb;
        // IB [rB] (the columns of the skew of rB through IB), then the same for A.
        float nbz = -rb.Z, nbx = -rb.X, nby = -rb.Y;
        var b00 = (nbz * ib.M1 + ib.M0 * 0f) + rb.Y * ib.M2;
        var b01 = (ib.M0 * rb.Z + ib.M1 * 0f) + nbx * ib.M2;
        var b02 = (ib.M0 * nby + ib.M1 * rb.X) + ib.M2 * 0f;
        var b10 = (nbz * ib.M4 + ib.M3 * 0f) + ib.M5 * rb.Y;
        var b11 = (rb.Z * ib.M3 + ib.M4 * 0f) + nbx * ib.M5;
        var b12 = (ib.M3 * nby + ib.M4 * rb.X) + ib.M5 * 0f;
        var b20 = (nbz * ib.M7 + ib.M6 * 0f) + rb.Y * ib.M8;
        var b21 = (rb.Z * ib.M6 + ib.M7 * 0f) + nbx * ib.M8;
        var b22 = (ib.M6 * nby + ib.M7 * rb.X) + ib.M8 * 0f;
        float naz = -ra.Z, nax = -ra.X, nay = -ra.Y;
        var a00 = (naz * ia.M1 + ia.M0 * 0f) + ia.M2 * ra.Y;
        var a01 = (ia.M0 * ra.Z + ia.M1 * 0f) + ia.M2 * nax;
        var a02 = (ia.M0 * nay + ia.M1 * ra.X) + ia.M2 * 0f;
        var a10 = (ia.M4 * naz + ia.M3 * 0f) + ia.M5 * ra.Y;
        var a11 = (ia.M3 * ra.Z + ia.M4 * 0f) + ia.M5 * nax;
        var a12 = (ia.M4 * ra.X + nay * ia.M3) + ia.M5 * 0f;
        var a20 = (ia.M7 * naz + ia.M6 * 0f) + ia.M8 * ra.Y;
        var a21 = (ra.Z * ia.M6 + ia.M7 * 0f) + ia.M8 * nax;
        var a22 = (nay * ia.M6 + ia.M7 * ra.X) + ia.M8 * 0f;
        var k0 = (m - ((ra.Z * a10 + a00 * 0f) + a20 * nay)) - ((rb.Z * b10 + b00 * 0f) + b20 * nby);
        var k1 = (0f - ((a11 * ra.Z + a01 * 0f) + nay * a21)) - ((rb.Z * b11 + b01 * 0f) + b21 * nby);
        var k2 = (0f - ((a12 * ra.Z + a02 * 0f) + nay * a22)) - ((rb.Z * b12 + b02 * 0f) + b22 * nby);
        var k3 = (0f - ((naz * a00 + a10 * 0f) + a20 * ra.X)) - ((nbz * b00 + b10 * 0f) + b20 * rb.X);
        var k4 = (m - ((naz * a01 + a11 * 0f) + a21 * ra.X)) - ((nbz * b01 + b11 * 0f) + b21 * rb.X);
        var k5 = (0f - ((naz * a02 + a12 * 0f) + ra.X * a22)) - ((nbz * b02 + b12 * 0f) + b22 * rb.X);
        var k6 = (0f - ((ra.Y * a00 + nax * a10) + a20 * 0f)) - ((nbx * b10 + rb.Y * b00) + b20 * 0f);
        var k7 = (0f - ((a11 * nax + ra.Y * a01) + a21 * 0f)) - ((nbx * b11 + rb.Y * b01) + b21 * 0f);
        var k8 = (m - ((a12 * nax + a02 * ra.Y) + a22 * 0f)) - ((b02 * rb.Y + nbx * b12) + b22 * 0f);
        row.Mass = Inverse(k0, k1, k2, k3, k4, k5, k6, k7, k8);
        row.RA = ra;
        row.RB = rb;
        row.Impulse = impulse;
        row.MA = ma;
        row.MB = mb;
        row.IA = ia;
        row.IB = ib;
    }

    /// <summary>An inverse inertia times the header's mass scale.</summary>
    internal static Mat3 Scaled(float s, in Mat3 m) => new()
    {
        M0 = s * m.M0, M1 = s * m.M1, M2 = s * m.M2, M3 = s * m.M3, M4 = s * m.M4,
        M5 = s * m.M5, M6 = s * m.M6, M7 = s * m.M7, M8 = s * m.M8,
    };

    internal static Quat Conjugate(Quat q) => new(-q.X, -q.Y, -q.Z, q.W);

    /// <summary>a . (IA + IB) a as the position pass sums it.</summary>
    internal static float Effective(in Mat3 ia, in Mat3 ib, Vec3 t)
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
    internal static void RotateBody(ref SolverBody sb, in Mat3 inertia, Vec3 l)
    {
        if (sb.InfiniteInertia != 0)
            return;
        var theta = ContactSolver.Apply(inertia, l);
        var dq = RnMath.Mul(new Quat(theta.X * 0.5f, theta.Y * 0.5f, theta.Z * 0.5f, 0f * 0.5f), sb.Q);
        var q = new Quat(sb.Q.X + dq.X * 1f, sb.Q.Y + dq.Y * 1f, sb.Q.Z + dq.Z * 1f, sb.Q.W + dq.W * 1f);
        Integrator.SetOrientation(ref sb, RnMath.Normalize(q));
    }

    /// <summary>One limited angular axis in a velocity iteration: the clamped impulse change applied to both bodies.</summary>
    internal static void SolveLimit(ref AngularRow r, ref Vec3 wa, ref Vec3 wb)
    {
        var old = r.Impulse;
        var lambda = -((((wb.Y - wa.Y) * r.Axis.Y + (wb.X - wa.X) * r.Axis.X) + (wb.Z - wa.Z) * r.Axis.Z) + r.Bias) * r.Mass + old;
        // maxss then minss: a NaN becomes the minimum.
        lambda = lambda > r.Min ? lambda : r.Min;
        lambda = lambda < r.Max ? lambda : r.Max;
        r.Impulse = lambda;
        var d = lambda - old;
        wa = new(wa.X - d * r.IA.X, wa.Y - d * r.IA.Y, wa.Z - d * r.IA.Z);
        wb = new(d * r.IB.X + wb.X, d * r.IB.Y + wb.Y, d * r.IB.Z + wb.Z);
    }

    /// <summary>
    /// The point block in a velocity iteration (as FUN_18022c860 and
    /// FUN_18021bcd0 inline it): the relative anchor velocity driven to zero
    /// through the inverse effective mass, applied to both bodies.
    /// </summary>
    internal static void SolvePoint(ref PointBlock p, ref SolverBody a, ref SolverBody b, Vec3 va, Vec3 vb, Vec3 wa, Vec3 wb)
    {
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

    /// <summary>
    /// The anchors pulled together in a position iteration, at most 8 units,
    /// through a full position impulse on each body (the conical and the
    /// revolute joint inline the same code).
    /// </summary>
    internal static void PointPosition(ref SolverBody a, ref SolverBody b, in JointHeader h, Vec3 pa, Vec3 pb)
    {
        var ia = Scaled(h.ScaleA, a.WorldInvInertia);
        var ib = Scaled(h.ScaleB, b.WorldInvInertia);
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
