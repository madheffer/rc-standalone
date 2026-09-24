using System.Numerics;

namespace Source2.Compiler.Physics;

/// <summary>
/// The convex hulls the map builder makes for a brush entity's mesh
/// (<c>FUN_1801ff720</c>), from the mesh's faces to shipped <see cref="RnHull"/>s.
///
/// <para>The faces are triangulated into a fresh triangle mesh whose vertices
/// are numbered in first-appearance order over the faces' corners
/// (<c>FUN_181310a90</c>, <c>FUN_181308060</c>). convex_single hulls every
/// vertex in that order. convex_multi splits the triangles into groups that
/// touch at a vertex (<c>FUN_18130c0d0</c>) and gathers each group's vertices
/// through a hash set read back in slot order (<c>FUN_18130f460</c>). Each point
/// list is hulled with the map builder's options, and that hull's vertex list
/// is hulled again by <c>RnHullCreate</c> when the model compiles.</para>
///
/// <para>Not ported: the 1/32 weld on the polygon mesh, the simplifier a
/// positive simplification error asks for, and the ear clipper for faces of
/// four or more corners (a fan stands in; it only decides probe order on a
/// hash collision).</para>
/// </summary>
public static class BrushHulls
{
    /// <summary><c>HammerMeshPhysicsType_t</c>.</summary>
    public enum PhysicsType
    {
        None = 0,
        Default = 1,
        ConvexSingle = 2,
        ConvexMulti = 3,
        Mesh = 4,
    }

    /// <summary>
    /// FUN_181083540: "default" inside an entity is convex_multi unless the
    /// class inherits one of the PhysicsTypeOverride base classes; outside one
    /// it is a mesh.
    /// </summary>
    public static PhysicsType Resolve(PhysicsType stored, bool inEntity, bool meshOverride, bool multiOverride, bool singleOverride)
    {
        if (stored != PhysicsType.Default)
            return stored;
        if (!inEntity || meshOverride)
            return PhysicsType.Mesh;
        if (multiOverride)
            return PhysicsType.ConvexMulti;
        return singleOverride ? PhysicsType.ConvexSingle : PhysicsType.ConvexMulti;
    }

    /// <summary>The point lists the map builder hulls, in its order.</summary>
    public static List<Vector3[]> Inputs(Vector3[] positions, int[][] faces, PhysicsType type)
    {
        var index = new Dictionary<int, int>();
        var points = new List<Vector3>();
        var triangles = new List<(int A, int B, int C)>();
        foreach (var face in faces)
        {
            if (face.Length < 3)
                continue;
            var local = new int[face.Length];
            for (var j = 0; j < face.Length; j++)
            {
                if (!index.TryGetValue(face[j], out var i))
                {
                    i = points.Count;
                    index[face[j]] = i;
                    points.Add(positions[face[j]]);
                }
                local[j] = i;
            }
            for (var j = 1; j + 1 < face.Length; j++)
                triangles.Add((local[0], local[j], local[j + 1]));
        }
        if (type == PhysicsType.ConvexSingle)
            return [[.. points]];
        if (type != PhysicsType.ConvexMulti)
            return [];
        var result = new List<Vector3[]>();
        foreach (var group in Groups(points.Count, triangles))
            result.Add([.. GroupVertices(group, triangles).Select(v => points[(int)v])]);
        return result;
    }

    private static int[] Corners((int A, int B, int C) t) => [t.A, t.B, t.C];

    // FUN_18130c0d0: faces in a set, each group grown breadth first from the
    // first occupied slot through faces sharing a vertex.
    private static List<List<uint>> Groups(int vertexCount, List<(int A, int B, int C)> triangles)
    {
        var around = new List<uint>[vertexCount];
        for (var i = 0; i < vertexCount; i++)
            around[i] = [];
        for (var t = 0; t < triangles.Count; t++)
        {
            foreach (var v in Corners(triangles[t]))
                around[v].Add((uint)t);
        }
        var remaining = new ValveHashSet();
        if (triangles.Count > 0)
            remaining.Reserve(triangles.Count * 4 / 3);
        for (var t = 0; t < triangles.Count; t++)
            remaining.Add((uint)t, 0);
        var groups = new List<List<uint>>();
        while (remaining.First() is { } seed)
        {
            remaining.Remove(seed);
            var group = new List<uint> { seed };
            for (var i = 0; i < group.Count; i++)
            {
                var face = group[i];
                foreach (var n in Neighbours(Corners(triangles[(int)face]), around))
                {
                    if (n == face || remaining.Find(n) < 0)
                        continue;
                    group.Add(n);
                    remaining.Remove(n);
                }
            }
            groups.Add(group);
        }
        return groups;
    }

    // FUN_18130d0b0: every face around the corners, through a set.
    private static IEnumerable<uint> Neighbours(int[] corners, List<uint>[] around)
    {
        var set = new ValveHashSet();
        set.Reserve(corners.Length * 32 / 3);
        foreach (var v in corners)
        {
            foreach (var f in around[v])
                set.Add(f, 0);
        }
        return set.InSlotOrder().Select(e => e.Key).ToList();
    }

    // FUN_18130f460: the group's vertices through a set, in slot order.
    private static IEnumerable<uint> GroupVertices(List<uint> group, List<(int A, int B, int C)> triangles)
    {
        var set = new ValveHashSet();
        if (group.Count > 0)
            set.Reserve(group.Count * 32 / 3);
        foreach (var face in group)
        {
            foreach (var v in Corners(triangles[(int)face]))
            {
                var slot = set.Find((uint)v);
                if (slot >= 0)
                    set.Increment(slot);
                else
                    set.Add((uint)v, 1);
            }
        }
        return set.InSlotOrder().Select(e => e.Key).ToList();
    }

    /// <summary>
    /// The model compile's check of a hull node (FUN_1802c0e70): a raw
    /// quickhull of the node's points at tolerance 0, whose vertex list, in
    /// list order, is what the shape keeps (FUN_180c261c0). Null when that
    /// hull is invalid ("Inconsistent hull geometry").
    /// </summary>
    public static Vector3[]? ShapePoints(Vector3[] nodePoints)
    {
        var flat = new float[nodePoints.Length * 3];
        for (var i = 0; i < nodePoints.Length; i++)
        {
            flat[i * 3] = nodePoints[i].X;
            flat[i * 3 + 1] = nodePoints[i].Y;
            flat[i * 3 + 2] = nodePoints[i].Z;
        }
        var qh = new QuickHull();
        qh.Build(nodePoints.Length, flat, 0f, true);
        if (!qh.IsValid())
            return null;
        return [.. qh.HullVertices.Select(v => new Vector3(v.X, v.Y, v.Z))];
    }

    /// <summary>Each point list hulled by the map builder, then by <c>RnHullCreate</c>.</summary>
    public static List<RnHull?> Build(Vector3[] positions, int[][] faces, PhysicsType type)
    {
        var hulls = new List<RnHull?>();
        foreach (var input in Inputs(positions, faces, type))
        {
            var qh = RnHullBuilder.BuildHull(input, RnHullBuilder.Options.MapBuilder, out _);
            if (qh == null)
            {
                hulls.Add(null);
                continue;
            }
            var vertices = ShapePoints(qh.HullVertices.Select(v => new Vector3(v.X, v.Y, v.Z)).ToArray());
            if (vertices == null)
            {
                hulls.Add(null);
                continue;
            }
            var hull = RnHullBuilder.Create(vertices, RnHullBuilder.Options.Compile, out _);
            if (hull != null)
                RnHullBuilder.Transform(hull, RnHullBuilder.Identity);
            hulls.Add(hull);
        }
        return hulls;
    }
}
