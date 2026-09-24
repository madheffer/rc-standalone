using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The polygon meshes of a <c>.vmap</c> (<c>CMapMesh</c>, each holding a
/// <c>CDmePolygonMesh</c>), read into world space face by face.
///
/// <para>The mesh is half-edge: <c>faceEdgeIndices</c> gives each face one of
/// its half-edges, <c>edgeNextIndices</c> walks the loop and
/// <c>edgeVertexIndices</c> names the vertex a half-edge points to, which is
/// the face's corner order. A face's material is
/// <c>materials[materialindex]</c>.</para>
///
/// <para>A group that a <c>CMapInstance</c> targets is not compiled where it
/// stands; each instance compiles the group's contents again. A node's world
/// matrix (<c>FUN_180ffdf20</c>) is the instance path's matrix times the
/// node's own <c>AngleMatrix(angles, origin)</c> (its vtable slot 0xa0), and
/// each instance on the path adds the instance's matrix times the inverse of
/// its target's (<c>FUN_180ffddd0</c>), outermost first.</para>
/// </summary>
public static class MapMeshes
{
    /// <summary>One face: its corners in world space, in loop order, and its material.</summary>
    public sealed record Face(Vector3[] Corners, string Material);

    /// <summary>One mesh and where it hangs: under the world, or under an entity (with its classname).</summary>
    public sealed record Mesh(int NodeId, string ParentType, string? ParentClass, Vector3 Origin, Vector3 Angles, Vector3 Scales, Face[] Faces)
    {
        /// <summary>The <c>CMapMesh</c> element, for its other settings.</summary>
        public DmxBinary.Element? Element { get; init; }

        /// <summary>The world matrix the faces were placed with (matrix3x4, row major).</summary>
        internal float[] World { get; init; } = [];

        /// <summary>The instance path's matrix alone, without the node's own transform.</summary>
        internal float[] Path { get; init; } = [];

        /// <summary>The <c>CMapInstance</c> node ids this copy was reached through, outermost first.</summary>
        public int[] Instances { get; init; } = [];

        /// <summary>Whether the mesh is rotated or scaled, which the plain local + origin path does not cover.</summary>
        public bool Transformed => Angles != Vector3.Zero || Scales != Vector3.One;
    }

    public static List<Mesh> Read(DmxBinary.Document doc)
    {
        var targets = new HashSet<DmxBinary.Element>(ReferenceEqualityComparer.Instance);
        foreach (var instance in doc.OfType("CMapInstance"))
        {
            if (instance.Get<DmxBinary.Element>("target") is { } target)
                targets.Add(target);
        }
        var meshes = new List<Mesh>();
        foreach (var world in doc.OfType("CMapWorld"))
            Walk(world, world, Identity, [], targets, meshes);
        return meshes;
    }

    private static void Walk(DmxBinary.Element node, DmxBinary.Element parent, float[] path, int[] instances,
                             HashSet<DmxBinary.Element> targets, List<Mesh> meshes)
    {
        foreach (var child in node.GetElements("children"))
        {
            switch (child.Type)
            {
                case "CMapMesh":
                    var className = parent.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname");
                    meshes.Add(new Mesh(child.GetValue<int>("nodeID") ?? -1, parent.Type, className,
                                        child.GetValue<Vector3>("origin") ?? Vector3.Zero, child.GetValue<Vector3>("angles") ?? Vector3.Zero,
                                        child.GetValue<Vector3>("scales") ?? Vector3.One, Faces(child, path))
                               { Element = child, Instances = instances, World = Concat(path, Local(child)), Path = path });
                    break;
                case "CMapInstance":
                    if (child.Get<DmxBinary.Element>("target") is not { } target)
                        break;
                    var step = Concat(Local(child), Invert(Local(target)));
                    Walk(target, target, Concat(path, step), [.. instances, child.GetValue<int>("nodeID") ?? -1], targets, meshes);
                    break;
                default:
                    if (!targets.Contains(child))
                        Walk(child, child, path, instances, targets, meshes);
                    break;
            }
        }
    }

    private static Face[] Faces(DmxBinary.Element mesh, float[] path)
    {
        var data = mesh.Get<DmxBinary.Element>("meshData") ?? throw new InvalidDataException("CMapMesh without meshData.");
        var positions = Stream(data, "vertexData", "position");
        var materials = Ints(data, "faceData", "materialindex");
        var names = (data.Get<object?[]>("materials") ?? []).Select(x => x as string ?? "").ToArray();
        var next = IntArray(data, "edgeNextIndices");
        var to = IntArray(data, "edgeVertexIndices");
        var first = IntArray(data, "faceEdgeIndices");

        var scales = mesh.GetValue<Vector3>("scales") ?? Vector3.One;
        var world = Concat(path, Local(mesh));
        var faces = new Face[first.Length];
        for (var f = 0; f < first.Length; f++)
        {
            var corners = new List<Vector3>();
            var e = first[f];
            do
            {
                corners.Add(Transform(world, (Vector3)positions[to[e]]! * scales));
                e = next[e];
            }
            while (e != first[f] && corners.Count <= next.Length);
            var material = f < materials.Length && materials[f] >= 0 && materials[f] < names.Length ? names[materials[f]] : "";
            faces[f] = new Face([.. corners], material);
        }
        return faces;
    }

    // Matrices are Valve's matrix3x4_t: three rows of (axis x, axis y, axis z,
    // translation), row major.
    private static readonly float[] Identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];

    // A point through a matrix: each row dotted with (x, y, z, 1) as a SIMD
    // horizontal add pairs it, (x + z) + (y + t). Measured on atixref: a roll
    // of -89.999985 degrees and instanced copies of a mesh at a yaw of 180.5
    // tell the orders apart, and only this one rebuilds all of both.
    internal static Vector3 Transform(float[] m, Vector3 v)
        => new(((m[0] * v.X) + (m[2] * v.Z)) + ((m[1] * v.Y) + m[3]),
               ((m[4] * v.X) + (m[6] * v.Z)) + ((m[5] * v.Y) + m[7]),
               ((m[8] * v.X) + (m[10] * v.Z)) + ((m[9] * v.Y) + m[11]));

    // A node's own matrix (vtable slot 0xa0, FUN_181255fb0): AngleMatrix of its
    // angles with its origin as the translation.
    internal static float[] Local(DmxBinary.Element node)
    {
        var m = AngleMatrix(node.GetValue<Vector3>("angles") ?? Vector3.Zero);
        var origin = node.GetValue<Vector3>("origin") ?? Vector3.Zero;
        m[3] = origin.X;
        m[7] = origin.Y;
        m[11] = origin.Z;
        return m;
    }

    // FUN_181255d60: pitch, yaw, roll in degrees, each times 0.017453292f
    // through tier0's V_sincosf (the CRT sinf and cosf), grouped as the
    // binary groups them. The columns are the forward, left and up axes.
    internal static float[] AngleMatrix(Vector3 angles)
    {
        const float Radians = 0.017453292f;
        float sp = MathF.Sin(angles.X * Radians), cp = MathF.Cos(angles.X * Radians);
        float sy = MathF.Sin(angles.Y * Radians), cy = MathF.Cos(angles.Y * Radians);
        float sr = MathF.Sin(angles.Z * Radians), cr = MathF.Cos(angles.Z * Radians);
        return [cy * cp, (sr * sp * cy) - (cr * sy), (cr * sp * cy) - (-sy * sr), 0,
                sy * cp, (sr * sp * sy) + (cr * cy), (cr * sp * sy) - (sr * cy), 0,
                -sp, sr * cp, cr * cp, 0];
    }

    // FUN_181258890 (ConcatTransforms, SIMD): row i of a times b, the terms
    // summed z, y, x and then a's translation (or +0).
    internal static float[] Concat(float[] a, float[] b)
    {
        var o = new float[12];
        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 4; j++)
                o[(i * 4) + j] = (((a[(i * 4) + 2] * b[8 + j]) + (a[(i * 4) + 1] * b[4 + j])) + (a[i * 4] * b[j])) + (j == 3 ? a[(i * 4) + 3] : 0f);
        }
        return o;
    }

    // FUN_18125aba0 (MatrixInvert): the transpose, and the translation as
    // -(t.z * r2 + t.y * r1 + t.x * r0) against the transposed rows. The
    // rescale it does when the first column is not unit length never applies
    // to a rotation.
    internal static float[] Invert(float[] m)
    {
        float[] o = [m[0], m[4], m[8], 0, m[1], m[5], m[9], 0, m[2], m[6], m[10], 0];
        float tx = m[3], ty = m[7], tz = m[11];
        for (var i = 0; i < 3; i++)
            o[(i * 4) + 3] = -(((tz * o[(i * 4) + 2]) + (ty * o[(i * 4) + 1])) + (tx * o[i * 4]));
        return o;
    }

    private static object?[] Stream(DmxBinary.Element data, string array, string name)
    {
        var holder = data.Get<DmxBinary.Element>(array) ?? throw new InvalidDataException($"meshData without {array}.");
        var stream = holder.GetElements("streams").FirstOrDefault(s => s.Name.StartsWith(name + ":", StringComparison.Ordinal))
                     ?? throw new InvalidDataException($"{array} without a {name} stream.");
        return stream.Get<object?[]>("data") ?? [];
    }

    private static int[] Ints(DmxBinary.Element data, string array, string name)
    {
        var holder = data.Get<DmxBinary.Element>(array);
        var stream = holder?.GetElements("streams").FirstOrDefault(s => s.Name.StartsWith(name + ":", StringComparison.Ordinal));
        return (stream?.Get<object?[]>("data") ?? []).Select(x => x is int i ? i : -1).ToArray();
    }

    private static int[] IntArray(DmxBinary.Element e, string name)
        => (e.Get<object?[]>(name) ?? []).Select(x => x is int i ? i : -1).ToArray();
}
