using System.Numerics;
using Source2.Compiler.Simulation;
using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;
using ValveResourceFormat.ResourceTypes;

namespace Source2.Compiler.Maps;

public static partial class SettleWorld
{
    /// <summary>
    /// A model with several physics parts or a joint (PhysPart_BuildFromModel,
    /// FUN_18105dbd0, through world vfn 0x418 into CPhysAggregateInstance,
    /// vphysics2 FUN_180042ba0): one body per part, its joints, and the
    /// entity-wide collision attributes resourcecompiler gives every shape.
    /// </summary>
    public sealed class AggregateBuild
    {
        public required PhysAggregateData Phys { get; init; }

        /// <summary>The part's transform (part +0x30): the node's position, scale and orientation.</summary>
        public required CTransform Part { get; init; }

        /// <summary>The largest of the node's scales (part +0x50), which multiplies the bind poses' translations.</summary>
        public required float NodeScale { get; init; }

        /// <summary>The bodies' indices in the build list, in part order.</summary>
        public List<int> Bodies { get; } = [];

        /// <summary>Per part: mass, inertia scale, damping, drag (VPhysXBodyPart_t +4, +0x74..+0x84).</summary>
        public List<PartPhysics> Parts { get; } = [];

        /// <summary>The model's VPhysXJoint_t records, in order.</summary>
        public List<KVObject> Joints { get; } = [];

        /// <summary>Per part, the model's pose for the part's bone in model space, or null when the skeleton has no such bone.</summary>
        public List<CTransform?> Poses { get; } = [];

        /// <summary>The model's skeleton (m_modelSkeleton), which the write-back orders and filters the bones by; null without one.</summary>
        public ModelBones? Skeleton { get; set; }
    }

    public readonly record struct PartPhysics(int Flags, float Mass, float InertiaScale, float LinearDamping, float AngularDamping,
                                              float LinearDrag, float AngularDrag);

    private static IReadOnlyList<KVObject> JointRecords(PhysAggregateData phys)
        => phys.Data.ContainsKey("m_joints") ? phys.Data.GetArray("m_joints") : [];

    /// <summary>
    /// The aggregate branch of FUN_18105dbd0: each part's shapes as the model
    /// stores them (180044bc0 adds them at the aggregate's scale, 1 here),
    /// with the collision attributes they end with. Refused: a node scale
    /// other than 1, a deformer, a static_prop class, or a model without a
    /// bind pose per part.
    /// </summary>
    private static void FromAggregate(DmxBinary.Element node, float[] local, int nodeId, PhysAggregateData phys, Context context,
                                      LatticeDeformer? deformer)
    {
        var (models, rules, bodies) = (context.Models, context.Rules, context.Bodies);
        string? Refusal()
        {
            var scales = node.GetValue<Vector3>("scales") ?? Vector3.One;
            if (scales != Vector3.One)
                return "an aggregate at a scale other than 1";
            if (deformer != null)
                return "an aggregate under a deformer";
            if (phys.BindPose.Length != phys.Parts.Length)
                return "an aggregate without a bind pose per part";
            var cls = node.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname") ?? "";
            if (context.Schema == null || context.Schema.HasFlag(cls, "static_prop") || !context.Schema.Inherits(cls, "BasePhysicsSimulated"))
                return "an aggregate of a class that is static or not BasePhysicsSimulated";
            if (phys.Parts.Any(p => p.Shape.Hulls.Length > 0 || p.Shape.Meshes.Length > 0))
                return "an aggregate with hull or mesh shapes";
            return null;
        }
        if (Refusal() is { } refused)
        {
            bodies.Add(new BodyBuild(nodeId, default, 1f, Quaternion.Identity, []) { Node = node, Unsupported = refused });
            return;
        }
        var props = node.Get<DmxBinary.Element>("entity_properties")!;
        var aggregate = new AggregateBuild
        {
            Phys = phys,
            Part = new CTransform(new Vector3(local[3], local[7], local[11]), 1f, Orientation(Unscaled(local))),
            NodeScale = 1f,
        };
        aggregate.Joints.AddRange(JointRecords(phys));
        aggregate.Skeleton = props.Get<string>("model") is { } path ? models.ModelSkeleton(path) : null;
        aggregate.Poses.AddRange(ModelPoses(phys, aggregate.Skeleton));
        var table = phys.CollisionAttributes.Select(rules.Convert).ToArray();
        var entity = EntityAttributes(props, table, context.Schema!, rules);
        var surfaces = phys.SurfacePropertyHashes;
        ContactSolver.Material Material(int index)
            => index >= 0 && index < surfaces.Length && models.Surface(surfaces[index]) is { } m ? m
             : models.Surface(DefaultSurface) ?? DefaultMaterial;
        var raw = phys.Data.GetArray("m_parts");
        for (var i = 0; i < phys.Parts.Length; i++)
        {
            var part = phys.Parts[i];
            var p = raw[i];
            aggregate.Parts.Add(new PartPhysics(part.Flags, part.Mass, p.GetFloatProperty("m_flInertiaScale"),
                p.GetFloatProperty("m_flLinearDamping"), p.GetFloatProperty("m_flAngularDamping"),
                p.GetFloatProperty("m_flLinearDrag"), p.GetFloatProperty("m_flAngularDrag")));
            if (p.GetIntegerProperty("m_bOverrideMassCenter") != 0)
                throw new NotSupportedException("an aggregate part with an overridden mass centre (body vfn 0x108) is not ported");
            var shape = part.Shape;
            var perShape = shape.CollisionAttributeIndices ?? [];
            var k = 0;
            CollisionAttributes Attributes()
            {
                var index = perShape.Length > 0 ? (k < perShape.Length ? perShape[k] : -1) : part.CollisionAttributeIndex;
                k++;
                return Layered(index >= 0 && index < table.Length ? table[index] : default, entity);
            }
            var body = new BodyBuild(nodeId, default, 1f, Quaternion.Identity, []) { Node = node, Aggregate = aggregate, PartIndex = i };
            foreach (var s in shape.Spheres)
                body.Shapes.Add(new ShapeBuild(SphereType, null, 1f, null, Vector3.One, Attributes(), Material(s.SurfacePropertyIndex),
                                               s.UserFriendlyName ?? "") { Sphere = (s.Shape.Center, s.Shape.Radius) });
            foreach (var c in shape.Capsules)
                body.Shapes.Add(new ShapeBuild(CapsuleType, null, 1f, null, Vector3.One, Attributes(), Material(c.SurfacePropertyIndex),
                                               c.UserFriendlyName ?? "") { Capsule = (c.Shape.Center[0], c.Shape.Center[1], c.Shape.Radius) });
            aggregate.Bodies.Add(bodies.Count);
            bodies.Add(body);
        }
    }

    /// <summary>
    /// The record resourcecompiler gives every shape of the aggregate (rc
    /// 18105dbd5..18105dc90): the interaction layers of the palette entry
    /// vfn 0xe8 picks (180046280: the first with a group above 3 after
    /// entry 0, else entry 0), group 4, flags 7, owner -1; then FUN_18105ce70:
    /// a not-solid entity gets the never-colliding record (group 1), else a
    /// BasePhysicsSimulated class interacts with 0xc3001, a collision_group
    /// key names the group, and a class that is not static_prop drops layer
    /// bit 0x400.
    /// </summary>
    private static CollisionAttributes EntityAttributes(DmxBinary.Element props, CollisionAttributes[] table, FgdSchema schema, CollisionRules rules)
    {
        if (props.Get<string>("solid") == "0")
            return new CollisionAttributes { EntityId = 0, OwnerId = -1, Group = 1, Flags = 7 };
        var chosen = 0;
        for (var i = 1; i < table.Length; i++)
            if (table[chosen].Group < 4 && table[i].Group > 3)
                chosen = i;
        var a = new CollisionAttributes { InteractsAs = table.Length > 0 ? table[chosen].InteractsAs : 0, EntityId = 0, OwnerId = -1, Group = 4, Flags = 7 };
        a.InteractsWith |= 0xc3001;
        if (props.Get<string>("collision_group") is { Length: > 0 } group)
        {
            var index = rules.Group(group);
            if (index >= 0)
                a.Group = (byte)index;
        }
        a.InteractsAs &= ~0x400ul;
        return a;
    }

    /// <summary>
    /// A shape's attributes after 180044bc0 sets its palette entry as the base
    /// (180256c80: the base layers when not 0, the base group the entry's when
    /// not 0, else 4) and the entity record goes on top (180256d00): the
    /// record's fields, its layers with the base's added, the base's group
    /// when it is below 4. An entry whose layers are the default mask
    /// (18045b2a0, set at runtime; the solid layers an empty InteractAs list
    /// gets) leaves no base layers, as the capture's +0x78 shows. The entity
    /// id is the body's (-1, vfn 0x470).
    /// </summary>
    private static CollisionAttributes Layered(CollisionAttributes entry, CollisionAttributes entity)
    {
        var baseLayers = entry.InteractsAs == CollisionRules.ContentsSolid && entry.Group != 3 ? 0ul : entry.InteractsAs;
        var baseGroup = entry.Group != 0 ? entry.Group : (byte)4;
        var a = entity;
        a.InteractsAs |= baseLayers;
        if (baseGroup < 4)
            a.Group = baseGroup;
        a.FunctionMask = entity.FunctionMask;
        a.MaskIsDirect = entity.MaskIsDirect;
        a.EntityId = -1;
        return a;
    }

    /// <summary>
    /// A part's body transform as vphysics2 places it (FUN_180045630): the
    /// part's matrix concatenated with the bind pose (translation times the
    /// aggregate scale), its largest column scale, and the unscaled
    /// rotation's MatrixQuaternion.
    /// </summary>
    internal static (Vector3 Position, float Scale, Quaternion Orientation) VphysicsPlacement(AggregateBuild a, int part)
    {
        var m = a.Part.Matrix();
        var b = Physics.StaticPropHulls.BindPose(a.Phys, part);
        var c = MapMeshes.Concat(m, b);
        float Column(int k) => MathF.Sqrt(((c[k] * c[k]) + (c[4 + k] * c[4 + k])) + (c[8 + k] * c[8 + k]));
        float sx = Column(0), sy = Column(1), sz = Column(2);
        var w = MathF.Abs(sx);
        if (w <= MathF.Abs(sy))
            w = MathF.Abs(sy);
        if (w <= MathF.Abs(sz))
            w = MathF.Abs(sz);
        return (new Vector3(c[3], c[7], c[11]), w, Orientation(Unscaled(c)));
    }

    /// <summary>
    /// The pose resourcecompiler then sets (rc 18105e18a..18105e247, body vfn
    /// 0x1b8): the bone's transform in the model's own pose (181749d20 over the
    /// pose FUN_181f03210 builds, looked up by the part's bone name), else the
    /// bind pose decomposed (18125b270, 18125de90); its translation times the
    /// node scale, composed under the part's transform (181253510).
    /// </summary>
    internal static CTransform RcPlacement(AggregateBuild a, int part)
    {
        CTransform bone;
        if (part < a.Poses.Count && a.Poses[part] is { } pose)
        {
            bone = pose;
        }
        else
        {
            var b = Physics.StaticPropHulls.BindPose(a.Phys, part);
            bone = new CTransform(new Vector3(b[3], b[7], b[11]), 1f, Orientation(Unscaled(b)));
        }
        bone = bone with { Position = bone.Position * a.NodeScale, Scale = 1f };
        return CTransform.Compose(a.Part, bone);
    }

    /// <summary>
    /// The skeleton's reference pose, parent-relative: each bone's position,
    /// scale and rotation from m_modelSkeleton. This is the pose an animation
    /// helper's skeleton set leaves (animationsystem 18006d560 -> 1804abdb0 ->
    /// 1804acdd0: skeleton +0x98 positions, +0xc8 scales, +0xb0 rotations),
    /// and the locals rc's 18174dc50 composes.
    /// </summary>
    public static CTransform[] ReferencePose(ModelBones skeleton)
    {
        var pose = new CTransform[skeleton.Names.Length];
        for (var i = 0; i < pose.Length; i++)
            pose[i] = new CTransform(skeleton.Positions[i], skeleton.Scales[i], skeleton.Rotations[i]);
        return pose;
    }

    /// <summary>
    /// A parent-relative pose in model space (rc 1817dd320 for a helper's
    /// pose, 18174dc50 for the reference pose): bone order, a root bone
    /// composed under <paramref name="root"/>, every other under its parent's
    /// model-space transform (181253510).
    /// </summary>
    public static CTransform[] ModelSpace(IReadOnlyList<CTransform> locals, IReadOnlyList<int> parents, CTransform root)
    {
        var space = new CTransform[locals.Count];
        for (var i = 0; i < space.Length; i++)
            space[i] = CTransform.Compose(parents[i] < 0 ? root : space[parents[i]], locals[i]);
        return space;
    }

    /// <summary>
    /// The model's pose in model space for each part's bone (the bone found
    /// by its name, case ignored): the reference pose from an identity root,
    /// node +0x310 as 181f0bb60 fills it, which the aggregate build reads
    /// (181f03210 with flag 0 at rc 18105e100). The animation helper's pose
    /// (+0x328, after its update) is not read here (GROUND_TRUTH 166). Not
    /// ported: stored bone overrides (181efec40), which replace both.
    /// </summary>
    private static IEnumerable<CTransform?> ModelPoses(PhysAggregateData phys, ModelBones? skeleton)
    {
        CTransform[]? space = null;
        if (skeleton != null)
            space = ModelSpace(ReferencePose(skeleton), skeleton.Parents, new CTransform(Vector3.Zero, 1f, Quaternion.Identity));
        var names = phys.BoneNames ?? [];
        for (var p = 0; p < phys.Parts.Length; p++)
        {
            var at = space == null || p >= names.Length ? -1 : Array.FindIndex(skeleton!.Names, n => n.Equals(names[p], StringComparison.OrdinalIgnoreCase));
            yield return at >= 0 ? space![at] : null;
        }
    }

    /// <summary>
    /// The aggregate's bodies (FUN_180045630, then 180044bc0 per part): each
    /// created at vphysics2's placement, keyframed (mode 3: SetType 1), its
    /// shapes added, its mass set (body vfn 0x100, FUN_1801bbb60: clamped to
    /// 1e6, 500 when not positive, flag 0x4a bit 8, then the mass update),
    /// the inertia scaled when the part's scale is above 1 (vfn 0x178), the
    /// damping (0x2f0, 0x300) and drag (0x2c0, 0x2d0, flag 0x249 bit 5) set.
    /// Then resourcecompiler's pose for each (vfn 0x1b8). The joints follow.
    /// </summary>
    private static List<RnBody> CreateAggregate(RnWorld w, AggregateBuild a, IReadOnlyList<BodyBuild> bodies)
    {
        var made = new List<RnBody>();
        for (var i = 0; i < a.Bodies.Count; i++)
        {
            var build = bodies[a.Bodies[i]];
            var (position, scale, orientation) = VphysicsPlacement(a, i);
            var q = new Quat(orientation.X, orientation.Y, orientation.Z, orientation.W);
            var origin = new Vec3(position.X, position.Y, position.Z);
            var b = w.AddBody(NewBody(origin, scale, q));
            // vfn 0x480: the part index at body +0x48.
            b.State.PartIndex = (ushort)i;
            RnMassUpdate.Run(ref b.State, [], origin, q);
            w.SetType(b, 1);
            // SetType ends with the wake (FUN_1801c0ff0).
            ContactLifecycle.Wake(w, b, false);
            var shapes = new List<RnMassUpdate.Shape>();
            foreach (var shape in build.Shapes)
            {
                w.AddShape(b, shape.Type, default, null, new Vec3(1f, 1f, 1f), shape.Attributes, shape.Material, round: Round(shape));
                shapes.Add(MassShape(shape));
            }
            var part = a.Parts[i];
            if (part.Mass > 0f)
            {
                var mass = part.Mass <= 1e6f ? part.Mass : 1e6f;
                if (mass <= 0f)
                    mass = 500f;
                b.State.Flags4A |= 0x100;
                b.State.InertiaDivisor = mass;
            }
            RnMassUpdate.Run(ref b.State, shapes, RnTransform.Of(b.State).T, b.State.Orientation);
            if (part.InertiaScale > 1f)
            {
                b.State.InertiaScale = (1f / part.InertiaScale) * b.State.InertiaScale;
                RnMassUpdate.Run(ref b.State, shapes, RnTransform.Of(b.State).T, b.State.Orientation);
            }
            if (part.LinearDamping >= 0f)
                b.State.LinearDamping = part.LinearDamping;
            if (part.AngularDamping >= 0f)
                b.State.AngularDamping = part.AngularDamping;
            b.State.LinearDrag = part.LinearDrag;
            b.State.AngularDrag = part.AngularDrag;
            b.State.Flags249 |= 0x20;
            made.Add(b);
        }
        CreateJoints(w, a, made);
        for (var i = 0; i < made.Count; i++)
        {
            var xf = RcPlacement(a, i);
            SetTransform(made[i], new Vec3(xf.Position.X, xf.Position.Y, xf.Position.Z),
                         new Quat(xf.Rotation.X, xf.Rotation.Y, xf.Rotation.Z, xf.Rotation.W));
        }
        return made;
    }

    /// <summary>
    /// CRnBody::SetTransform (FUN_1801bcf70): nothing unless the origin moves
    /// by 1.4210855e-14 squared or the quaternion's difference reaches it;
    /// then the orientation, the world inverse inertia, the mass centre at
    /// the new origin and the previous frame are set.
    /// </summary>
    internal static void SetTransform(RnBody body, Vec3 origin, Quat q)
    {
        ref var s = ref body.State;
        var now = RnTransform.Of(s).T;
        float dx = origin.X - now.X, dy = origin.Y - now.Y, dz = origin.Z - now.Z;
        Quat o = s.Orientation;
        float qx = q.X - o.X, qy = q.Y - o.Y, qz = q.Z - o.Z, qw = q.W - o.W;
        if (!(1.4210855e-14f <= (dx * dx) + (dy * dy) + (dz * dz)) && !(1.4210855e-14f <= ((qx * qx) + (qy * qy)) + ((qz * qz) + (qw * qw))))
            return;
        s.Orientation = q;
        s.WorldInvInertia = Continuous.WorldInverseInertia(s);
        s.Position = RnMassUpdate.CentreOfMass(s, origin, q);
        s.PreviousPosition = s.Position;
        s.PreviousOrientation = s.Orientation;
        s.Cleared1E8 = 0f;
    }

    private static float Float(KVObject o, string key) => o.GetFloatProperty(key);

    private static bool Flag(KVObject o, string key) => o.ContainsKey(key) && Convert.ToBoolean(o[key]);

    private static (Vec3 Position, Quat Rotation) Frame(KVObject o, string key)
    {
        var v = o.GetArray<object>(key).Select(Convert.ToSingle).ToArray();
        return (new Vec3(v[0], v[1], v[2]), new Quat(v[4], v[5], v[6], v[7]));
    }

    /// <summary>The joints' frame quaternions as 18005d370 and 18005cb80 normalise them: a dpps length, the identity when zero.</summary>
    private static Quat NormalizeFrame(Quat q)
    {
        var length = MathF.Sqrt(((q.X * q.X) + (q.Y * q.Y)) + ((q.Z * q.Z) + (q.W * q.W)));
        return length == 0f ? Quat.Identity : new Quat(q.X / length, q.Y / length, q.Z / length, q.W / length);
    }

    /// <summary>The CRnJoint base ctor's state (FUN_1802042b0) and the flag bits from the world desc.</summary>
    private static void BaseJoint(ref JointCommon c, int type, bool collision, bool linearDisabled, bool angularDisabled)
    {
        c.WorldIndex = -1;
        c.Type = type;
        c.Flags = (ushort)((collision ? 2 : 0) | (linearDisabled ? 4 : 0) | (angularDisabled ? 8 : 0));
        c.BucketIndex = -1;
        c.IslandIndex = -1;
        c.Colour = 0xff;
        c.SolverA = -1;
        c.SolverB = -1;
    }

    /// <summary>
    /// The joints (FUN_180047600): per VPhysXJoint_t the desc (frames, the
    /// enable bits, friction, elasticity, plasticity, limits converted to
    /// degrees), then the type's creator: types 4 and 10 CRnConicalJoint
    /// (FUN_18005d370), 3 CRnRevoluteJoint (FUN_18005cb80), each constructed,
    /// added to the world (1801ef300, 1801f0750), then set in the creator's
    /// order. Limits make the degree round trip x * 57.295776 * 0.017453292.
    /// </summary>
    private static void CreateJoints(RnWorld w, AggregateBuild a, List<RnBody> made)
    {
        const float ToDegrees = 57.295776f, ToRadians = 0.017453292f;
        foreach (var rec in a.Joints)
        {
            var type = (int)rec.GetIntegerProperty("m_nType");
            var flags = (int)rec.GetIntegerProperty("m_nFlags");
            if ((flags & 1) != 0)
                throw new NotSupportedException("a joint to the world's static body (VPhysXJoint_t flags bit 0) is not ported");
            var i1 = (int)rec.GetIntegerProperty("m_nBody1");
            var i2 = (int)rec.GetIntegerProperty("m_nBody2");
            if (i1 >= made.Count || i2 >= made.Count)
                continue;
            RnBody b1 = made[i1], b2 = made[i2];
            var (p1, q1) = Frame(rec, "m_Frame1");
            var (p2, q2) = Frame(rec, "m_Frame2");
            q1 = NormalizeFrame(q1);
            q2 = NormalizeFrame(q2);
            var collision = Flag(rec, "m_bEnableCollision");
            var linearDisabled = Flag(rec, "m_bIsLinearConstraintDisabled");
            var angularDisabled = Flag(rec, "m_bIsAngularConstraintDisabled");
            var friction = Float(rec, "m_flFriction");
            var elasticity = Float(rec, "m_flElasticity");
            if (Flag(rec, "m_bEnableAngularMotor") || Flag(rec, "m_bEnableLinearMotor") || Flag(rec, "m_bEnableLinearLimit"))
                throw new NotSupportedException("a joint with a motor or a linear limit is not ported");
            if (friction <= 0f && elasticity > 0f)
                throw new NotSupportedException("an elastic joint (18022c480, 18021ade0) is not ported");
            RnJoint joint;
            switch (type)
            {
                case 4 or 10:
                {
                    var s = new ConicalJointState
                    {
                        Frame1 = q1, Frame2 = q2, Anchor1 = p1, Anchor2 = p2, Size = 0x1e8, Mode = 3, Target = Quat.Identity,
                        Frequency = 10f, DampingRatio = 1f, TwistMin = -MathF.PI, TwistMax = MathF.PI, SwingMax = MathF.PI,
                    };
                    joint = RnJoint.Of(s);
                    BaseJoint(ref joint.Common, 10, collision, linearDisabled, angularDisabled);
                    w.AddJoint(joint, b1, b2);
                    ref var j = ref joint.Conical;
                    // The setters, from the desc: frequency 0 (desc +0xa4), damping the elastic damping (+0xa8).
                    j.Frequency = 0f;
                    j.DampingRatio = Float(rec, "m_flElasticDamping");
                    if (friction > 0f && FrictionSide(joint) is { } side)
                    {
                        j.Mode = 1;
                        j.MotorImpulse = default;
                        j.MaxTorque = FrictionTorque(w, joint, side, friction);
                    }
                    if (Flag(rec, "m_bEnableSwingLimit"))
                    {
                        j.SwingLimited = 1;
                        j.SwingMax = (rec.GetSubCollection("m_SwingLimit").GetFloatProperty("m_flMax") * ToDegrees) * ToRadians;
                    }
                    if (Flag(rec, "m_bEnableTwistLimit"))
                    {
                        var t = rec.GetSubCollection("m_TwistLimit");
                        j.TwistLimited = 1;
                        j.TwistMin = (t.GetFloatProperty("m_flMin") * ToDegrees) * ToRadians;
                        j.TwistMax = (t.GetFloatProperty("m_flMax") * ToDegrees) * ToRadians;
                    }
                    break;
                }
                case 3 when (flags & 2) == 0:
                {
                    var s = new RevoluteJointState
                    {
                        Frame1 = q1, Frame2 = q2, Anchor1 = p1, Anchor2 = p2, Size = 0x1ac, Mode = 3, Frequency = 10f, DampingRatio = 1f,
                        Min = -MathF.PI, Max = MathF.PI,
                    };
                    joint = RnJoint.Of(s);
                    BaseJoint(ref joint.Common, 3, collision, linearDisabled, angularDisabled);
                    w.AddJoint(joint, b1, b2);
                    ref var j = ref joint.Revolute;
                    j.Frequency = 0f;
                    j.DampingRatio = Float(rec, "m_flElasticDamping");
                    if (friction > 0f && FrictionSide(joint) is { } side)
                    {
                        j.Mode = 1;
                        j.MotorImpulse = 0f;
                        j.TargetSpeed = 0f;
                        j.MaxTorque = FrictionTorque(w, joint, side, friction);
                    }
                    if (Flag(rec, "m_bEnableTwistLimit"))
                    {
                        var t = rec.GetSubCollection("m_TwistLimit");
                        j.Limited = 1;
                        j.Min = (t.GetFloatProperty("m_flMin") * ToDegrees) * ToRadians;
                        j.Max = (t.GetFloatProperty("m_flMax") * ToDegrees) * ToRadians;
                    }
                    break;
                }
                default:
                    throw new NotSupportedException($"a joint of type {type} (flags {flags}) is not ported");
            }
        }
    }

    /// <summary>FUN_180204ff0: body 2 when it is not static, else body 1 when that is not; none when both are static.</summary>
    private static int? FrictionSide(RnJoint j)
        => j.Body2!.State.BodyType != 0 ? 1 : j.Body1!.State.BodyType != 0 ? 0 : null;

    /// <summary>
    /// FUN_180204d70: the friction torque on a side, r ((|g| m) friction), r the
    /// larger of the inertia radius sqrt((Ixx + Iyy + Izz) / (m 3)) (the
    /// inertia from FUN_1801b4d30's cofactor inverse of the scaled local
    /// inverse inertia) and the anchor's (times the body scale) distance
    /// from the local mass centre.
    /// </summary>
    private static float FrictionTorque(RnWorld w, RnJoint j, int side, float friction)
    {
        var b = side == 0 ? j.Body1! : j.Body2!;
        ref readonly var s = ref b.State;
        var anchor = j.Type == RnJoint.ConicalType
            ? (side == 0 ? j.Conical.Anchor1 : j.Conical.Anchor2)
            : (side == 0 ? j.Revolute.Anchor1 : j.Revolute.Anchor2);
        var k = s.Scale;
        float ax = k * anchor.X, ay = anchor.Y * k, az = anchor.Z * k;
        var mass = s.InertiaDivisor;
        var inv = 1f / mass;
        ref readonly var m = ref s.LocalInvInertia;
        float m0 = inv * m.M0, m1 = inv * m.M1, m2 = inv * m.M2, m3 = inv * m.M3, m4 = inv * m.M4, m5 = inv * m.M5,
              m6 = inv * m.M6, m7 = inv * m.M7, m8 = inv * m.M8;
        var c00 = m4 * m8 - m7 * m5;
        var c10 = m5 * m6 - m3 * m8;
        var c20 = m7 * m3 - m6 * m4;
        var det = m0 * c00 + m1 * c10 + m2 * c20;
        if (!(1.17549435e-35f <= MathF.Abs(det)))
            throw new NotSupportedException("a friction torque on a body with a singular inertia (18055e880) is not ported");
        var r = 1f / det;
        var ixx = c00 * r;
        var iyy = (m0 * m8 - m2 * m6) * r;
        var izz = (m0 * m4 - m3 * m1) * r;
        var radius = MathF.Sqrt((ixx + iyy + izz) / (mass * 3f));
        var c = s.LocalMassCenter;
        var d = MathF.Sqrt((c.Z - az) * (c.Z - az) + (c.Y - ay) * (c.Y - ay) + (c.X - ax) * (c.X - ax));
        if (radius <= d)
            radius = d;
        var g = w.Gravity;
        var gravity = MathF.Sqrt(g.Y * g.Y + g.Z * g.Z + g.X * g.X);
        // The disassembly's order (180204f30..180204f6e): radius times ((|g| m) friction).
        return radius * ((gravity * mass) * friction);
    }
}
