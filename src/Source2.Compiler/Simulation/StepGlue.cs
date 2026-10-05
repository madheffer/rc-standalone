namespace Source2.Compiler.Simulation;

/// <summary>
/// The parts of CRnWorld::Step (FUN_180200840) around the passes: the clock,
/// the applied-force wake, the world-bounds check. In a plain world (no step
/// callbacks, no controllers, no bodies with applied forces, everything inside
/// the bounds) only the clock does anything.
/// </summary>
public sealed class StepClock
{
    /// <summary>CRnWorld+0x1CC: simulated time, dt + time each step.</summary>
    public float Time;

    /// <summary>CRnWorld+0x1D0: the time at the last first-substep.</summary>
    public float PreviousTime;

    /// <summary>CRnWorld+0x1D4: steps taken.</summary>
    public int StepCount;

    /// <summary>CRnWorld+0x48 and +0x94: two per-frame event counts, reset on a first substep.</summary>
    public int Count48, Count94;

    /// <summary>
    /// The bookkeeping of FUN_180200840 before the passes: nothing below a dt
    /// of 1e-6; a first substep resets the frame; then the counter and time.
    /// Returns whether the step runs.
    /// </summary>
    public bool Begin(float dt, bool first)
    {
        if (!(1e-06f < dt))
            return false;
        if (first)
        {
            PreviousTime = Time;
            Count48 = 0;
            Count94 = 0;
        }
        StepCount += 1;
        Time = dt + Time;
        return true;
    }
}

public static class StepGlue
{
    /// <summary>The fixed step the applied-force test assumes, whatever dt is.</summary>
    private const float SixtiethOfASecond = 0.0166666675f;

    /// <summary>
    /// FUN_1801f5b70 for one body in the applied-force list (CRnWorld+0xA58).
    /// A sleeping body whose force or torque changed by enough to move it
    /// over 1/60 s (speed squared over 0.16, spin squared over 0.00487), or
    /// with a velocity scale above 1 and some velocity, is flagged to wake
    /// (+0x4A 0x10); one that stays asleep has its inputs cleared. An awake
    /// body records its force and torque. Either way a body whose recorded
    /// force and torque are all within 0.01 is flagged to leave the list (0x40).
    /// </summary>
    public static void AppliedForceTest(ref RnBodyState b)
    {
        if ((b.Flags249 & 4) != 0)
        {
            var dx = b.Force.X - b.SleepingForce.X;
            var dy = b.Force.Y - b.SleepingForce.Y;
            var dz = b.Force.Z - b.SleepingForce.Z;
            if (!(dx == 0f && dy == 0f && dz == 0f))
            {
                var s = b.InvMass * SixtiethOfASecond;
                var x = dx * s + b.LinearImpulse.X;
                var y = dy * s + b.LinearImpulse.Y;
                var z = dz * s + b.LinearImpulse.Z;
                if ((y * y + x * x) + z * z > 0.160000011f)
                    b.Flags4A |= 0x10;
            }
            var tx = b.Torque.X - b.SleepingTorque.X;
            var ty = b.Torque.Y - b.SleepingTorque.Y;
            var tz = b.Torque.Z - b.SleepingTorque.Z;
            if (!(tx == 0f && ty == 0f && tz == 0f))
            {
                ref readonly var m = ref b.WorldInvInertia;
                var wx = (((m.M3 * ty) + (m.M0 * tx)) + (m.M6 * tz)) * SixtiethOfASecond + b.AngularImpulse.X;
                var wy = (((m.M4 * ty) + (m.M1 * tx)) + (m.M7 * tz)) * SixtiethOfASecond + b.AngularImpulse.Y;
                var wz = (((m.M5 * ty) + (m.M2 * tx)) + (m.M8 * tz)) * SixtiethOfASecond + b.AngularImpulse.Z;
                if ((wy * wy + wx * wx) + wz * wz > 0.00487387972f)
                    b.Flags4A |= 0x10;
            }
            var v = b.LinearVelocity;
            var w = b.AngularVelocity;
            if ((b.LinearVelocityScale > 1f && (v.X * v.X + v.Y * v.Y) + v.Z * v.Z > 0f)
                || (b.AngularVelocityScale > 1f && (w.X * w.X + w.Y * w.Y) + w.Z * w.Z > 0f))
                b.Flags4A |= 0x10;
            if ((b.Flags4A & 0x10) == 0)
            {
                b.Force = default;
                b.Torque = default;
                b.LinearImpulse = default;
                b.AngularImpulse = default;
                b.AngularVelocityScale = 1f;
                b.LinearVelocityScale = 1f;
                goto recorded;
            }
        }
        b.SleepingForce = b.Force;
        b.SleepingTorque = b.Torque;
    recorded:
        if (Small(b.SleepingForce.X) && Small(b.SleepingForce.Y) && Small(b.SleepingForce.Z)
            && Small(b.SleepingTorque.X) && Small(b.SleepingTorque.Y) && Small(b.SleepingTorque.Z))
            b.Flags4A |= 0x40;
    }

    private static bool Small(float f) => f > -0.01f && 0.01f > f;

    /// <summary>
    /// CRnWorld::WakeBodiesFromAppliedForces (FUN_180203d70): the test on every
    /// listed body, then, last to first, a flagged body is woken and a body
    /// flagged to leave is swapped out of the list.
    /// </summary>
    public static void WakeBodiesFromAppliedForces(RnWorld w, List<RnBody> forced)
    {
        foreach (var b in forced)
            AppliedForceTest(ref b.State);
        for (var i = forced.Count - 1; i >= 0; i--)
        {
            var b = forced[i];
            if ((b.State.Flags4A & 0x10) != 0)
            {
                ContactLifecycle.Wake(w, b, false);
                b.State.Flags4A &= unchecked((ushort)~0x10);
            }
            if ((b.State.Flags4A & 0x40) != 0)
            {
                forced[i] = forced[^1];
                forced.RemoveAt(forced.Count - 1);
                b.State.Flags4A &= unchecked((ushort)~0x40);
            }
        }
    }

    /// <summary>
    /// The detection half of CRnWorld::ClampToWorldBounds (FUN_1801faf80 with
    /// FUN_1801dc300): in the trees that hold moving shapes (kind bit 4), the
    /// dynamic bodies with a proxy not inside [-(h - 1), h - 1]^3, h the world
    /// half size (CRnWorld+0x1B4). A subtree inside the box is skipped.
    /// </summary>
    public static List<ulong> ShapesOutsideBounds(Broadphase bp, float halfSize)
    {
        var e = halfSize - 1f;
        var box = new Aabb(new(-e, -e, -e), new(e, e, e));
        var found = new List<ulong>();
        foreach (var tree in bp.Trees)
        {
            if ((tree.Kind & 4) == 0 || tree.Nodes.Root < 0)
                continue;
            var stack = new Stack<int>();
            var i = tree.Nodes.Root;
            for (;;)
            {
                ref readonly var n = ref tree.Nodes.Nodes[i];
                var inside = n.Max.X <= box.Max.X && box.Min.X <= n.Min.X && n.Max.Y <= box.Max.Y
                             && box.Min.Y <= n.Min.Y && n.Max.Z <= box.Max.Z && box.Min.Z <= n.Min.Z;
                if (!inside)
                {
                    if (n.Child1 != -1)
                    {
                        stack.Push(n.Child2);
                        i = n.Child1;
                        continue;
                    }
                    if (bp.Shape(n.Shape) is { } shape && shape.Body.State.BodyType == 2)
                        found.Add(n.Shape);
                }
                if (stack.Count == 0)
                    break;
                i = stack.Pop();
            }
        }
        return found;
    }

    /// <summary>
    /// CRnWorld::ClampToWorldBounds (FUN_1801faf80), after the continuous
    /// solve: each dynamic body with a proxy outside the bounds
    /// (<see cref="ShapesOutsideBounds"/>) loses its velocities unless static,
    /// takes its box (FUN_1801b1a30: its shapes' boxes at its frame, unioned
    /// into a box that starts at the world origin, all zeros) grown by its
    /// origin, moves by the least that brings that box inside
    /// [-(h - 1) + 5, (h - 1) - 5] on each axis (FUN_1801b9c20: position and
    /// previous position, the proxies moved at once), and is put to sleep
    /// (<see cref="SleepBody"/>). Then the pairs are queried again and new
    /// contacts made. Valve visits the bodies in the bucket order of a hash set
    /// keyed by body pointer; this visits them as the trees list them, which
    /// matches whenever one body leaves at a step (with several, the awake
    /// list's swap removals follow the pointers, ledger 52).
    /// </summary>
    public static void ClampToWorldBounds(RnWorld w)
    {
        var bp = w.Broadphase ?? throw new InvalidOperationException("the clamp needs the broadphase");
        var shapes = ShapesOutsideBounds(bp, w.MaxCoordinate);
        if (shapes.Count == 0)
            return;
        var byProxy = w.Bodies.ToDictionary(b => b.Proxy, ReferenceEqualityComparer.Instance);
        var bodies = new List<RnBody>();
        foreach (var handle in shapes)
            if (bp.Shape(handle) is { } shape && byProxy[shape.Body] is var b && !bodies.Contains(b))
                bodies.Add(b);

        var e = w.MaxCoordinate - 1f;
        var lo = -e - -5f;
        var hi = e - 5f;
        foreach (var b in bodies)
        {
            ref var s = ref b.State;
            if (s.BodyType != 0)
            {
                s.LinearVelocity = default;
                s.AngularVelocity = default;
            }
            var frame = RnTransform.Of(s);
            float minX = 0f, minY = 0f, minZ = 0f, maxX = 0f, maxY = 0f, maxZ = 0f;
            foreach (var shape in b.Shapes)
            {
                var a = shape.Proxy.ComputeAabb(frame);
                if (a.Min.X <= minX) minX = a.Min.X;
                if (a.Min.Y <= minY) minY = a.Min.Y;
                if (a.Min.Z <= minZ) minZ = a.Min.Z;
                if (maxX <= a.Max.X) maxX = a.Max.X;
                if (maxY <= a.Max.Y) maxY = a.Max.Y;
                if (maxZ <= a.Max.Z) maxZ = a.Max.Z;
            }
            var o = frame.T;
            if (maxX <= o.X) maxX = o.X;
            if (o.X <= minX) minX = o.X;
            if (o.Z <= minZ) minZ = o.Z;
            if (maxY <= o.Y) maxY = o.Y;
            if (o.Y <= minY) minY = o.Y;
            if (maxZ <= o.Z) maxZ = o.Z;
            static float Shift(float min, float max, float lo, float hi)
            {
                var d = 0f;
                if (hi - max <= 0f)
                    d = hi - max;
                if (d <= lo - min)
                    d = lo - min;
                return d;
            }
            var delta = new Vec3(Shift(minX, maxX, lo, hi), Shift(minY, maxY, lo, hi), Shift(minZ, maxZ, lo, hi));
            s.Position = new(delta.X + s.Position.X, delta.Y + s.Position.Y, delta.Z + s.Position.Z);
            s.PreviousPosition = s.Position;
            bp.MoveBodyImmediate(b.Proxy);
            SleepBody(w, b);
            SleepBody(w, b);
        }
        bp.PreStepQuery(w.Query, w.Threads, w.Priority);
        w.BuildNewContacts();
    }

    /// <summary>
    /// Puts a body to sleep from outside the solver (FUN_1801be010): for an
    /// enabled body not asleep, its forces are kept as the sleeping ones, its
    /// velocities zeroed unless static, its frame becomes the previous one,
    /// the sleep timers clear, it is flagged asleep, its frame origin is set to
    /// FLT_MAX, and it leaves the awake bodies (FUN_1801f7200). Asleep, it is
    /// no longer fast: the flag clears and its proxies are put in the tree they
    /// now belong in (FUN_1802552d0, FUN_1802d7560). Flags4A bit 0x80 clears
    /// either way. A mesh shape not in mode 3 (FUN_180242830) is not ported.
    /// </summary>
    public static void SleepBody(RnWorld w, RnBody b)
    {
        ref var s = ref b.State;
        if ((s.Flags249 & 1) == 0)
            return;
        if ((s.Flags249 & 4) == 0)
        {
            s.SleepingForce = s.Force;
            s.SleepingTorque = s.Torque;
            if (s.BodyType != 0)
            {
                s.LinearVelocity = default;
                s.AngularVelocity = default;
            }
            s.PreviousPosition = s.Position;
            s.PreviousOrientation = s.Orientation;
            s.Cleared1E8 = 0f;
            s.SleepTimer = 0f;
            s.Flags249 |= 4;
            foreach (var shape in b.Shapes)
                if (shape.Type == 3 && shape.MeshMode != 3)
                    throw new NotSupportedException("putting a body with a mesh shape not in mode 3 to sleep (FUN_180242830) is not ported");
            s.FrameOrigin = new(float.MaxValue, float.MaxValue, float.MaxValue);
            ContactLifecycle.RemoveAwake(w, b);
            if ((s.Flags249 & 0x80) != 0)
            {
                s.Flags249 &= 0x7f;
                foreach (var shape in b.Shapes)
                    if (shape.Proxy.HasProxy)
                        w.Broadphase?.ReselectTree(shape.Proxy);
            }
        }
        s.Flags4A &= unchecked((ushort)~0x80);
    }
}
