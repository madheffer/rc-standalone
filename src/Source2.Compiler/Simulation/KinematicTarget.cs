namespace Source2.Compiler.Simulation;

/// <summary>
/// The move-to-target controller a kinematic body carries (CRnBody +0x80):
/// the transform it should reach and the time left to get there. Each solve
/// sets the body's velocities to cover the rest of the way (FUN_1802c4cd0),
/// and the writeback snaps a body that has all but arrived (FUN_1802c4bb0).
/// </summary>
public sealed class KinematicTarget
{
    /// <summary>+0x00: the orientation to reach.</summary>
    public Quat Orientation;

    /// <summary>+0x18: the body origin to reach.</summary>
    public Vec3 Position;

    /// <summary>+0x24: seconds left; each step takes dt off, down to 0.</summary>
    public float Time;

    /// <summary>Squared speed (and spin) under which an arriving body stops (0x1803df1ac).</summary>
    private const float StopSpeedSquared = 1.1920929e-05f;

    /// <summary>Time left under which the body counts as arrived (0x1803db134).</summary>
    private const float ArrivedTime = 0.00011920929f;

    /// <summary>
    /// The velocities that carry the solver body to the target over the time
    /// left, or over dt when less is left (FUN_1802c4cd0). The target's origin
    /// is turned into a centre of mass through the target orientation.
    /// </summary>
    public void Drive(ref SolverBody sb, float dt)
    {
        if (sb.TimeScale == 0f)
            return;
        var t = Time;
        var span = dt > t ? dt : t;
        var left = t - dt;
        Time = 0f > left ? 0f : left;

        var q = Orientation;
        var c = sb.LocalMassCenter;
        var a = (c.Z * q.Y - c.Y * q.Z) + c.X * q.W;
        var b = (c.X * q.Z - c.Z * q.X) + c.Y * q.W;
        var d = (c.Y * q.X - c.X * q.Y) + c.Z * q.W;
        var inverse = 1f / span;
        var x = q.Y * d - q.Z * b;
        var y = q.Z * a - q.X * d;
        var z = q.X * b - q.Y * a;
        sb.V = new Vec3(
            ((((x + x) + c.X) + Position.X) - sb.Position.X) * inverse,
            ((((y + y) + c.Y) + Position.Y) - sb.Position.Y) * inverse,
            ((((z + z) + c.Z) + Position.Z) - sb.Position.Z) * inverse);

        // dpps 0xff: the lanes pair up as (x + y) + (z + w).
        var p = sb.Q;
        var dot = (p.X * q.X + p.Y * q.Y) + (p.Z * q.Z + p.W * q.W);
        if (0f > dot)
            p = new Quat(0f - p.X, 0f - p.Y, 0f - p.Z, 0f - p.W);
        var k = 2f / span;
        var delta = new Quat((q.X - p.X) * k, (q.Y - p.Y) * k, (q.Z - p.Z) * k, (q.W - p.W) * k);
        var w = RnMath.Mul(delta, new Quat(-p.X, -p.Y, -p.Z, p.W));
        sb.W = new Vec3(w.X, w.Y, w.Z);
    }

    /// <summary>
    /// At writeback (FUN_1802c4bb0): a kinematic body out of time and all but
    /// still stops, and one that also all but stopped turning takes the
    /// target orientation exactly.
    /// </summary>
    public void Settle(ref SolverBody sb)
    {
        if (sb.TimeScale == 0f)
            return;
        var v = sb.V;
        if ((v.X != 0f || v.Y != 0f || v.Z != 0f) && sb.BodyType == 1
            && StopSpeedSquared > (v.Y * v.Y + v.X * v.X) + v.Z * v.Z && ArrivedTime > Time)
            sb.V = default;
        var q = sb.Q;
        var o = Orientation;
        if (q.X == o.X && q.Y == o.Y && q.Z == o.Z && q.W == o.W)
            return;
        var s = sb.W;
        if (sb.BodyType == 1 && StopSpeedSquared > (s.Y * s.Y + s.X * s.X) + s.Z * s.Z && ArrivedTime > Time)
        {
            sb.W = default;
            Integrator.SetOrientation(ref sb, o);
        }
    }
}
