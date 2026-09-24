using System.Numerics;
using Source2.Compiler.Physics;

namespace Source2.Compiler.Simulation;

/// <summary>
/// A body's mass update (vphysics2 FUN_1801c0880) and the pieces it calls:
/// the shapes' mass properties summed about the body origin (FUN_1801b2c30),
/// the mass, mass centre and inertia written unless set by hand (flags 0x4a
/// bits 8, 10 and 9), then the inverse mass, the world position of the mass
/// centre, the world inverse inertia and the radii the continuous test reads.
/// </summary>
/// <remarks>
/// Runs as the update does with every flag raised, which is where a body
/// ends once its last shape is in and its type is final. Not ported: the
/// proxy refresh (FUN_1801b9ca0) at the end.
/// </remarks>
public static class RnMassUpdate
{
    /// <summary>One shape as the update reads it: its geometry, scale, material and whether its function mask counts it.</summary>
    public readonly record struct Shape(int Type, RnHull? Hull, float HullScale, RnMesh? Mesh, Vector3 MeshScale,
                                        ContactSolver.Material Material, bool Counts)
    {
        /// <summary>A capsule's centres and radius before its scale (<see cref="HullScale"/>).</summary>
        public (Vector3 A, Vector3 B, float Radius)? Capsule { get; init; }

        /// <summary>The capsule at its scale, as the shape keeps it at +0xb8.</summary>
        public (Vector3 A, Vector3 B, float Radius) ScaledCapsule
            => (Capsule!.Value.A * HullScale, Capsule.Value.B * HullScale, Capsule.Value.Radius * HullScale);
    }

    /// <summary>Mass properties: inertia (row major) about the centre, the centre, the mass.</summary>
    public struct Properties
    {
        public Mat3 Inertia;
        public Vec3 Center;
        public float Mass;
    }

    /// <summary>
    /// Runs the update on <paramref name="b"/> whose frame is
    /// <paramref name="origin"/> and <paramref name="orientation"/>.
    /// </summary>
    public static void Run(ref RnBodyState b, IReadOnlyList<Shape> shapes, Vec3 origin, Quat orientation)
    {
        var sum = Sum(b, shapes);
        var flags = b.Flags4A;
        if ((flags & 0x100) == 0)
        {
            var mass = sum.Mass <= 1e6f ? sum.Mass : 1e6f;
            if (mass <= 0f)
                mass = 500f;
            b.InertiaDivisor = mass;
        }
        if ((flags & 0x400) == 0)
            b.LocalMassCenter = sum.Center;
        if ((flags & 0x200) == 0)
        {
            var inertia = sum.Inertia;
            if (b.InertiaDivisor != sum.Mass && 0f < sum.Mass)
            {
                var ratio = b.InertiaDivisor / sum.Mass;
                for (var i = 0; i < 9; i++)
                    inertia[i] *= ratio;
            }
            if (sum.Center.X != b.LocalMassCenter.X || sum.Center.Y != b.LocalMassCenter.Y || sum.Center.Z != b.LocalMassCenter.Z)
            {
                var shift = Parallel(b.InertiaDivisor, new Vec3(b.LocalMassCenter.X - sum.Center.X, b.LocalMassCenter.Y - sum.Center.Y,
                                                                b.LocalMassCenter.Z - sum.Center.Z));
                for (var i = 0; i < 9; i++)
                    inertia[i] += shift[i];
            }
            SetInertia(ref b, inertia);
        }

        var k = 0f;
        if (b.BodyType == 2)
        {
            var s = b.Scale;
            k = (b.TimeScale * b.MassScale) / ((s * s) * s);
        }
        b.InvMass = (1f / b.InertiaDivisor) * k;

        b.Position = CentreOfMass(b, origin, orientation);
        b.WorldInvInertia = Continuous.WorldInverseInertia(b);

        b.PreviousPosition = b.Position;
        b.PreviousOrientation = b.Orientation;
        b.Cleared1E8 = 0f;
        float inner = float.MaxValue, outer = 0f;
        if (b.BodyType != 0)
        {
            var sc = b.Scale;
            var v = new Vec3(sc * b.LocalMassCenter.X, sc * b.LocalMassCenter.Y, sc * b.LocalMassCenter.Z);
            foreach (var shape in shapes)
            {
                var r = InnerRadius(shape);
                if (r <= inner)
                    inner = r;
                var o = OuterRadius(shape, v);
                if (outer <= o)
                    outer = o;
            }
        }
        b.InnerRadius = inner;
        b.OuterRadius = outer;
    }

    /// <summary>
    /// FUN_1801b9ca0 on a dynamic body: the drag axes. The shapes' bounds in
    /// the body (vfunc 0x88, 1/16 out on every side) and their projected
    /// area fractions (FUN_1801b3420: a lone shape's own, vfunc 0x90, several
    /// shapes' largest) make the linear axes, the bounds' extents times the
    /// area across each axis times the inverse mass; the angular axes
    /// integrate the box's half extents against the inverse inertia's
    /// diagonal (FUN_180007ae0). Only hulls are read.
    /// </summary>
    public static void Drag(ref RnBodyState b, IReadOnlyList<Shape> shapes)
    {
        if (b.BodyType != 2 || shapes.Count == 0)
            return;
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = -float.MaxValue, maxY = -float.MaxValue, maxZ = -float.MaxValue;
        foreach (var shape in shapes)
        {
            if (shape.Type != BroadphaseShape.HullType)
                throw new NotSupportedException("drag over a shape that is not a hull");
            var h = shape.Hull!;
            var sc = shape.HullScale;
            float x0 = (sc * h.BoundsMin.X) - 0.0625f, y0 = (h.BoundsMin.Y * sc) - 0.0625f, z0 = (sc * h.BoundsMin.Z) - 0.0625f;
            float x1 = (h.BoundsMax.X * sc) + 0.0625f, y1 = (h.BoundsMax.Y * sc) + 0.0625f, z1 = (h.BoundsMax.Z * sc) + 0.0625f;
            if (x0 <= minX) minX = x0;
            if (maxX <= x1) maxX = x1;
            if (y0 <= minY) minY = y0;
            if (maxY <= y1) maxY = y1;
            if (z0 <= minZ) minZ = z0;
            if (maxZ <= z1) maxZ = z1;
        }
        Vector3 areas;
        if (shapes.Count == 1)
            areas = shapes[0].Hull!.OrthographicAreas;
        else
        {
            areas = Vector3.Zero;
            foreach (var shape in shapes)
            {
                var a = shape.Hull!.OrthographicAreas;
                if (areas.X <= a.X) areas.X = a.X;
                if (areas.Y <= a.Y) areas.Y = a.Y;
                if (areas.Z <= a.Z) areas.Z = a.Z;
            }
        }
        var inverseMass = b.InvMass;
        float lz = maxZ - minZ, ly = maxY - minY, lx = maxX - minX;
        b.LinearDragAxes = new Vec3(((lz * ly) * areas.X) * inverseMass, inverseMass * ((lz * lx) * areas.Y),
                                    inverseMass * ((ly * lx) * areas.Z));
        var k = 0f;
        var s = b.Scale;
        if (b.BodyType == 2)
        {
            var s2 = s * s;
            k = (b.TimeScale * b.InertiaScale) / ((s2 * s) * s2);
        }
        var inverse = 1f / b.InertiaDivisor;
        float m0 = (inverse * b.LocalInvInertia.M0) * k, m4 = (inverse * b.LocalInvInertia.M4) * k, m8 = (inverse * b.LocalInvInertia.M8) * k;
        const float Third = 0.33333334f;
        float hx = lx * 0.5f, hz = lz * 0.5f, hy = ly * 0.5f;
        float x2 = hx * hx, z2 = hz * hz, y2 = hy * hy;
        var z4 = (z2 * 0.5f) * z2;
        var y4 = (y2 * 0.5f) * y2;
        var x4 = (x2 * 0.5f) * x2;
        var a0 = (((hx * (z2 * Third)) * x2) + (hx * z4)) + ((hx * z2) * y2);
        var b0 = (((hx * (y2 * Third)) * x2) + (hx * y4)) + ((hx * y2) * z2);
        var c0 = (((hy * (z2 * Third)) * y2) + (hy * z4)) + ((hy * z2) * x2);
        var d0 = (((hy * (x2 * Third)) * y2) + (hy * x4)) + ((hy * x2) * z2);
        var e0 = (((hz * (y2 * Third)) * z2) + (hz * y4)) + ((y2 * hz) * x2);
        var f0 = (((hz * (x2 * Third)) * z2) + (hz * x4)) + ((hz * x2) * y2);
        b.AngularDragAxes = new Vec3(((a0 * m0) * areas.Y) + ((b0 * m0) * areas.Z),
                                     ((c0 * m4) * areas.X) + ((d0 * m4) * areas.Z),
                                     ((e0 * m8) * areas.X) + ((f0 * m8) * areas.Y));
    }

    /// <summary>
    /// FUN_1801b2c30: each counted shape with mass adds its mass, its mass
    /// times its centre, and its inertia moved to the origin (FUN_1802930d0);
    /// then the centre is divided out and the inertia moved back to it. A
    /// hull reads its shape scale only while the body's scale is 1.
    /// </summary>
    public static Properties Sum(in RnBodyState b, IReadOnlyList<Shape> shapes)
    {
        var sum = new Properties();
        var unit = b.Scale == 1f;
        foreach (var shape in shapes)
        {
            if (!shape.Counts)
                continue;
            var p = shape.Type switch
            {
                BroadphaseShape.HullType => Hull(shape.Hull!, unit ? shape.HullScale : 1f, shape.Material),
                1 => CapsuleProperties(unit ? shape.ScaledCapsule : shape.Capsule!.Value, shape.Material),
                _ => default,
            };
            if (!(0f < p.Mass))
                continue;
            sum.Mass = p.Mass + sum.Mass;
            sum.Center.X = (p.Center.X * p.Mass) + sum.Center.X;
            sum.Center.Z = (p.Center.Z * p.Mass) + sum.Center.Z;
            sum.Center.Y = (p.Center.Y * p.Mass) + sum.Center.Y;
            var shift = Parallel(p.Mass, p.Center);
            for (var i = 0; i < 9; i++)
                sum.Inertia[i] = (p.Inertia[i] + shift[i]) + sum.Inertia[i];
        }
        if (0f < sum.Mass)
        {
            var inverse = 1f / sum.Mass;
            sum.Center = new Vec3(inverse * sum.Center.X, inverse * sum.Center.Y, inverse * sum.Center.Z);
            var shift = Parallel(sum.Mass, sum.Center);
            for (var i = 0; i < 9; i++)
                sum.Inertia[i] -= shift[i];
        }
        return sum;
    }

    /// <summary>
    /// FUN_180292350: a hull's mass properties at scale s and the material's
    /// density (none at zero density). The inertia is the hull's unit one,
    /// transposed, times s^5 density; the mass is its volume times s^3
    /// density. A material with a thickness makes it a shell: the mass is
    /// s^2 area times thickness times density and the inertia scales with it.
    /// </summary>
    public static Properties Hull(RnHull hull, float s, in ContactSolver.Material material)
    {
        var p = new Properties();
        var density = material.Density;
        if (density == 0f)
            return p;
        var m = hull.MassProperties;
        p.Center = new Vec3(s * m[3], s * m[7], s * m[11]);
        var k = ((((s * density) * s) * s) * s) * s;
        p.Inertia = new Mat3
        {
            M0 = k * m[0], M1 = k * m[4], M2 = k * m[8],
            M3 = k * m[1], M4 = k * m[5], M5 = k * m[9],
            M6 = k * m[2], M7 = k * m[6], M8 = k * m[10],
        };
        var solid = (((hull.Volume * density) * s) * s) * s;
        p.Mass = solid;
        if (0f < material.Thickness)
        {
            var shell = (((s * s) * hull.SurfaceArea) * material.Thickness) * density;
            p.Mass = shell;
            var ratio = shell / solid;
            for (var i = 0; i < 9; i++)
                p.Inertia[i] = ratio * p.Inertia[i];
        }
        return p;
    }

    /// <summary>
    /// FUN_180291d80: a capsule's mass properties. Two hemispheres and a
    /// cylinder (or their shells, with a thickness), the inertia built about
    /// the z axis and turned onto the capsule's axis by the rotation taking
    /// z to it (FUN_180089a30, then FUN_180292de0).
    /// </summary>
    public static Properties CapsuleProperties((Vector3 A, Vector3 B, float Radius) c, in ContactSolver.Material material)
    {
        var p = new Properties();
        var d = material.Density;
        if (d == 0f)
            return p;
        var (a, b, r) = c;
        float dz = a.Z - b.Z, dx = a.X - b.X, dy = a.Y - b.Y;
        var len = MathF.Sqrt(((dy * dy) + (dx * dx)) + (dz * dz));
        float sphere, cylinder;
        var t = material.Thickness;
        if (t > 0f)
        {
            sphere = (((r * 12.566371f) * r) * d) * t;
            cylinder = (((r * 6.2831855f) * len) * d) * t;
        }
        else
        {
            sphere = (((r * 4.1887903f) * r) * r) * d;
            cylinder = (((r * 3.1415927f) * r) * len) * d;
        }
        p.Mass = cylinder + sphere;
        var ends = ((sphere * 0.4f) * r) * r;
        var side = ((((r * 3f) * r) + (len * len)) * cylinder) / 12f;
        var axial = (((r * cylinder) * r) * 0.5f) + ends;
        var across = ((((len + len) + (r * 3f)) * (sphere * len)) * 0.125f) + (ends + side);
        p.Center = new Vec3((b.X + a.X) * 0.5f, (b.Y + a.Y) * 0.5f, (b.Z + a.Z) * 0.5f);
        var rotation = new Mat3 { M0 = 1f, M4 = 1f, M8 = 1f };
        if (0f < len)
        {
            float ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
            var n = ((ux * ux) + (uy * uy)) + (uz * uz);
            if (n > 1.17549435e-35f)
            {
                var inverse = 1f / MathF.Sqrt(n);
                ux *= inverse;
                uy *= inverse;
                uz *= inverse;
            }
            else
                ux = uy = uz = 0f;
            rotation = RnMath.Matrix(Between(new Vec3(0f, 0f, 1f), new Vec3(ux, uy, uz)));
        }
        p.Inertia = RnMath.RotateInertia(rotation, new Mat3 { M0 = across, M4 = across, M8 = axial });
        return p;
    }

    /// <summary>
    /// FUN_180089a30: the rotation taking unit vector a to unit vector b, from
    /// their half-way vector h: (h x a... as the lanes compute it, h . a),
    /// normalised by the root of its DPPS square; a half-way vector near zero
    /// takes a perpendicular of a instead, and a zero length the identity.
    /// </summary>
    private static Quat Between(Vec3 a, Vec3 b)
    {
        float hx = (a.X + b.X) * 0.5f, hy = (a.Y + b.Y) * 0.5f, hz = (a.Z + b.Z) * 0.5f;
        Quat q;
        if (((hx * hx) + (hy * hy)) + (hz * hz) <= 1.1920929e-07f)
            q = MathF.Abs(a.X) <= 0.5f ? new Quat(0f, a.Z, -a.Y, 0f) : new Quat(a.Y, -a.X, 0f, 0f);
        else
            q = new Quat((hz * a.Y) - (hy * a.Z), (hx * a.Z) - (hz * a.X), (hy * a.X) - (hx * a.Y),
                         ((hx * a.X) + (hy * a.Y)) + (hz * a.Z));
        var length = MathF.Sqrt(((q.X * q.X) + (q.Y * q.Y)) + ((q.Z * q.Z) + (q.W * q.W)));
        return length == 0f ? Quat.Identity : new Quat(q.X / length, q.Y / length, q.Z / length, q.W / length);
    }

    /// <summary>FUN_1802930d0: the inertia of a point mass m at c about the origin.</summary>
    public static Mat3 Parallel(float m, Vec3 c)
    {
        var xm = c.X * -m;
        var xy = xm * c.Y;
        var xz = xm * c.Z;
        var yz = (c.Y * -m) * c.Z;
        return new Mat3
        {
            M0 = ((c.Y * c.Y) + (c.Z * c.Z)) * m,
            M1 = xy, M2 = xz, M3 = xy,
            M4 = ((c.X * c.X) + (c.Z * c.Z)) * m,
            M5 = yz, M6 = xz, M7 = yz,
            M8 = ((c.X * c.X) + (c.Y * c.Y)) * m,
        };
    }

    /// <summary>
    /// FUN_1801bb2b0: the inertia over the mass, scaled down below 1e11,
    /// inverted by cofactors (zero when the determinant is under 1.2e-35),
    /// stored as the local inverse inertia.
    /// </summary>
    public static void SetInertia(ref RnBodyState b, Mat3 inertia)
    {
        b.Flags4A &= 0xfdff;
        var inverseMass = 1f / b.InertiaDivisor;
        float a0 = inverseMass * inertia[0], a1 = inverseMass * inertia[1], a2 = inverseMass * inertia[2];
        float a3 = inverseMass * inertia[3], a4 = inverseMass * inertia[4], a5 = inverseMass * inertia[5];
        float a6 = inverseMass * inertia[6], a7 = inverseMass * inertia[7], a8 = inverseMass * inertia[8];
        var r0 = MathF.Abs(a0) <= MathF.Abs(a1) ? MathF.Abs(a1) : MathF.Abs(a0);
        var r1 = MathF.Abs(a3) <= MathF.Abs(a4) ? MathF.Abs(a4) : MathF.Abs(a3);
        if (r0 <= MathF.Abs(a2))
            r0 = MathF.Abs(a2);
        if (r1 <= MathF.Abs(a5))
            r1 = MathF.Abs(a5);
        var r2 = MathF.Abs(a6) <= MathF.Abs(a7) ? MathF.Abs(a7) : MathF.Abs(a6);
        if (r0 <= r1)
            r0 = r1;
        if (r2 <= MathF.Abs(a8))
            r2 = MathF.Abs(a8);
        if (r0 <= r2)
            r0 = r2;
        if (1e11f < r0)
        {
            var f = 1e11f / r0;
            a2 *= f; a0 *= f; a1 *= f; a4 *= f; a5 *= f; a7 *= f; a8 *= f; a3 *= f; a6 *= f;
        }
        var c1 = (a5 * a6) - (a8 * a3);
        var c0 = (a4 * a8) - (a5 * a7);
        var c2 = (a7 * a3) - (a4 * a6);
        var det = ((a0 * c0) + (c1 * a1)) + (a2 * c2);
        var m = new Mat3();
        if (1.17549435e-35f <= MathF.Abs(det))
        {
            var inv = 1f / det;
            m.M0 = c0 * inv;
            m.M1 = ((a2 * a7) - (a1 * a8)) * inv;
            m.M2 = ((a1 * a5) - (a4 * a2)) * inv;
            m.M3 = c1 * inv;
            m.M4 = ((a8 * a0) - (a2 * a6)) * inv;
            m.M5 = ((a2 * a3) - (a0 * a5)) * inv;
            m.M6 = c2 * inv;
            m.M7 = ((a1 * a6) - (a0 * a7)) * inv;
            m.M8 = ((a0 * a4) - (a1 * a3)) * inv;
        }
        // No inverse (all zero) stands as the identity (DAT_18055e8a8,
        // measured on atixref's static mesh bodies).
        if (m.M0 == 0f && m.M1 == 0f && m.M2 == 0f && m.M3 == 0f && m.M4 == 0f && m.M5 == 0f && m.M6 == 0f && m.M7 == 0f && m.M8 == 0f)
            m = new Mat3 { M0 = 1f, M4 = 1f, M8 = 1f };
        b.LocalInvInertia = m;
    }

    /// <summary>The mass centre in the world: the scaled local centre rotated by the frame and moved to its origin.</summary>
    public static Vec3 CentreOfMass(in RnBodyState b, Vec3 origin, Quat q)
    {
        var s = b.Scale;
        float vx = s * b.LocalMassCenter.X, vy = s * b.LocalMassCenter.Y, vz = s * b.LocalMassCenter.Z;
        var tx = (vz * q.Y) - (vy * q.Z);
        var ty = (vx * q.Z) - (vz * q.X);
        var tz = (vy * q.X) - (vx * q.Y);
        tx += tx;
        ty += ty;
        tz += tz;
        // Four lanes at once: (t x q) + ((w t) + v), times 1, plus the origin.
        return new Vec3(
            ((((tz * q.Y) - (ty * q.Z)) + ((q.W * tx) + vx)) * 1f) + origin.X,
            ((((tx * q.Z) - (tz * q.X)) + ((q.W * ty) + vy)) * 1f) + origin.Y,
            ((((ty * q.X) - (tx * q.Y)) + ((q.W * tz) + vz)) * 1f) + origin.Z);
    }

    /// <summary>Shape vfunc 200: a hull's least centroid radius times its scale; a capsule's radius; a mesh's 1/16.</summary>
    private static float InnerRadius(in Shape shape)
        => shape.Type switch
        {
            BroadphaseShape.HullType => shape.Hull!.MinCentroidRadius * shape.HullScale,
            1 => shape.ScaledCapsule.Radius,
            _ => 0.0625f,
        };

    /// <summary>
    /// Shape vfunc 0xd0: how far the shape reaches from v, a hull by its
    /// scaled centroid and angular radius, a mesh by the far corner of its
    /// scaled bounds.
    /// </summary>
    private static float OuterRadius(in Shape shape, Vec3 v)
    {
        if (shape.Type == 1)
        {
            // FUN_18024f530: the farther centre, plus the radius.
            var (a, c, radius) = shape.ScaledCapsule;
            float ax = v.X - a.X, az = v.Z - a.Z, cz = v.Z - c.Z, cx = v.X - c.X, ay = v.Y - a.Y, cy = v.Y - c.Y;
            var da = ((ay * ay) + (ax * ax)) + (az * az);
            var dc = ((cy * cy) + (cx * cx)) + (cz * cz);
            if (da <= dc)
                da = dc;
            return MathF.Sqrt(da) + radius;
        }
        if (shape.Type == BroadphaseShape.HullType)
        {
            var h = shape.Hull!;
            var s = shape.HullScale;
            var dx = (s * h.Centroid.X) - v.X;
            var dy = (s * h.Centroid.Y) - v.Y;
            var dz = (s * h.Centroid.Z) - v.Z;
            return MathF.Sqrt(((dx * dx) + (dy * dy)) + (dz * dz)) + (s * h.MaxAngularRadius);
        }
        var mesh = shape.Mesh!;
        var sc = shape.MeshScale;
        var x = v.X - (sc.X * mesh.Min.X);
        var x2 = (sc.X * mesh.Max.X) - v.X;
        if (x <= x2)
            x = x2;
        var y = v.Y - (sc.Y * mesh.Min.Y);
        var y2 = (sc.Y * mesh.Max.Y) - v.Y;
        var z = v.Z - (sc.Z * mesh.Min.Z);
        if (y <= y2)
            y = y2;
        var z2 = (sc.Z * mesh.Max.Z) - v.Z;
        if (z <= z2)
            z = z2;
        return MathF.Sqrt(((z * z) + (y * y)) + (x * x));
    }
}
