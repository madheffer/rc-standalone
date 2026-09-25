using System.Numerics;
using Source2.Compiler.Physics;
using Source2.Compiler.Simulation;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using VrfHull = ValveResourceFormat.ResourceTypes.RubikonPhysics.Shapes.Hull;
using VrfMesh = ValveResourceFormat.ResourceTypes.RubikonPhysics.Shapes.Mesh;

namespace Source2.Compiler.Maps;

/// <summary>
/// The physics world resourcecompiler's settle builds from a map document
/// (the Hammer MapDocLib path, FUN_180f4c9e0): one body per collected node
/// and part, created static at the node's world transform, with the shapes
/// of its collision (a map mesh's own, or its model's PHYS aggregate).
/// </summary>
/// <remarks>
/// Covered so far: entities whose model has one physics part and no joints
/// (PhysPart_BuildFromModel, FUN_18105dbd0, the single-body branch) at a
/// uniform scale, their hulls and meshes as the model stores them.
/// </remarks>
public static partial class SettleWorld
{
    /// <summary>One shape as the settle adds it to its body.</summary>
    public sealed record ShapeBuild(int Type, RnHull? Hull, float HullScale, RnMesh? Mesh, Vector3 MeshScale,
                                    CollisionAttributes Attributes, ContactSolver.Material Material, string Name)
    {
        /// <summary>A capsule's (type 1) two centres and radius as the model stores them; its scale is <see cref="HullScale"/>.</summary>
        public (Vector3 A, Vector3 B, float Radius)? Capsule { get; init; }
    }

    /// <summary>
    /// One body: the node it comes from, the transform the settle gives it
    /// (world vfn 0x1e8, then body vfn 0x1b8 with the scale in the fourth
    /// float), and its shapes in the order they are added.
    /// </summary>
    public sealed record BodyBuild(int NodeId, Vector3 Position, float Scale, Quaternion Orientation, List<ShapeBuild> Shapes)
    {
        /// <summary>The map node the body comes from (for a placed copy, the template node).</summary>
        public DmxBinary.Element? Node { get; init; }
    }

    /// <summary>The model resources the build reads: a model path's physics aggregate, or null.</summary>
    public interface IModels
    {
        PhysAggregateData? Physics(string model);

        /// <summary>The surface property with this name hash as a Rubikon material (FUN_180072c70), or null for none.</summary>
        ContactSolver.Material? Surface(uint nameHash);

        /// <summary>The surface property with this name hash's own name (its surfacePropertyName), or null for none.</summary>
        string? SurfaceName(uint nameHash);

        /// <summary>A compiled material's int and string attributes, or null when it is missing.</summary>
        MaterialInfo? Material(string path);

        /// <summary>An entry of scripts/collision_properties.txt by name, or null.</summary>
        CollisionProperty? CollisionProperty(string name);

        /// <summary>A model's key values (m_modelInfo's m_keyValueText), or null.</summary>
        ValveKeyValue.KVObject? ModelKeyValues(string model);
    }

    /// <summary>What a compiled material (.vmat_c) carries for physics.</summary>
    public sealed record MaterialInfo(IReadOnlyDictionary<string, long> Ints, IReadOnlyDictionary<string, string> Strings);

    /// <summary>One named collision property: its group and its layer lists, comma separated.</summary>
    public sealed record CollisionProperty(string Group, string InteractAs, string InteractWith, string InteractExclude);

    /// <summary>
    /// The compiled surface properties (surfaceproperties.vsurf_c) as
    /// materials: density times 1.6387063e-5 (kg/m^3 to kg/in^3), friction,
    /// elasticity and thickness, by name hash.
    /// </summary>
    public static Dictionary<uint, ContactSolver.Material> SurfaceMaterials(ValveKeyValue.KVObject vsurf)
    {
        var entries = vsurf.GetArray("SurfacePropertiesList").ToDictionary(e => (uint)e.GetUInt32Property("m_nameHash"));
        // A thickness left out comes from the base surface, and so on up; with
        // none stated anywhere it is 0.1 (measured on atixref's settle).
        float Thickness(ValveKeyValue.KVObject e)
        {
            for (var at = e; at != null;)
            {
                var physics = at.GetSubCollection("physics");
                if (physics.ContainsKey("thickness"))
                    return physics.GetFloatProperty("thickness");
                at = entries.GetValueOrDefault((uint)at.GetUInt32Property("m_baseNameHash"));
            }
            return 0.1f;
        }
        var found = new Dictionary<uint, ContactSolver.Material>();
        foreach (var (hash, entry) in entries)
        {
            var physics = entry.GetSubCollection("physics");
            found[hash] = new ContactSolver.Material
            {
                Density = physics.GetFloatProperty("density") * 1.6387063e-05f,
                Friction = physics.GetFloatProperty("friction"),
                Restitution = physics.GetFloatProperty("elasticity"),
                Thickness = Thickness(entry),
            };
        }
        return found;
    }

    /// <summary>The compiled surface properties' names (surfacePropertyName), by name hash.</summary>
    public static Dictionary<uint, string> SurfaceNames(ValveKeyValue.KVObject vsurf)
        => vsurf.GetArray("SurfacePropertiesList").ToDictionary(e => (uint)e.GetUInt32Property("m_nameHash"), e => e.GetStringProperty("surfacePropertyName"));

    /// <summary>Rubikon's shape type for a capsule (a sphere is 0, a hull 2, a mesh 3).</summary>
    public const int CapsuleType = 1;

    /// <summary>The name hash of the surface property "default", which a shape without one gets.</summary>
    public const uint DefaultSurface = 1977497166;

    /// <summary>
    /// Every body the covered node kinds produce: the nodes the map holds, in
    /// element order, then the nodes its instances place, at the ids the
    /// instance bake gives them (see <see cref="MapInstances"/>). createdOnLoad
    /// counts the nodes the loader makes before the bake (smart prop locators).
    /// </summary>
    public static List<BodyBuild> Build(DmxBinary.Document document, IModels models, FgdSchema? schema = null, int createdOnLoad = 0)
    {
        var context = new Context(models, new CollisionRules(), schema, []);
        // A group an instance places is built where the instances put it,
        // not where it stands (as MapMeshes walks it).
        var targets = new HashSet<DmxBinary.Element>(ReferenceEqualityComparer.Instance);
        foreach (var instance in document.OfType("CMapInstance"))
            if (instance.Get<DmxBinary.Element>("target") is { } target)
                targets.Add(target);
        // A node the visibility manager hides is not compiled, so it has no
        // body: Mako's ten hidden meshes are missing from Valve's settle, and
        // no captured body (atixref, c2m2, Mako) sits under a hidden node.
        var hidden = MapEntities.HiddenNodes(document);
        foreach (var world in document.OfType("CMapWorld"))
            Walk(world, null, context, targets, hidden);
        var parents = new Dictionary<DmxBinary.Element, DmxBinary.Element>(ReferenceEqualityComparer.Instance);
        foreach (var e in document.Elements)
            foreach (var child in e.GetElements("children"))
                parents.TryAdd(child, e);
        // A placed node stands where the bake moved its copy (see Baked).
        MapInstances.Expand(document, MapEntities.From(document), createdOnLoad, (node, id, through) =>
        {
            if (targets.Contains(node))
                return;
            if (ModelOf(node, models) is { } phys)
                FromModel(node, NodeWorld(Baked(node, through)), id, phys, context);
            else if (node.Type == "CMapMesh")
            {
                var owner = parents.GetValueOrDefault(node);
                while (owner != null && owner.Type != "CMapEntity")
                    owner = parents.GetValueOrDefault(owner);
                FromMesh(node, NodeWorld(Baked(node, through)), id, owner, context);
            }
        });
        // PhysDoc_BuildObjects std::sorts the collected nodes by pointer. The
        // loader allocates them in element order, so element order is that
        // sort but for where entity and mesh allocations interleave by page
        // (3955 of atixref's 4143 neighbours agree); copies come last.
        var index = new Dictionary<DmxBinary.Element, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < document.Elements.Count; i++)
            index[document.Elements[i]] = i;
        return [.. context.Bodies.Select((b, i) => (b, i))
            .OrderBy(t => t.b.Node is { } n && t.b.NodeId == (n.GetValue<int>("nodeID") ?? -1) ? index[n] : int.MaxValue)
            .ThenBy(t => t.i).Select(t => t.b)];
    }

    /// <summary>
    /// MapNode_WorldMatrix: the instance step matrix (identity once the bake
    /// has collapsed every instance) concatenated with the node's own, which
    /// turns each -0 entry into +0.
    /// </summary>
    private static float[] NodeWorld(float[] local) => MapMeshes.Concat([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0], local);

    /// <summary>What every builder reads and where the bodies go.</summary>
    private sealed record Context(IModels Models, CollisionRules Rules, FgdSchema? Schema, List<BodyBuild> Bodies);

    private static PhysAggregateData? ModelOf(DmxBinary.Element node, IModels models)
        => node.Type == "CMapEntity" && node.Get<DmxBinary.Element>("entity_properties") is { } props
           && props.Get<string>("model") is { Length: > 0 } model ? models.Physics(model) : null;

    /// <summary>
    /// The document walk: each node's children in stored order, a model's
    /// entity and a map mesh built where they stand, hidden nodes and what
    /// is under them left out. owner is the nearest entity above (FUN_180f8b770).
    /// </summary>
    private static void Walk(DmxBinary.Element node, DmxBinary.Element? owner, Context context, HashSet<DmxBinary.Element> targets,
                             HashSet<int> hidden)
    {
        foreach (var child in node.GetElements("children"))
        {
            if (hidden.Contains(child.GetValue<int>("nodeID") ?? -1))
                continue;
            if (ModelOf(child, context.Models) is { } phys)
                FromModel(child, NodeWorld(MapMeshes.Local(child)), child.GetValue<int>("nodeID") ?? -1, phys, context);
            else if (child.Type == "CMapMesh")
                FromMesh(child, NodeWorld(MapMeshes.Local(child)), child.GetValue<int>("nodeID") ?? -1, owner, context);
            if (child.Type != "CMapInstance" && !targets.Contains(child))
                Walk(child, child.Type == "CMapEntity" ? child : owner, context, targets, hidden);
        }
    }

    /// <summary>
    /// A placed node's matrix once the bake has moved its copy. Each collapse
    /// (CMapInstance_Collapse) clones the target group, sets the clone to the
    /// instance's origin and angles, and moves every node in it by the change
    /// (FUN_180f834d0): the instance's matrix against the target's inverse, both
    /// built in double (FUN_1811145c0). A nested instance is moved by its outer
    /// collapse before its own; nothing moves when instance and target agree.
    /// </summary>
    internal static float[] Baked(DmxBinary.Element node, IReadOnlyList<DmxBinary.Element> through)
    {
        var origin = node.GetValue<Vector3>("origin") ?? Vector3.Zero;
        var angles = node.GetValue<Vector3>("angles") ?? Vector3.Zero;
        float[]? delta = null;
        foreach (var instance in through)
        {
            var at = instance.GetValue<Vector3>("origin") ?? Vector3.Zero;
            var turn = instance.GetValue<Vector3>("angles") ?? Vector3.Zero;
            if (delta != null)
                (at, turn) = Moved(delta, at, turn);
            var target = instance.Get<DmxBinary.Element>("target")!;
            var from = target.GetValue<Vector3>("origin") ?? Vector3.Zero;
            var fromAngles = target.GetValue<Vector3>("angles") ?? Vector3.Zero;
            delta = at == from && turn == fromAngles ? null
                  : MapMeshes.Concat(AngleMatrixDouble(turn, at), MapMeshes.Invert(AngleMatrixDouble(fromAngles, from)));
        }
        if (delta != null)
            (origin, angles) = Moved(delta, origin, angles);
        var m = MapMeshes.AngleMatrix(angles);
        m[3] = origin.X;
        m[7] = origin.Y;
        m[11] = origin.Z;
        return m;
    }

    /// <summary>
    /// A node moved by a matrix (TransformBy with flags 0x41, FUN_181001820):
    /// the origin through the matrix as a 4x4 with a last row of (0, 0, 0, 1)
    /// (FUN_180fdef60), the angles as the matrix's own angles rebuilt, times
    /// the node's (FUN_180fdf210), and stored through SetAngles (FUN_180fdeda0).
    /// </summary>
    private static (Vector3 Origin, Vector3 Angles) Moved(float[] m, Vector3 o, Vector3 a)
    {
        Vector3 origin = new((((m[0] * o.X) + (m[1] * o.Y)) + (m[2] * o.Z)) + m[3],
                             (((m[4] * o.X) + (m[5] * o.Y)) + (m[6] * o.Z)) + m[7],
                             (((m[8] * o.X) + (m[9] * o.Y)) + (m[10] * o.Z)) + m[11]);
        var turned = MatrixAngles(MapMeshes.Concat(MapMeshes.AngleMatrix(MatrixAngles(m)), MapMeshes.AngleMatrix(a)));
        return (origin, SetAngles(turned));
    }

    /// <summary>FUN_180fdeda0: any angle under 0.001 becomes 0, and a negative yaw gains 360 until it is not.</summary>
    private static Vector3 SetAngles(Vector3 a)
    {
        float pitch = MathF.Abs(a.X) < 0.001f ? 0f : a.X, yaw = MathF.Abs(a.Y) < 0.001f ? 0f : a.Y;
        var roll = MathF.Abs(a.Z) < 0.001f ? 0f : a.Z;
        while (yaw < 0f)
            yaw += 360f;
        return new Vector3(pitch, yaw, roll);
    }

    /// <summary>
    /// FUN_18125cba0 (MatrixAngles): yaw from the first column, pitch from its
    /// length in the plane, roll from the third row; a column shorter than 0.001
    /// takes yaw from the second column and no roll.
    /// </summary>
    private static Vector3 MatrixAngles(float[] m)
    {
        const float Degrees = 57.295776f;
        var length = MathF.Sqrt((m[4] * m[4]) + (m[0] * m[0]));
        if (length <= 0.001f)
            return new Vector3(MathF.Atan2(-m[8], length) * Degrees, MathF.Atan2(-m[1], m[5]) * Degrees, 0f);
        return new Vector3(MathF.Atan2(-m[8], length) * Degrees, MathF.Atan2(m[4], m[0]) * Degrees,
                           MathF.Atan2(m[9], m[10]) * Degrees);
    }

    /// <summary>
    /// FUN_1811145c0: AngleMatrix with each sine and cosine taken in double on
    /// the angle folded into [-90, 90] (sine) or [0, 180] (cosine), then the
    /// products in float.
    /// </summary>
    private static float[] AngleMatrixDouble(Vector3 angles, Vector3 origin)
    {
        static double Fold(float degrees, bool cosine)
        {
            var d = (double)degrees % 360.0;
            if (d < 0.0)
                d += 360.0;
            if (cosine)
                return d > 180.0 ? 360.0 - d : d;
            return d > 270.0 ? d - 360.0 : d > 90.0 ? 180.0 - d : d;
        }
        static float Sin(float degrees) => (float)Math.Sin((Fold(degrees, false) * 3.141592653589793) / 180.0);
        static float Cos(float degrees) => (float)Math.Cos((Fold(degrees, true) * 3.141592653589793) / 180.0);
        float sp = Sin(angles.X), sy = Sin(angles.Y), sr = Sin(angles.Z);
        float cp = Cos(angles.X), cy = Cos(angles.Y), cr = Cos(angles.Z);
        return [cy * cp, (cy * (sr * sp)) - (sy * cr), ((cr * sp) * cy) - ((-sy) * sr), origin.X,
                sy * cp, (sy * (sr * sp)) + (cr * cy), ((cr * sp) * sy) - (sr * cy), origin.Y,
                -sp, sr * cp, cr * cp, origin.Z];
    }

    /// <summary>
    /// A model's single physics part (FUN_18105dbd0): the body at the node's
    /// transform, decomposed into position, uniform scale and orientation,
    /// and each hull and mesh of the part at that scale.
    /// </summary>
    private static void FromModel(DmxBinary.Element node, float[] local, int nodeId, PhysAggregateData phys, Context context)
    {
        var (models, rules, bodies) = (context.Models, context.Rules, context.Bodies);
        var parts = phys.Parts;
        if (parts.Length == 0)
            return;
        if (parts.Length > 1)
            throw new NotSupportedException("a model with several physics parts (an aggregate)");
        var scales = node.GetValue<Vector3>("scales") ?? Vector3.One;
        var u = MathF.Max(MathF.Max(MathF.Abs(scales.X), MathF.Abs(scales.Y)), MathF.Abs(scales.Z));
        // Only a static_prop class keeps a non-uniform scale; any other gets
        // the largest axis on all three.
        var className = node.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname");
        var scale = className == "prop_static" ? scales : new Vector3(u);
        // The body's scale is the longest column of the rotation (FUN_181003930
        // through FUN_18125b270), which is 0.99999994 for some angles.
        var longest = Enumerable.Range(0, 3)
            .Select(c => MathF.Sqrt(((local[c] * local[c]) + (local[4 + c] * local[4 + c])) + (local[8 + c] * local[8 + c])))
            .Aggregate(0f, (a, b) => a <= b ? b : a);
        var body = new BodyBuild(nodeId, new Vector3(local[3], local[7], local[11]), longest,
                                 WithIdentity(Orientation(Unscaled(local))), []) { Node = node };
        // A non-uniform scale takes the matrix path: each shape's points go
        // through the scale matrix (FUN_18125c200) and are cooked again at
        // scale 1 (RnHullCreate, RnMeshCreate).
        float[]? matrix = NonUniform(scale) ? [scale.X, 0, 0, 0, 0, scale.Y, 0, 0, 0, 0, scale.Z, 0] : null;
        var shape = parts[0].Shape;
        var props = node.Get<DmxBinary.Element>("entity_properties")!;
        var table = phys.CollisionAttributes.Select(rules.Convert).ToArray();
        var surfaces = phys.SurfacePropertyHashes;
        CollisionAttributes Attributes(int index) => ShapeAttributes(props, className, table, index, parts[0].CollisionAttributeIndex);
        ContactSolver.Material Material(int index)
            => index >= 0 && index < surfaces.Length && models.Surface(surfaces[index]) is { } m ? m
             : models.Surface(DefaultSurface) ?? DefaultMaterial;
        // Capsules are added before hulls (the shape desc's order); a
        // non-uniform scale would need the matrix path, which a capsule lacks.
        foreach (var c in shape.Capsules)
        {
            if (matrix != null)
                throw new NotSupportedException("a capsule at a non-uniform scale");
            body.Shapes.Add(new ShapeBuild(CapsuleType, null, u, null, Vector3.One, Attributes(c.CollisionAttributeIndex),
                                           Material(c.SurfacePropertyIndex), c.UserFriendlyName ?? "")
                            { Capsule = (c.Shape.Center[0], c.Shape.Center[1], c.Shape.Radius) });
        }
        if (shape.Spheres.Length > 0)
            throw new NotSupportedException("a model with sphere shapes");
        foreach (var h in shape.Hulls)
        {
            var hull = Hull(h.Shape);
            if (matrix != null)
            {
                var points = hull.VertexPositions.Select(v => MapMeshes.Transform(matrix, v)).ToArray();
                hull = RnHullBuilder.Create(points, null, out _) ?? throw new InvalidDataException("a scaled hull did not cook");
            }
            body.Shapes.Add(new ShapeBuild(BroadphaseShape.HullType, hull, matrix != null ? 1f : u, null, Vector3.One,
                                           Attributes(h.CollisionAttributeIndex), Material(h.SurfacePropertyIndex), h.UserFriendlyName ?? ""));
        }
        foreach (var m in shape.Meshes)
        {
            var mesh = Mesh(m.Shape);
            if (matrix != null)
            {
                var points = mesh.Vertices.Select(v => MapMeshes.Transform(matrix, v)).ToArray();
                var indices = mesh.Triangles.SelectMany(t => new[] { t.A, t.B, t.C }).ToArray();
                mesh = RnMeshBuilder.Create(indices, points, mesh.Materials.Length > 0 ? mesh.Materials : null)
                       ?? throw new InvalidDataException("a scaled mesh did not cook");
            }
            body.Shapes.Add(new ShapeBuild(BroadphaseShape.MeshType, null, 0, mesh, matrix != null ? Vector3.One : new Vector3(u),
                                           Attributes(m.CollisionAttributeIndex), Material(m.SurfacePropertyIndex), m.UserFriendlyName ?? ""));
        }
        bodies.Add(body);
    }

    /// <summary>The material FUN_18105dbd0 starts from when the model names no surface property.</summary>
    private static readonly ContactSolver.Material DefaultMaterial = new() { Density = 0.015625f, Friction = 0.2f };

    /// <summary>
    /// A shape's collision attributes (FUN_18105dbd0): the model's entry for
    /// the shape, else for its part; an entity that is not solid (solid 0)
    /// gets the never-colliding record instead; any class but prop_static
    /// also interacts with solid, window, passbullets, player and npc
    /// (0xc3001). The entity id is the body's, -1 in the settle.
    /// </summary>
    private static CollisionAttributes ShapeAttributes(DmxBinary.Element props, string? className, CollisionAttributes[] table,
                                                       int shapeIndex, int partIndex)
    {
        if (props.Get<string>("solid") == "0")
            return new CollisionAttributes { EntityId = -1, OwnerId = -1, Group = 1, Flags = 7 };
        var a = shapeIndex >= 0 && shapeIndex < table.Length ? table[shapeIndex]
              : partIndex >= 0 && partIndex < table.Length ? table[partIndex]
              : new CollisionAttributes { EntityId = -1, OwnerId = -1, Flags = 7 };
        if (className != "prop_static")
            a.InteractsWith |= 0xc3001;
        return a;
    }

    /// <summary>FUN_18105dbd0's test: two axes differ by more than 1e-5 of the larger (at least 1).</summary>
    private static bool NonUniform(Vector3 s)
    {
        static bool Differ(float a, float b) => MathF.Max(1f, MathF.Max(MathF.Abs(a), MathF.Abs(b))) * 1e-05f < MathF.Abs(a - b);
        return Differ(s.X, s.Y) || Differ(s.X, s.Z) || Differ(s.Y, s.Z);
    }

    /// <summary>
    /// The body's orientation as FUN_181253510 hands it over: the part's
    /// quaternion times the identity bind pose, lane by lane as the SIMD
    /// product sums it (which turns a -0 into +0), then normalised by
    /// dividing by the root of ((x^2 + y^2) + (z^2 + w^2)).
    /// </summary>
    internal static Quaternion WithIdentity(Quaternion a)
    {
        var x = (a.Z * -0f) + (((a.W * 0f) + (a.X * 1f)) + (a.Y * 0f));
        var y = (a.Z * 0f) + (((a.W * 0f) + (a.X * -0f)) + (a.Y * 1f));
        var z = (a.Z * 1f) + (((a.W * 0f) + (a.X * 0f)) + (a.Y * -0f));
        var w = (a.Z * -0f) + (((a.W * 1f) + (a.X * -0f)) + (a.Y * -0f));
        var length = MathF.Sqrt(((x * x) + (y * y)) + ((z * z) + (w * w)));
        return length == 0f ? default : new Quaternion(x / length, y / length, z / length, w / length);
    }

    /// <summary>
    /// FUN_18125b270's rotation: each column divided by its length
    /// ((x^2 + y^2) + z^2, rooted; a zero length leaves the column).
    /// </summary>
    internal static float[] Unscaled(float[] m)
    {
        var o = (float[])m.Clone();
        for (var c = 0; c < 3; c++)
        {
            var length = MathF.Sqrt(((m[c] * m[c]) + (m[4 + c] * m[4 + c])) + (m[8 + c] * m[8 + c]));
            var inverse = length == 0f ? 1f : 1f / length;
            for (var r = 0; r < 3; r++)
                o[(r * 4) + c] = m[(r * 4) + c] * inverse;
        }
        return o;
    }

    /// <summary>
    /// A rotation matrix (matrix3x4, row major) as a quaternion
    /// (FUN_18125de90): from the trace when it is not negative, else from the
    /// largest diagonal element, each branch with its own sums.
    /// </summary>
    internal static Quaternion Orientation(float[] m)
    {
        float m00 = m[0], m01 = m[1], m02 = m[2], m10 = m[4], m11 = m[5], m12 = m[6], m20 = m[8], m21 = m[9], m22 = m[10];
        var xy = m00 + m11;
        var trace = xy + m22;
        if (!(0f > trace))
        {
            var s = MathF.Sqrt(trace + 1f);
            var k = 0.5f / s;
            return new Quaternion((m21 - m12) * k, (m02 - m20) * k, (m10 - m01) * k, s * 0.5f);
        }
        if (m22 > m00 && m22 > m11)
        {
            var s = MathF.Sqrt((m22 - xy) + 1f);
            var k = 0.5f / s;
            return new Quaternion((m20 + m02) * k, (m21 + m12) * k, s * 0.5f, (m10 - m01) * k);
        }
        if (!(m22 > m00) && !(m00 < m11))
        {
            var s = MathF.Sqrt((m00 - (m22 + m11)) + 1f);
            var k = 0.5f / s;
            return new Quaternion(s * 0.5f, (m10 + m01) * k, (m20 + m02) * k, (m21 - m12) * k);
        }
        {
            var s = MathF.Sqrt((m11 - (m22 + m00)) + 1f);
            var k = 0.5f / s;
            return new Quaternion((m10 + m01) * k, s * 0.5f, (m21 + m12) * k, (m02 - m20) * k);
        }
    }

    /// <summary>An RnHull_t as a model stores it (VPhysXAggregateData_t m_Hull).</summary>
    public static RnHull Hull(VrfHull h)
    {
        var mass = h.Data.GetArray<object>("m_MassProperties")?.Select(Convert.ToSingle).ToArray() ?? new float[12];
        var hull = new RnHull
        {
            Centroid = h.Centroid,
            MaxAngularRadius = h.MaxAngularRadius,
            BoundsMin = h.Min,
            BoundsMax = h.Max,
            OrthographicAreas = h.OrthographicAreas,
            MassProperties = mass,
            Volume = h.Volume,
            VertexPositions = h.GetVertexPositions().ToArray(),
            Vertices = h.GetVertices().ToArray(),
            Edges = h.GetEdges().ToArray().Select(e => (e.Next, e.Twin, e.Origin, e.Face)).ToArray(),
            Faces = h.GetFaces().ToArray().Select(f => f.Edge).ToArray(),
            Planes = h.GetPlanes().ToArray().Select(p => (p.Normal, p.Offset)).ToArray(),
            Flags = (uint)h.Data.GetInt32Property("m_nFlags"),
        };
        // The load's post pass (FUN_1801314f0): a hull saved before explicit
        // vertex indices gets each vertex's outgoing edge (the last edge
        // leaving it); a zero surface area is computed with the orthographic
        // areas (RnHull_SurfaceArea), a zero centroid radius too.
        if (hull.Vertices.Length == 0)
        {
            hull.Vertices = new byte[hull.VertexPositions.Length];
            for (var i = 0; i < hull.Edges.Length; i++)
                hull.Vertices[hull.Edges[i].Origin] = (byte)i;
        }
        hull.SurfaceArea = h.Data.GetFloatProperty("m_flSurfaceArea");
        hull.MinCentroidRadius = h.Data.GetFloatProperty("m_flMinCentroidRadius");
        if (hull.SurfaceArea == 0f)
            RnHullBuilder.Areas(hull);
        if (hull.MinCentroidRadius == 0f)
            RnHullBuilder.CentroidRadius(hull);
        return hull;
    }

    /// <summary>An RnMesh_t as a model stores it.</summary>
    public static RnMesh Mesh(VrfMesh m)
    {
        var nodes = m.ParseNodes();
        var triangles = m.GetTriangles();
        return new RnMesh
        {
            Min = m.Min,
            Max = m.Max,
            Nodes = nodes.ToArray().Select(n => new RnMesh.Node(n.Min, (uint)n.Type << 30 | n.ChildOffset, n.Max, n.TriangleOffset)).ToArray(),
            Vertices = m.GetVertices().ToArray(),
            Triangles = triangles.ToArray().Select(t => (t.X, t.Y, t.Z)).ToArray(),
            Materials = m.Materials.Select(x => (byte)x).ToArray(),
        };
    }

    /// <summary>
    /// vphysics2's default collision rules (FUN_180296fa0): the collision
    /// groups and interaction layers by name, the presets that stand for
    /// several layers, and the layers a model names that the rules do not
    /// know, which take the next free bit from 32 on in the order they are
    /// first met.
    /// </summary>
    public sealed class CollisionRules
    {
        private static readonly string[] Groups =
        [
            "always", "never", "trigger", "ConditionallySolid", "Default", "debris", "interactive_debris", "interactive",
            "Player", "breakable_glass", "vehicle", "player_movement", "npc", "in_vehicle", "weapon", "", "Projectile",
            "door_blocker", "passable_door", "dissolving", "pushaway", "npc_actor", "npc_scripted", "pz_clip", "props",
        ];

        private static readonly string[] Layers =
        [
            "solid", "hitboxes", "trigger", "sky", "playerclip", "npcclip", "blocklos", "blocklight", "ladder", "pickup",
            "blocksound", "nodraw", "window", "passbullets", "worldGeometry", "water", "slime", "touchall", "player", "npc",
            "debris", "physics_prop", "NavIgnore", "NavLocalIgnore", "PostProcessingVolume", "vehicleclip", "CarriedObject",
            "pushaway", "serverentityonclient", "CarriedWeapon", "StaticLevel",
        ];

        private readonly Dictionary<string, ulong> _layers = new(StringComparer.OrdinalIgnoreCase);
        // A name the rules do not know takes the lowest free bit, and 31 is
        // the first (FUN_180296fa0 registers 0 to 30): atixref's csgo_grenadeclip
        // takes 31 and csgo_thrown_grenade 32, c2m2's first new layer 31.
        private int _next = 31;

        /// <summary>CONTENTS_SOLID: solid, blocksound, blocklos, blocklight; also what an empty interact-as means.</summary>
        public const ulong ContentsSolid = 0x4c1;

        public CollisionRules()
        {
            for (var i = 0; i < Layers.Length; i++)
                _layers[Layers[i]] = 1ul << i;
            _layers["CONTENTS_SOLID"] = ContentsSolid;
            _layers["CONTENTS_SOLID_NO_BLOCK_LOS"] = ContentsSolid & ~0x40ul;
        }

        public int Group(string name)
        {
            var i = Array.FindIndex(Groups, g => g.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i;
        }

        /// <summary>
        /// A layer's bit, registering an unknown name. An empty name is a name
        /// like any other: Mako's spindle_01_a lists "" as its interact-as and
        /// Valve gives it a new bit (32). Lists parsed from text drop empty
        /// entries before they get here (see <see cref="Mask"/>).
        /// </summary>
        public ulong Layer(string name)
        {
            if (!_layers.TryGetValue(name, out var bits))
                _layers[name] = bits = 1ul << _next++;
            return bits;
        }

        /// <summary>A comma separated list of layer names as one mask (the collision interface's string parse).</summary>
        public ulong Mask(string list)
            => list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Aggregate(0ul, (a, n) => a | Layer(n));

        /// <summary>A model's VPhysXCollisionAttributes_t as the loaded aggregate holds it.</summary>
        public CollisionAttributes Convert(ValveKeyValue.KVObject attributes)
        {
            ulong Bits(string key) => (attributes.GetArray<string>(key) ?? []).Aggregate(0ul, (a, n) => a | Layer(n));
            var a = new CollisionAttributes
            {
                InteractsAs = Bits("m_InteractAsStrings"),
                InteractsWith = Bits("m_InteractWithStrings"),
                InteractsExclude = Bits("m_InteractExcludeStrings"),
                EntityId = -1,
                OwnerId = -1,
                Flags = 7,
            };
            if ((attributes.GetArray<string>("m_InteractAsStrings") ?? []).Length == 0)
                a.InteractsAs = ContentsSolid;
            var group = Group(attributes.GetStringProperty("m_CollisionGroupString") ?? "");
            a.Group = (byte)(group < 0 ? 0 : group);
            return a;
        }
    }
}
