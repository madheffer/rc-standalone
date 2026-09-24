using System.Runtime.InteropServices;

namespace Source2.Compiler.Simulation;

/// <summary>
/// Rubikon's serial island solve (FUN_1803111e0 / FUN_180310dc0): build the
/// solver bodies and integrate their velocities, prepare the contacts, run the
/// velocity iterations, integrate the transforms (and test for sleep), run the
/// position iterations, write the bodies back.
///
/// <para>Contacts between two dynamic bodies are solved before contacts with a
/// static or kinematic body, each group in the order its contacts joined the
/// island. That order is the caller's to keep; it decides every float.</para>
/// </summary>
public static class IslandSolver
{
    /// <summary>A contact as the island holds it.</summary>
    public sealed class Contact
    {
        /// <summary>Solver body index of shape A's body (contact+0x6c) and of B's (+0x70).</summary>
        public int BodyA, BodyB;

        /// <summary>The cached manifolds the narrowphase left.</summary>
        public CachedManifold[] Cache = [];

        public ContactSolver.ContactSetup Setup;
    }

    /// <summary>What the world passes down to an island solve.</summary>
    /// <param name="Dt">The step.</param>
    /// <param name="Gravity">The world's gravity (world+0x190).</param>
    /// <param name="AirDensity">world+0x1a0.</param>
    /// <param name="VelocityIterations">The world's minimum (world+0x1bc, 8).</param>
    /// <param name="PositionIterations">The world's minimum (world+0x1b8, 2).</param>
    /// <param name="Sleeping">Whether bodies may fall asleep (world+0x208).</param>
    public readonly record struct Settings(
        float Dt, Vec3 Gravity, float AirDensity, int VelocityIterations, int PositionIterations, bool Sleeping);

    /// <summary>
    /// Solves one island. <paramref name="bodies"/> are in island order, static
    /// bodies included (each island keeps its own copy of a static body's solver
    /// body). Returns true when every moving body in it is ready to sleep.
    /// </summary>
    /// <param name="bodies">The island's bodies, written back on return.</param>
    /// <param name="touchesDynamic">Per body: whether an enabled joint ties it to another dynamic body (body +0x70; contacts do not count).</param>
    /// <param name="dynamicPairs">Contacts between two dynamic bodies ("contacts A").</param>
    /// <param name="otherPairs">The rest ("contacts B").</param>
    /// <param name="settings">World settings.</param>
    public static bool Solve(Span<RnBodyState> bodies, ReadOnlySpan<bool> touchesDynamic,
                             IReadOnlyList<Contact> dynamicPairs, IReadOnlyList<Contact> otherPairs,
                             in Settings settings)
    {
        var dt = settings.Dt;
        var velocityIterations = settings.VelocityIterations;
        var positionIterations = settings.PositionIterations;
        foreach (ref readonly var b in bodies)
        {
            velocityIterations = Math.Max(velocityIterations, b.MinVelocityIterations);
            positionIterations = Math.Max(positionIterations, b.MinPositionIterations);
        }

        var solver = new SolverBody[bodies.Length];
        for (var i = 0; i < bodies.Length; i++)
            BuildAndIntegrate(ref bodies[i], ref solver[i], touchesDynamic[i], false, settings);

        var contacts = new List<Contact>(dynamicPairs.Count + otherPairs.Count);
        contacts.AddRange(dynamicPairs);
        contacts.AddRange(otherPairs);
        var streams = new byte[contacts.Count][];
        for (var c = 0; c < contacts.Count; c++)
            streams[c] = Prepare(contacts[c], solver, dt);

        for (var iteration = 0; iteration < velocityIterations; iteration++)
        {
            var last = iteration == velocityIterations - 1;
            for (var c = 0; c < contacts.Count; c++)
            {
                if (streams[c].Length == 4)
                    continue;
                ContactSolver.SolveVelocity(streams[c], solver);
                if (last)
                    ContactSolver.StoreImpulses(streams[c], contacts[c].Cache);
            }
        }

        var asleep = true;
        for (var i = 0; i < solver.Length; i++)
        {
            if (solver[i].BodyType == 0)
                continue;
            Integrator.IntegratePosition(ref solver[i], dt);
            if (!settings.Sleeping)
                continue;
            Integrator.SleepTest(ref solver[i], dt);
            asleep &= solver[i].ReadyToSleep != 0;
        }

        for (var iteration = 0; iteration < positionIterations; iteration++)
            for (var c = 0; c < contacts.Count; c++)
            {
                if (streams[c].Length == 4)
                    continue;
                var header = MemoryMarshal.AsRef<ContactHeader>(streams[c]);
                ContactSolver.SolvePosition(header, solver, contacts[c].Cache, contacts[c].Setup);
            }

        var sleeps = settings.Sleeping && asleep;
        for (var i = 0; i < bodies.Length; i++)
            if (solver[i].BodyType != 0)
                WriteBack(ref bodies[i], solver[i], sleeps, dt);
        return sleeps;
    }

    /// <summary>
    /// The per-body start of a solve (FUN_18030d370, and FUN_18030d070 for a
    /// batch of free bodies): keep the transform the step started from (and
    /// on the first step of a frame the frame's start), build the solver body,
    /// and for a dynamic body without a controller integrate its velocities.
    /// </summary>
    internal static void BuildAndIntegrate(ref RnBodyState b, ref SolverBody sb, bool touchesDynamic, bool first,
                                           in Settings settings, KinematicTarget? target = null)
    {
        b.PreviousPosition = b.Position;
        b.PreviousOrientation = b.Orientation;
        b.Cleared1E8 = 0f;
        if (first)
        {
            b.FrameOrientation = b.Orientation;
            b.FrameLinearVelocity = b.LinearVelocity;
            b.FrameAngularVelocity = b.AngularVelocity;
            b.FrameOrigin = RnTransform.Of(b).T;
        }
        Integrator.Build(b, ref sb, touchesDynamic);
        if (b.BodyType == 0)
            return;
        if ((b.Flags249 & 4) != 0)
        {
            b.Flags249 &= unchecked((byte)~4);
            b.Force = b.SleepingForce;
            b.Torque = b.SleepingTorque;
        }
        if (b.Controller != 0)
        {
            if (target == null)
                throw new ArgumentException("the body has a controller (+0x80) but no target was given");
            target.Drive(ref sb, settings.Dt);
            sb.V0 = sb.V;
            sb.W0 = sb.W;
        }
        else if (b.BodyType == 2)
        {
            Integrator.IntegrateLinear(b, ref sb, settings.Dt, settings.Gravity, settings.AirDensity);
            Integrator.IntegrateAngular(b, ref sb, settings.Dt, settings.AirDensity);
        }
    }

    /// <summary>
    /// Writes a contact's header and rows (FUN_1803100d0 with the prepare). Two
    /// immovable bodies give only the 4-byte type 0x12, which the iterations
    /// skip. Between two bodies of different solve priority, the lower one does
    /// not push the higher unless the higher is immovable.
    /// </summary>
    internal static byte[] Prepare(Contact contact, SolverBody[] solver, float dt)
    {
        ref readonly var a = ref solver[contact.BodyA];
        ref readonly var b = ref solver[contact.BodyB];
        if (a.InfiniteMass != 0 && b.InfiniteMass != 0)
            return BitConverter.GetBytes(0x12);

        var size = 0x18;
        foreach (var m in contact.Cache)
            size += 0xc8 + 0x5c * m.PointCount;
        var stream = new byte[size];
        ref var header = ref MemoryMarshal.AsRef<ContactHeader>(stream.AsSpan());
        header.BodyA = contact.BodyA;
        header.BodyB = contact.BodyB;
        header.MassScaleA = 1f;
        header.MassScaleB = 1f;
        if (a.SolvePriority != b.SolvePriority)
        {
            if (a.SolvePriority - b.SolvePriority < 1)
            {
                if (a.InfiniteMass == 0)
                    header.MassScaleB = 0f;
            }
            else if (b.InfiniteMass == 0)
            {
                header.MassScaleA = 0f;
            }
        }
        ContactSolver.Prepare(stream, solver, contact.Cache, contact.Setup, dt, warmStart: true);
        return stream;
    }

    /// <summary>
    /// Copies a solved body back (FUN_180313990), clears its per-step inputs,
    /// puts it to sleep with its island, and updates its fast-mover bit, which
    /// picks its broadphase tree (FUN_180313090).
    /// </summary>
    private static void WriteBack(ref RnBodyState b, in SolverBody sb, bool sleeps, float dt)
    {
        if (sb.TimeScale != 0f)
        {
            var k = 1f / sb.TimeScale;
            b.LinearVelocity = new(k * sb.V.X, k * sb.V.Y, k * sb.V.Z);
            k = 1f / sb.TimeScale;
            b.AngularVelocity = new(k * sb.W.X, k * sb.W.Y, k * sb.W.Z);
            b.Position = sb.Position;
            b.Orientation = sb.Q;
            b.WorldInvInertia = sb.WorldInvInertia;
        }
        b.SleepTimer = sb.SleepTimer;
        b.Flags4A &= unchecked((ushort)~0x80);
        b.Force = default;
        b.LinearImpulse = default;
        b.LinearVelocityScale = 1f;
        b.Torque = default;
        b.AngularImpulse = default;
        b.AngularVelocityScale = 1f;
        if (sleeps)
            PutToSleep(ref b);

        var fast = (b.Flags249 & 0x80) != 0;
        var nowFast = false;
        if ((b.Flags249 & 4) == 0)
        {
            var v = b.LinearVelocity;
            var m = MathF.Abs(v.X);
            if (m <= MathF.Abs(v.Y))
                m = MathF.Abs(v.Y);
            if (m <= MathF.Abs(v.Z))
                m = MathF.Abs(v.Z);
            nowFast = (fast ? 0.8f : 1f) * 0.66f < m * dt;
        }
        if (fast != nowFast)
            b.Flags249 = (byte)((b.Flags249 & 0x7f) | (nowFast ? 0x80 : 0));
    }

    /// <summary>
    /// Puts a body to sleep (FUN_1801be270): flags it, keeps the transform it
    /// sleeps in, and restarts its sleep timer. Its velocities are left as they are.
    /// </summary>
    internal static void PutToSleep(ref RnBodyState b)
    {
        b.Flags249 |= 4;
        b.PreviousPosition = b.Position;
        b.PreviousOrientation = b.Orientation;
        b.Cleared1E8 = 0f;
        b.SleepTimer = 0f;
    }
}
