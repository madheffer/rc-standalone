using System.Numerics;
using Source2.Compiler.Maps;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler.Physics;

/// <summary>
/// A solid prop_static's hulls as they reach the world physics part.
/// physicsbuilder (CreateStaticPropShapesForPropInstance, 0924: 1800174e0)
/// takes each body of the prop model's compiled physics, moves it by the
/// prop's matrix times the body's bind pose, and hands each hull to the
/// world ModelDoc as a PhysicsShapeHull node (180152fd0): the hull's vertex
/// positions with the node's origin and angles, or, when that matrix is
/// scaled, the positions moved and no node transform. resourcecompiler then
/// quickhulls the node's points (1802c0e70), cooks them with RnHullCreate and
/// moves the hull by the node's transform (ShapeBuilder_BuildHullShape).
/// Measured on ze_hold_em_nb: all 129 hulls of its 86 props are bit for bit,
/// region SVMs included.
/// </summary>
public static class StaticPropHulls
{
    /// <summary>A prop_static as the builder reads it: model, node origin, angles and scales.</summary>
    public sealed record Prop(int NodeId, string Model, Vector3 Origin, Vector3 Angles, Vector3 Scales);

    /// <summary>One hull node: the points it carries and its transform (origin, angles).</summary>
    public sealed record HullNode(int Part, Vector3[] Points, Vector3 Origin, Vector3 Angles);

    /// <summary>
    /// The prop's matrix (1800b5a50 then 1800b8fc0): AngleMatrix of its angles
    /// with its origin as the translation, each column times its scale.
    /// </summary>
    public static float[] PropMatrix(Prop prop)
    {
        var m = MapMeshes.AngleMatrix(prop.Angles);
        (m[3], m[7], m[11]) = (prop.Origin.X, prop.Origin.Y, prop.Origin.Z);
        var s = prop.Scales;
        m[0] *= s.X;
        m[1] *= s.Y;
        m[2] *= s.Z;
        m[4] *= s.X;
        m[5] *= s.Y;
        m[6] *= s.Z;
        m[8] *= s.X;
        m[9] *= s.Y;
        m[10] *= s.Z;
        return m;
    }

    /// <summary>A body's bind pose (m_bindPose, a 3x4 each), identity past the end of the list.</summary>
    public static float[] BindPose(PhysAggregateData phys, int body)
    {
        var poses = phys.Data.GetArray("m_bindPose");
        if (poses == null || body >= poses.Count)
            return [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];
        var pose = poses[body];
        return [.. Enumerable.Range(0, 12).Select(i => (float)pose[i])];
    }

    /// <summary>
    /// The hull nodes physicsbuilder makes for a prop, body by body and hull by
    /// hull. A hull its quickhull check rejects (18015fb60: invalid, or more
    /// than 256 faces, half-edges or vertices) makes none. Bone overrides by
    /// body name (the instance's table at +0x268) and the lattice branch (a
    /// hull turned into a mesh node) are not ported.
    /// </summary>
    public static List<HullNode> Nodes(Prop prop, PhysAggregateData phys)
    {
        var nodes = new List<HullNode>();
        var m = PropMatrix(prop);
        for (var part = 0; part < phys.Parts.Length; part++)
        {
            var local = MapMeshes.Concat(m, BindPose(phys, part));
            foreach (var desc in phys.Parts[part].Shape.Hulls)
            {
                var points = desc.Shape.GetVertexPositions().ToArray();
                var scale = ColumnLengths(local);
                Vector3 origin = Vector3.Zero, angles = Vector3.Zero;
                if (MathF.Abs(scale.X - 1f) > 0.01f || MathF.Abs(scale.Y - 1f) > 0.01f || MathF.Abs(scale.Z - 1f) > 0.01f)
                {
                    // 1800b9810: the point with w = 1 against each row.
                    for (var i = 0; i < points.Length; i++)
                        points[i] = MapMeshes.Transform(local, points[i]);
                }
                else
                {
                    origin = new Vector3(local[3], local[7], local[11]);
                    angles = MatrixAngles(local);
                }
                if (Complexity(points) is not { } c || c.Faces > 256 || c.HalfEdges > 256 || c.Vertices > 256)
                    continue;
                nodes.Add(new HullNode(part, points, origin, angles));
            }
        }
        return nodes;
    }

    /// <summary>
    /// resourcecompiler's hull shape for a node (1802c0e70, 180c261c0,
    /// ShapeBuilder_BuildHullShape): the quickhull of its points at tolerance 0,
    /// at most 256 faces, half-edges and vertices, cooked by RnHullCreate, its
    /// region SVM built, then moved by QuaternionMatrix of the node's
    /// transform. Null where the compile drops the node.
    /// </summary>
    public static RnHull? Shape(HullNode node)
    {
        if (Complexity(node.Points) is not { } c || c.Faces > 256 || c.HalfEdges > 256 || c.Vertices > 256)
            return null;
        var points = BrushHulls.ShapePoints(node.Points);
        if (points == null)
            return null;
        var hull = RnHullBuilder.Create(points, RnHullBuilder.Options.Compile, out _);
        if (hull == null)
            return null;
        hull.RegionSvm = RegionSvmBuilder.Build(hull);
        RnHullBuilder.Transform(hull, NodeTransform(node).Matrix());
        return hull;
    }

    /// <summary>
    /// A node's transform as the model compile reads it (1814f34e0): its origin
    /// and AngleQuaternion of its angles, composed with the inverse of the
    /// parent's (the world model's identity). The composition is what turns a
    /// -0 in the quaternion into +0 (every hull's region SVM planes show it).
    /// </summary>
    internal static CTransform NodeTransform(HullNode node)
        => CTransform.Compose(new CTransform(node.Origin, 1f, CTransform.AngleQuaternion(node.Angles)),
                              new CTransform(Vector3.Zero, 1f, Quaternion.Identity).Inverse());

    // 1800b6b50: each column's length.
    private static Vector3 ColumnLengths(float[] m)
        => new(MathF.Sqrt((m[0] * m[0]) + (m[4] * m[4]) + (m[8] * m[8])),
               MathF.Sqrt((m[1] * m[1]) + (m[5] * m[5]) + (m[9] * m[9])),
               MathF.Sqrt((m[2] * m[2]) + (m[6] * m[6]) + (m[10] * m[10])));

    // 1800b9240 (MatrixAngles): the same as resourcecompiler's FUN_18125cba0.
    private static Vector3 MatrixAngles(float[] m)
    {
        const float Degrees = 57.295776f;
        var length = MathF.Sqrt((m[4] * m[4]) + (m[0] * m[0]));
        if (length <= 0.001f)
            return new Vector3(MathF.Atan2(-m[8], length) * Degrees, MathF.Atan2(-m[1], m[5]) * Degrees, 0f);
        return new Vector3(MathF.Atan2(-m[8], length) * Degrees, MathF.Atan2(m[4], m[0]) * Degrees,
                           MathF.Atan2(m[9], m[10]) * Degrees);
    }

    // A tolerance-0 quickhull's face, half-edge and vertex counts; null when invalid.
    private static (int Faces, int HalfEdges, int Vertices)? Complexity(Vector3[] points)
    {
        var flat = new float[points.Length * 3];
        for (var i = 0; i < points.Length; i++)
            (flat[i * 3], flat[(i * 3) + 1], flat[(i * 3) + 2]) = (points[i].X, points[i].Y, points[i].Z);
        var qh = new QuickHull();
        qh.Build(points.Length, flat, 0f, true);
        if (!qh.IsValid())
            return null;
        int faces = 0, halfEdges = 0;
        foreach (var f in qh.HullFaces)
        {
            faces++;
            var e = f.Edge;
            if (e == null)
                continue;
            var start = e;
            do
            {
                halfEdges++;
                e = e.Next;
            } while (e != start && halfEdges < 1 << 20);
        }
        return (faces, halfEdges, qh.HullVertices.Count());
    }
}
