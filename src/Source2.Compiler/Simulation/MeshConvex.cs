using Source2.Compiler.Physics;

namespace Source2.Compiler.Simulation;

/// <summary>
/// The convex side of a mesh contact: a sphere (type 0), a capsule (1) or a
/// hull (2), with the two shape virtuals the contact asks it for (vfn 0x80,
/// its box in a frame; vfn 0xc0, its bounding sphere in the world).
/// </summary>
public readonly record struct ConvexShape(int Type, HullRef Hull, RoundCollision.Sphere Sphere, RoundCollision.Capsule Capsule)
{
    public static ConvexShape Of(HullRef hull) => new(BroadphaseShape.HullType, hull, default, default);

    public static ConvexShape Of(RoundCollision.Sphere sphere) => new(BroadphaseShape.SphereType, default, sphere, default);

    public static ConvexShape Of(RoundCollision.Capsule capsule) => new(BroadphaseShape.CapsuleType, default, default, capsule);

    /// <summary>A world shape's convex side (hull, sphere or capsule), as the contact reads it.</summary>
    public static ConvexShape Of(RnShape shape) => shape.Type switch
    {
        BroadphaseShape.HullType => Of(shape.Hull),
        BroadphaseShape.SphereType => Of(new RoundCollision.Sphere(shape.Proxy.CentreA, shape.Proxy.Radius)),
        BroadphaseShape.CapsuleType => Of(new RoundCollision.Capsule(shape.Proxy.CentreA, shape.Proxy.CentreB, shape.Proxy.Radius)),
        _ => throw new NotSupportedException($"a shape of type {shape.Type} against a mesh"),
    };

    /// <summary>
    /// The box in a frame (vfn 0x80): a hull's grown by 1/16
    /// (<see cref="MeshCollision.HullBounds"/>); a sphere's (FUN_18024ba70)
    /// and a capsule's (FUN_18024d770) unpadded.
    /// </summary>
    public (Vec3 Min, Vec3 Max) Bounds(in RnTransform xf)
    {
        switch (Type)
        {
            case BroadphaseShape.HullType:
                return MeshCollision.HullBounds(Hull, xf);
            case BroadphaseShape.SphereType:
            {
                var c = Through(xf, Sphere.Centre);
                var r = Sphere.Radius;
                return (new Vec3(c.X - r, c.Y - r, c.Z - r), new Vec3(c.X + r, c.Y + r, c.Z + r));
            }
            default:
            {
                var a = Through(xf, Capsule.A);
                var b = Through(xf, Capsule.B);
                var r = Capsule.Radius;
                float Lo(float p, float q) => (q - r) <= (p - r) ? q - r : p - r;
                float Hi(float p, float q) => (p + r) <= (q + r) ? q + r : p + r;
                return (new Vec3(Lo(a.X, b.X), Lo(a.Y, b.Y), Lo(a.Z, b.Z)), new Vec3(Hi(a.X, b.X), Hi(a.Y, b.Y), Hi(a.Z, b.Z)));
            }
        }
    }

    /// <summary>
    /// The bounding sphere in the world (vfn 0xc0): a hull's
    /// (<see cref="MeshCollision.BoundingSphere"/>, grown by 1/16); a
    /// sphere's centre and radius (FUN_18024c750); a capsule's midpoint and
    /// half length plus radius (FUN_18024f390).
    /// </summary>
    public (Vec3 Centre, float Radius) BoundingSphere(in RnTransform xf)
    {
        ref readonly var m = ref xf.R;
        switch (Type)
        {
            case BroadphaseShape.HullType:
                return MeshCollision.BoundingSphere(Hull, xf);
            case BroadphaseShape.SphereType:
            {
                var c = Sphere.Centre;
                return (new Vec3(((c.Y * m.M3) + (c.X * m.M0)) + (c.Z * m.M6) + xf.T.X,
                                 ((c.Y * m.M4) + (c.X * m.M1)) + (c.Z * m.M7) + xf.T.Y,
                                 ((c.Y * m.M5) + (c.X * m.M2)) + (c.Z * m.M8) + xf.T.Z), Sphere.Radius);
            }
            default:
            {
                Vec3 a = Capsule.A, b = Capsule.B;
                var mz = (b.Z + a.Z) * 0.5f;
                var my = (b.Y + a.Y) * 0.5f;
                var mx = (b.X + a.X) * 0.5f;
                float dz = mz - a.Z, dx = mx - a.X, dy = my - a.Y;
                var half = MathF.Sqrt(((dy * dy) + (dz * dz)) + (dx * dx));
                return (new Vec3(((my * m.M3) + (mx * m.M0)) + (mz * m.M6) + xf.T.X,
                                 ((mx * m.M1) + (my * m.M4)) + (mz * m.M7) + xf.T.Y,
                                 ((mx * m.M2) + (my * m.M5)) + (mz * m.M8) + xf.T.Z), half + Capsule.Radius);
            }
        }
    }

    /// <summary>
    /// The GJK proxy (vfn 0xb0): a hull's (<see cref="ContinuousSolve.ProxyOf(HullRef)"/>);
    /// a sphere's one point (FUN_18012e650) or a capsule's two (FUN_18012e600)
    /// at scale 1, the radius the time of impact adds (+0x38) the shape's own
    /// radius, at least 1/16.
    /// </summary>
    public GjkProxy Proxy() => Type switch
    {
        BroadphaseShape.HullType => ContinuousSolve.ProxyOf(Hull),
        BroadphaseShape.SphereType => new GjkProxy
        {
            Vertices = [Sphere.Centre], Count = 1, Scale = 1f, Radius = Sphere.Radius <= 0.0625f ? 0.0625f : Sphere.Radius,
        },
        _ => new GjkProxy
        {
            Vertices = [Capsule.A, Capsule.B], Count = 2, Scale = 1f, Radius = Capsule.Radius <= 0.0625f ? 0.0625f : Capsule.Radius,
        },
    };

    /// <summary>
    /// The centre in a frame (vfn 0xb8): a hull's scaled centroid; a sphere's
    /// centre (FUN_18024c680); a capsule's midpoint (FUN_18024f2a0).
    /// </summary>
    public Vec3 Centre(in RnTransform xf)
    {
        ref readonly var m = ref xf.R;
        switch (Type)
        {
            case BroadphaseShape.HullType:
            {
                var s = Hull.Scale;
                var c = Hull.Hull.Centroid;
                return SeparationFunction.Mul(xf, new Vec3(s * c.X, s * c.Y, s * c.Z));
            }
            case BroadphaseShape.SphereType:
                return Through(xf, Sphere.Centre);
            default:
            {
                Vec3 a = Capsule.A, b = Capsule.B;
                var mz = (b.Z + a.Z) * 0.5f;
                var mx = (b.X + a.X) * 0.5f;
                var my = (b.Y + a.Y) * 0.5f;
                return new Vec3(((mx * m.M0) + (my * m.M3)) + (mz * m.M6) + xf.T.X,
                                ((mx * m.M1) + (my * m.M4)) + (mz * m.M7) + xf.T.Y,
                                ((mx * m.M2) + (my * m.M5)) + (mz * m.M8) + xf.T.Z);
            }
        }
    }

    /// <summary>The inner radius (vfn 0xc8): a hull's scaled min centroid radius, a sphere's or capsule's radius.</summary>
    public float InnerRadius => Type switch
    {
        BroadphaseShape.HullType => Hull.Hull.MinCentroidRadius * Hull.Scale,
        BroadphaseShape.SphereType => Sphere.Radius,
        _ => Capsule.Radius,
    };

    /// <summary>A point through the frame, summed as the sphere and capsule boxes sum it.</summary>
    private static Vec3 Through(in RnTransform xf, Vec3 p)
    {
        ref readonly var m = ref xf.R;
        return new(((p.Y * m.M3) + (p.X * m.M0)) + (p.Z * m.M6) + xf.T.X,
                   ((p.Y * m.M4) + (p.X * m.M1)) + (p.Z * m.M7) + xf.T.Y,
                   ((p.Y * m.M5) + (p.X * m.M2)) + (p.Z * m.M8) + xf.T.Z);
    }
}
