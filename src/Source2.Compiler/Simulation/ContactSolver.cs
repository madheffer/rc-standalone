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
        /// <summary>The surface's density in kg per cubic inch (the vsurf's times 1.6387063e-5, FUN_180072c70).</summary>
        [FieldOffset(0x00)] public float Density;
        [FieldOffset(0x04)] public float Friction;
        [FieldOffset(0x08)] public float Restitution;

        /// <summary>The vsurf's thickness (a surface of a hollow object).</summary>
        [FieldOffset(0x0c)] public float Thickness;
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

    /// <summary>What a contact brings to the prepare besides its cache.</summary>
    /// <param name="MaterialA">Shape A's material.</param>
    /// <param name="MaterialB">Shape B's material.</param>
    /// <param name="Slop">Subtracted from every separation (contact+0x94).</param>
    /// <param name="SoftCap">The soft contact's largest separation it pulls in (contact+0x8c).</param>
    public readonly record struct ContactSetup(Material MaterialA, Material MaterialB, float Slop, float SoftCap);

    /// <summary>A soft contact's spring and damper, per second.</summary>
    private const float TwoPi = 6.28318548f;

    /// <summary>Approach speed below which restitution applies.</summary>
    private const float BounceSpeed = -40f;

    /// <summary>
    /// Writes a contact's rows from its cache and warm starts both bodies with
    /// the cached impulses (FUN_1801d3fc0, with the point loop FUN_1801d3360).
    /// The header's bodies and mass scales are the caller's; the prepare fills
    /// in the manifold count and the rows after it.
    /// </summary>
    /// <param name="stream">The contact's rows, starting at its header.</param>
    /// <param name="bodies">The island's solver bodies.</param>
    /// <param name="cache">The contact's cached manifolds.</param>
    /// <param name="setup">Materials and separation settings.</param>
    /// <param name="dt">The step.</param>
    /// <param name="warmStart">False zeroes the cached impulses instead.</param>
    /// <returns>The bytes the contact's rows take.</returns>
    public static int Prepare(Span<byte> stream, Span<SolverBody> bodies, Span<CachedManifold> cache,
                              in ContactSetup setup, float dt, bool warmStart)
    {
        ref var header = ref MemoryMarshal.AsRef<ContactHeader>(stream);
        ref var a = ref bodies[header.BodyA];
        ref var b = ref bodies[header.BodyB];
        header.ManifoldCount = cache.Length;
        var massA = header.MassScaleA * a.InvMass;
        var massB = header.MassScaleB * b.InvMass;
        var ia = Scaled(header.MassScaleA, a.WorldInvInertia);
        var ib = Scaled(header.MassScaleB, b.WorldInvInertia);
        var restitutionScale = (a.NoDynamicContact & b.NoDynamicContact) == 0 ? 0f : 1f;
        var mix = MixMaterials(a.FrictionScale, b.FrictionScale, restitutionScale, setup.MaterialA, setup.MaterialB);

        var va = a.V;
        var wa = a.W;
        var vb = b.V;
        var wb = b.W;
        var at = 0x18;
        foreach (ref var m in cache)
        {
            ref var row = ref MemoryMarshal.AsRef<ManifoldRow>(stream[at..]);
            row.PointCount = m.PointCount;
            row.Size = m.PointCount * 0x5c + 0xc8;
            var points = MemoryMarshal.Cast<byte, PointRow>(stream.Slice(at + 0xc8, m.PointCount * 0x5c));
            var (frictionLimit, twistLimit) = PreparePoints(points, ref m, a, b, massA, ia, massB, ib, mix, setup, dt);

            for (var i = 0; i < points.Length; i++)
            {
                ref var p = ref points[i];
                if (!warmStart)
                {
                    p.Impulse = 0f;
                    continue;
                }
                var l = p.Impulse;
                var (nx, ny, nz) = (l * p.Normal.X, p.Normal.Y * l, p.Normal.Z * l);
                va = new(va.X - nx * p.InvMassA, va.Y - ny * p.InvMassA, va.Z - nz * p.InvMassA);
                wa = new(wa.X - l * p.AngularA.X, wa.Y - l * p.AngularA.Y, wa.Z - l * p.AngularA.Z);
                vb = new(vb.X + nx * p.InvMassB, vb.Y + ny * p.InvMassB, vb.Z + nz * p.InvMassB);
                wb = new(wb.X + l * p.AngularB.X, wb.Y + l * p.AngularB.Y, wb.Z + l * p.AngularB.Z);
            }

            var c = m.Centre;
            var ra = new Vec3(c.X - a.Position.X, c.Y - a.Position.Y, c.Z - a.Position.Z);
            var rb = new Vec3(c.X - b.Position.X, c.Y - b.Position.Y, c.Z - b.Position.Z);
            ref var f = ref row.Friction;
            PrepareFriction(ref f, massA, ia, ra, massB, ib, rb, m.T1, m.T2, 0f, 0f, m.Impulse1, m.Impulse2,
                            frictionLimit);
            if (!warmStart)
            {
                f.Impulse1 = 0f;
                f.Impulse2 = 0f;
            }
            else
            {
                var (l1, l2) = (f.Impulse1, f.Impulse2);
                var px = (f.T2.X * l2) + (l1 * f.T1.X);
                var py = (l2 * f.T2.Y) + (l1 * f.T1.Y);
                var pz = (l2 * f.T2.Z) + (l1 * f.T1.Z);
                va = new(va.X - f.InvMassA * px, va.Y - f.InvMassA * py, va.Z - f.InvMassA * pz);
                wa = new(wa.X - ((l2 * f.AngularA2.X) + (l1 * f.AngularA1.X)),
                         wa.Y - ((l2 * f.AngularA2.Y) + (l1 * f.AngularA1.Y)),
                         wa.Z - ((l2 * f.AngularA2.Z) + (l1 * f.AngularA1.Z)));
                vb = new(vb.X + f.InvMassB * px, vb.Y + f.InvMassB * py, vb.Z + f.InvMassB * pz);
                wb = new(wb.X + ((l2 * f.AngularB2.X) + (l1 * f.AngularB1.X)),
                         wb.Y + ((l2 * f.AngularB2.Y) + (l1 * f.AngularB1.Y)),
                         wb.Z + ((l2 * f.AngularB2.Z) + (l1 * f.AngularB1.Z)));
            }

            var n = m.Normal;
            var k = ((TwistColumn(ia, ib, n, 1) * n.Y) + (TwistColumn(ia, ib, n, 0) * n.X))
                    + (TwistColumn(ia, ib, n, 2) * n.Z);
            row.Normal = n;
            row.TwistMass = k * k > SingularMass ? 1f / k : 0f;
            row.TwistBias = 0f;
            row.TwistLimit = twistLimit;
            row.TwistImpulse = m.TwistImpulse;
            row.TwistA = Apply(ia, n);
            row.TwistB = Apply(ib, n);
            if (!warmStart)
            {
                row.TwistImpulse = 0f;
            }
            else
            {
                var l = row.TwistImpulse;
                wa = new(wa.X - l * row.TwistA.X, wa.Y - l * row.TwistA.Y, wa.Z - l * row.TwistA.Z);
                wb = new(wb.X + l * row.TwistB.X, wb.Y + l * row.TwistB.Y, wb.Z + l * row.TwistB.Z);
            }
            at += row.Size;
        }

        a.V = va;
        a.W = wa;
        b.V = vb;
        b.W = wb;
        return at;
    }

    /// <summary>((IA + IB)ᵀ n) along one axis, the twist mass's building block.</summary>
    private static float TwistColumn(in Mat3 ia, in Mat3 ib, Vec3 n, int c)
        => (((ia[c] + ib[c]) * n.X) + ((ia[3 + c] + ib[3 + c]) * n.Y)) + ((ia[6 + c] + ib[6 + c]) * n.Z);

    private static Mat3 Scaled(float s, in Mat3 m)
    {
        var r = new Mat3();
        for (var i = 0; i < 9; i++)
            r[i] = s * m[i];
        return r;
    }

    /// <summary>
    /// The point loop (FUN_1801d3360): anchors both points in the world, works
    /// out each point's bias (soft spring, restitution, speculative gap) and
    /// writes its row. Returns the friction and twist limits the cached normal
    /// impulses give.
    /// </summary>
    private static (float Friction, float Twist) PreparePoints(
        Span<PointRow> rows, ref CachedManifold m, in SolverBody a, in SolverBody b, float massA, in Mat3 ia,
        float massB, in Mat3 ib, Mix mix, in ContactSetup setup, float dt)
    {
        var n = m.Normal;
        var frictionLimit = 0f;
        var twistLimit = 0f;
        for (var i = 0; i < rows.Length; i++)
        {
            ref var cached = ref m.Points[i];
            var pa = Anchor(a, cached.LocalA);
            var pb = Anchor(b, cached.LocalB);
            var ra = new Vec3(pa.X - a.Position.X, pa.Y - a.Position.Y, pa.Z - a.Position.Z);
            var rb = new Vec3(pb.X - b.Position.X, pb.Y - b.Position.Y, pb.Z - b.Position.Z);

            var bias = 0f;
            var softness = 0f;
            if (mix.Frequency > 0f && mix.DampingRatio > 0f)
            {
                var mass = EffectiveMass(massA, ia, CrossA(ra, n), massB, ib, CrossB(rb, n), 0f);
                if (mass > 0f)
                {
                    var omega = mix.Frequency * TwoPi;
                    var k = (omega * mass) * omega;
                    var c = ((mass + mass) * mix.DampingRatio) * omega;
                    softness = (1f / (c + k * dt)) / dt;
                    var s = (((((pb.Y - pa.Y) * n.Y) + ((pb.X - pa.X) * n.X)) + ((pb.Z - pa.Z) * n.Z))
                             - setup.Slop) + 0.03125f;
                    bias = MinSs(setup.SoftCap, s) * ((k * softness) * dt);
                }
            }

            if (0f < mix.Restitution)
            {
                var vn = (((((rb.X * b.W0.Z) - (rb.Z * b.W0.X)) + b.V0.Y)
                           - (((a.W0.Z * ra.X) - (a.W0.X * ra.Z)) + a.V0.Y)) * n.Y
                          + ((((rb.Z * b.W0.Y) - (rb.Y * b.W0.Z)) + b.V0.X)
                             - (((a.W0.Y * ra.Z) - (a.W0.Z * ra.Y)) + a.V0.X)) * n.X)
                         + ((((rb.Y * b.W0.X) - (rb.X * b.W0.Y)) + b.V0.Z)
                            - (((a.W0.X * ra.Y) - (a.W0.Y * ra.X)) + a.V0.Z)) * n.Z;
                vn += 0f;
                if (BounceSpeed > vn)
                    bias += vn * mix.Restitution;
            }

            var gap = (((((pb.X - pa.X) * n.X) + ((pb.Y - pa.Y) * n.Y)) + ((pb.Z - pa.Z) * n.Z)) - setup.Slop)
                      - -0.015625f;
            if (gap > 0f)
                bias += gap / dt;

            PreparePoint(ref rows[i], massA, ia, ra, massB, ib, rb, n, bias, softness, cached.Impulse, mix.Friction);

            var (dx, dy, dz) = (pa.X - m.Centre.X, pa.Y - m.Centre.Y, pa.Z - m.Centre.Z);
            var arm = MaxSs(MathF.Sqrt(((dy * dy) + (dx * dx)) + (dz * dz)), 0.03125f);
            rows[i].TwistArm = arm;
            frictionLimit = (mix.Friction * cached.Impulse) + frictionLimit;
            twistLimit = ((arm * mix.Friction) * cached.Impulse) + twistLimit;
        }
        return (frictionLimit, twistLimit);
    }

    /// <summary>The position pass's stiffness and the largest push per iteration.</summary>
    private const float PositionBeta = 0.1f;
    private const float MaxPositionCorrection = -8f;

    /// <summary>
    /// One position iteration over one contact (FUN_1801d4c10, called with
    /// beta 0.1): a contact whose shapes are both rigid gets each cached point
    /// pushed out along its cached normal, point by point, moving and turning
    /// the bodies as it goes. Soft contacts are left to their springs.
    /// </summary>
    /// <param name="header">The contact's header (bodies and mass scales).</param>
    /// <param name="bodies">The island's solver bodies.</param>
    /// <param name="cache">The contact's cached manifolds.</param>
    /// <param name="setup">Materials and slop.</param>
    /// <returns>The smallest separation met, starting from 0.</returns>
    public static float SolvePosition(in ContactHeader header, Span<SolverBody> bodies,
                                      ReadOnlySpan<CachedManifold> cache, in ContactSetup setup)
    {
        if ((setup.MaterialA.Frequency > 0f && setup.MaterialA.DampingRatio > 0f)
            || (setup.MaterialB.Frequency > 0f && setup.MaterialB.DampingRatio > 0f))
            return 0f;
        ref var a = ref bodies[header.BodyA];
        ref var b = ref bodies[header.BodyB];
        var smallest = 0f;
        foreach (ref readonly var m in cache)
        {
            var n = m.Normal;
            for (var i = 0; i < m.PointCount; i++)
            {
                ref readonly var cached = ref m.Points[i];
                var pa = Anchor(a, cached.LocalA);
                var pb = Anchor(b, cached.LocalB);
                var s = ((((pb.Y - pa.Y) * n.Y) + ((pb.X - pa.X) * n.X)) + ((pb.Z - pa.Z) * n.Z)) - setup.Slop;
                smallest = MinSs(smallest, s);
                var c = MinSs(MaxSs((s + 0.03125f) * PositionBeta, MaxPositionCorrection), 0f);
                if (!(0f > c))
                    continue;

                var ra = new Vec3(pa.X - a.Position.X, pa.Y - a.Position.Y, pa.Z - a.Position.Z);
                var rb = new Vec3(pb.X - b.Position.X, pb.Y - b.Position.Y, pb.Z - b.Position.Z);
                var ca = CrossA(ra, n);
                var cb = CrossA(rb, n);
                var massA = header.MassScaleA * a.InvMass;
                var massB = header.MassScaleB * b.InvMass;
                var ia = Scaled(header.MassScaleA, a.WorldInvInertia);
                var ib = Scaled(header.MassScaleB, b.WorldInvInertia);
                var k = (Quadratic(ia, ca) + (massB + massA)) + Quadratic(ib, cb);
                var lambda = -c / k;
                var p = new Vec3(n.X * lambda, n.Y * lambda, n.Z * lambda);
                ApplyPositionImpulse(ref a, massA, ia, new Vec3(-p.X, -p.Y, -p.Z), ra);
                ApplyPositionImpulse(ref b, massB, ib, p, rb);
            }
        }
        return smallest;
    }

    /// <summary>
    /// Moves and turns a solver body by a position impulse at r (FUN_1801b1750):
    /// q += (I (r x P) / 2, 0) q, renormalised; the position by P m.
    /// </summary>
    public static void ApplyPositionImpulse(ref SolverBody sb, float mass, in Mat3 inertia, Vec3 p, Vec3 r)
    {
        if (sb.InfiniteInertia == 0)
        {
            var c = new Vec3((r.Y * p.Z) - (r.Z * p.Y), (p.X * r.Z) - (r.X * p.Z), (r.X * p.Y) - (p.X * r.Y));
            var theta = Apply(inertia, c);
            var dq = RnMath.Mul(new Quat(theta.X * 0.5f, theta.Y * 0.5f, theta.Z * 0.5f, 0f * 0.5f), sb.Q);
            var q = new Quat(sb.Q.X + dq.X * 1f, sb.Q.Y + dq.Y * 1f, sb.Q.Z + dq.Z * 1f, sb.Q.W + dq.W * 1f);
            Integrator.SetOrientation(ref sb, RnMath.Normalize(q));
        }
        if (sb.InfiniteMass == 0)
            sb.Position = new(p.X * mass + sb.Position.X, p.Y * mass + sb.Position.Y, p.Z * mass + sb.Position.Z);
    }

    /// <summary>
    /// After the last velocity iteration, carries each impulse back into the
    /// contact's cache to warm start the next step (FUN_1801d5880).
    /// </summary>
    public static void StoreImpulses(ReadOnlySpan<byte> stream, Span<CachedManifold> cache)
    {
        var at = 0x18;
        foreach (ref var m in cache)
        {
            ref readonly var row = ref MemoryMarshal.AsRef<ManifoldRow>(stream[at..]);
            m.TwistImpulse = row.TwistImpulse;
            m.Impulse1 = row.Friction.Impulse1;
            m.Impulse2 = row.Friction.Impulse2;
            var points = MemoryMarshal.Cast<byte, PointRow>(stream.Slice(at + 0xc8, row.PointCount * 0x5c));
            for (var i = 0; i < points.Length; i++)
                m.Points[i].Impulse = points[i].Impulse;
            at += row.Size;
        }
    }

    /// <summary>
    /// A body-local contact point in the world: rotated as u = q x p + w p,
    /// p + 2 (q x u), then moved by the body's origin, which is its centre of
    /// mass less the rotated mass-centre offset.
    /// </summary>
    private static Vec3 Anchor(in SolverBody body, Vec3 p)
    {
        var q = body.Q;
        var ux = ((q.Y * p.Z) - (q.Z * p.Y)) + (p.X * q.W);
        var uy = ((q.Z * p.X) - (q.X * p.Z)) + (p.Y * q.W);
        var uz = ((q.X * p.Y) - (q.Y * p.X)) + (p.Z * q.W);
        var tx = (q.Y * uz) - (q.Z * uy);
        var ty = (ux * q.Z) - (q.X * uz);
        var tz = (q.X * uy) - (ux * q.Y);
        var com = RnMath.Rotate(q, body.LocalMassCenter);
        return new(
            ((tx + tx) + p.X) + (body.Position.X - com.X),
            ((ty + ty) + p.Y) + (body.Position.Y - com.Y),
            ((tz + tz) + p.Z) + (body.Position.Z - com.Z));
    }
}
