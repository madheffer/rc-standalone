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
        if (!(Tiny <= MathF.Abs(det)))
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
}
