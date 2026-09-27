using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// Valve's CTransform: a position, a uniform scale and a quaternion. The map
/// builder moves a brush entity's physics mesh with two of them
/// (FUN_18020b230): the mesh node's, into the world, and the entity's
/// inverse, into the entity. Matrices built this way differ from
/// <see cref="MapMeshes"/>' angle matrices in the last bits (a yaw of 90
/// gives cos 5.96e-8 here, not -4.37e-8), which moves points of a rotated
/// entity by up to 0.001 at map coordinates. Measured against the matrices a
/// compile hands FUN_1802b1ff0, bit for bit.
/// </summary>
public readonly record struct CTransform(Vector3 Position, float Scale, Quaternion Rotation)
{
    /// <summary>A map node's transform: its origin and its angles, scale 1.</summary>
    public static CTransform FromNode(DmxBinary.Element node) => new(
        node.GetValue<Vector3>("origin") ?? Vector3.Zero, 1f,
        AngleQuaternion(node.GetValue<Vector3>("angles") ?? Vector3.Zero));

    // FUN_18125d940: pitch, yaw and roll halved (times 0.00872664619) through
    // V_sincosf, grouped as the binary groups them.
    public static Quaternion AngleQuaternion(Vector3 angles)
    {
        const float Half = 0.00872664619f;
        float sp = MathF.Sin(angles.X * Half), cp = MathF.Cos(angles.X * Half);
        float sy = MathF.Sin(angles.Y * Half), cy = MathF.Cos(angles.Y * Half);
        float sr = MathF.Sin(angles.Z * Half), cr = MathF.Cos(angles.Z * Half);
        return new Quaternion(
            (cy * (sr * cp)) - (sy * (cr * sp)),
            (cy * (cr * sp)) + (sy * (sr * cp)),
            (sy * (cr * cp)) - (cy * (sr * sp)),
            (sy * (sr * sp)) + (cy * (cr * cp)));
    }

    /// <summary>
    /// FUN_181253780 for a scale of 1: the conjugate, normalised by a dpps
    /// length (1.19e-7 standing in for zero), and the position rotated by the
    /// unnormalised conjugate and negated as 0 - v.
    /// </summary>
    public CTransform Inverse()
    {
        if (Scale != 1f)
            throw new NotSupportedException("inverting a scaled CTransform is not ported");
        float a = -Rotation.X, b = -Rotation.Y, c = -Rotation.Z, w = Rotation.W;
        var v = Position;
        var ux = (v.Z * b) - (v.Y * c);
        ux = ux + ux;
        var uy = (v.X * c) - (v.Z * a);
        uy = uy + uy;
        var uz = (v.Y * a) - (v.X * b);
        uz = uz + uz;
        var p = new Vector3(
            0f - (((uz * b) - (uy * c)) + ((w * ux) + v.X)),
            0f - (((ux * c) - (uz * a)) + ((w * uy) + v.Y)),
            0f - (((uy * a) - (ux * b)) + ((w * uz) + v.Z)));
        var len = MathF.Sqrt(((a * a) + (b * b)) + ((c * c) + (w * w)));
        if (len == 0f)
            len = 1.1920929e-07f;
        return new CTransform(p, Scale, new Quaternion(a / len, b / len, c / len, w / len));
    }

    /// <summary>
    /// FUN_181253510 (SIMD): <paramref name="a"/> applied after <paramref name="b"/>.
    /// The position is a's plus a's scale times b's rotated by a (t = 2 q x p,
    /// then (q x t) + (w t + p)); the rotation is a * b, grouped as
    /// a.z-terms + ((a.w b + a.x-terms) + a.y-terms), normalised by a dpps
    /// length; the scale is the product.
    /// </summary>
    public static CTransform Compose(CTransform a, CTransform b)
    {
        Quaternion q = a.Rotation, r = b.Rotation;
        var p = b.Position;
        var tx = (p.Z * q.Y) - (p.Y * q.Z);
        var ty = (p.X * q.Z) - (p.Z * q.X);
        var tz = (p.Y * q.X) - (p.X * q.Y);
        tx += tx;
        ty += ty;
        tz += tz;
        var moved = new Vector3(
            ((((tz * q.Y) - (ty * q.Z)) + ((q.W * tx) + p.X)) * a.Scale) + a.Position.X,
            ((((tx * q.Z) - (tz * q.X)) + ((q.W * ty) + p.Y)) * a.Scale) + a.Position.Y,
            ((((ty * q.X) - (tx * q.Y)) + ((q.W * tz) + p.Z)) * a.Scale) + a.Position.Z);
        var x = (q.Z * -r.Y) + (((q.W * r.X) + (q.X * r.W)) + (q.Y * r.Z));
        var y = (q.Z * r.X) + (((q.W * r.Y) + (q.X * -r.Z)) + (q.Y * r.W));
        var z = (q.Z * r.W) + (((q.W * r.Z) + (q.X * r.Y)) + (q.Y * -r.X));
        var w = (q.Z * -r.Z) + (((q.W * r.W) + (q.X * -r.X)) + (q.Y * -r.Y));
        var len = MathF.Sqrt(((x * x) + (y * y)) + ((z * z) + (w * w)));
        if (len == 0f)
            throw new NotSupportedException("composing to a zero quaternion is not ported");
        return new CTransform(moved, a.Scale * b.Scale, new Quaternion(x / len, y / len, z / len, w / len));
    }

    /// <summary>FUN_181260150: the rotation's 3x4 matrix with the position as its translation.</summary>
    public float[] Matrix()
    {
        if (Scale != 1f)
            throw new NotSupportedException("a scaled CTransform's matrix is not ported");
        float x = Rotation.X, y = Rotation.Y, z = Rotation.Z, w = Rotation.W;
        float x2 = x + x, y2 = y + y, z2 = z + z;
        return [(1f - (y * y2)) - (z * z2), (y2 * x) - (z2 * w), (y2 * w) + (z2 * x), Position.X,
                (z2 * w) + (y2 * x), (1f - (x * x2)) - (z * z2), (z2 * y) - (x2 * w), Position.Y,
                (z2 * x) - (y2 * w), (x2 * w) + (z2 * y), (1f - (x * x2)) - (y * y2), Position.Z];
    }

    /// <summary>
    /// A point moved by this transform, in resourcecompiler's order (a sphere
    /// or capsule node's centres, 180c25810 and 180c25230): t = 2 q x v, then
    /// ((q x t) + v + w t) times the scale plus the position.
    /// </summary>
    public Vector3 TransformPoint(Vector3 v)
    {
        float qx = Rotation.X, qy = Rotation.Y, qz = Rotation.Z, qw = Rotation.W;
        var tx = (v.Z * qy) - (v.Y * qz);
        var ty = (v.X * qz) - (v.Z * qx);
        var tz = (v.Y * qx) - (v.X * qy);
        tx += tx;
        ty += ty;
        tz += tz;
        return new Vector3(
            ((((tz * qy) - (ty * qz)) + v.X + (qw * tx)) * Scale) + Position.X,
            ((((tx * qz) - (tz * qx)) + v.Y + (qw * ty)) * Scale) + Position.Y,
            ((((ty * qx) - (tx * qy)) + v.Z + (qw * tz)) * Scale) + Position.Z);
    }
}
