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
    public sealed record Prop(int NodeId, string Model, Vector3 Origin, Vector3 Angles, Vector3 Scales)
    {
        /// <summary>The prop's collision_override key: a collision property's name, or empty.</summary>
        public string CollisionOverride { get; init; } = "";

        /// <summary>The prop's surface_property_override key: a surface property's name, or empty.</summary>
        public string SurfaceOverride { get; init; } = "";

        /// <summary>
        /// The lattice the prop takes from a CMapDeformer above it (entity
        /// +0x2b0), only when set (PropDeformer_IsSet); its shapes then become
        /// deformed triangle meshes (<see cref="DeformedHulls"/>, <see cref="Meshes"/>).
        /// </summary>
        internal Maps.LatticeDeformer? Deformer { get; init; }
    }

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
    public static List<HullNode> Nodes(Prop prop, PhysAggregateData phys) => Nodes(prop, phys, out _);

    /// <summary><see cref="Nodes(Prop, PhysAggregateData)"/>, with each node's hull description (collision attribute and surface indices).</summary>
    public static List<HullNode> Nodes(Prop prop, PhysAggregateData phys, out Dictionary<HullNode, ValveResourceFormat.ResourceTypes.RubikonPhysics.HullDescriptor> descs)
    {
        var nodes = new List<HullNode>();
        descs = new(ReferenceEqualityComparer.Instance);
        if (prop.Deformer != null)
            return nodes;
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
                var hullNode = new HullNode(part, points, origin, angles);
                nodes.Add(hullNode);
                descs[hullNode] = desc;
            }
        }
        return nodes;
    }

    /// <summary>
    /// A sphere or capsule node: its centres (one for a sphere, two for a
    /// capsule) and radius as physicsbuilder hands them over, the body, and
    /// the shape's collision attribute and surface indices in the model.
    /// </summary>
    public sealed record RoundNode(int Part, Vector3[] Centers, float Radius, int Attribute, int Surface);

    /// <summary>
    /// The sphere nodes physicsbuilder makes for a prop (180153740) and its
    /// capsule nodes (180152230), body by body: each centre moved by the prop
    /// matrix times the bind pose (1800b9810), the radius times the largest of
    /// that matrix's column lengths (1800b6b50). The lattice branch (the shape
    /// turned into a mesh node) is not ported.
    /// </summary>
    public static (List<RoundNode> Spheres, List<RoundNode> Capsules) Rounds(Prop prop, PhysAggregateData phys)
    {
        var spheres = new List<RoundNode>();
        var capsules = new List<RoundNode>();
        var m = PropMatrix(prop);
        // Under a deformer they are tessellated meshes instead (DeformedRounds).
        if (prop.Deformer != null)
            return (spheres, capsules);
        for (var part = 0; part < phys.Parts.Length; part++)
        {
            var local = MapMeshes.Concat(m, BindPose(phys, part));
            var c = ColumnLengths(local);
            var scale = MathF.Abs(c.X);
            if (scale <= MathF.Abs(c.Y))
                scale = MathF.Abs(c.Y);
            if (scale <= MathF.Abs(c.Z))
                scale = MathF.Abs(c.Z);
            foreach (var desc in phys.Parts[part].Shape.Spheres)
                spheres.Add(new RoundNode(part, [MapMeshes.Transform(local, desc.Shape.Center)], scale * desc.Shape.Radius,
                    desc.CollisionAttributeIndex, desc.SurfacePropertyIndex));
            foreach (var desc in phys.Parts[part].Shape.Capsules)
                capsules.Add(new RoundNode(part, [MapMeshes.Transform(local, desc.Shape.Center[0]), MapMeshes.Transform(local, desc.Shape.Center[1])],
                    scale * desc.Shape.Radius, desc.CollisionAttributeIndex, desc.SurfacePropertyIndex));
        }
        return (spheres, capsules);
    }

    /// <summary>
    /// resourcecompiler's sphere or capsule for a node (180c25810, 180c25230):
    /// the centres moved by the node's transform (the world model's identity,
    /// which turns a -0 into +0), the radius times its scale. Null when the
    /// radius is not above 0, which drops the shape.
    /// </summary>
    public static (Vector3[] Centers, float Radius) Round(RoundNode node)
    {
        var transform = CTransform.Compose(new CTransform(Vector3.Zero, 1f, CTransform.AngleQuaternion(Vector3.Zero)),
                                           new CTransform(Vector3.Zero, 1f, Quaternion.Identity).Inverse());
        var radius = transform.Scale * node.Radius;
        return ([.. node.Centers.Select(transform.TransformPoint)], radius);
    }

    /// <summary>
    /// One mesh node: its points (moved) and triangle indices, the body, and
    /// the surface: the mesh's own (<c>Surface</c> -1) or, for a mesh split
    /// by per-triangle material, that surface index.
    /// </summary>
    public sealed record MeshNode(int Part, Vector3[] Points, int[] Indices, ValveResourceFormat.ResourceTypes.RubikonPhysics.MeshDescriptor Desc, int Surface);

    /// <summary>
    /// The mesh nodes physicsbuilder makes for a prop's meshes (180153390),
    /// body by body. A mesh with fewer than two per-triangle materials is one
    /// node: every vertex, then every triangle (1805f1c50). Otherwise each
    /// surface index with triangles is a node (1805f1740): every vertex
    /// first, then, for its triangles in order, each vertex they use appended
    /// again on first use, the indices pointing at the appended copies. Every
    /// point is moved by the prop matrix times the bind pose (1800b9810),
    /// scaled or not; the lattice branch is not ported.
    /// </summary>
    public static List<MeshNode> Meshes(Prop prop, PhysAggregateData phys)
    {
        var nodes = new List<MeshNode>();
        var m = PropMatrix(prop);
        var surfaces = phys.SurfacePropertyHashes.Length;
        for (var part = 0; part < phys.Parts.Length; part++)
        {
            var local = MapMeshes.Concat(m, BindPose(phys, part));
            foreach (var desc in phys.Parts[part].Shape.Meshes)
            {
                var mesh = desc.Shape;
                var vertices = mesh.GetVertices().ToArray();
                var triangles = mesh.GetTriangles().ToArray();
                var materials = mesh.Materials ?? [];
                Vector3 Move(Vector3 v) => MapMeshes.Transform(local, v);
                if (materials.Length < 2)
                {
                    var indices = new int[triangles.Length * 3];
                    for (var t = 0; t < triangles.Length; t++)
                        (indices[t * 3], indices[(t * 3) + 1], indices[(t * 3) + 2]) = (triangles[t].X, triangles[t].Y, triangles[t].Z);
                    nodes.Add(Deformed(new MeshNode(part, [.. vertices.Select(Move)], indices, desc, -1), prop.Deformer));
                    continue;
                }
                for (var surface = 0; surface < surfaces; surface++)
                {
                    if (!materials.Any(x => x == surface))
                        continue;
                    var points = new List<Vector3>(vertices.Select(Move));
                    var remap = new int[vertices.Length];
                    Array.Fill(remap, -1);
                    var indices = new List<int>();
                    for (var t = 0; t < triangles.Length && t < materials.Length; t++)
                    {
                        if (materials[t] != surface)
                            continue;
                        foreach (var v in new[] { triangles[t].X, triangles[t].Y, triangles[t].Z })
                        {
                            if (remap[v] < 0)
                            {
                                remap[v] = points.Count;
                                points.Add(Move(vertices[v]));
                            }
                            indices.Add(remap[v]);
                        }
                    }
                    nodes.Add(Deformed(new MeshNode(part, [.. points], [.. indices], desc, surface), prop.Deformer));
                }
            }
        }
        return nodes;
    }

    /// <summary>A deformed prop's sphere or capsule as the triangle mesh node it becomes, with the shape's collision attribute and surface.</summary>
    public sealed record DeformedRound(int Part, Vector3[] Points, int[] Indices, int Attribute, int Surface);

    /// <summary>
    /// A deformed prop's spheres (1814c8600) and capsules (1814c70f0), body by
    /// body, each tessellated (<see cref="SphereMesh"/>, <see cref="CapsuleMesh"/>),
    /// its points moved by the prop matrix times the bind pose, deformed and
    /// made a triangle mesh node (1814c8a60).
    /// </summary>
    public static (List<DeformedRound> Spheres, List<DeformedRound> Capsules) DeformedRounds(Prop prop, PhysAggregateData phys)
    {
        var spheres = new List<DeformedRound>();
        var capsules = new List<DeformedRound>();
        if (prop.Deformer == null)
            return (spheres, capsules);
        var m = PropMatrix(prop);
        for (var part = 0; part < phys.Parts.Length; part++)
        {
            var local = MapMeshes.Concat(m, BindPose(phys, part));
            var body = part;
            DeformedRound Node((Vector3[] Points, int[] Indices) mesh, int attribute, int surface)
            {
                var node = Deformed(new MeshNode(body, [.. mesh.Points.Select(p => MapMeshes.Transform(local, p))], mesh.Indices, null!, -1), prop.Deformer);
                return new DeformedRound(body, node.Points, node.Indices, attribute, surface);
            }
            foreach (var desc in phys.Parts[part].Shape.Spheres)
                spheres.Add(Node(SphereMesh(desc.Shape.Center, desc.Shape.Radius), desc.CollisionAttributeIndex, desc.SurfacePropertyIndex));
            foreach (var desc in phys.Parts[part].Shape.Capsules)
                capsules.Add(Node(CapsuleMesh(desc.Shape.Center[0], desc.Shape.Center[1], desc.Shape.Radius), desc.CollisionAttributeIndex, desc.SurfacePropertyIndex));
        }
        return (spheres, capsules);
    }

    /// <summary>
    /// A sphere's triangle mesh (1819d5810 with 5 and 12, through 1819d51e0):
    /// five meridians at -2 pi i / 5, each twelve points at latitudes
    /// j pi / 13 - pi / 2 (x the sine, the meridian's cosine and sine in y and
    /// z), then the two poles on x (-r, then r); quads between neighbouring
    /// meridians and a cap of two triangles per meridian, every point then
    /// offset by the centre.
    /// </summary>
    public static (Vector3[] Points, int[] Indices) SphereMesh(Vector3 center, float radius)
    {
        const int Rings = 5, Segments = 12;
        const float Pi = 3.1415927f, HalfPi = 1.5707964f;
        var points = new Vector3[(Rings * Segments) + 2];
        points[Rings * Segments] = new Vector3(-radius, 0f, 0f);
        points[(Rings * Segments) + 1] = new Vector3(radius, 0f, 0f);
        var indices = new List<int>(Rings * Segments * 6);
        var first = (Pi / (Segments + 1)) - HalfPi;
        var start = 0;
        for (var i = 0; i < Rings; i++)
        {
            var a = ((float)i * -6.2831855f) / Rings;
            float sa = MathF.Sin(a), ca = MathF.Cos(a);
            var next = Segments * ((i + 1) % Rings);
            Vector3 At(float b)
            {
                float sb = MathF.Sin(b), cb = MathF.Cos(b);
                return new Vector3(radius * sb, (cb * ca) * radius, (cb * sa) * radius);
            }
            points[start] = At(first);
            for (var j = 2; j <= Segments; j++)
            {
                var k = j - 2;
                indices.AddRange([next + k, start + k, next + k + 1, next + k + 1, start + k, start + k + 1]);
                points[start + j - 1] = At((((float)j * Pi) / (Segments + 1)) - HalfPi);
            }
            indices.AddRange([Rings * Segments, start, next, (Rings * Segments) + 1, next + Segments - 1, start + Segments - 1]);
            start += Segments;
        }
        for (var v = 0; v < points.Length; v++)
            points[v] = new Vector3(center.X + points[v].X, center.Y + points[v].Y, center.Z + points[v].Z);
        return (points, [.. indices]);
    }

    /// <summary>
    /// A capsule's triangle mesh (1819d61d0, through 1819d5950 with 12 and 2),
    /// built along x from 0 to its length: twelve meridians at -2 pi i / 12,
    /// each with a point pair per latitude j pi / 6, j from 0 to 2 (-r sin + 0
    /// on the first end, r sin + length on the second), then the two poles;
    /// two quads per latitude step and a cap per meridian. The points are then
    /// moved by the frame of its axis (the axis normalised when longer than
    /// 1e-5, else x; a perpendicular from 18125d0d0; their cross product) at
    /// the first centre.
    /// </summary>
    public static (Vector3[] Points, int[] Indices) CapsuleMesh(Vector3 c0, Vector3 c1, float radius)
    {
        const int Rings = 12, Segments = 2, Per = (Segments * 2) + 2;
        const float Pi = 3.1415927f;
        var diff = new Vector3(c1.X - c0.X, c1.Y - c0.Y, c1.Z - c0.Z);
        var length = MathF.Sqrt(((diff.Z * diff.Z) + (diff.Y * diff.Y)) + (diff.X * diff.X));
        var points = new Vector3[(Rings * Per) + 2];
        var zero = radius * 0f;
        points[Rings * Per] = new Vector3(0f - radius, zero, zero);
        points[(Rings * Per) + 1] = new Vector3(radius + length, zero, zero);
        var indices = new List<int>((Segments + 1) * Rings * 12);
        var b0 = 0f / (Segments + 1);
        var start = 0;
        for (var i = 0; i < Rings; i++)
        {
            var a = ((float)i * -6.2831855f) / Rings;
            float sa = MathF.Sin(a), ca = MathF.Cos(a);
            var next = ((i + 1) % Rings) * Per;
            void Pair(int at, float b)
            {
                float sb = MathF.Sin(b), cb = MathF.Cos(b);
                float y = radius * (cb * ca), z = radius * (cb * sa);
                points[at] = new Vector3((radius * -sb) + 0f, y, z);
                points[at + 1] = new Vector3((radius * sb) + length, y, z);
            }
            indices.AddRange([next, start, next + 1, next + 1, start, start + 1]);
            Pair(start, b0);
            for (var j = 1; j <= Segments; j++)
            {
                var k = 2 * (j - 1);
                indices.AddRange([next + k + 1, start + k + 1, next + k + 3, next + k + 3, start + k + 1, start + k + 3,
                                  next + k, next + k + 2, start + k, next + k + 2, start + k + 2, start + k]);
                Pair(start + (2 * j), (((float)j * Pi) * 0.5f) / (Segments + 1));
            }
            indices.AddRange([Rings * Per, start + (2 * Segments), next + (2 * Segments), (Rings * Per) + 1, next + (2 * Segments) + 1, start + (2 * Segments) + 1]);
            start += Per;
        }
        Vector3 d;
        if (length > 1e-5f)
        {
            var inverse = 1f / length;
            d = new Vector3(diff.X * inverse, diff.Y * inverse, diff.Z * inverse);
        }
        else
            d = new Vector3(1f, 0f, 0f);
        // 18125d0d0: ((1 - z) (y y - 0) + z, 0, -x) normalised, d's part taken off, normalised again.
        var r = new Vector3(((1f - d.Z) * ((d.Y * d.Y) - 0f)) + d.Z, 0f, -d.X);
        LightMath.Normalize(ref r);
        var dot = ((r.Z * d.Z) + (d.Y * r.Y)) + (r.X * d.X);
        r = new Vector3(r.X - (dot * d.X), r.Y - (d.Y * dot), r.Z - (d.Z * dot));
        LightMath.Normalize(ref r);
        float[] frame = [d.X, r.X, (r.Z * d.Y) - (d.Z * r.Y), c0.X,
                         d.Y, r.Y, (d.Z * r.X) - (r.Z * d.X), c0.Y,
                         d.Z, r.Z, (r.Y * d.X) - (d.Y * r.X), c0.Z];
        for (var v = 0; v < points.Length; v++)
            points[v] = MapMeshes.Transform(frame, points[v]);
        return (points, [.. indices]);
    }

    /// <summary>A deformed prop's hull as the triangle mesh node it becomes, with the hull's description.</summary>
    public sealed record DeformedHull(int Part, Vector3[] Points, int[] Indices, ValveResourceFormat.ResourceTypes.RubikonPhysics.HullDescriptor Desc);

    /// <summary>
    /// A deformed prop's hulls (1814c7e90 with a deformer), body by body and
    /// hull by hull: the hull triangulated (1819589f0: its vertices times 1,
    /// then each face fanned from its first half-edge's origin), the points
    /// moved by the prop matrix times the bind pose, deformed, and made a
    /// triangle mesh node (1814c8a60). No quickhull check here.
    /// </summary>
    public static List<DeformedHull> DeformedHulls(Prop prop, PhysAggregateData phys)
    {
        var nodes = new List<DeformedHull>();
        if (prop.Deformer == null)
            return nodes;
        var m = PropMatrix(prop);
        for (var part = 0; part < phys.Parts.Length; part++)
        {
            var local = MapMeshes.Concat(m, BindPose(phys, part));
            foreach (var desc in phys.Parts[part].Shape.Hulls)
            {
                var hull = desc.Shape;
                var edges = hull.GetEdges().ToArray();
                var points = hull.GetVertexPositions().ToArray().Select(v => new Vector3(v.X * 1f, v.Y * 1f, v.Z * 1f)).ToArray();
                var indices = new List<int>();
                foreach (var face in hull.GetFaces().ToArray())
                {
                    var first = edges[face.Edge];
                    var e1 = edges[first.Next];
                    var e2 = edges[e1.Next];
                    while (true)
                    {
                        indices.Add(first.Origin);
                        indices.Add(e1.Origin);
                        indices.Add(e2.Origin);
                        var next = e2.Next;
                        if (next == face.Edge)
                            break;
                        (e1, e2) = (e2, edges[next]);
                    }
                }
                var node = Deformed(new MeshNode(part, [.. points.Select(p => MapMeshes.Transform(local, p))], [.. indices], null!, -1), prop.Deformer);
                nodes.Add(new DeformedHull(part, node.Points, node.Indices, desc));
            }
        }
        return nodes;
    }

    // 1814c8a60 past the move: the points deformed in the world (identity
    // matrix, PropDeformer_ApplyArrays), and with the mirror flag the whole
    // index list reversed. Without a deformer the node is as it was.
    private static MeshNode Deformed(MeshNode node, Maps.LatticeDeformer? deformer)
    {
        if (deformer == null)
            return node;
        var evaluator = deformer.For(Identity);
        var points = node.Points.Select(evaluator.Deform).ToArray();
        var indices = node.Indices;
        if (deformer.Mirror)
            indices = [.. indices.Reverse()];
        return node with { Points = points, Indices = indices };
    }

    private static readonly float[] Identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];

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
    internal static Vector3 MatrixAngles(float[] m)
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
