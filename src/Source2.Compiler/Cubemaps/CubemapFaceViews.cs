using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Source2.Compiler.Simulation;

namespace Source2.Compiler.Cubemaps;

/// <summary>
/// The game's cubemap face views (client.dll BuildCubemaps 18020e130, the face setup at 18020e5bc ..
/// 18020e728), float for float: the record's angles and the face's go through AngleQuaternion (1817445e0),
/// their product (181745900, record first), QuaternionMatrix + MatrixAngles (1817463a0 -> 1817439d0, which is
/// where 90 becomes 89.99999237); the view object (template 18174dd60: FOV 90, aspect 1, NDC rect -1..1, no
/// jitter) takes the record's origin and those angles (18174fd60 -> 1817503b0: AngleMatrix 18173cd10 with the
/// origin, concatenated with the axis swap at 182291300 by 18173fd50, inverted by 181741a70), and the
/// projection (18174fe70 -> 18174fb00 over the frustum corners of 18174ef50, then the identity jitter
/// multiplied in by 181746490). Sines and cosines are tier0's V_sincosf (the CRT's, <see cref="CrtMath"/>),
/// arctangents V_atan2f (<see cref="CrtMath.Atan2"/>). 181746490 is a 4 x 4 multiply by DPPS, whose sum is
/// (p0 + p1) + (p2 + p3) on the hardware (unicorn's emulation of it sums from +0, which is why ledger 118's
/// emulation saw a +0.0 where the game uploads -0.0).
/// </summary>
public static class CubemapFaceViews
{
    /// <summary>The face table (client 181ad10d0): pitch, yaw, roll for +X, -X, +Y, -Y, +Z, -Z; FOV 90 each.</summary>
    public static readonly Vector3[] FaceAngles =
    [
        new(0f, 0f, 90f), new(0f, 180f, -90f), new(0f, 90f, 0f), new(0f, -90f, 180f), new(-90f, 90f, 0f), new(90f, -90f, 0f),
    ];

    /// <summary>
    /// tier0's V_tanf at FOV 90's half angle in radians (90 * 0.5 * 0.017453292 = 0.7853982f): exactly 1, read
    /// back from tier0 (Tier0TrigOracleTests). The cubemap views use no other FOV.
    /// </summary>
    private const float TanHalfFov90 = 1f;

    /// <summary>One face's view: the angles the view ends with, its matrices (row-major) and the camera-relative product.</summary>
    public sealed record View(
        Vector3 Origin, Vector3 Angles, float Near, float Far,
        float[] CameraToWorld, float[] WorldToView, float[] Projection, float[] CameraRelative);

    /// <summary>A face of the cube recorded at <paramref name="origin"/> with the record's <paramref name="angles"/> and near / far.</summary>
    public static View Face(Vector3 origin, Vector3 angles, float near, float far, int face)
    {
        var q = QuaternionMult(AngleQuaternion(angles), AngleQuaternion(FaceAngles[face]));
        var viewAngles = MatrixAngles(QuaternionMatrix(q));
        var cameraToWorld = AngleMatrix(viewAngles, origin);
        var worldToView = MatrixInvert(ConcatTransforms(cameraToWorld, AxisSwap));
        var projection = Mul4(Identity4, Perspective(near, far, 90f, 1f, -1f, -1f, 1f, 1f));
        // The camera-relative product: the projection times the world-to-view without its translation.
        float[] rotation =
        [
            worldToView[0], worldToView[1], worldToView[2], 0f,
            worldToView[4], worldToView[5], worldToView[6], 0f,
            worldToView[8], worldToView[9], worldToView[10], 0f,
            0f, 0f, 0f, 1f,
        ];
        return new View(origin, viewAngles, near, far, cameraToWorld, worldToView, projection, Mul4(projection, rotation));
    }

    /// <summary>182291300: view space from Source's (x forward, y left, z up) to the renderer's (x right, y up, z back).</summary>
    private static readonly float[] AxisSwap = [0f, 0f, -1f, 0f, -1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f];

    private static readonly float[] Identity4 = [1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f];

    private static (float Sin, float Cos) SinCos(float x) => (CrtMath.Sin(x), CrtMath.Cos(x));

    /// <summary>1817445e0: (x, y, z, w) from pitch, yaw, roll in degrees (half angles by 0.008726646).</summary>
    public static Vector4 AngleQuaternion(Vector3 angles)
    {
        const float k = 0.008726646f;
        var (sp, cp) = SinCos(angles.X * k);
        var (sy, cy) = SinCos(angles.Y * k);
        var (sr, cr) = SinCos(angles.Z * k);
        float srcp = sr * cp, srsp = sr * sp, crsp = cr * sp, crcp = cr * cp;
        return new Vector4(cy * srcp - sy * crsp, cy * crsp + sy * srcp, sy * crcp - cy * srsp, sy * srsp + cy * crcp);
    }

    /// <summary>181745900: p q, the lanes summed as the SSE code does.</summary>
    public static Vector4 QuaternionMult(Vector4 p, Vector4 q)
    {
        // xmm3 = (qw, -qz, qy, -qx), xmm2 = (qz, qw, -qx, -qy), xmm5 = (-qy, qx, qw, -qz);
        // ((py xmm2) + ((pw q) + (px xmm3))) + (pz xmm5).
        float L(float a, float b, float c, float d) => (a + (b + c)) + d;
        return new Vector4(
            L(p.Y * q.Z, p.W * q.X, p.X * q.W, p.Z * -q.Y),
            L(p.Y * q.W, p.W * q.Y, p.X * -q.Z, p.Z * q.X),
            L(p.Y * -q.X, p.W * q.Z, p.X * q.Y, p.Z * q.W),
            L(p.Y * -q.Y, p.W * q.W, p.X * -q.X, p.Z * -q.Z));
    }

    /// <summary>1817463a0's QuaternionMatrix: a 3 x 4, row-major, no translation.</summary>
    public static float[] QuaternionMatrix(Vector4 q)
    {
        float x2 = q.X + q.X, y2 = q.Y + q.Y, z2 = q.Z + q.Z;
        float xx = q.X * x2, yy = q.Y * y2, zz = q.Z * z2;
        float a0 = (1f - yy) - zz, a1 = (1f - xx) - zz, a2 = (1f - xx) - yy;
        float yx = y2 * q.X, zy = z2 * q.Y, zx = z2 * q.X;
        float zw = z2 * q.W, xw = x2 * q.W, yw = y2 * q.W;
        float s0 = zw + yx, s1 = xw + zy, s2 = yw + zx;
        float d0 = yx - zw, d1 = zy - xw, d2 = zx - yw;
        return [a0, d0, s2, 0f, s0, a1, d1, 0f, d2, s1, a2, 0f];
    }

    /// <summary>1817439d0: pitch, yaw, roll in degrees from a rotation (57.295776 per radian).</summary>
    public static Vector3 MatrixAngles(float[] m)
    {
        const float r2d = 57.295776f;
        var xy = MathF.Sqrt(m[4] * m[4] + m[0] * m[0]);
        if (xy > 0.001f)
        {
            var yaw = CrtMath.Atan2(m[4], m[0]);
            var pitch = CrtMath.Atan2(-m[8], xy);
            var roll = CrtMath.Atan2(m[9], m[10]);
            return new Vector3(pitch * r2d, yaw * r2d, roll * r2d);
        }
        var yaw2 = CrtMath.Atan2(-m[1], m[5]);
        var pitch2 = CrtMath.Atan2(-m[8], xy);
        return new Vector3(pitch2 * r2d, yaw2 * r2d, 0f);
    }

    /// <summary>18173cd10 + 18173cf60: the camera-to-world 3 x 4 from angles in degrees, the origin in the last column.</summary>
    public static float[] AngleMatrix(Vector3 angles, Vector3 origin)
    {
        const float k = 0.017453292f;
        var (sp, cp) = SinCos(angles.X * k);
        var (sy, cy) = SinCos(angles.Y * k);
        var (sr, cr) = SinCos(angles.Z * k);
        var srsp = sr * sp;
        var crsp = cr * sp;
        return
        [
            cy * cp, srsp * cy - cr * sy, crsp * cy - (-sy) * sr, origin.X,
            sy * cp, srsp * sy + cr * cy, crsp * sy - sr * cy, origin.Y,
            -sp, sr * cp, cr * cp, origin.Z,
        ];
    }

    /// <summary>
    /// 18173fd50: a b for 3 x 4s, each row ((az b2 + ay b1) + ax b0) plus a's row masked to its translation
    /// (so the rotation lanes get +0.0 added, which turns -0.0 into +0.0).
    /// </summary>
    public static float[] ConcatTransforms(float[] a, float[] b)
    {
        var o = new float[12];
        for (var i = 0; i < 3; i++)
        {
            float ax = a[i * 4], ay = a[i * 4 + 1], az = a[i * 4 + 2], aw = a[i * 4 + 3];
            for (var j = 0; j < 4; j++)
                o[i * 4 + j] = ((az * b[8 + j] + ay * b[4 + j]) + ax * b[j]) + (j == 3 ? aw : 0f);
        }
        return o;
    }

    /// <summary>181741a70: the inverse of a rotation and translation 3 x 4 (transpose, -R^T t), rescaled unless the first column's length squared is within 0.001 of 1.</summary>
    public static float[] MatrixInvert(float[] m)
    {
        float[] o =
        [
            m[0], m[4], m[8], 0f,
            m[1], m[5], m[9], 0f,
            m[2], m[6], m[10], 0f,
        ];
        float tx = m[3], ty = m[7], tz = m[11];
        o[3] = -((tz * o[2] + ty * o[1]) + tx * o[0]);
        o[7] = -((o[6] * tz + o[5] * ty) + tx * o[4]);
        o[11] = -((o[10] * tz + o[9] * ty) + tx * o[8]);
        var s = (m[4] * m[4] + m[0] * m[0]) + m[8] * m[8];
        if (!(0.001f > MathF.Abs(s - 1f)))
        {
            var inv = s != 0f ? 1f / s : 1f;
            for (var i = 0; i < 12; i++)
                o[i] *= inv;
        }
        return o;
    }

    /// <summary>
    /// 18174fb00 over 18174ef50's frustum corners (forward (0, 0, 1), up (0, 1, 0), right (1, -0, -0)) at the
    /// near plane: P00 = 2n / (x1 - x0), P11 = 2n / (y1 - y0), P02, P12 the offsets, P22 = f / (n - f), P23 =
    /// n f / (n - f), P32 = -1. The aspect is clamped below at 1/1024.
    /// </summary>
    public static float[] Perspective(float near, float far, float fov, float aspect, float x0, float y0, float x1, float y1)
    {
        if (fov != 90f)
            throw new NotSupportedException("only the cubemap views' FOV of 90 (tan exactly 1) is ported");
        var t = TanHalfFov90;
        var halfHeight = t / MathF.Max(0.0009765625f, aspect) * near;
        var halfWidth = t * near;
        // Corners: centre C = (0 n, 0 n, 1 n), up U = (0, 1, 0) halfHeight, right R = (1, -0, -0) halfWidth.
        float cx = 0f * near, cy = 0f * near;
        float ux = 0f * halfHeight, uy = 1f * halfHeight;
        float rx = 1f * halfWidth, ry = -0f * halfWidth;
        var c0x = (rx * x0 + cx) + ux * y0;
        var c0y = uy * y0 + (ry * x0 + cy);
        var c1x = (x1 * rx + cx) + ux * y0;
        var c2y = uy * y1 + (ry * x0 + cy);
        float w = c1x - c0x, h = c2y - c0y, sx = c1x + c0x, sy = c2y + c0y;
        var p = new float[16];
        p[0] = (near + near) / w;
        p[5] = (near + near) / h;
        p[2] = sx / w;
        p[6] = sy / h;
        if (float.IsPositiveInfinity(far))
        {
            p[10] = -1f;
            p[11] = -near;
        }
        else
        {
            var nf = near - far;
            p[10] = far / nf;
            p[11] = near * far / nf;
        }
        p[14] = -1f;
        return p;
    }

    /// <summary>181746490: a b for row-major 4 x 4s, each element a DPPS of a row and a column.</summary>
    public static float[] Mul4(float[] a, float[] b)
    {
        var o = new float[16];
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
                o[i * 4 + j] = Dot4(a[i * 4], a[i * 4 + 1], a[i * 4 + 2], a[i * 4 + 3], b[j], b[4 + j], b[8 + j], b[12 + j]);
        return o;
    }

    /// <summary>DPPS with mask 0xff: (a0 b0 + a1 b1) + (a2 b2 + a3 b3), each step rounded.</summary>
    public static float Dot4(float a0, float a1, float a2, float a3, float b0, float b1, float b2, float b3)
    {
        if (Sse41.IsSupported)
            return Sse41.DotProduct(Vector128.Create(a0, a1, a2, a3), Vector128.Create(b0, b1, b2, b3), 0xff).ToScalar();
        return (a0 * b0 + a1 * b1) + (a2 * b2 + a3 * b3);
    }
}
