namespace Source2.Compiler.Simulation;

/// <summary>
/// The small vector routines Rubikon's step is built from, each with the float
/// operations in the order vphysics2 executes them. A different grouping gives
/// a different last bit, and the settle is long enough for that to show.
/// </summary>
public static class RnMath
{
    /// <summary>
    /// Hamilton product a * b (FUN_1800899b0). The SIMD lanes add in this
    /// order: w-term, x-term, y-term, z-term.
    /// </summary>
    public static Quat Mul(Quat a, Quat b) => new(
        ((a.W * b.X + a.X * b.W) + a.Y * b.Z) - a.Z * b.Y,
        ((a.W * b.Y - a.X * b.Z) + a.Y * b.W) + a.Z * b.X,
        ((a.W * b.Z + a.X * b.Y) - a.Y * b.X) + a.Z * b.W,
        ((a.W * b.W - a.X * b.X) - a.Y * b.Y) - a.Z * b.Z);

    /// <summary>
    /// Rotates v by q the way the angular integrator's tail does:
    /// t = 2 (q x v), then r = (q x t) + (v + w t).
    /// </summary>
    public static Vec3 Rotate(Quat q, Vec3 v)
    {
        var cx = v.Z * q.Y - v.Y * q.Z;
        var cy = v.X * q.Z - v.Z * q.X;
        var cz = v.Y * q.X - v.X * q.Y;
        var tx = cx + cx;
        var ty = cy + cy;
        var tz = cz + cz;
        return new(
            (tz * q.Y - ty * q.Z) + (v.X + q.W * tx),
            (tx * q.Z - tz * q.X) + (v.Y + q.W * ty),
            (ty * q.X - tx * q.Y) + (v.Z + q.W * tz));
    }

    /// <summary>
    /// Normalises a quaternion as the position integrator does: the length is
    /// sqrt of a DPPS sum, (xx + yy) + (zz + ww), each lane divided by it, and
    /// a zero length gives the identity.
    /// </summary>
    public static Quat Normalize(Quat q)
    {
        var n = MathF.Sqrt((q.X * q.X + q.Y * q.Y) + (q.Z * q.Z + q.W * q.W));
        return n == 0f ? Quat.Identity : new(q.X / n, q.Y / n, q.Z / n, q.W / n);
    }

    /// <summary>The rotation matrix of a unit quaternion (FUN_1801bc710).</summary>
    public static Mat3 Matrix(Quat q)
    {
        var xx = q.X * q.X;
        var yy = q.Y * q.Y;
        var zz = q.Z * q.Z;
        var yx = q.Y * q.X;
        var zy = q.Z * q.Y;
        var zx = q.Z * q.X;
        var wx = q.W * q.X;
        var wy = q.W * q.Y;
        var wz = q.W * q.Z;
        return new Mat3
        {
            M0 = 1f - Twice(zz + yy),
            M1 = Twice(wz + yx),
            M2 = Twice(zx - wy),
            M3 = Twice(yx - wz),
            M4 = 1f - Twice(zz + xx),
            M5 = Twice(zy + wx),
            M6 = Twice(wy + zx),
            M7 = Twice(zy - wx),
            M8 = 1f - Twice(yy + xx),
        };
    }

    /// <summary>
    /// R^T I R for a symmetric I, reading only its lower triangle (FUN_180292de0).
    /// Each output is a column of R dotted with I times another column, and the
    /// result is symmetric, so each off-diagonal pair is computed once.
    /// </summary>
    public static Mat3 RotateInertia(Mat3 r, Mat3 i)
    {
        var p0 = Product(r, i, 0);
        var p1 = Product(r, i, 1);
        var p2 = Product(r, i, 2);
        var m = new Mat3
        {
            M0 = Dot(p0, r, 0),
            M1 = Dot(p0, r, 1),
            M2 = Dot(p0, r, 2),
            M4 = Dot(p1, r, 1),
            M5 = Dot(p1, r, 2),
            M8 = Dot(p2, r, 2),
        };
        m.M3 = m.M1;
        m.M6 = m.M2;
        m.M7 = m.M5;
        return m;
    }

    /// <summary>I times column c of r, in the grouping FUN_180292de0 uses.</summary>
    private static Vec3 Product(Mat3 r, Mat3 i, int c)
    {
        var a0 = r[c];
        var a1 = r[3 + c];
        var a2 = r[6 + c];
        return new(
            (i.M3 * a1 + i.M0 * a0) + i.M6 * a2,
            (i.M3 * a0 + i.M4 * a1) + i.M7 * a2,
            (i.M7 * a1 + i.M6 * a0) + i.M8 * a2);
    }

    private static float Dot(Vec3 p, Mat3 r, int c)
        => (p.X * r[c] + p.Y * r[3 + c]) + p.Z * r[6 + c];

    private static float Twice(float x) => x + x;

    /// <summary>
    /// Scales v down so v dt travels at most <paramref name="limit"/>
    /// (FUN_1801b2570 with 80, FUN_1801b2450 with pi/2). A non-finite
    /// component zeroes the vector. The test sums x first, the length z first.
    /// </summary>
    public static Vec3 Clamp(Vec3 v, float dt, float limitSquared, float limit)
    {
        if (!Finite(v.X) || !Finite(v.Y) || !Finite(v.Z))
            return default;
        var x = v.X * dt;
        var y = v.Y * dt;
        var z = v.Z * dt;
        var xx = x * x;
        var yy = y * y;
        var zz = z * z;
        if (!((xx + yy) + zz > limitSquared))
            return v;
        var k = limit / MathF.Sqrt((zz + yy) + xx);
        return new(v.X * k, v.Y * k, v.Z * k);
    }

    /// <summary>The clamp's own test: exponent bits all set means Inf or NaN.</summary>
    private static bool Finite(float f)
        => (BitConverter.SingleToInt32Bits(f) & 0x7f800000) != 0x7f800000;

    /// <summary>tier0's V_cosf.</summary>
    public static float Cos(float x) => CrtMath.Cos(x);

    /// <summary>tier0's V_sincosf: sinf, then cosf.</summary>
    public static (float Sin, float Cos) SinCos(float x) => (CrtMath.Sin(x), CrtMath.Cos(x));

    /// <summary>tier0's V_expf.</summary>
    public static float Exp(float x) => CrtMath.Exp(x);
}
