using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Source2.Compiler.Simulation;

/// <summary>What one TOI pass hands its bodies (the 0x20-byte block FUN_1801ffe80 builds).</summary>
public sealed class ToiPass
{
    /// <summary>+0: the step.</summary>
    public float Dt;

    /// <summary>+4, +5: the first and the last pass.</summary>
    public bool First, Last;

    /// <summary>+6: bodies run one by one (a contact update flushes at once).</summary>
    public bool Serial;

    /// <summary>+8: where a parallel pass's contact updates go (the scratch's collide lists).</summary>
    public CollideLists? Lists;
}

/// <summary>
/// Continuous collision's solve (the tail of CRnWorld::SolveContinuous,
/// FUN_1801ffe80, and CRnWorld::SolveTOIBodies, FUN_1802001b0): every body
/// that moved far enough is swept against the static and kinematic bodies it
/// touches, moved back to the first time of impact, has its contacts updated
/// there, is solved against them alone, and finishes the step from there.
/// </summary>
/// <remarks>
/// Valve runs a pass's bodies on threads; every list they fill is sorted
/// before use and a body only reads bodies no other body of the pass
/// writes, so running them in order gives the same result.
/// </remarks>
public static class ContinuousSolve
{
    /// <summary>The tail of FUN_1801ffe80: the dynamic list, then the bullet list (+0xab0).</summary>
    public static void Run(RnWorld w, float dt)
    {
        SolveToiBodies(w, new ToiPass { Dt = dt, First = true, Serial = true }, w.ContinuousBodies[0]);
        SolveToiBodies(w, new ToiPass { Dt = dt, First = true, Serial = true }, w.ContinuousBodies[1]);
    }

    private readonly struct MoreShapes : ILess<RnBody>
    {
        public bool Less(in RnBody a, in RnBody b) => b.Shapes.Count < a.Shapes.Count;
    }

    /// <summary>
    /// FUN_1802001b0: up to world +0x1c0 passes. Each pass runs every listed
    /// body (a body that must wait for another is run again one by one, last
    /// first), flushes the contact updates, drops the bodies that finished,
    /// and refreshes the broadphase and its new pairs.
    /// </summary>
    public static void SolveToiBodies(RnWorld w, ToiPass pass, List<RnBody> list)
    {
        if (list.Count == 0)
            return;
        var broadphase = w.Broadphase ?? throw new InvalidOperationException("the TOI solve needs the broadphase");
        var results = new int[list.Count];
        for (var p = 0; p < w.ContinuousPasses; p++)
        {
            pass.Last = p == w.ContinuousPasses - 1;
            pass.First = p == 0;
            broadphase.BeginHierarchyUpdate(w.Hierarchy, 1, w.Threads, w.Priority);
            if (w.Threads > 1 && list.Count > 1)
                MsvcSort.Sort(CollectionsMarshal.AsSpan(list), new MoreShapes());
            var lists = new CollideLists();
            pass.Lists = lists;
            pass.Serial = false;
            for (var i = 0; i < list.Count; i++)
                results[i] = ToiBody(w, list[i], pass);
            Flush(w, lists, w.Threads > 1);
            pass.Serial = true;
            pass.Lists = null;
            for (var i = list.Count - 1; i >= 0; i--)
            {
                var result = results[i];
                if (result == 2)
                    result = ToiBody(w, list[i], pass);
                if (result == 0)
                {
                    list[i].State.Flags249 &= unchecked((byte)~0x40);
                    list[i] = list[^1];
                    list.RemoveAt(list.Count - 1);
                }
            }
            broadphase.FinalizeHierarchyUpdate(w.Hierarchy, w.Query);
            w.BuildNewContacts();
            if (list.Count == 0)
                break;
        }
    }

    /// <summary>
    /// FUN_1801be330: the search, then (unless the body must wait) its
    /// sensor contacts brought to where it stopped and its proxies moved over
    /// the rest of its sweep. Returns 0 done, 1 not yet, 2 run again alone.
    /// </summary>
    public static int ToiBody(RnWorld w, RnBody body, ToiPass pass)
    {
        var result = ToiSearch(w, body, pass);
        if (result == 0)
        {
            foreach (var c in ContactLifecycle.ContactsOf(body))
            {
                if ((c.Flags78 & 1) != 0)
                    continue;
                var other = c.A.Body == body ? c.B.Body : c.A.Body;
                if ((other.State.Flags249 & 0x40) == 0 || other.State.BodyType != 2)
                    throw new NotSupportedException("sensor contacts after a TOI (FUN_1801be330) are not ported");
            }
        }
        else if (result == 2)
        {
            return 2;
        }
        var (start, end) = Broadphase.Frames(body.State);
        foreach (var shape in body.Shapes)
        {
            var a = shape.Proxy.ComputeAabb(start);
            var b = shape.Proxy.ComputeAabb(end);
            var box = new Aabb(
                new(b.Min.X <= a.Min.X ? b.Min.X : a.Min.X, b.Min.Y <= a.Min.Y ? b.Min.Y : a.Min.Y, b.Min.Z <= a.Min.Z ? b.Min.Z : a.Min.Z),
                new(a.Max.X <= b.Max.X ? b.Max.X : a.Max.X, a.Max.Y <= b.Max.Y ? b.Max.Y : a.Max.Y, a.Max.Z <= b.Max.Z ? b.Max.Z : a.Max.Z));
            w.Broadphase!.MoveProxy(shape.Proxy, box, false, w.Hierarchy);
        }
        return result;
    }

    /// <summary>Material +0x10 and +0x14 both positive: a soft contact, which continuous collision ignores.</summary>
    private static bool Soft(in ContactSolver.Material m) => m.Frequency > 0f && m.DampingRatio > 0f;

    /// <summary>The other body of a contact on <paramref name="body"/>.</summary>
    private static RnBody Other(RnContact c, RnBody body) =>
        c.A.Body == body ? c.B.Body
        : c.B.Body == body ? c.A.Body
        : throw new InvalidOperationException("a contact on a body's shapes without the body");

    /// <summary>
    /// FUN_1801beac0, the time of impact search of one body: the earliest
    /// impact over its solid contacts with static bodies, flagged kinematic
    /// ones and (for a bullet, +0x24b == 1) dynamic ones, each pair swept from
    /// the later of the two start fractions. The body moves to it; unless it
    /// finished the step, it is solved against the contacts found there (8
    /// velocity and up to 16 position iterations, the others immovable) and
    /// then moves on with its new velocities for the rest of the step.
    /// </summary>
    public static int ToiSearch(RnWorld w, RnBody body, ToiPass pass)
    {
        Observe?.Invoke(body, -1);
        var result = Search(w, body, pass);
        Observe?.Invoke(body, result);
        return result;
    }

    /// <summary>Called around every search, for tests that follow one: with -1 before, with the result after.</summary>
    [ThreadStatic]
    public static Action<RnBody, int>? Observe;

    private static int Search(RnWorld w, RnBody body, ToiPass pass)
    {
        ref var s = ref body.State;
        var bullet = s.ContinuousList == 1;
        var candidates = new List<RnContact>();
        var tMin = 1f;
        var own = Continuous.SweepOf(s);
        foreach (var c in ContactLifecycle.ContactsOf(body))
        {
            if ((c.Flags78 & 1) == 0)
                continue;
            var other = Other(c, body);
            ref var o = ref other.State;
            var include = false;
            if (o.BodyType == 0)
                include = true;
            else if (o.BodyType == 1)
                include = (o.Flags249 & 0x10) != 0;
            else if (bullet && o.ContinuousList != 2)
                include = true;
            if (!include)
                continue;
            if (bullet && (o.Flags249 & 0x40) != 0 && o.BodyType == 2 && !pass.Serial)
                return 2;
            candidates.Add(c);
            if (!(0f < tMin))
                continue;
            Sweep mine, theirs;
            float offset;
            if (s.Cleared1E8 < o.Cleared1E8)
            {
                mine = Continuous.SweepAt(s, o.Cleared1E8, out offset);
                theirs = Continuous.SweepOf(o);
            }
            else
            {
                mine = own;
                theirs = Continuous.SweepAt(o, s.Cleared1E8, out _);
                offset = 0f;
            }
            var t = other == c.A.Body ? ContactToi(c, theirs, mine, tMin) : ContactToi(c, mine, theirs, tMin);
            t += offset;
            tMin = t < tMin ? t : tMin;
        }
        if (Continuous.Advance(ref s, tMin))
            return 0;

        var bodies = new List<SolverBody>();
        var first = new SolverBody();
        Integrator.Build(s, ref first, JoinedToDynamic(body));
        bodies.Add(first);
        if (first.InfiniteMass != 0 && Continuous.Advance(ref s, 1f))
            return 0;

        var hitDynamic = false;
        var hard = false;
        var solve = new List<RnContact>();
        var manifolds = 0;
        foreach (var c in candidates)
        {
            var other = Other(c, body);
            ref var o = ref other.State;
            var xfBody = RnTransform.Of(s);
            var sweep = Continuous.SweepOf(o);
            float t;
            if (o.BodyType == 2 && (o.Flags249 & 0x40) == 0)
                t = 1f;
            else
            {
                var rest = 1f - o.Cleared1E8;
                if (rest <= 1.1920929e-05f)
                    rest = 1.1920929e-05f;
                t = (s.Cleared1E8 - o.Cleared1E8) / rest;
                if (t <= 0f)
                    t = 0f;
                if (1f <= t)
                    t = 1f;
            }
            var xfOther = Continuous.At(sweep, t);
            if (other == c.A.Body)
                UpdateContact(w, c, xfOther, xfBody, pass);
            else
                UpdateContact(w, c, xfBody, xfOther, pass);
            if (c.Manifolds.Count < 1)
                continue;
            solve.Add(c);
            var sb = new SolverBody();
            Integrator.Build(o, ref sb, JoinedToDynamic(other));
            sb.WorldInvInertia = default;
            sb.LocalInvInertia = default;
            sb.InvMass = 0f;
            SetTransform(ref sb, xfOther);
            bodies.Add(sb);
            manifolds += c.Manifolds.Count;
            if (bullet && o.BodyType == 2)
                hitDynamic = true;
            if (!hard)
                hard = HasHardPoint(c);
        }
        var skip = false;
        if (!hard && tMin == 0f)
        {
            skip = true;
            manifolds = 0;
        }
        if (hitDynamic && 0f < tMin)
            return 0;

        var solver = CollectionsMarshal.AsSpan(bodies);
        if (0 < manifolds)
        {
            var streams = new byte[solve.Count][];
            for (var k = 0; k < solve.Count; k++)
            {
                var c = solve[k];
                var size = 0x18;
                foreach (var m in c.Manifolds)
                    size += 0xc8 + 0x5c * m.PointCount;
                var stream = new byte[size];
                ref var header = ref MemoryMarshal.AsRef<ContactHeader>(stream.AsSpan());
                var bodyIsA = c.B.Body != body;
                header.BodyA = bodyIsA ? 0 : k + 1;
                header.BodyB = bodyIsA ? k + 1 : 0;
                header.MassScaleA = 1f;
                header.MassScaleB = 1f;
                ContactSolver.Prepare(stream, solver, CollectionsMarshal.AsSpan(c.Manifolds), Setup(c), pass.Dt, warmStart: false);
                streams[k] = stream;
            }
            if (!hitDynamic)
                for (var iteration = 0; iteration < 8; iteration++)
                    foreach (var stream in streams)
                        ContactSolver.SolveVelocity(stream, solver);
            for (var iteration = 0; ; )
            {
                var ok = true;
                for (var k = 0; k < solve.Count; k++)
                    ok &= SolvePosition(solve[k], MemoryMarshal.AsRef<ContactHeader>(streams[k].AsSpan()), solver, 0.7f, -0.046875f);
                if (ok)
                    break;
                iteration++;
                if (15 < iteration)
                    break;
            }
            foreach (var c in solve)
                if (c.Mesh is { } mesh)
                    mesh.Manifolds = [.. c.Manifolds];
        }

        ref readonly var sb0 = ref solver[0];
        if (sb0.TimeScale != 0f)
        {
            var inverse = 1f / sb0.TimeScale;
            s.LinearVelocity = new(inverse * sb0.V.X, inverse * sb0.V.Y, inverse * sb0.V.Z);
            inverse = 1f / sb0.TimeScale;
            s.AngularVelocity = new(inverse * sb0.W.X, inverse * sb0.W.Y, inverse * sb0.W.Z);
            s.Position = sb0.Position;
            s.Orientation = sb0.Q;
            s.WorldInvInertia = sb0.WorldInvInertia;
        }
        s.SleepTimer = sb0.SleepTimer;
        s.Flags4A &= unchecked((ushort)~0x80);
        var moved = s.PreviousPosition.X != s.Position.X || s.PreviousPosition.Y != s.Position.Y || s.PreviousPosition.Z != s.Position.Z
                    || !(s.PreviousOrientation.X == s.Orientation.X && s.PreviousOrientation.Y == s.Orientation.Y
                         && s.PreviousOrientation.Z == s.Orientation.Z && s.PreviousOrientation.W == s.Orientation.W);
        Continuous.FinishStep(ref s, pass.Dt);
        if (skip)
            return 0;
        if (tMin == 0f && (!moved || !pass.First))
        {
            var sweep = Continuous.SweepOf(s);
            var fraction = 1f;
            foreach (var shape in body.Shapes)
            {
                var f = Fallback(shape, sweep);
                fraction = f <= fraction ? f : fraction;
            }
            if (Continuous.Advance(ref s, fraction))
                return 0;
            Continuous.FinishStep(ref s, pass.Dt);
        }
        if (pass.Last)
        {
            s.Position = s.PreviousPosition;
            s.Orientation = s.PreviousOrientation;
            s.WorldInvInertia = Continuous.WorldInverseInertia(s);
            return 0;
        }
        return 1;
    }

    /// <summary>The joint walk of RnSolverBody_Build; with no joints ported, a body with one throws.</summary>
    private static bool JoinedToDynamic(RnBody b)
    {
        if (b.State.JointHead != 0)
            throw new NotSupportedException("joints (the body +0x70 edge list) are not ported");
        return false;
    }

    private static ContactSolver.ContactSetup Setup(RnContact c) => new(c.A.Material, c.B.Material, c.Slop, c.SoftCap);

    /// <summary>
    /// Whether a contact with a manifold makes the TOI solve run: neither
    /// side soft; for a mesh, some point on a triangle that is not soft.
    /// </summary>
    private static bool HasHardPoint(RnContact c)
    {
        if (Soft(c.A.Material))
            return false;
        if (c.Mesh == null)
            return !Soft(c.B.Material);
        foreach (var m in c.Manifolds)
            if (m.PointCount > 0 && !Soft(c.B.Material))
                return true;
        return false;
    }

    /// <summary>
    /// FUN_1801b93e0 with its check off: the solver body takes the frame's
    /// rotation (as a quaternion, FUN_1802fe400's conversion) and puts its
    /// centre of mass where the frame puts the scaled mass-centre offset.
    /// </summary>
    private static void SetTransform(ref SolverBody sb, in RnTransform xf)
    {
        Integrator.SetOrientation(ref sb, QuatOf(xf.R));
        sb.Position = SeparationFunction.Mul(xf, sb.LocalMassCenter);
    }

    /// <summary>A rotation matrix's quaternion, largest-component first, normalised as the DLL does (zero length gives the identity).</summary>
    public static Quat QuatOf(in Mat3 m)
    {
        var trace = (m.M4 + m.M0) + m.M8;
        Quat q;
        if (!(trace < 0f))
            q = new(m.M5 - m.M7, m.M6 - m.M2, m.M1 - m.M3, trace + 1f);
        else if (m.M4 < m.M0 && m.M8 < m.M0)
            q = new(((m.M0 - m.M4) - m.M8) + 1f, m.M3 + m.M1, m.M2 + m.M6, m.M5 - m.M7);
        else if (m.M4 <= m.M8)
            q = new(m.M2 + m.M6, m.M7 + m.M5, ((m.M8 - m.M0) - m.M4) + 1f, m.M1 - m.M3);
        else
            q = new(m.M3 + m.M1, ((m.M4 - m.M8) - m.M0) + 1f, m.M7 + m.M5, m.M6 - m.M2);
        return RnMath.Normalize(q);
    }

    // ---------------------------------------------------------------- contact update at the impact

    /// <summary>
    /// The TOI's contact update: FUN_1801e4090 (a parallel pass: the update,
    /// then the contact goes to the began, ended and resized lists) or
    /// FUN_1801e45f0 (one by one: the same into lists of its own, flushed at
    /// once by FUN_1801dcb80).
    /// </summary>
    private static void UpdateContact(RnWorld w, RnContact c, in RnTransform xfA, in RnTransform xfB, ToiPass pass)
    {
        var lists = pass.Lists ?? new CollideLists();
        Update(c, xfA, xfB);
        if (c.Manifolds.Count < 1)
        {
            if (c.TouchState == 0)
                lists.Ended.Add(c);
        }
        else
        {
            if ((c.Flags78 & 1) != 0 && (c.A.Reports || c.B.Reports))
                throw new NotSupportedException("contact reports (FUN_1801d1560) are not ported");
            if (c.TouchState != 0)
                lists.Began.Add(c);
            else if ((c.Flags74 & 2) != 0)
                throw new NotSupportedException("persisting touch events (FUN_1801ef380) are not ported");
        }
        if (c.Size98 != c.Size88)
            lists.Resized.Add(c);
        if (pass.Lists == null)
            Flush(w, lists, false);
    }

    /// <summary>The contact's update (vtable slot 2), as the collide pass runs it.</summary>
    private static void Update(RnContact c, in RnTransform xfA, in RnTransform xfB)
    {
        if (c.Mesh is { } mesh)
        {
            MeshCollision.Update(mesh, xfA, c.A.Hull, xfB, c.B.Mesh!, c.B.MeshScale);
            c.Manifolds = [.. mesh.Manifolds];
            c.Size98 = mesh.SizeEstimate;
            return;
        }
        if (c.A.Type != 2 || c.B.Type != 2)
            throw new NotSupportedException($"shape pair {c.A.Type}/{c.B.Type} is not ported");
        if (!Finite(xfA) || !Finite(xfB))
            return;
        CachedManifold result = default;
        var previous = c.Manifolds.Count > 0 ? c.Manifolds[0] : default;
        var old = c.Manifolds.Count > 0 ? new ReadOnlySpan<CachedManifold>(ref previous) : default;
        if (c.Manifolds.Count > 0)
            result = previous;
        var hit = HullCollision.Collide(old, ref result, xfA, c.A.Hull, xfB, c.B.Hull, ref c.Sat);
        c.Manifolds = hit ? [result] : [];
        c.Size98 = MeshCollision.SizeEstimate(c.Manifolds);
    }

    private static bool Finite(in RnTransform xf)
    {
        ReadOnlySpan<float> f =
        [
            xf.R.M0, xf.R.M1, xf.R.M2, xf.R.M3, xf.R.M4, xf.R.M5, xf.R.M6, xf.R.M7, xf.R.M8,
            xf.T.X, xf.T.Y, xf.T.Z,
        ];
        foreach (var v in f)
            if ((BitConverter.SingleToUInt32Bits(v) & 0x7f800000) == 0x7f800000)
                return false;
        return true;
    }

    /// <summary>
    /// FUN_1801dc6a0 (and FUN_1801dcb80 for one contact): the collide
    /// post-pass without its wakes. Lists sorted by key when threaded.
    /// </summary>
    private static void Flush(RnWorld w, CollideLists lists, bool sort)
    {
        if (lists.Lost.Count != 0 || lists.BothAsleep.Count != 0)
            throw new InvalidOperationException("a TOI contact update loses no pair");
        if (sort)
            ContactLifecycle.SortByKey(lists.Began);
        foreach (var c in lists.Began)
        {
            ContactLifecycle.SetTouchState(c, 0);
            if ((c.Flags74 & 2) != 0)
                throw new NotSupportedException("touch events (FUN_1801fe920) are not ported");
            UpdateEdge(w, c);
        }
        lists.Began.Clear();
        if (sort)
            ContactLifecycle.SortByKey(lists.Ended);
        foreach (var c in lists.Ended)
        {
            ContactLifecycle.SetTouchState(c, 1);
            if ((c.Flags74 & 2) != 0)
                throw new NotSupportedException("touch events (FUN_1801feca0) are not ported");
            UpdateEdge(w, c);
        }
        lists.Ended.Clear();
        foreach (var c in lists.Resized)
        {
            if (c.Island is { } island)
                IslandManagerOps.Resize(island, c, c.Size98 - c.Size88);
            c.Size88 = c.Size98;
        }
        lists.Resized.Clear();
    }

    /// <summary>A contact is an island edge while it is listed, solid and touching.</summary>
    private static void UpdateEdge(RnWorld w, RnContact c)
    {
        var want = c.AllIndex != -1 && (c.Flags78 & 1) != 0 && c.TouchState == 0;
        if (want == c.InIsland)
            return;
        if (want)
            IslandManagerOps.ActivateEdge(w.Islands, c);
        else
            IslandManagerOps.DeactivateEdge(w.Islands, c);
    }

    // ---------------------------------------------------------------- position iterations

    private static float MinSs(float a, float b) => a < b ? a : b;

    private static float MaxSs(float a, float b) => a > b ? a : b;

    /// <summary>
    /// CRnContact_SolvePosition (FUN_1801d4c10) with the TOI's arguments: each
    /// cached point pushed out along its normal by <paramref name="beta"/>
    /// of its depth (at most 8). True when the deepest point met is at least
    /// <paramref name="threshold"/>, or the contact is soft.
    /// </summary>
    private static bool SolvePosition(RnContact c, in ContactHeader header, Span<SolverBody> bodies, float beta, float threshold)
    {
        if (Soft(c.A.Material) || (c.Mesh == null && Soft(c.B.Material)))
            return true;
        ref var a = ref bodies[header.BodyA];
        ref var b = ref bodies[header.BodyB];
        var smallest = 0f;
        foreach (ref readonly var m in CollectionsMarshal.AsSpan(c.Manifolds))
        {
            var n = m.Normal;
            for (var i = 0; i < m.PointCount; i++)
            {
                if (c.Mesh != null && Soft(c.B.Material))
                    continue;
                ref readonly var cached = ref m.Points[i];
                var pa = Anchor(a, cached.LocalA);
                var pb = Anchor(b, cached.LocalB);
                var s = ((((pb.Y - pa.Y) * n.Y) + ((pb.X - pa.X) * n.X)) + ((pb.Z - pa.Z) * n.Z)) - c.Slop;
                smallest = MinSs(smallest, s);
                var correction = MinSs(MaxSs((s + 0.03125f) * beta, -8f), 0f);
                if (!(0f > correction))
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
                var lambda = -correction / k;
                var p = new Vec3(n.X * lambda, n.Y * lambda, n.Z * lambda);
                ContactSolver.ApplyPositionImpulse(ref a, massA, ia, new Vec3(-p.X, -p.Y, -p.Z), ra);
                ContactSolver.ApplyPositionImpulse(ref b, massB, ib, p, rb);
            }
        }
        return threshold <= smallest;
    }

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

    private static Vec3 CrossA(Vec3 r, Vec3 n) => new(
        (n.Z * r.Y) - (n.Y * r.Z),
        (n.X * r.Z) - (r.X * n.Z),
        (r.X * n.Y) - (n.X * r.Y));

    private static float Quadratic(in Mat3 i, Vec3 a)
    {
        var r = ContactSolver.Apply(i, a);
        return ((r.Y * a.Y) + (r.X * a.X)) + (r.Z * a.Z);
    }

    private static Mat3 Scaled(float s, in Mat3 m)
    {
        var r = new Mat3();
        for (var i = 0; i < 9; i++)
            r[i] = s * m[i];
        return r;
    }

    // ---------------------------------------------------------------- contact times of impact

    private static readonly ConditionalWeakTable<Physics.RnHull, GjkProxy> Proxies = new();

    /// <summary>A hull shape's proxy (FUN_180251480): its vertices, box flag and bounds at the shape's scale, radius 1/16.</summary>
    public static GjkProxy ProxyOf(HullRef hull)
    {
        var proxy = Proxies.GetValue(hull.Hull, h => GjkProxy.OfHull(
            h.VertexPositions.Select(v => new Vec3(v.X, v.Y, v.Z)).ToArray(), h.Flags,
            new Vec3(h.BoundsMin.X, h.BoundsMin.Y, h.BoundsMin.Z), new Vec3(h.BoundsMax.X, h.BoundsMax.Y, h.BoundsMax.Z), 1f));
        if (proxy.Scale == hull.Scale)
            return proxy;
        var h = hull.Hull;
        return GjkProxy.OfHull(proxy.Vertices, h.Flags,
            new Vec3(h.BoundsMin.X, h.BoundsMin.Y, h.BoundsMin.Z), new Vec3(h.BoundsMax.X, h.BoundsMax.Y, h.BoundsMax.Z), hull.Scale);
    }

    /// <summary>The contact's time of impact (vtable slot 3): FUN_1803074d0 for two hulls, FUN_180302250 for a hull on a mesh.</summary>
    public static float ContactToi(RnContact c, in Sweep a, in Sweep b, float tMax)
    {
        if (c.Mesh != null)
            return MeshToi(c, a, b, tMax);
        if (c.A.Type != 2 || c.B.Type != 2)
            throw new NotSupportedException($"TOI of shape pair {c.A.Type}/{c.B.Type} is not ported");
        return ConvexToi(c, a, b, tMax);
    }

    /// <summary>FUN_1803074d0: the proxies' time of impact, adjusted; a soft side gives 1.</summary>
    public static float ConvexToi(RnContact c, in Sweep a, in Sweep b, float tMax)
    {
        var proxyB = ProxyOf(c.B.Hull);
        var proxyA = ProxyOf(c.A.Hull);
        var r = TimeOfImpact.Compute(a, proxyA, b, proxyB, tMax, 0x20);
        var t = Adjust(c, a, b, r);
        if (t < 1f && (Soft(c.A.Material) || Soft(c.B.Material)))
            t = 1f;
        return t;
    }

    /// <summary>
    /// FUN_1801d1910: a touching or overlapping result keeps its time, a
    /// separated one is 1, and a failed search falls back to how far each
    /// shape may move safely (the smaller of <see cref="Fallback"/>).
    /// </summary>
    private static float Adjust(RnContact c, in Sweep a, in Sweep b, (ToiState State, float T) r)
    {
        if (r.State is ToiState.Overlapped or ToiState.Touching)
            return r.T;
        if (r.State == ToiState.Separated)
            return 1f;
        var fa = Fallback(c.A, a);
        var fb = Fallback(c.B, b);
        return fa < fb ? fa : fb;
    }

    /// <summary>
    /// FUN_1802552f0: for a sphere, capsule or hull that is not soft, the
    /// fraction of the sweep over which its inner radius (at least 1/32)
    /// covers the travel plus the turn times the offset of its centre from
    /// the mass centre; 1 when the whole sweep is safe.
    /// </summary>
    public static float Fallback(RnShape shape, in Sweep sweep)
    {
        if (shape.Type >= 3 || Soft(shape.Material))
            return 1f;
        if (shape.Type != 2)
            throw new NotSupportedException("the fallback of a sphere or capsule is not ported");
        var identity = new RnTransform { R = new Mat3 { M0 = 1f, M4 = 1f, M8 = 1f } };
        var hull = shape.Hull;
        var point = SeparationFunction.Mul(identity, ScaledCentroid(hull));
        var dx = point.X - sweep.LocalCenter.X;
        var dy = point.Y - sweep.LocalCenter.Y;
        var dz = point.Z - sweep.LocalCenter.Z;
        var r = MathF.Sqrt((dy * dy + dx * dx) + dz * dz);
        var q0 = sweep.Q0;
        var w = RnMath.Mul(new Quat(-q0.X, -q0.Y, -q0.Z, q0.W), sweep.Q).W;
        w = w > -1f ? w : -1f;
        w = w < 1f ? w : 1f;
        var angle = Acos(w);
        var degrees = (angle + angle) * 57.2957764f;
        if (degrees > 180f)
            degrees += -360f;
        var tx = sweep.C.X - sweep.C0.X;
        var ty = sweep.C.Y - sweep.C0.Y;
        var tz = sweep.C.Z - sweep.C0.Z;
        var travel = MathF.Sqrt((tx * tx + ty * ty) + tz * tz);
        var motion = travel + (degrees * 0.0174532924f) * r;
        var inner = hull.Hull.MinCentroidRadius * hull.Scale;
        inner = inner > 0.03125f ? inner : 0.03125f;
        return inner > motion ? 1f : inner / motion;
    }

    /// <summary>
    /// tier0's V_acosf: the argument clamped to [-1, 1], then the CRT's
    /// acosf linked into tier0 (1802df0a4): a rational approximation in
    /// r = x^2, or r = (1 - |x|) / 2 with sqrt(r) split in two above 1/2,
    /// finished in double for the small and negative cases.
    /// </summary>
    public static float Acos(float x)
    {
        x = x > -1f ? x : -1f;
        x = x < 1f ? x : 1f;
        var bits = BitConverter.SingleToUInt32Bits(x);
        var exponent = (bits >> 23) & 0xff;
        if (exponent < 0x65)
            return 1.57079637f;
        if (exponent >= 0x7f)
            return x == 1f ? 0f : x == -1f ? 3.14159274f : throw new NotSupportedException("acosf outside [-1, 1]");
        var a = (bits & 0x80000000) != 0 ? -x : x;
        float r, s = 0f;
        var large = exponent >= 0x7e;
        if (large)
        {
            r = (1f - a) * 0.5f;
            s = MathF.Sqrt(r);
            a = s;
        }
        else
        {
            r = a * a;
        }
        var u = -0.0133819291f - r * 0.00396137452f;
        u *= r;
        u -= 0.0565298684f;
        u *= r;
        u += 0.184161603f;
        u *= r;
        u /= 1.10496962f - r * 0.836411297f;
        const double PiOver2 = 1.5707963267948966;
        const double PiOver2Lo = 6.123233995736766e-17;
        if (!large)
            return (float)(PiOver2 - ((double)x - (PiOver2Lo - (double)(u * x))));
        if ((bits & 0x80000000) != 0)
        {
            var t = ((double)(u * a) - PiOver2Lo) + (double)s;
            return (float)(3.1415926535897931 - (t + t));
        }
        var c = BitConverter.UInt32BitsToSingle(BitConverter.SingleToUInt32Bits(s) & 0xffff0000);
        var f = (r - c * c) / (c + s);
        return ((s + s) * u + (f + f)) + c * 2f;
    }

    private static Vec3 ScaledCentroid(HullRef hull)
    {
        var s = hull.Scale;
        var c = hull.Hull.Centroid;
        return new(s * c.X, s * c.Y, s * c.Z);
    }

    /// <summary>
    /// FUN_180302250: a hull swept over a mesh. The hull's box in mesh space
    /// at the start and at <paramref name="tMax"/> (against the mesh's end)
    /// gives a swept box; the triangles it meets (<see cref="MeshSweep.Query"/>)
    /// are each swept against the hull, with both sweeps moved to start at the
    /// origin and the triangles expressed there. A time counts when the hull
    /// is then on the triangle's front and the triangle is not soft. From 150
    /// triangles on Valve searches them in parallel, each up to
    /// <paramref name="tMax"/>; below, one by one, each up to the best so far.
    /// </summary>
    public static float MeshToi(RnContact c, in Sweep sweepA, in Sweep sweepB, float tMax)
    {
        if (Soft(c.A.Material))
            return 1f;
        var mesh = c.B.Mesh!;
        var scale = c.B.MeshScale;
        var a = sweepA;
        var b = sweepB;
        var hull = c.A.Hull;
        var proxyA = ProxyOf(hull);
        var rel0 = MeshCollision.Relative(Continuous.Frame(a.Q0, a.LocalCenter, a.C0), Continuous.Frame(b.Q0, b.LocalCenter, b.C0));
        var rel1 = MeshCollision.Relative(Continuous.At(a, tMax), Continuous.Frame(b.Q, b.LocalCenter, b.C));
        var (min0, max0) = MeshCollision.HullBounds(hull, rel0);
        var (min1, max1) = MeshCollision.HullBounds(hull, rel1);
        var c0 = new Vec3((max0.X + min0.X) * 0.5f, (max0.Y + min0.Y) * 0.5f, (max0.Z + min0.Z) * 0.5f);
        var hx = (max0.X - min0.X) * 0.5f;
        var hy = (max0.Y - min0.Y) * 0.5f;
        var hz = (max0.Z - min0.Z) * 0.5f;
        var h1x = (max1.X - min1.X) * 0.5f;
        var h1y = (max1.Y - min1.Y) * 0.5f;
        var h1z = (max1.Z - min1.Z) * 0.5f;
        hx = hx <= h1x ? h1x : hx;
        hy = hy <= h1y ? h1y : hy;
        hz = hz <= h1z ? h1z : hz;
        var delta = new Vec3((max1.X + min1.X) * 0.5f - c0.X, (max1.Y + min1.Y) * 0.5f - c0.Y, (max1.Z + min1.Z) * 0.5f - c0.Z);
        var half = new Vec3(hx + 0.0625f, hy + 0.0625f, hz + 0.0625f);
        var triangles = new List<int>();
        MeshSweep.Query(mesh, scale, c0, delta, half, false, triangles);
        if (triangles.Count == 0)
            return tMax;

        var d = new Vec3(b.C0.X - a.C0.X, b.C0.Y - a.C0.Y, b.C0.Z - a.C0.Z);
        a.C = new(a.C.X - a.C0.X, a.C.Y - a.C0.Y, a.C.Z - a.C0.Z);
        b.C = new(b.C.X - b.C0.X, b.C.Y - b.C0.Y, b.C.Z - b.C0.Z);
        a.C0 = default;
        b.C0 = default;
        var offset = Unrotate(b.Q0, d);

        var best = tMax;
        var parallel = triangles.Count >= 0x96;
        var corners = new Vec3[3];
        var triangle = new GjkProxy { Vertices = corners, Count = 3, Scale = 1f, Radius = 0.0625f };
        foreach (var index in triangles)
        {
            var current = best;
            if (current == 0f)
                break;
            var (ia, ib, ic) = mesh.Triangles[index];
            corners[0] = Place(mesh.Vertices[ia], scale, offset);
            corners[1] = Place(mesh.Vertices[ib], scale, offset);
            corners[2] = Place(mesh.Vertices[ic], scale, offset);
            var r = TimeOfImpact.Compute(a, proxyA, b, triangle, parallel ? tMax : current, 0x20);
            var t = Adjust(c, a, b, r);
            if (!(t < current))
                continue;
            var point = SeparationFunction.Mul(Continuous.At(a, t), ScaledCentroid(hull));
            var xfB = Continuous.At(b, t);
            var v0 = corners[0];
            var v1 = corners[1];
            var v2 = corners[2];
            var nx = (v2.Z - v0.Z) * (v1.Y - v0.Y) - (v1.Z - v0.Z) * (v2.Y - v0.Y);
            var ny = (v1.Z - v0.Z) * (v2.X - v0.X) - (v2.Z - v0.Z) * (v1.X - v0.X);
            var nz = (v2.Y - v0.Y) * (v1.X - v0.X) - (v1.Y - v0.Y) * (v2.X - v0.X);
            var local = SeparationFunction.RotT(xfB, SeparationFunction.Sub(point, xfB.T));
            var side = (((local.Y * ny) + (local.Z * nz)) + (local.X * nx)) - (((v0.Z * nz) + (v0.Y * ny)) + (v0.X * nx));
            if (0f > side)
                continue;
            if (Soft(c.B.Material))
                continue;
            best = best < t ? best : t;
        }
        return best;
    }

    private static Vec3 Place(System.Numerics.Vector3 v, Vec3 scale, Vec3 offset) =>
        new(v.X * scale.X + offset.X, v.Y * scale.Y + offset.Y, v.Z * scale.Z + offset.Z);

    /// <summary>d rotated by the conjugate of q: t = 2 (d x q), (d + w t) + (t x q).</summary>
    private static Vec3 Unrotate(Quat q, Vec3 d)
    {
        var tx = q.Z * d.Y - q.Y * d.Z;
        var ty = q.X * d.Z - q.Z * d.X;
        var tz = q.Y * d.X - q.X * d.Y;
        tx += tx;
        ty += ty;
        tz += tz;
        return new(
            (d.X + q.W * tx) + (q.Z * ty - q.Y * tz),
            (d.Y + q.W * ty) + (q.X * tz - q.Z * tx),
            (d.Z + q.W * tz) + (q.Y * tx - q.X * ty));
    }
}
