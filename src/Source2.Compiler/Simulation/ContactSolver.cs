using System.Runtime.InteropServices;

namespace Source2.Compiler.Simulation;

/// <summary>
/// Rubikon's contact constraint: a sequential impulse solver with soft normal
/// constraints, one central 2-D friction anchor and a twist friction per
/// manifold, warm started from the contact cache.
///
/// <para>Ported from vphysics2 with each float operation in its order, and
/// checked against the DLL's own functions in ContactSolverOracleTests.</para>
/// </summary>
public static class ContactSolver
{
    /// <summary>Below this K squared an effective mass is zero.</summary>
    private const float SingularMass = 1.17549435e-35f;

    /// <summary>Rubikon's material: friction, restitution, and the soft contact's spring.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x18)]
    public struct Material
    {
        [FieldOffset(0x04)] public float Friction;
        [FieldOffset(0x08)] public float Restitution;
        [FieldOffset(0x10)] public float Frequency;
        [FieldOffset(0x14)] public float DampingRatio;
    }

    /// <summary>A pair's mixed material (FUN_1801d17c0).</summary>
    public readonly record struct Mix(float Friction, float Restitution, float Frequency, float DampingRatio);

    /// <summary>
    /// Mixes two materials: friction is the geometric mean of the scaled
    /// frictions, restitution the product, both clamped to [0, 1]; the spring
    /// is the stiffer of the two soft ones (the lower positive frequency).
    /// </summary>
    /// <param name="frictionScaleA">Body A's friction scale.</param>
    /// <param name="frictionScaleB">Body B's friction scale.</param>
    /// <param name="restitutionScale">1, or 0 when either body touches another dynamic body.</param>
    /// <param name="a">A's material.</param>
    /// <param name="b">B's material.</param>
    /// <param name="frictionScale">The world's global friction scale (1).</param>
    public static Mix MixMaterials(float frictionScaleA, float frictionScaleB, float restitutionScale,
                                   in Material a, in Material b, float frictionScale = 1f)
    {
        var friction = MathF.Sqrt((frictionScaleA * a.Friction) * (frictionScaleB * b.Friction)) * frictionScale;
        friction = 0f > friction ? 0f : MinSs(1f, friction);
        var restitution = (a.Restitution * b.Restitution) * restitutionScale;
        restitution = 0f > restitution ? 0f : MinSs(1f, restitution);
        var (frequency, damping) = (b.Frequency, b.DampingRatio);
        if (a.Frequency > 0f)
            (frequency, damping) = b.Frequency > 0f && !(b.Frequency > a.Frequency)
                ? (b.Frequency, b.DampingRatio)
                : (a.Frequency, a.DampingRatio);
        return new(friction, restitution, frequency, damping);
    }

    /// <summary>minss a, b: b when a is not below it (so NaN in b wins).</summary>
    private static float MinSs(float a, float b) => a < b ? a : b;

    /// <summary>
    /// 1 / (mA + mB + aᵀ IA a + bᵀ IB b + extra), or 0 when that is
    /// singular (FUN_1801d1610).
    /// </summary>
    public static float EffectiveMass(float massA, in Mat3 ia, Vec3 a, float massB, in Mat3 ib, Vec3 b, float extra)
    {
        var k = ((Quadratic(ia, a) + (massA + massB)) + Quadratic(ib, b)) + extra;
        return k * k > SingularMass ? 1f / k : 0f;
    }

    /// <summary>(I a) · a, with I a summed column by column.</summary>
    private static float Quadratic(in Mat3 i, Vec3 a)
    {
        var r = Apply(i, a);
        return ((r.Y * a.Y) + (r.X * a.X)) + (r.Z * a.Z);
    }

    /// <summary>Iᵀ a, which is I a for the symmetric inertias it is used on.</summary>
    public static Vec3 Apply(in Mat3 i, Vec3 a) => new(
        ((a.X * i.M0) + (a.Y * i.M3)) + (a.Z * i.M6),
        ((a.Y * i.M4) + (a.X * i.M1)) + (a.Z * i.M7),
        ((a.Y * i.M5) + (a.X * i.M2)) + (a.Z * i.M8));

    /// <summary>r x n, in the operand order the prepare uses for body A.</summary>
    private static Vec3 CrossA(Vec3 r, Vec3 n) => new(
        (n.Z * r.Y) - (n.Y * r.Z),
        (n.X * r.Z) - (r.X * n.Z),
        (r.X * n.Y) - (n.X * r.Y));

    /// <summary>r x n, in the operand order the prepare uses for body B.</summary>
    private static Vec3 CrossB(Vec3 r, Vec3 n) => new(
        (r.Y * n.Z) - (r.Z * n.Y),
        (r.Z * n.X) - (r.X * n.Z),
        (r.X * n.Y) - (r.Y * n.X));

    /// <summary>Writes one normal constraint (FUN_1801d2860).</summary>
    public static void PreparePoint(ref PointRow p, float massA, in Mat3 ia, Vec3 ra, float massB, in Mat3 ib,
                                    Vec3 rb, Vec3 n, float bias, float softness, float impulse, float friction)
    {
        var a = CrossA(ra, n);
        var b = CrossB(rb, n);
        p.Normal = n;
        p.RA = ra;
        p.RB = rb;
        p.Bias = bias;
        p.Softness = softness;
        p.Mass = EffectiveMass(massA, ia, a, massB, ib, b, softness);
        p.Impulse = impulse;
        p.Friction = friction;
        p.InvMassA = massA;
        p.InvMassB = massB;
        p.AngularA = Apply(ia, a);
        p.AngularB = Apply(ib, b);
    }

    /// <summary>Writes a manifold's central friction (FUN_1801d2030).</summary>
    public static void PrepareFriction(ref FrictionRow f, float massA, in Mat3 ia, Vec3 ra, float massB, in Mat3 ib,
                                       Vec3 rb, Vec3 t1, Vec3 t2, float bias1, float bias2,
                                       float impulse1, float impulse2, float limit)
    {
        var a1 = CrossA(ra, t1);
        var b1 = CrossB(rb, t1);
        var a2 = CrossA(ra, t2);
        var b2 = CrossB(rb, t2);
        var q = Apply(ia, a2);
        var w = Apply(ib, b2);
        var k11 = (Quadratic(ia, a1) + (massA + massB)) + Quadratic(ib, b1);
        var k22 = ((((a2.X * q.X) + (a2.Y * q.Y)) + (a2.Z * q.Z)) + (massA + massB))
                  + (((b2.X * w.X) + (b2.Y * w.Y)) + (b2.Z * w.Z));
        var k12 = (((a1.X * q.X) + (a1.Y * q.Y)) + (a1.Z * q.Z)) + (((b1.X * w.X) + (b1.Y * w.Y)) + (b1.Z * w.Z));
        var inverse = 1f / ((k22 * k11) - (k12 * k12));

        f.RA = ra;
        f.RB = rb;
        f.T1 = t1;
        f.T2 = t2;
        f.K00 = k22 * inverse;
        f.K01 = -k12 * inverse;
        f.K10 = -k12 * inverse;
        f.K11 = k11 * inverse;
        f.Bias1 = bias1;
        f.Bias2 = bias2;
        f.Impulse1 = impulse1;
        f.Impulse2 = impulse2;
        f.Limit = limit;
        f.InvMassA = massA;
        f.InvMassB = massB;
        f.AngularA1 = Apply(ia, a1);
        f.AngularA2 = q;
        f.AngularB1 = Apply(ib, b1);
        f.AngularB2 = w;
    }

    /// <summary>
    /// One velocity iteration over one contact's rows (FUN_1801d5e60): each
    /// manifold's twist, then its friction, then its points. Friction and twist
    /// are limited by the normal impulses of the previous iteration.
    /// </summary>
    /// <param name="stream">The contact's rows, starting at its header.</param>
    /// <param name="bodies">The island's solver bodies.</param>
    /// <returns>The bytes the contact's rows take.</returns>
    public static int SolveVelocity(Span<byte> stream, Span<SolverBody> bodies)
    {
        ref var header = ref MemoryMarshal.AsRef<ContactHeader>(stream);
        ref var bodyA = ref bodies[header.BodyA];
        ref var bodyB = ref bodies[header.BodyB];
        var va = bodyA.V;
        var wa = bodyA.W;
        var vb = bodyB.V;
        var wb = bodyB.W;

        var at = 0x18;
        for (var m = 0; m < header.ManifoldCount; m++)
        {
            ref var row = ref MemoryMarshal.AsRef<ManifoldRow>(stream[at..]);

            // Twist.
            var x = (((wb.Y - wa.Y) * row.Normal.Y) + ((wb.X - wa.X) * row.Normal.X))
                    + ((wb.Z - wa.Z) * row.Normal.Z);
            var twist = ((-(x + row.TwistBias)) * row.TwistMass) + row.TwistImpulse;
            twist = MinSs(MaxSs(twist, -row.TwistLimit), row.TwistLimit);
            var dt = twist - row.TwistImpulse;
            row.TwistImpulse = twist;
            wa = new(wa.X - dt * row.TwistA.X, wa.Y - dt * row.TwistA.Y, wa.Z - dt * row.TwistA.Z);
            wb = new(wb.X + dt * row.TwistB.X, wb.Y + dt * row.TwistB.Y, wb.Z + dt * row.TwistB.Z);

            SolveFriction(ref row.Friction, ref va, ref wa, ref vb, ref wb);

            var limitF = 0f;
            var limitT = 0f;
            var points = MemoryMarshal.Cast<byte, PointRow>(stream.Slice(at + 0xc8, row.PointCount * 0x5c));
            for (var i = 0; i < points.Length; i++)
            {
                ref var p = ref points[i];
                var dx = ((((p.RB.Z * wb.Y) - (p.RB.Y * wb.Z)) + vb.X) - va.X) - ((p.RA.Z * wa.Y) - (p.RA.Y * wa.Z));
                var dy = ((((p.RB.X * wb.Z) - (p.RB.Z * wb.X)) + vb.Y) - va.Y) - ((p.RA.X * wa.Z) - (p.RA.Z * wa.X));
                var dz = ((((p.RB.Y * wb.X) - (p.RB.X * wb.Y)) + vb.Z) - va.Z) - ((p.RA.Y * wa.X) - (p.RA.X * wa.Y));
                var vn = (((dy * p.Normal.Y) + (dx * p.Normal.X)) + (dz * p.Normal.Z)) + p.Bias;
                var lambda = (-(vn + p.Impulse * p.Softness)) * p.Mass + p.Impulse;
                lambda = MaxSs(lambda, 0f);
                var d = lambda - p.Impulse;
                p.Impulse = lambda;
                va = new(va.X - p.InvMassA * (p.Normal.X * d), va.Y - p.InvMassA * (p.Normal.Y * d),
                         va.Z - p.InvMassA * (p.Normal.Z * d));
                wa = new(wa.X - d * p.AngularA.X, wa.Y - d * p.AngularA.Y, wa.Z - d * p.AngularA.Z);
                vb = new(vb.X + p.InvMassB * (p.Normal.X * d), vb.Y + p.InvMassB * (p.Normal.Y * d),
                         vb.Z + p.InvMassB * (p.Normal.Z * d));
                wb = new(wb.X + d * p.AngularB.X, wb.Y + d * p.AngularB.Y, wb.Z + d * p.AngularB.Z);
                limitF = limitF + p.Friction * lambda;
                limitT = limitT + (p.Friction * p.TwistArm) * lambda;
            }
            row.Friction.Limit = limitF;
            row.TwistLimit = limitT;
            at += row.Size;
        }

        bodyA.V = va;
        bodyA.W = wa;
        bodyB.V = vb;
        bodyB.W = wb;
        return at;
    }

    /// <summary>The central friction step (FUN_1801d5a00), clamped to a circle.</summary>
    private static void SolveFriction(ref FrictionRow f, ref Vec3 va, ref Vec3 wa, ref Vec3 vb, ref Vec3 wb)
    {
        var dx = ((((wb.Y * f.RB.Z) - (wb.Z * f.RB.Y)) + vb.X) - va.X) - ((wa.Y * f.RA.Z) - (wa.Z * f.RA.Y));
        var dy = ((((wb.Z * f.RB.X) - (wb.X * f.RB.Z)) + vb.Y) - va.Y) - ((wa.Z * f.RA.X) - (wa.X * f.RA.Z));
        var dz = ((((wb.X * f.RB.Y) - (wb.Y * f.RB.X)) + vb.Z) - va.Z) - ((wa.X * f.RA.Y) - (wa.Y * f.RA.X));
        var c1 = (((dy * f.T1.Y) + (dx * f.T1.X)) + (dz * f.T1.Z)) + f.Bias1;
        var c2 = (((dy * f.T2.Y) + (dx * f.T2.X)) + (dz * f.T2.Z)) + f.Bias2;
        var old1 = f.Impulse1;
        var old2 = f.Impulse2;
        var l1 = (((-c1) * f.K00) + ((-c2) * f.K10)) + old1;
        var l2 = (((-c1) * f.K01) + ((-c2) * f.K11)) + old2;
        f.Impulse1 = l1;
        f.Impulse2 = l2;
        var squared = (l2 * l2) + (l1 * l1);
        if (squared > f.Limit * f.Limit)
        {
            var s = f.Limit / MathF.Sqrt(squared);
            l2 = s * l2;
            l1 = s * l1;
            f.Impulse1 = l1;
            f.Impulse2 = l2;
        }
        var d1 = l1 - old1;
        var d2 = l2 - old2;
        var px = (d2 * f.T2.X) + (d1 * f.T1.X);
        var py = (d2 * f.T2.Y) + (d1 * f.T1.Y);
        var pz = (d2 * f.T2.Z) + (d1 * f.T1.Z);
        va = new(va.X - f.InvMassA * px, va.Y - f.InvMassA * py, va.Z - f.InvMassA * pz);
        wa = new(wa.X - ((d1 * f.AngularA1.X) + (d2 * f.AngularA2.X)),
                 wa.Y - ((d1 * f.AngularA1.Y) + (d2 * f.AngularA2.Y)),
                 wa.Z - ((d1 * f.AngularA1.Z) + (d2 * f.AngularA2.Z)));
        vb = new(vb.X + f.InvMassB * px, vb.Y + f.InvMassB * py, vb.Z + f.InvMassB * pz);
        wb = new(wb.X + ((d1 * f.AngularB1.X) + (d2 * f.AngularB2.X)),
                 wb.Y + ((d1 * f.AngularB1.Y) + (d2 * f.AngularB2.Y)),
                 wb.Z + ((d1 * f.AngularB1.Z) + (d2 * f.AngularB2.Z)));
    }

    /// <summary>maxss a, b: b when a is not above it.</summary>
    private static float MaxSs(float a, float b) => a > b ? a : b;
}
