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
    /// half size (CRnWorld+0x1B4). A subtree inside the box is skipped. The
    /// fix-up for such bodies (translate back, zero the velocity, sleep) is not
    /// ported and throws: a settle keeps its props inside the map.
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
        if (found.Count > 0)
            throw new NotSupportedException("bodies outside the world bounds (the FUN_1801faf80 fix-up) are not ported");
        return found;
    }
}
