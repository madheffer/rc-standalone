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
/// <para>Each material piece is welded at 1/32 first, in the mesh node's
/// space (<see cref="Pieces"/>).</para>
///
/// <para>Not ported: the order faces come round a vertex (triangle order
/// stands in; it only decides probe order on a hash collision).</para>
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

    /// <summary>
    /// FUN_18020b230: a brush entity mesh's material pieces as the builder
    /// hulls them. Each piece's per-corner mesh (<see cref="Maps.MapMeshCorners"/>)
    /// is welded at 1/32 (<see cref="MeshWeld"/>), then moved by the mesh
    /// node's CTransform and the entity's inverse. The faces are the welded
    /// triangles; <c>Local</c> holds the welded points before the move.
    /// <paramref name="path"/> is the instance path's matrix for a world mesh
    /// inside an instance.
    /// </summary>
    /// <param name="transformOf">A brush entity node's CTransform, its own by default; an
    /// instance copy's is its baked placement (<see cref="Maps.SettleWorld.BakedPlacement"/>).</param>
    public static List<(int Material, Vector3[] Positions, int[][] Faces, Vector3[] Local)> Pieces(DmxBinary.Element mesh, DmxBinary.Element entity, float[]? path = null,
        Func<DmxBinary.Element, Maps.CTransform>? transformOf = null, int? wholeMeshMaterial = null)
        => [.. PiecesWithCorners(mesh, entity, path, transformOf: transformOf, wholeMeshMaterial: wholeMeshMaterial).Select(p => (p.Material, p.Positions, p.Faces, p.Local))];

    /// <summary>
    /// <see cref="Pieces"/>, with the .vmap vertex behind each face corner
    /// (<c>CornerIds</c>, face for face) and its faceVertexData index
    /// (<c>CornerData</c>); <c>Bias</c> is the face set's lightmap scale bias.
    /// </summary>
    /// <param name="placed">A world mesh's matrix as the instance collapse left it (its node
    /// moved through the instances, then its own AngleMatrix), used instead of the node's
    /// matrix and <paramref name="path"/>.</param>
    /// <param name="builderMeshes">When given, receives each piece's CMesh as physicsbuilder gets it from a painted mesh
    /// (<see cref="BuilderMesh"/>), piece for piece.</param>
    internal static List<(int Material, Vector3[] Positions, int[][] Faces, Vector3[] Local, int[][] CornerIds, int[][] CornerData, int Bias)> PiecesWithCorners(DmxBinary.Element mesh, DmxBinary.Element entity, float[]? path = null, bool shiftTexcoords = true,
        Func<DmxBinary.Element, Maps.CTransform>? transformOf = null, float[]? placed = null, int? wholeMeshMaterial = null,
        List<BuilderMesh>? builderMeshes = null)
    {
        transformOf ??= Maps.CTransform.FromNode;
        if (placed != null)
            path = null;
        // A world mesh moves by its node's own matrix (AngleMatrix with the
        // origin, vtable slot 0xa0): atixref's rotated tool meshes at a pitch of
        // 89.99999 or a yaw of 179.99997 match only that way (506 pieces placed,
        // 500 bit for bit, against 501 and 492). A brush entity's mesh moves by
        // the CTransforms (Mako's 1266 hulls).
        var toWorld = placed ?? (entity.Type == "CMapWorld" ? Maps.MapMeshes.Local(mesh) : transformOf(mesh).Matrix());
        var toEntity = transformOf(entity).Inverse().Matrix();
        Vector3 Place(Vector3 p)
        {
            var moved = Maps.MapMeshes.Transform(toEntity, Maps.MapMeshes.Transform(toWorld, p));
            return path == null ? moved : Maps.MapMeshes.Transform(path, moved);
        }
        // A world mesh's faces are cut on their world positions: in the mesh's
        // own space eleven atixref faces (ceilings and floors of 17 to 55
        // corners, a rotated cylinder's caps) break near ties the other way.
        // A brush entity's stay in its own space.
        var result = new List<(int, Vector3[], int[][], Vector3[], int[][], int[][], int)>();
        foreach (var piece in Maps.MapMeshCorners.Build(mesh, shiftTexcoords, entity.Type == "CMapWorld" ? Place : null, withTangent: false, allStreams: false, wholeMeshMaterial))
        {
            var kept = new List<int>();
            var (vertices, stride, streams) = (piece.Vertices, piece.Stride, piece.Streams);
            if (entity.Type == "CMapWorld" && piece.VertexIds.Length > 0)
            {
                // A world mesh's weld never joins two .vmap vertices: each corner
                // also carries its vertex, matched exactly. atixref's ceiling
                // (node 6490) has a sliver quad whose corners 0.004 apart stay
                // two, and the triangle between them stays.
                stride = piece.Stride + 1;
                vertices = new float[piece.VertexIds.Length * stride];
                for (var c = 0; c < piece.VertexIds.Length; c++)
                {
                    Array.Copy(piece.Vertices, c * piece.Stride, vertices, c * stride, piece.Stride);
                    vertices[(c * stride) + piece.Stride] = piece.VertexIds[c];
                }
                streams = [.. piece.Streams, new MeshWeld.Stream("vertex", piece.Stride, 1, false, 0x22)];
            }
            var (v, indices) = MeshWeld.Weld(vertices, stride, piece.Indices, streams, 1f / 32f, true, kept);
            var local = new Vector3[v.Length / stride];
            for (var i = 0; i < local.Length; i++)
                local[i] = new Vector3(v[i * stride], v[(i * stride) + 1], v[(i * stride) + 2]);
            // A mesh inside an instance also moves by the instance path. Applying
            // it after the node's own move, or as one matrix, place the same
            // atixref pieces; which one the builder uses is not measured yet.
            var positions = local.Select(Place).ToArray();
            var faces = new int[indices.Length / 3][];
            var corners = new int[faces.Length][];
            var cornerData = new int[faces.Length][];
            for (var t = 0; t < faces.Length; t++)
            {
                faces[t] = [indices[t * 3], indices[(t * 3) + 1], indices[(t * 3) + 2]];
                var src = kept[t] * 3;
                corners[t] = piece.VertexIds.Length == 0 ? [] : [piece.VertexIds[src], piece.VertexIds[src + 1], piece.VertexIds[src + 2]];
                cornerData[t] = piece.CornerData.Length == 0 ? [] : [piece.CornerData[src], piece.CornerData[src + 1], piece.CornerData[src + 2]];
            }
            result.Add((piece.Material, positions, faces, local, corners, cornerData, piece.Bias));
            if (builderMeshes != null)
                builderMeshes.Add(Builder(mesh, piece, v, stride, positions, faces, local, corners, cornerData, entity.Type == "CMapWorld" ? toWorld : null));
        }
        return result;
    }

    /// <summary>Each triangle mesh <see cref="Simplified"/> hands the simplifier (tests).</summary>
    [ThreadStatic]
    internal static Action<List<Vector3>, List<(int A, int B, int C)>, float>? SimplifyTrace;

    /// <summary>
    /// MapBuilder_MeshFromMapMesh (rc 0923: the function at 181308a42): with a
    /// physicsSimplificationError above 0, a brush entity piece's triangle mesh
    /// (<see cref="TriangleMesh"/>'s, the gather 181310620 makes) goes through
    /// the edge collapse as it stands, no weld first (<see cref="MeshSimplifier"/>,
    /// whose copy in rc is 181bf9d70), and MapBuilder_TriangleMesh (181308060)
    /// gets the result. Fewer than 4 vertices or 2 triangles are left alone.
    /// Every call of a compile replays bit for bit (MapSimplifyReplay: 167 on
    /// s2c_simplify). What is left stands where <see cref="TriangleMesh"/>'s
    /// output would.
    /// </summary>
    internal static (List<Vector3> Points, List<(int A, int B, int C)> Triangles) Simplified((List<Vector3> Points, List<(int A, int B, int C)> Triangles) mesh, float simplify)
    {
        if (simplify <= 0f)
            return mesh;
        SimplifyTrace?.Invoke(mesh.Points, mesh.Triangles, simplify);
        if (mesh.Points.Count < 4 || mesh.Triangles.Count < 2)
            return mesh;
        var flat = new float[mesh.Points.Count * 3];
        for (var i = 0; i < mesh.Points.Count; i++)
            (flat[i * 3], flat[(i * 3) + 1], flat[(i * 3) + 2]) = (mesh.Points[i].X, mesh.Points[i].Y, mesh.Points[i].Z);
        var (positions, indices) = MeshSimplifier.Run(flat, [.. mesh.Triangles.SelectMany(t => new[] { t.A, t.B, t.C })], simplify, -1);
        var points = new List<Vector3>(positions.Length / 3);
        for (var i = 0; i < positions.Length; i += 3)
            points.Add(new Vector3((float)positions[i], (float)positions[i + 1], (float)positions[i + 2]));
        var triangles = new List<(int A, int B, int C)>(indices.Length / 3);
        for (var t = 0; t < indices.Length; t += 3)
            triangles.Add((indices[t], indices[t + 1], indices[t + 2]));
        return (points, triangles);
    }

    /// <summary>
    /// A painted mesh's piece as PhysicsBuilder_MeshPieces (180016230)
    /// receives it: one vertex per .vmap vertex in <see cref="TriangleMesh"/>'s
    /// order, with the triangles, each vertex carrying the streams of the
    /// corner that made it (position, two texcoords, normal, tangent,
    /// PerVertexLighting, VertexPaintTintColor, VertexPaintBlendParams; 26
    /// floats). Captured on s2c_simplify (capture_simplify.py): no two vertices
    /// share a position; a world mesh's positions in the world, the first
    /// texcoord shifted, the second at its default, the rest as the .vmap
    /// stores them. An unpainted mesh's piece comes as positions only.
    /// </summary>
    internal sealed record BuilderMesh(float[] Vertices, int Stride, int[] Indices, IReadOnlyList<MeshWeld.Stream> Streams)
    {
        /// <summary>The stream layout physicsbuilder's CMesh carries.</summary>
        public static readonly IReadOnlyList<MeshWeld.Stream> Layout =
        [
            new("position", 0, 3, false, 42), new("texcoord", 3, 2, false, 41), new("texcoord", 5, 2, false, 41), new("normal", 7, 3, false, 42),
            new("tangent", 10, 4, false, 43), new("PerVertexLighting", 14, 4, false, 43), new("VertexPaintTintColor", 18, 4, false, 43),
            new("VertexPaintBlendParams", 22, 4, false, 43),
        ];
    }

    private static BuilderMesh Builder(DmxBinary.Element mesh, Maps.MapMeshCorners.Piece piece, float[] welded, int stride, Vector3[] positions, int[][] faces,
                                       Vector3[] local, int[][] corners, int[][] cornerData, float[]? toWorld)
    {
        int Offset(string name) => piece.Streams.FirstOrDefault(x => x.Name == name) is { Count: > 0 } x ? x.First : -1;
        var (texcoord, normal, lighting) = (Offset("texcoord"), Offset("normal"), Offset("PerVertexLighting"));
        var streams = mesh.Get<DmxBinary.Element>("meshData")?.Get<DmxBinary.Element>("faceVertexData")?.GetElements("streams") ?? [];
        object?[]? Stream(string name) => streams.FirstOrDefault(x => x.Name.Split(':')[0] == name)?.Get<object?[]>("data");
        var (tangents, tints, paints) = (Stream("tangent"), Stream("VertexPaintTintColor"), Stream("VertexPaintBlendParams"));
        var made = new List<(int Face, int Corner)>();
        var (points, triangles) = TriangleMesh(positions, faces, local, corners, made);
        var result = new float[points.Count * 26];
        for (var i = 0; i < points.Count; i++)
        {
            var (f, j) = made[i];
            var at = faces[f][j] * stride;
            var data = cornerData[f].Length > j ? cornerData[f][j] : -1;
            var o = i * 26;
            (result[o], result[o + 1], result[o + 2]) = (points[i].X, points[i].Y, points[i].Z);
            if (texcoord >= 0)
                (result[o + 3], result[o + 4]) = (welded[at + texcoord], welded[at + texcoord + 1]);
            var n = normal >= 0 ? new Vector3(welded[at + normal], welded[at + normal + 1], welded[at + normal + 2]) : Vector3.Zero;
            if (toWorld != null)
                n = Maps.NodeMeshEntries.Rotate(toWorld, n);
            (result[o + 7], result[o + 8], result[o + 9]) = (n.X, n.Y, n.Z);
            if (tangents != null && data >= 0 && data < tangents.Length && tangents[data] is Vector4 t)
            {
                var d = new Vector3(t.X, t.Y, t.Z);
                if (toWorld != null)
                    d = Maps.NodeMeshEntries.Rotate(toWorld, d);
                (result[o + 10], result[o + 11], result[o + 12], result[o + 13]) = (d.X, d.Y, d.Z, t.W);
            }
            if (lighting >= 0)
                (result[o + 14], result[o + 15], result[o + 16], result[o + 17]) = (welded[at + lighting], welded[at + lighting + 1], welded[at + lighting + 2], welded[at + lighting + 3]);
            (result[o + 18], result[o + 19], result[o + 20], result[o + 21]) = tints != null && data >= 0 && data < tints.Length && tints[data] is Vector4 c ? (c.X, c.Y, c.Z, c.W) : (1f, 1f, 1f, 1f);
            if (paints != null && data >= 0 && data < paints.Length && paints[data] is Vector4 p)
                (result[o + 22], result[o + 23], result[o + 24], result[o + 25]) = (p.X, p.Y, p.Z, p.W);
        }
        return new BuilderMesh(result, 26, [.. triangles.SelectMany(t => new[] { t.A, t.B, t.C })], BuilderMesh.Layout);
    }

    /// <summary>The point lists the map builder hulls, in its order.</summary>
    /// <param name="simplify">The mesh's physicsSimplificationError as the builder gets it (<see cref="Simplified"/>).</param>
    public static List<Vector3[]> Inputs(Vector3[] positions, int[][] faces, PhysicsType type, Vector3[]? local = null, int[][]? cornerIds = null, float simplify = 0f)
    {
        var (points, triangles) = Simplified(TriangleMesh(positions, faces, local, cornerIds), simplify);
        if (type == PhysicsType.ConvexSingle)
            return [[.. points]];
        if (type != PhysicsType.ConvexMulti)
            return [];
        var result = new List<Vector3[]>();
        foreach (var group in Groups(points.Count, triangles))
            result.Add([.. GroupVertices(group, triangles).Select(v => points[(int)v])]);
        return result;
    }

    /// <summary>
    /// physicsbuilder's convex_multi groups (PhysicsBuilder_ConvexMulti
    /// 18001ac20 through 1800d33e0 and 1800d39a0), on a per-material CMesh as
    /// it stands: a union-find over the vertex buffer joined by each
    /// triangle's three corners; groups numbered by their lowest vertex, each
    /// group's vertices in buffer order. A vertex no triangle uses is a group
    /// of its own. The map builder's brush entity grouping (<see cref="Inputs"/>)
    /// is another function.
    /// </summary>
    public static List<Vector3[]> BufferGroups(Vector3[] points, int[] indices)
    {
        var parent = new int[points.Length];
        for (var i = 0; i < parent.Length; i++)
            parent[i] = i;
        int Find(int x)
        {
            while (parent[x] != x)
                x = parent[x] = parent[parent[x]];
            return x;
        }
        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            int a = Find(indices[t]), b = Find(indices[t + 1]), c = Find(indices[t + 2]);
            parent[b] = a;
            parent[Find(c)] = Find(a);
        }
        var groupOf = new Dictionary<int, int>();
        var groups = new List<List<Vector3>>();
        for (var v = 0; v < points.Length; v++)
        {
            var root = Find(v);
            if (!groupOf.TryGetValue(root, out var g))
            {
                g = groupOf[root] = groups.Count;
                groups.Add([]);
            }
            groups[g].Add(points[v]);
        }
        return [.. groups.Select(g => g.ToArray())];
    }

    /// <summary>
    /// The triangle mesh FUN_181308060 is handed: vertices in the order the
    /// triangles' corners meet them, and the triangles.
    /// </summary>
    /// <remarks><paramref name="firstCorner"/>, when given, receives the (face,
    /// corner) that made each vertex.</remarks>
    public static (List<Vector3> Points, List<(int A, int B, int C)> Triangles) TriangleMesh(Vector3[] positions, int[][] faces, Vector3[]? local = null, int[][]? cornerIds = null, List<(int Face, int Corner)>? firstCorner = null)
    {
        // The map builder cuts faces in the mesh's own space, before any
        // transform: <paramref name="local"/> when given.
        local ??= positions;
        // A brush entity piece's half-edge mesh joins coincident vertices
        // (HalfEdgeJoin) and drops a triangle repeating a vertex. A world mesh
        // shape's CMesh keeps each .vmap vertex apart, so two at one position
        // stay two (atixref, node 6617); with cornerIds the corners join by
        // .vmap vertex instead.
        var joined = cornerIds == null ? HalfEdgeJoin(positions, faces) : null;
        var byId = new Dictionary<int, int>();
        var f = -1;
        var points = new List<Vector3>();
        var triangles = new List<(int A, int B, int C)>();
        foreach (var face in faces)
        {
            f++;
            if (face.Length < 3)
                continue;
            if (joined != null && face.Distinct().Count() != face.Length)
                continue;
            // FUN_181310a90: a triangle as it stands, anything larger through
            // the triangulator; a face it cannot cut adds no vertices either.
            int[] cut = face.Length == 3 ? [0, 1, 2] : Maps.PolygonTriangulator.Triangulate([.. face.Select(v => local[v])]);
            if (cut.Length < 3)
                continue;
            var slot = new int[face.Length];
            // The source mesh is already triangulated, so vertices are
            // numbered in the order the triangles' corners meet them.
            foreach (var j in cut)
            {
                var p = positions[face[j]];
                int i;
                var id = cornerIds != null ? cornerIds[f][j] : joined![face[j]];
                if (!byId.TryGetValue(id, out i))
                {
                    i = points.Count;
                    firstCorner?.Add((f, j));
                    byId[id] = i;
                    points.Add(p);
                }
                slot[j] = i;
            }
            for (var j = 0; j + 2 < cut.Length; j += 3)
                triangles.Add((slot[cut[j]], slot[cut[j + 1]], slot[cut[j + 2]]));
        }
        return (points, triangles);
    }

    /// <summary>
    /// FUN_18131e190(mesh, 3, 1e-7), run on a brush entity piece's half-edge
    /// mesh after the conversion (1813319d0) made one vertex per welded vertex
    /// and dropped triangles repeating one. Each vertex in order is queried in
    /// a KD tree (<see cref="Maps.VertexKdTree"/>) over a box of +-1e-7, and
    /// merges into the first candidate the tree returns that is earlier, not
    /// merged away and closer than 1e-7, when FUN_1812f2390 mode 3 allows it:
    /// both vertices open (an edge with one face side, FUN_1812f1c60 and
    /// FUN_1812f1410) and no face shared (FUN_1812edc10). A vertex at a
    /// triangle whose two corners coincide therefore stays apart, and a later
    /// vertex there joins whichever the tree lists first (cs_script_demo's
    /// queen: Valve's 20,341 vertices and every index). Returns, per vertex,
    /// the vertex it ends up as.
    /// </summary>
    public static int[] HalfEdgeJoin(Vector3[] positions, int[][] faces)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(faces);
        var tol = BitConverter.Int32BitsToSingle(0x33d6bf95);
        var n = positions.Length;
        var kept = faces.Where(t => t.Length >= 3 && t.Distinct().Count() == t.Length).Select(t => t.ToArray()).ToList();
        var facesOf = new List<int>[n];
        for (var v = 0; v < n; v++)
            facesOf[v] = [];
        for (var t = 0; t < kept.Count; t++)
            foreach (var v in kept[t])
                facesOf[v].Add(t);
        // An edge is open when one face side holds it; a vertex is open with no
        // edges or one open edge.
        bool Open(int v)
        {
            if (facesOf[v].Count == 0)
                return true;
            var sides = new Dictionary<int, int>();
            foreach (var t in facesOf[v])
            {
                var face = kept[t];
                var at = Array.IndexOf(face, v);
                var next = face[(at + 1) % face.Length];
                var prev = face[(at + face.Length - 1) % face.Length];
                sides[next] = sides.GetValueOrDefault(next) + 1;
                sides[prev] = sides.GetValueOrDefault(prev) + 1;
            }
            return sides.Values.Any(c => c == 1);
        }
        var tree = new Maps.VertexKdTree(positions);
        var removed = new bool[n];
        var into = Enumerable.Range(0, n).ToArray();
        var found = new List<int>();
        for (var v = 0; v < n; v++)
        {
            var p = positions[v];
            found.Clear();
            tree.FindInBox(new Vector3(p.X - tol, p.Y - tol, p.Z - tol), new Vector3(p.X + tol, p.Y + tol, p.Z + tol), found);
            foreach (var j in found)
            {
                if (j >= v || removed[j])
                    continue;
                var q = positions[j];
                float dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
                if (!((dy * dy) + (dx * dx) + (dz * dz) < tol * tol))
                    continue;
                if (!Open(v) || !Open(j) || facesOf[v].Intersect(facesOf[j]).Any())
                    continue;
                foreach (var t in facesOf[v])
                    kept[t][Array.IndexOf(kept[t], v)] = j;
                facesOf[j].AddRange(facesOf[v]);
                facesOf[v].Clear();
                removed[v] = true;
                into[v] = j;
                break;
            }
        }
        // A vertex merged into one that later merged on follows it there.
        for (var v = 0; v < n; v++)
            while (into[into[v]] != into[v])
                into[v] = into[into[v]];
        return into;
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

    /// <summary>
    /// Each point list hulled by the map builder, then by <c>RnHullCreate</c>.
    /// A list the builder cannot hull, such as a single flat face, adds no
    /// hull at all: FUN_18131efc0 only appends on success, and the node list
    /// is made from what it appended (FUN_18131f680, FUN_1814e81b0).
    /// </summary>
    public static List<RnHull> Build(Vector3[] positions, int[][] faces, PhysicsType type, Vector3[]? local = null)
    {
        var hulls = new List<RnHull>();
        foreach (var input in Inputs(positions, faces, type, local))
        {
            var qh = RnHullBuilder.BuildHull(input, RnHullBuilder.Options.MapBuilder, out _);
            if (qh == null)
                continue;
            var vertices = ShapePoints(qh.HullVertices.Select(v => new Vector3(v.X, v.Y, v.Z)).ToArray());
            if (vertices == null)
                continue;
            var hull = RnHullBuilder.Create(vertices, RnHullBuilder.Options.Compile, out _);
            if (hull == null)
                continue;
            hull.RegionSvm = RegionSvmBuilder.Build(hull);
            RnHullBuilder.Transform(hull, RnHullBuilder.Identity);
            hulls.Add(hull);
        }
        return hulls;
    }
}
