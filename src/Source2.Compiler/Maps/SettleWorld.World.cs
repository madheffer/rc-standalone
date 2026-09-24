using System.Numerics;
using Source2.Compiler.Simulation;

namespace Source2.Compiler.Maps;

public static partial class SettleWorld
{
    /// <summary>
    /// The settle's world built from <see cref="Build"/>'s bodies alone, as
    /// PhysDoc builds it: a world at Rubikon's defaults with the collision
    /// group table vphysics2 starts with (<see cref="CollisionGroupTable"/>),
    /// the document's own shapeless body first, then each body created
    /// static at its frame (world vfn 0x1e8, SetType(0), vfn 0x1b8) with its
    /// shapes added in order and then the mass update; then the
    /// settled bodies (<see cref="Settled"/>, in the given order) made
    /// dynamic and woken, which is SetType(2) (FUN_1801bd3e0).
    /// </summary>
    /// <remarks>
    /// Capsules hold their proxy's place only: capsule collision is not
    /// ported, and their proxy box is the ends through the frame padded by
    /// the radius.
    /// </remarks>
    public static RnWorld CreateWorld(IReadOnlyList<BodyBuild> bodies, IReadOnlyList<int> settled)
    {
        var w = new RnWorld();
        w.Islands.Colouring = true;
        var broadphase = new Broadphase(CollisionGroupTable.Build(), 0);
        w.Broadphase = broadphase;
        w.AddBody(NewBody(new Vec3(0f, 0f, 0f), 1f, Quat.Identity));
        var built = new List<RnBody>();
        foreach (var body in bodies)
        {
            var q = new Quat(body.Orientation.X, body.Orientation.Y, body.Orientation.Z, body.Orientation.W);
            var b = w.AddBody(NewBody(new Vec3(body.Position.X, body.Position.Y, body.Position.Z), body.Scale, q));
            if (body.Node?.Type == "CMapMesh")
            {
                // FUN_18105d760 zeroes a map mesh body's damping and drag
                // (body vfns 0x2f0, 0x2c0, 0x300, 0x2d0).
                b.State.LinearDamping = 0f;
                b.State.AngularDamping = 0f;
                b.State.LinearDrag = 0f;
                b.State.AngularDrag = 0f;
                b.State.Flags249 |= 0x20;
            }
            // SetTransform (body vfn 0x1b8) places the mass centre and the
            // previous frame through the mass update.
            var origin = new Vec3(body.Position.X, body.Position.Y, body.Position.Z);
            RnMassUpdate.Run(ref b.State, [], origin, q);
            var joined = new List<RnMassUpdate.Shape>();
            foreach (var shape in body.Shapes)
            {
                if (shape.Type == CapsuleType)
                    AddCapsule(w, broadphase, b, shape);
                else
                    w.AddShape(b, shape.Type, shape.Hull is { } hull ? new HullRef(hull, shape.HullScale) : default, shape.Mesh,
                               new Vec3(shape.MeshScale.X, shape.MeshScale.Y, shape.MeshScale.Z), shape.Attributes, shape.Material);
                joined.Add(MassShape(shape));
            }
            // The shapes join with the update flag clear (FUN_1801b9650 skips
            // FUN_1801c0880), so each proxy sits at the body's own frame and
            // the mass update runs once, from that frame, with every shape in.
            if (joined.Count > 0)
                RnMassUpdate.Run(ref b.State, joined, origin, q);
            built.Add(b);
        }
        foreach (var i in settled)
        {
            var b = built[i];
            var origin = RnTransform.Of(b.State).T;
            w.MakeDynamic(b);
            RnMassUpdate.Run(ref b.State, [.. bodies[i].Shapes.Select(MassShape)], origin, b.State.Orientation);
            RnMassUpdate.Drag(ref b.State, [.. bodies[i].Shapes.Select(MassShape)]);
            b.State.StaticFlag = 0;
            // FUN_1801c0ff0: an asleep body wakes; its sleep timer restarts.
            b.State.Flags249 &= 0xfb;
            b.State.SleepTimer = 0f;
            ContactLifecycle.AddAwake(w, b);
            b.State.Flags4A &= 0xff7f;
        }
        return w;
    }

    /// <summary>A shape as the mass update reads it; the function mask decides whether its mass counts.</summary>
    private static RnMassUpdate.Shape MassShape(ShapeBuild s)
        => new(s.Type, s.Hull, s.HullScale, s.Mesh, s.MeshScale, s.Material,
               ((s.Attributes.MaskIsDirect == 1 ? s.Attributes.FunctionMask : ~s.Attributes.FunctionMask) & 1) != 0)
        { Capsule = s.Capsule };

    /// <summary>
    /// A body as the world's default description leaves it (world vfn 0x1e8,
    /// read from a fresh vphysics2 body), then static at the given frame:
    /// unit scales, mass 500 with the identity inverse inertia, drag 1,
    /// velocity scales 1, the frame origin at FLT_MAX, and flags 0x3f.
    /// </summary>
    internal static RnBodyState NewBody(Vec3 position, float scale, Quat orientation)
    {
        var s = new RnBodyState
        {
            Scale = 1f,
            MassScale = 1f,
            InertiaScale = 1f,
            GravityScale = 1f,
            FrictionScale = 1f,
            TimeScale = 1f,
            InertiaDivisor = 500f,
            LocalInvInertia = new Mat3 { M0 = 1f, M4 = 1f, M8 = 1f },
            Orientation = Quat.Identity,
            PreviousOrientation = Quat.Identity,
            LinearDrag = 1f,
            AngularDrag = 1f,
            LinearVelocityScale = 1f,
            AngularVelocityScale = 1f,
            InnerRadius = float.MaxValue,
            FrameOrigin = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue),
            MinVelocityIterations = 1,
            Flags249 = 0x3f,
        };
        s.ActiveIndex = -1;
        s.StaticFlag = 1;
        s.Word150 = 1f;
        s.Word154 = 1f;
        s.Word1AC = 0x00c80000;
        s.Word1CC = 1f;
        // SetScale (FUN_1801bca60) ignores a change under 0.001.
        if (MathF.Abs(scale - 1f) >= 0.001f && scale > 0f)
            s.Scale = scale;
        s.Position = position;
        s.Orientation = orientation;
        return s;
    }

    private static void AddCapsule(RnWorld w, Broadphase broadphase, RnBody b, ShapeBuild shape)
    {
        var s = new RnShape(b, w.NextHandle()) { Type = CapsuleType };
        s.Attributes = shape.Attributes;
        b.Shapes.Add(s);
        w.ShapesByHandle[s.Proxy.Handle] = s;
        if ((b.State.Flags249 & 1) == 0)
            return;
        var (a, c, r) = shape.Capsule!.Value;
        a *= shape.HullScale;
        c *= shape.HullScale;
        r *= shape.HullScale;
        var xf = RnTransform.Of(b.State);
        var pa = Apply(xf, a);
        var pc = Apply(xf, c);
        broadphase.CreateProxy(s.Proxy, new Aabb(new(MathF.Min(pa.X, pc.X) - r, MathF.Min(pa.Y, pc.Y) - r, MathF.Min(pa.Z, pc.Z) - r),
                                                 new(MathF.Max(pa.X, pc.X) + r, MathF.Max(pa.Y, pc.Y) + r, MathF.Max(pa.Z, pc.Z) + r)));
    }

    private static Vec3 Apply(in RnTransform xf, Vector3 p)
    {
        ref readonly var m = ref xf.R;
        return new(m.M0 * p.X + m.M3 * p.Y + m.M6 * p.Z + xf.T.X,
                   m.M1 * p.X + m.M4 * p.Y + m.M7 * p.Z + xf.T.Y,
                   m.M2 * p.X + m.M5 * p.Y + m.M8 * p.Z + xf.T.Z);
    }
}
