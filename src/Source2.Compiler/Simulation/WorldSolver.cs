namespace Source2.Compiler.Simulation;

/// <summary>
/// What a Solve pass hands to its post-pass: bodies that were solved while
/// out of the awake list (scratch +0x8e0), bodies that fell asleep (+0x918),
/// and contacts to put back in the active list (+0x950). A pass leaves them
/// filled; the next pass wants a new set.
/// </summary>
public sealed class SolveLists
{
    public readonly List<RnBody> Woken = [];
    public readonly List<RnBody> Slept = [];
    public readonly List<RnContact> Reactivated = [];
}

/// <summary>
/// The Solve pass of a step (FUN_1801ff820 with the worker FUN_180311610):
/// every serial island through <see cref="IslandSolver"/>, then the free
/// single bodies in batches of eight, then the post-pass that moves bodies
/// between awake and asleep and puts contacts back in the active list.
/// </summary>
/// <remarks>
/// One thread's order: the worker takes the coloured islands, then the serial
/// ones, then the free batches, and every list it fills is sorted before use,
/// except the two dynamic continuous lists, whose order Valve leaves to its
/// threads. Graph-coloured islands, joints and mesh shapes on moving bodies
/// are not ported and throw. The broadphase side is FUN_1802d4ed0
/// before, the proxy moves of FUN_1801b9270 in each writeback and
/// FUN_1802d6050 after.
/// </remarks>
public static class WorldSolver
{
    public static void Solve(RnWorld w, float dt, bool first, SolveLists lists)
    {
        if (w.ActiveBodies.Count > 0 || w.Count688 > 0)
            w.StepFlags |= 2;
        if (w.Islands.Coloured.Count > 0)
            throw new NotSupportedException("graph-coloured islands (solve list C) are not ported");
        var settings = new IslandSolver.Settings(dt, w.Gravity, w.AirDensity,
            w.VelocityIterations, w.PositionIterations, w.Sleeping);
        w.Broadphase?.BeginHierarchyUpdate(w.Hierarchy, 0, w.Threads, w.Priority);

        foreach (var node in w.Islands.Serial)
            SolveIsland(w, (RnIsland)node, first, settings, lists);

        var free = w.Islands.Free;
        for (var start = 0; start < free.Count; start += 8)
            SolveBatch(w, free, start, first, settings, lists);

        Flush(w, lists);
        w.Broadphase?.FinalizeHierarchyUpdate(w.Hierarchy, w.Query);
    }

    /// <summary>
    /// NoDynamicContact's walk (FUN_1801b6000): a dynamic body joined by an
    /// enabled joint to another dynamic body. Contacts do not count. With no
    /// joints ported, a body with any joint throws.
    /// </summary>
    private static bool JoinedToDynamic(RnBody b)
    {
        if (b.State.JointHead != 0)
            throw new NotSupportedException("joints (the body +0x70 edge list) are not ported");
        return false;
    }

    /// <summary>
    /// One serial island (FUN_1803111e0 / FUN_180310dc0): the iteration counts
    /// are the island's maxima raised to the world's minimum; a removed body's
    /// hole gets a zeroed solver body, which nothing moves or writes back.
    /// </summary>
    private static void SolveIsland(RnWorld w, RnIsland island, bool first, in IslandSolver.Settings settings, SolveLists lists)
    {
        var dt = settings.Dt;
        var velocityIterations = Math.Max(island.VelocityIterations, settings.VelocityIterations);
        var positionIterations = Math.Max(island.PositionIterations, settings.PositionIterations);

        var solver = new SolverBody[island.Bodies.Count];
        for (var i = 0; i < solver.Length; i++)
            if (island.Bodies[i] is { } b)
                IslandSolver.BuildAndIntegrate(ref b.State, ref solver[i], JoinedToDynamic(b), first, settings, b.Target);

        var contacts = new List<(RnContact Contact, IslandSolver.Contact Solve)>();
        foreach (var group in island.Contacts)
            foreach (var c in group)
                contacts.Add((c, new IslandSolver.Contact
                {
                    BodyA = c.SolverA,
                    BodyB = c.SolverB,
                    Cache = [.. c.Manifolds],
                    Setup = new ContactSolver.ContactSetup(c.A.Material, c.B.Material, c.Slop, c.SoftCap),
                }));
        var streams = new byte[contacts.Count][];
        for (var k = 0; k < contacts.Count; k++)
            streams[k] = IslandSolver.Prepare(contacts[k].Solve, solver, dt);

        for (var iteration = 0; iteration < velocityIterations; iteration++)
        {
            var last = iteration == velocityIterations - 1;
            for (var k = 0; k < contacts.Count; k++)
            {
                if (streams[k].Length == 4)
                    continue;
                ContactSolver.SolveVelocity(streams[k], solver);
                if (last)
                    ContactSolver.StoreImpulses(streams[k], contacts[k].Solve.Cache);
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
            for (var k = 0; k < contacts.Count; k++)
            {
                if (streams[k].Length == 4)
                    continue;
                var header = System.Runtime.InteropServices.MemoryMarshal.AsRef<ContactHeader>(streams[k]);
                ContactSolver.SolvePosition(header, solver, contacts[k].Solve.Cache, contacts[k].Solve.Setup);
            }

        // The solve writes the impulses into the contact's manifold block.
        foreach (var (c, s) in contacts)
        {
            c.Manifolds = [.. s.Cache];
            if (c.Mesh is { } mesh)
                mesh.Manifolds = [.. s.Cache];
        }

        var sleeps = settings.Sleeping && asleep;
        for (var i = 0; i < solver.Length; i++)
            if (island.Bodies[i] is { } b)
                WriteBack(w, b, solver[i], sleeps, dt, lists);
    }

    /// <summary>
    /// Up to eight free bodies (the worker's list A loop): started together
    /// (FUN_18030d070), moved (FUN_1803104c0, or FUN_1803106f0 with the sleep
    /// test when sleeping is on), each written back on its own sleep test
    /// (FUN_180313510).
    /// </summary>
    private static void SolveBatch(RnWorld w, List<IslandNode> free, int start, bool first, in IslandSolver.Settings settings, SolveLists lists)
    {
        var dt = settings.Dt;
        var bodies = new RnBody?[8];
        var solver = new SolverBody[8];
        for (var i = 0; i < 8 && start + i < free.Count; i++)
            bodies[i] = ((BodyNode)free[start + i]).Body;
        for (var i = 0; i < 8; i++)
            if (bodies[i] is { } b)
                IslandSolver.BuildAndIntegrate(ref b.State, ref solver[i], JoinedToDynamic(b), first, settings, b.Target);
        for (var i = 0; i < 8; i++)
        {
            if (solver[i].BodyType == 0)
                continue;
            Integrator.IntegratePosition(ref solver[i], dt);
            if (settings.Sleeping)
                Integrator.SleepTest(ref solver[i], dt);
        }
        for (var i = 0; i < 8; i++)
            if (bodies[i] is { } b)
                WriteBack(w, b, solver[i], solver[i].ReadyToSleep != 0, dt, lists);
    }

    /// <summary>
    /// A solved body's writeback (FUN_180313090 for islands, FUN_180313510 for
    /// free bodies; the same code but for where the sleep verdict comes from):
    /// the copy back and the cleared inputs, then either sleep, or a wake if
    /// the body was solved while out of the awake list (with its lost contacts)
    /// and the continuous test; last the fast-mover bit.
    /// </summary>
    private static void WriteBack(RnWorld w, RnBody b, in SolverBody sb, bool sleeps, float dt, SolveLists lists)
    {
        if (sb.BodyType == 0)
            return;
        ref var s = ref b.State;
        var solved = sb;
        if (s.Controller != 0)
            b.Target!.Settle(ref solved);
        CopyBack(ref s, solved);
        if (!sleeps)
        {
            RejectMeshShapes(b);
            if (b.ActiveIndex < 0)
            {
                lists.Woken.Add(b);
                foreach (var c in ContactLifecycle.ContactsOf(b))
                    if (c.ActiveIndex < 0)
                        lists.Reactivated.Add(c);
            }
            if (w.Continuous && (s.Flags249 & 8) != 0 && MovedFar(s))
            {
                JoinedToDynamic(b);
                var list = s.BodyType == 1 ? 2 : s.ContinuousList == 1 ? 1 : 0;
                w.ContinuousBodies[list].Add(b);
                s.Flags249 |= 0x40;
            }
        }
        else
        {
            RejectMeshShapes(b);
            IslandSolver.PutToSleep(ref s);
            lists.Slept.Add(b);
        }

        var fast = (s.Flags249 & 0x80) != 0;
        var nowFast = false;
        if ((s.Flags249 & 4) == 0)
        {
            var v = s.LinearVelocity;
            var m = MathF.Abs(v.X);
            if (m <= MathF.Abs(v.Y))
                m = MathF.Abs(v.Y);
            if (m <= MathF.Abs(v.Z))
                m = MathF.Abs(v.Z);
            nowFast = (fast ? 0.8f : 1f) * 0.66f < m * dt;
        }
        if (fast != nowFast)
            s.Flags249 = (byte)((s.Flags249 & 0x7f) | (nowFast ? 0x80 : 0));
        w.Broadphase?.UpdateBody(b.Proxy, fast != nowFast, w.Hierarchy);
    }

    /// <summary>FUN_180313990 and the cleared per-step inputs.</summary>
    private static void CopyBack(ref RnBodyState b, in SolverBody sb)
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
    }

    /// <summary>A mesh shape with mode other than 3 gets a per-step update here (FUN_1802435d0 / FUN_180242830); not ported.</summary>
    private static void RejectMeshShapes(RnBody b)
    {
        foreach (var shape in b.Shapes)
            if (shape.Type == 3 && shape.MeshMode != 3)
                throw new NotSupportedException("mesh shapes on moving bodies (FUN_1802435d0) are not ported");
    }

    /// <summary>
    /// Whether a body moved more than half its inner radius this step
    /// (FUN_1801b8f40): the rotation from the previous orientation, as the
    /// vector part of 2 (q - p) p*, reaches as far as its length times the outer
    /// radius but no further than outer minus inner; the travel adds to it.
    /// </summary>
    private static bool MovedFar(in RnBodyState b)
    {
        var p = b.PreviousOrientation;
        var q = b.Orientation;
        // dpps 0xff: the lanes pair up as (x + y) + (z + w).
        var dot = (p.X * q.X + p.Y * q.Y) + (p.Z * q.Z + p.W * q.W);
        if (0f > dot)
            p = new Quat(0f - p.X, 0f - p.Y, 0f - p.Z, 0f - p.W);
        var d = new Quat((q.X - p.X) * 2f, (q.Y - p.Y) * 2f, (q.Z - p.Z) * 2f, (q.W - p.W) * 2f);
        var r = RnMath.Mul(d, new Quat(-p.X, -p.Y, -p.Z, p.W));
        var angle = MathF.Sqrt(r.X * r.X + r.Y * r.Y + r.Z * r.Z);
        var reach = angle * b.OuterRadius;
        var limit = b.OuterRadius - b.InnerRadius;
        reach = reach < limit ? reach : limit;
        var dx = b.Position.X - b.PreviousPosition.X;
        var dy = b.Position.Y - b.PreviousPosition.Y;
        var dz = b.Position.Z - b.PreviousPosition.Z;
        var travel = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        return reach + travel > b.InnerRadius * 0.5f;
    }

    /// <summary>
    /// The Solve post-pass (FUN_1801ff820 after the worker): the body lists
    /// sorted by body index and the contact list by key; solved bodies that
    /// were asleep join the awake list, bodies that fell asleep leave it, and
    /// lost contacts of woken bodies go back in the active contacts.
    /// </summary>
    private static void Flush(RnWorld w, SolveLists lists)
    {
        if (w.SortLists)
        {
            ContactLifecycle.SortByIndex(w.ContinuousBodies[2]);
            ContactLifecycle.SortByIndex(lists.Woken);
            ContactLifecycle.SortByIndex(lists.Slept);
            ContactLifecycle.SortByKey(lists.Reactivated);
        }
        foreach (var b in lists.Woken)
            ContactLifecycle.AddAwake(w, b);
        foreach (var b in lists.Slept)
            ContactLifecycle.RemoveAwake(w, b);
        foreach (var c in lists.Reactivated)
            ContactLifecycle.Reactivate(w, c);
    }

    /// <summary>
    /// The first half of the continuous pass (FUN_1801ffe80): every solid
    /// contact of a fast kinematic body sends the body on its other side to
    /// the dynamic continuous list, once (flag 0x40 of body +0x249, which the
    /// kinematic body itself drops). The time of impact solve that follows
    /// (FUN_1802001b0 over +0xa78, then +0xab0) is not ported.
    /// </summary>
    public static void GatherContinuous(RnWorld w)
    {
        foreach (var k in w.ContinuousBodies[2])
        {
            foreach (var c in ContactLifecycle.ContactsOf(k))
            {
                if ((c.Flags78 & 1) == 0)
                    continue;
                var other = c.A.Body == k ? c.B.Body
                    : c.B.Body == k ? c.A.Body
                    : throw new InvalidOperationException("a contact on a body's shapes without the body");
                ref var flags = ref other.State.Flags249;
                if ((flags & 0x40) == 0 && (flags & 8) != 0)
                {
                    w.ContinuousBodies[0].Add(other);
                    flags |= 0x40;
                }
            }
            k.State.Flags249 &= unchecked((byte)~0x40);
        }
        w.ContinuousBodies[2].Clear();
    }
}
