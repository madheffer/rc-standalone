using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// BakeSubdivisionForFaces (resourcecompiler 0923: 1810c65c0 -> 1813baa40) on
/// a <see cref="HalfEdgeMesh"/> built from the .vmap's own half-edge arrays.
/// <list type="number">
/// <item>Faces with a level of 1 to 5 are gathered level by level, each in face
/// order, and each face's patch grids (positions with displacement, paint)
/// are computed from its corners before anything is split.</item>
/// <item>Each face is split (1813ca560): the point halfway along each side by
/// arc length (1813ba570), the corners cut off to those points, the cut edges
/// halved, their midpoints joined, the inner face collapsed to the centre;
/// the corner regions recurse, children in corner order 0, 1, 3, 2. Each
/// patch's grid points are recorded as they are made.</item>
/// <item>Each cell is cut on its diagonal from (r, c) to (r + 1, c + 1),
/// patch by patch and row by row.</item>
/// <item>Once every face is split, each face's grid points take its patch
/// grids' positions and paint, faces in gathering order: the last write
/// wins.</item>
/// </list>
/// The export then walks the dense face array, each face from its first
/// half-edge.
/// </summary>
internal static class SubdivisionBake
{
    /// <param name="cutSpace">Where the export's polygon cutter scores corners: a world
    /// mesh's faces are cut on their world positions (as <see cref="MapMeshCorners"/>).</param>
    /// <param name="cornerData">Each corner's face-vertex streams (by its
    /// edgeVertexDataIndices entry), carried through the splits as paint is and
    /// returned per triangle corner; null to leave them out.</param>
    /// <param name="layout">The face-vertex streams' DMX names and widths in
    /// <paramref name="cornerData"/>'s order; with a normal among them the baked
    /// faces' corner normals are recomputed.</param>
    /// <param name="smoothingAngle">The mesh's smoothingAngle.</param>
    // Experiment (S2C_SUBDIV_TANGENT): the grid tangent made orthogonal to the grid normal:
    // "gs" t - n (n.t), "gsn" with n normalised first, "gsu" then t normalised.
    private static void OrthogonaliseTangent(float[] point, IReadOnlyList<(string Name, int Width)> layout, string mode)
    {
        int Offset(string prefix)
        {
            var at = 0;
            foreach (var (name, width) in layout)
            {
                if (name.StartsWith(prefix, StringComparison.Ordinal))
                    return at;
                at += width;
            }
            return -1;
        }
        var no = Offset("normal");
        var to = Offset("tangent");
        if (no < 0 || to < 0)
            return;
        var n = new Vector3(point[no], point[no + 1], point[no + 2]);
        if (mode is "gsn" or "gsu")
            n = TJunctionFix.Normalise(n);
        var t = new Vector3(point[to], point[to + 1], point[to + 2]);
        var d = (n.X * t.X + n.Y * t.Y) + n.Z * t.Z;
        t = new Vector3(t.X - n.X * d, t.Y - n.Y * d, t.Z - n.Z * d);
        if (mode == "gsu")
            t = TJunctionFix.Normalise(t);
        (point[to], point[to + 1], point[to + 2]) = (t.X, t.Y, t.Z);
    }

    public static MeshTessellation.Result Bake(DmxBinary.Element data, Func<Vector3, Vector3>? cutSpace = null, Func<int, float[]>? cornerData = null,
                                               bool displace = true, IReadOnlyList<(string Name, int Width)>? layout = null, float smoothingAngle = 40f,
                                               bool shiftTexcoords = false)
    {
        var a = new FaceArrays(data);
        var mesh = new HalfEdgeMesh();
        var edgeData = (data.Get<object?[]>("edgeDataIndices") ?? []).Select(x => x is int i ? i : -1).ToArray();
        var edgeFlags = (data.Get<DmxBinary.Element>("edgeData")?.GetElements("streams").FirstOrDefault(x => x.Name.StartsWith("flags", StringComparison.Ordinal))
                         ?.Get<object?[]>("data") ?? []).Select(x => x is int i ? i : 0).ToArray();
        int Flags(int h) => h < edgeData.Length && edgeData[h] >= 0 && edgeData[h] < edgeFlags.Length ? edgeFlags[edgeData[h]] : 0;
        // The .vmap's arrays in order: element i gets handle i.
        for (var v = 0; v < a.VertexCount; v++)
            mesh.Vertices.Add(new HalfEdgeMesh.Vertex { Position = a.Pos(v) });
        for (var h = 0; h < a.To.Length; h++)
            mesh.HalfEdges.Add(new HalfEdgeMesh.HalfEdge { Vertex = a.To[h], Twin = a.Opposite[h], Next = a.Next[h], Face = a.EdgeFace[h] < 0 ? HalfEdgeMesh.Null : a.EdgeFace[h], Paint = a.Paint(h), Data = cornerData?.Invoke(a.CornerData[h]), Flags = Flags(h) });
        for (var f = 0; f < a.First.Length; f++)
            mesh.Faces.Add(new HalfEdgeMesh.Face { First = a.First[f], Source = f });
        for (var h = 0; h < a.To.Length; h++)
        {
            var from = a.To[a.Opposite[h]];
            if (mesh.Vertices[from].Out == HalfEdgeMesh.Null)
                mesh.Vertices[from].Out = h;
        }

        // ConvertMeshForBuilder's RemoveBadFaces (1810d9ca0) runs before the bake: each face, by
        // handle, that the polygon triangulator cannot cut whole on the mesh's own positions
        // (1813a5ec0) is removed (1813acf00 -> 1813aca90 with orphans removed).
        for (var f = 0; f < a.First.Length; f++)
        {
            var loop = a.Loop(f);
            if (loop.Count < 3 || PolygonTriangulator.Triangulate([.. loop.Select(h => a.Pos(a.To[h]))]).Length != (loop.Count - 2) * 3)
                RemoveFace(mesh, f);
        }

        // 1. Gather by level, faces in dense order within a level (HammerMesh_BakeAllSubdivision
        // 1810c6520 lists them from the dense face array, 180d54520); grids from the corners.
        var order = new List<int>();
        var dense = mesh.Faces.Handles.ToArray();
        for (var level = 1; level <= 5; level++)
            foreach (var f in dense)
                if (a.Level(a.First[f]) == level)
                    order.Add(f);
        var jobs = order.Select(f =>
        {
            var hs = a.Loop(f);
            var level = a.Level(a.First[f]);
            var grids = new Vector3[hs.Count][];
            var paints = new Vector4[hs.Count][];
            for (var i = 0; i < hs.Count; i++)
            {
                grids[i] = a.Grid(hs, i, level);
                if (displace && a.Displacement.Length > 0)
                    a.Displace(grids[i], hs, i, f);
                paints[i] = a.PaintGrid(hs, i, level);
            }
            var data = cornerData != null && layout != null ? Enumerable.Range(0, hs.Count).Select(i => a.DataGrid(hs, i, level, cornerData, layout)).ToArray() : null;
            if (data != null && Environment.GetEnvironmentVariable("S2C_SUBDIV_TANGENT") is { Length: > 0 } mode)
                foreach (var grid in data)
                    foreach (var point in grid)
                        OrthogonaliseTangent(point, layout!, mode);
            return (Face: f, Corners: hs.Select(h => a.To[h]).ToArray(), Level: level, Grids: grids, Paints: paints, Data: data, Points: new int[hs.Count][]);
        }).ToList();

        // 2. and 3. Split each face, then cut its cells.
        foreach (var job in jobs)
        {
            var n = 1 << (job.Level - 1);
            var g = n + 1;
            for (var k = 0; k < job.Corners.Length; k++)
                job.Points[k] = Enumerable.Repeat(HalfEdgeMesh.Null, g * g).ToArray();
            Split(mesh, job.Face, job.Corners, job.Level, 0, job.Points, null, n);
            // Each cell's corners take the patch's grids of paint and of every
            // face-vertex stream (1813c94f0 writes them all), before the cells
            // are cut.
            for (var k = 0; k < job.Corners.Length; k++)
            {
                var grid = job.Points[k];
                for (var r = 0; r < n; r++)
                    for (var c = 0; c < n; c++)
                    {
                        int[] at = [(r * g) + c, (r * g) + c + 1, ((r + 1) * g) + c + 1, ((r + 1) * g) + c];
                        var cell = FaceWith(mesh, grid[at[0]], grid[at[2]]);
                        if (cell == HalfEdgeMesh.Null)
                            continue;
                        foreach (var i in at)
                        {
                            var corner = mesh.Corner(cell, grid[i]);
                            if (corner != HalfEdgeMesh.Null)
                            {
                                mesh.He(corner).Paint = job.Paints[k][i];
                                if (job.Data != null)
                                {
                                    mesh.He(corner).Data = job.Data[k][i];
                                }
                            }
                        }
                    }
            }
            for (var k = 0; k < job.Corners.Length; k++)
            {
                var grid = job.Points[k];
                for (var r = 0; r < n; r++)
                    for (var c = 0; c < n; c++)
                    {
                        int p = grid[(r * g) + c], q = grid[((r + 1) * g) + c + 1];
                        var cell = FaceWith(mesh, p, q);
                        if (cell != HalfEdgeMesh.Null)
                            AddEdgeToFace(mesh, cell, p, q);
                    }
            }
        }

        // 4. Positions and paint from the grids, faces in order; last write wins.
        long writes = 0;
        foreach (var job in jobs)
            for (var k = 0; k < job.Corners.Length; k++)
                for (var i = 0; i < job.Points[k].Length; i++)
                {
                    var v = job.Points[k][i];
                    if (v == HalfEdgeMesh.Null)
                        continue;
                    mesh.Vertices[v].Position = job.Grids[k][i];
                    mesh.Vertices[v].Written = writes++;
                }

        // The wrapper then merges the baked faces' vertices that lie within
        // 1e-6 of each other (HalfEdge_MergeVertices 1813a8ae0).
        BeforeMerge?.Invoke(mesh);
        MergeVertices(mesh, a);
        AfterMerge?.Invoke(mesh);

        // 5. The baked faces' corner normals, as BakeSubdivisionForFaces
        // (1810c65c0) leaves them: its new edges soft, then 1810cddd0 on the
        // vertices of the baked faces.
        var normalAt = layout == null ? -1 : NormalOffset(layout);
        if (cornerData != null && normalAt >= 0)
        {
            foreach (var h in mesh.HalfEdges.Handles)
                if (mesh.He(h).Added)
                    mesh.He(h).Flags = (mesh.He(h).Flags & ~1) | 2;
            var baked = new SortedSet<int>();
            foreach (var f in mesh.Faces.Handles)
                if (a.Level(a.First[mesh.Faces[f].Source]) > 0)
                    foreach (var h in mesh.Loop(f))
                        baked.Add(mesh.He(h).Vertex);
            var cos = Simulation.CrtMath.Cos(MathF.Min(smoothingAngle, 180f) * 0.017453292f);
            foreach (var v in baked)
                RecomputeNormals(mesh, v, cos, normalAt);
        }

        if (cornerData != null && layout != null && shiftTexcoords)
            ShiftTexcoords(mesh, layout);

        return Export(mesh, a.WithPaint, cutSpace, cornerData != null);
    }

    /// <summary>
    /// <see cref="MapMeshCorners.ShiftTexcoords"/> on the baked mesh, which is
    /// what ConvertMeshForBuilder exports: faces in dense order, each corner
    /// its own texcoord, islands across edges whose ends' texcoords agree.
    /// </summary>
    private static void ShiftTexcoords(HalfEdgeMesh mesh, IReadOnlyList<(string Name, int Width)> layout)
    {
        var sets = new List<int>();
        var at = 0;
        foreach (var (name, width) in layout)
        {
            if (name.Split(':')[0] == "texcoord" && width == 2)
                sets.Add(at);
            at += width;
        }
        if (sets.Count == 0)
            return;
        var faces = mesh.Faces.Handles.ToList();
        var faceIndex = new Dictionary<int, int>();
        for (var i = 0; i < faces.Count; i++)
            faceIndex[faces[i]] = i;
        var edges = mesh.HalfEdges.Handles.ToList();
        var prev = new Dictionary<int, int>();
        foreach (var h in edges)
            if (mesh.He(h).Face != HalfEdgeMesh.Null)
                prev[mesh.He(h).Next] = h;
        foreach (var h in edges)
            if (mesh.He(h).Data != null)
                mesh.He(h).Data = (float[])mesh.He(h).Data!.Clone();
        Vector2 Uv(int h, int set) => new(mesh.He(h).Data![set], mesh.He(h).Data![set + 1]);
        if (!edges.Any(h => mesh.He(h).Face != HalfEdgeMesh.Null && sets.Any(s => Uv(h, s) is var uv
                && (uv.X < -1.03125f || 1.03125f < uv.X || uv.Y < -1.03125f || 1.03125f < uv.Y))))
            return;
        foreach (var set in sets)
        {
            bool Close(int a, int b)
            {
                var x = Uv(a, set);
                var y = Uv(b, set);
                return !((((x.Y - y.Y) * (x.Y - y.Y)) + ((x.X - y.X) * (x.X - y.X))) > 1e-6f);
            }
            var parent = Enumerable.Range(0, faces.Count).ToArray();
            int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
            foreach (var e in edges)
            {
                var o = mesh.He(e).Twin;
                if (mesh.He(e).Face == HalfEdgeMesh.Null || mesh.He(o).Face == HalfEdgeMesh.Null)
                    continue;
                if (Close(e, prev[o]) && Close(prev[e], o))
                    parent[Find(faceIndex[mesh.He(o).Face])] = Find(faceIndex[mesh.He(e).Face]);
            }
            foreach (var island in Enumerable.Range(0, faces.Count).GroupBy(Find))
            {
                var members = island.SelectMany(i => mesh.Loop(faces[i])).ToList();
                float minU = float.MaxValue, minV = float.MaxValue, maxU = -float.MaxValue, maxV = -float.MaxValue;
                foreach (var h in members)
                {
                    var uv = Uv(h, set);
                    if (uv.X <= minU) minU = uv.X;
                    if (maxU <= uv.X) maxU = uv.X;
                    if (uv.Y <= minV) minV = uv.Y;
                    if (maxV <= uv.Y) maxV = uv.Y;
                }
                if (!(minU < 0f || minV < 0f || 1f < maxU || 1f < maxV))
                    continue;
                var cu = (maxU + minU) * 0.5f;
                var cv = (maxV + minV) * 0.5f;
                var su = (float)(int)(cu < 0f ? cu - 0.5f : cu + 0.5f);
                var sv = (float)(int)(cv < 0f ? cv - 0.5f : cv + 0.5f);
                if (su == 0f && sv == 0f)
                    continue;
                foreach (var h in members)
                {
                    var d = mesh.He(h).Data!;
                    d[set] -= su;
                    d[set + 1] -= sv;
                }
            }
        }
    }

    private static int NormalOffset(IReadOnlyList<(string Name, int Width)> layout)
    {
        var at = 0;
        foreach (var (name, width) in layout)
        {
            if (name.Split(':')[0] == "normal")
                return at;
            at += width;
        }
        return -1;
    }

    /// <summary>181384e40: a face's Newell normal over its loop from its first half-edge.</summary>
    private static Vector3 FaceNormal(HalfEdgeMesh mesh, int face)
        => PolygonTriangulator.Newell([.. mesh.Loop(face).Select(h => mesh.Vertices[mesh.He(h).Vertex].Position)]);

    /// <summary>
    /// 1813a5ce0 on the edge of half-edge <paramref name="h"/>: an open edge is
    /// hard, then the flags (bit 0 hard, bit 1 soft), then the smoothing angle:
    /// its cosine + 1e-5 under the dot of the two faces' normals.
    /// </summary>
    private static bool Smooth(HalfEdgeMesh mesh, int h, float cos)
    {
        var t = mesh.He(h).Twin;
        if (mesh.He(h).Face == HalfEdgeMesh.Null || mesh.He(t).Face == HalfEdgeMesh.Null)
            return false;
        var flags = mesh.He(h).Flags;
        if ((flags & 1) != 0)
            return false;
        if ((flags & 2) != 0)
            return true;
        if (!(cos <= 0.99999f))
            return false;
        if (cos < 1e-5f)
            return true;
        var e = Math.Min(h, t);
        var (n1, n2) = (FaceNormal(mesh, mesh.He(e).Face), FaceNormal(mesh, mesh.He(mesh.He(e).Twin).Face));
        return cos + 1e-5f < (n1.Y * n2.Y) + (n1.Z * n2.Z) + (n1.X * n2.X);
    }

    /// <summary>
    /// 1813850e0 for each corner at <paramref name="v"/>: corners turn about
    /// the vertex (a corner to the twin of its next); the fan holding the
    /// corner runs from the turn after the last hard edge up to the first,
    /// and the corner's normal is the normalised sum of its faces' normals.
    /// </summary>
    private static void RecomputeNormals(HalfEdgeMesh mesh, int v, float cos, int at)
    {
        int Turn(int c) => mesh.He(mesh.He(c).Next).Twin;
        var corners = new List<int>();
        var start = mesh.Vertices[v].Out;
        var o = start;
        do
        {
            var t = mesh.He(o).Twin;
            if (mesh.He(t).Face != HalfEdgeMesh.Null)
                corners.Add(t);
            o = mesh.He(t).Next;
        }
        while (o != start);
        var normals = new List<(int Corner, Vector3 Normal)>();
        foreach (var c0 in corners)
        {
            int s = c0, end = c0, c = c0;
            var hard = false;
            do
            {
                c = Turn(c);
                if (!hard)
                    end = c;
                if (!Smooth(mesh, c, cos))
                {
                    hard = true;
                    s = c;
                }
            }
            while (c != c0);
            float x = 0f, y = 0f, z = 0f;
            var k = s;
            do
            {
                if (mesh.He(k).Face != HalfEdgeMesh.Null)
                {
                    var n = FaceNormal(mesh, mesh.He(k).Face);
                    x += n.X;
                    y += n.Y;
                    z += n.Z;
                }
                k = Turn(k);
            }
            while (k != end);
            normals.Add((c0, NodeMeshEntries.Normalise(new Vector3(x, y, z))));
        }
        foreach (var (c0, n) in normals)
        {
            var d = (float[])mesh.He(c0).Data!.Clone();
            (d[at], d[at + 1], d[at + 2]) = (n.X, n.Y, n.Z);
            mesh.He(c0).Data = d;
        }
    }

    // The patch-local grid coordinates of a quad's corners during the recursion.
    private readonly record struct Cell(int Patch, (int R, int C)[] At);

    /// <summary>
    /// 1813ca560 on <paramref name="face"/> with corners <paramref name="c"/>.
    /// At the top (<paramref name="where"/> null) corner k's patch runs from
    /// the corner (0, 0) along the next side (columns) and the previous side
    /// (rows) to the centre (n, n); below it each quad's corners carry their
    /// grid coordinates.
    /// </summary>
    private static void Split(HalfEdgeMesh mesh, int face, int[] c, int level, int depth, int[][] points, Cell? where, int n)
    {
        var m = c.Length;
        if (!(m > 2 && depth < level && (depth == 0 || m == 4)))
            return;
        // The point halfway along each side, from the side ending at corner 0.
        var mid = new int[m];
        for (var j = 0; j < m; j++)
        {
            var prev = (j + m - 1) % m;
            mid[prev] = mesh.SplitBetween(face, c[prev], c[j], 0.5f);
            if (mid[prev] == HalfEdgeMesh.Null)
                return;
        }
        // The corners cut off to those points; each cut edge halved.
        var e = new int[m];
        for (var k = 0; k < m; k++)
        {
            int from = mid[(k + m - 1) % m], to = mid[k];
            var f = FaceWith(mesh, from, to);
            if (f != HalfEdgeMesh.Null)
                AddEdgeToFace(mesh, f, from, to);
            e[k] = mesh.AddVertexToEdge(from, to, 0.5f);
        }
        // The halves' midpoints joined; the inner face collapsed.
        for (var k = 0; k < m; k++)
        {
            int from = e[(k + m - 1) % m], to = e[k];
            var f = FaceWith(mesh, from, to);
            if (f != HalfEdgeMesh.Null)
                AddEdgeToFace(mesh, f, from, to);
        }
        var centre = CollapseInner(mesh, e);

        if (where is null)
        {
            // Patch k: its corner, the next side's point, the centre, the previous side's point.
            for (var k = 0; k < m; k++)
            {
                var g = n + 1;
                var grid = points[k];
                grid[0] = c[k];
                grid[n] = mid[k];
                grid[(n * g) + n] = centre;
                grid[n * g] = mid[(k + m - 1) % m];
                var quad = new[] { c[k], mid[k], centre, mid[(k + m - 1) % m] };
                var at = new (int R, int C)[] { (0, 0), (0, n), (n, n), (n, 0) };
                var child = FaceWith(mesh, c[k], centre);
                Split(mesh, child, quad, level, depth + 1, points, new Cell(k, at), n);
            }
            return;
        }

        // A quad below the top: child k's corner j lies between corners k and j.
        var q = where.Value.At;
        var grid2 = points[where.Value.Patch];
        var gw = n + 1;
        (int R, int C) Half((int R, int C) x, (int R, int C) y) => ((x.R + y.R) / 2, (x.C + y.C) / 2);
        for (var j = 0; j < 4; j++)
        {
            var at = Half(q[j], q[(j + 1) % 4]);
            grid2[(at.R * gw) + at.C] = mid[j];
        }
        var middle = Half(q[0], q[2]);
        grid2[(middle.R * gw) + middle.C] = centre;
        int Vertex(int k, int j) => j == k ? c[k] : j == (k + 1) % 4 ? mid[k] : j == (k + 3) % 4 ? mid[(k + 3) % 4] : centre;
        foreach (var k in new[] { 0, 1, 3, 2 })
        {
            var quad = new int[4];
            var at = new (int R, int C)[4];
            for (var j = 0; j < 4; j++)
            {
                quad[j] = Vertex(k, j);
                at[j] = Half(q[k], q[j]);
            }
            var child = FaceWith(mesh, c[k], centre);
            Split(mesh, child, quad, level, depth + 1, points, new Cell(where.Value.Patch, at), n);
        }
    }

    /// <summary>18138f9f0: the face holding both vertices.</summary>
    private static int FaceWith(HalfEdgeMesh mesh, int a, int b)
    {
        var start = mesh.Vertices[a].Out;
        if (start == HalfEdgeMesh.Null)
            return HalfEdgeMesh.Null;
        var h = start;
        do
        {
            var f = mesh.He(h).Face;
            if (f != HalfEdgeMesh.Null && mesh.Loop(f).Any(x => mesh.He(x).Vertex == b))
                return f;
            h = mesh.He(mesh.He(h).Twin).Next;
        }
        while (h != start);
        return HalfEdgeMesh.Null;
    }

    /// <summary>181376f80 -> 181376810: an edge from a to b across <paramref name="face"/>, unless one is there.</summary>
    private static int AddEdgeToFace(HalfEdgeMesh mesh, int face, int a, int b)
    {
        HalfEdgeMesh.OpLog?.Invoke(mesh, "AddEdgeToFace", [face, a, b]);
        if (a == b || mesh.Between(a, b) != HalfEdgeMesh.Null)
            return HalfEdgeMesh.Null;
        int ca = mesh.Corner(face, a), cb = mesh.Corner(face, b);
        if (ca == HalfEdgeMesh.Null || cb == HalfEdgeMesh.Null)
            return HalfEdgeMesh.Null;
        return mesh.AddEdge(ca, cb);
    }

    /// <summary>
    /// CollapseFace (181384470) on the inner face (bounded by <paramref name="e"/>):
    /// its core (181384060) lists the face's vertices from its first half-edge,
    /// then from the first vertex collapses, in order, the edge to each next
    /// vertex (1813937a0, taken as the pair's lower half-edge handle) by the
    /// edge collapse (<see cref="Collapse"/>), continuing from the vertex each
    /// returns; a refused collapse ends it. The wrapper's vertex snapshot (the
    /// face vertices' average, 18137ea40) is overwritten by the patch grid
    /// writes, so the centre's position is left to them.
    /// </summary>
    private static int CollapseInner(HalfEdgeMesh mesh, int[] e)
    {
        var inner = mesh.He(mesh.Between(e[0], e[1])).Face;
        HalfEdgeMesh.OpLog?.Invoke(mesh, "CollapseFace", [inner]);
        var verts = mesh.Loop(inner).Select(h => mesh.He(h).Vertex).ToArray();
        var cur = verts[0];
        for (var i = 1; i < verts.Length; i++)
        {
            var edge = mesh.Between(cur, verts[i]);
            if (edge == HalfEdgeMesh.Null)
                continue;
            var twin = mesh.He(edge).Twin;
            if (twin <= edge)
                edge = twin;
            var v = Collapse(mesh, edge);
            if (v == HalfEdgeMesh.Null)
                break;
            cur = v;
        }
        return cur;
    }

    /// <summary>
    /// The export: faces in dense order, each from its first half-edge; a face
    /// of more than three corners is cut by <see cref="PolygonTriangulator"/>,
    /// its triangles together.
    /// </summary>
    /// <summary>For tests: the mesh as the vertex merge leaves it.</summary>
    internal static Action<HalfEdgeMesh>? AfterMerge;

    /// <summary>For tests: the mesh as the bake leaves it, before the vertex merge.</summary>
    internal static Action<HalfEdgeMesh>? BeforeMerge;

    /// <summary>
    /// <c>HalfEdge_MergeVertices</c> (1813a8ae0) as the bake wrapper calls it
    /// (tolerance 1e-6, averaging on). The baked faces' vertices are listed
    /// (faces in dense order, each loop from its first, each vertex once) and
    /// up to ten passes run: each clusters the list (a vertex seeds a cluster
    /// in list order and takes every unclaimed one within the tolerance,
    /// (dx dx + dy dy) + dz dz &lt;= tol tol, in list order), merges each
    /// cluster's others into its first (<see cref="MergePair"/>, retried while a
    /// round both merged and failed), sets each cluster's vertex to its sum
    /// times 1/count, and lists the survivors for the next pass: members that
    /// failed, not the merged vertex (Valve's is a new vertex the list does
    /// not hold). Captured on c2m2_fairgrounds_csgo (capture_bake.py).
    /// </summary>
    private static void MergeVertices(HalfEdgeMesh mesh, FaceArrays a)
    {
        const float Tolerance = 1e-6f;
        var limit = Tolerance * Tolerance;
        var list = new List<int>();
        var seen = new HashSet<int>();
        foreach (var f in mesh.Faces.Handles)
            if (a.Level(a.First[mesh.Faces[f].Source]) > 0)
                foreach (var h in mesh.Loop(f))
                    if (seen.Add(mesh.He(h).Vertex))
                        list.Add(mesh.He(h).Vertex);

        for (var pass = 0; pass < 10 && list.Count >= 2; pass++)
        {
            var at = list.Select(v => mesh.Vertices[v].Position).ToArray();
            var byX = Enumerable.Range(0, list.Count).OrderBy(i => at[i].X).ThenBy(i => i).ToArray();
            var xs = byX.Select(i => at[i].X).ToArray();
            var claimed = new bool[list.Count];
            var clusters = new List<List<int>>();
            for (var i = 0; i < list.Count; i++)
            {
                if (claimed[i])
                    continue;
                claimed[i] = true;
                var p = at[i];
                Vector3 lo = new(p.X - Tolerance, p.Y - Tolerance, p.Z - Tolerance), hi = new(p.X + Tolerance, p.Y + Tolerance, p.Z + Tolerance);
                var found = new List<int>();
                var k = Array.BinarySearch(xs, lo.X);
                for (k = k < 0 ? ~k : k; k > 0 && xs[k - 1] >= lo.X; k--)
                {
                }
                for (; k < xs.Length && xs[k] <= hi.X; k++)
                {
                    var j = byX[k];
                    var q = at[j];
                    if (claimed[j] || q.Y < lo.Y || q.Y > hi.Y || q.Z < lo.Z || q.Z > hi.Z)
                        continue;
                    float dx = q.X - p.X, dy = q.Y - p.Y, dz = q.Z - p.Z;
                    if ((dx * dx) + (dy * dy) + (dz * dz) <= limit)
                        found.Add(j);
                }
                found.Sort();
                foreach (var j in found)
                    claimed[j] = true;
                clusters.Add([i, .. found]);
            }

            var count = new int[clusters.Count];
            var sum = new Vector3[clusters.Count];
            var open = new bool[clusters.Count];
            var merged = new bool[list.Count];
            // The cluster's vertex: its first, then whatever each merge returns (1813a8ae0 passes
            // 1813a8840's out vertex as the next merge's first argument).
            var rep = clusters.Select(cl => list[cl[0]]).ToArray();
            for (var c = 0; c < clusters.Count; c++)
            {
                count[c] = 1;
                sum[c] = at[clusters[c][0]];
                open[c] = clusters[c].Count > 1;
            }
            for (var round = 0; round < list.Count; round++)
            {
                int done = 0, failed = 0;
                for (var c = 0; c < clusters.Count; c++)
                {
                    if (!open[c])
                        continue;
                    var fails = 0;
                    foreach (var j in clusters[c].Skip(1))
                    {
                        if (merged[j])
                            continue;
                        var other = at[j];
                        var collapses = mesh.Between(rep[c], list[j]) != HalfEdgeMesh.Null;
                        var into = MergePair(mesh, rep[c], list[j]);
                        HalfEdgeMesh.OpLog?.Invoke(mesh, collapses ? "MergeVertexPair(collapse)" : "MergeVertexPair", [rep[c], list[j], into]);
                        if (into != HalfEdgeMesh.Null)
                        {
                            rep[c] = into;
                            sum[c] = new Vector3(other.X + sum[c].X, other.Y + sum[c].Y, other.Z + sum[c].Z);
                            count[c]++;
                            merged[j] = true;
                            done++;
                        }
                        else
                        {
                            fails++;
                            failed++;
                        }
                    }
                    if (fails == 0)
                        open[c] = false;
                }
                if (failed == 0 || done == 0)
                    break;
            }
            var next = new List<int>();
            for (var c = 0; c < clusters.Count; c++)
            {
                var first = rep[c];
                var inv = 1f / count[c];
                mesh.Vertices[first].Position = new Vector3(inv * sum[c].X, inv * sum[c].Y, inv * sum[c].Z);
                // Valve's merged vertex is new and its old first is gone, so
                // only a cluster that merged nothing keeps its first listed.
                if (count[c] == 1)
                    next.Add(first);
                foreach (var j in clusters[c].Skip(1))
                    if (!merged[j])
                        next.Add(list[j]);
            }
            list = next;
        }
    }

    /// <summary>
    /// <c>MergeVertices</c> (1813a8840) through <c>HalfEdge_MergeFaces</c>
    /// (1813a7d00) for two vertices of the bake. Each must have exactly one
    /// boundary half-edge leaving it (1813861f0), and the faces inside those
    /// two must differ. When the far end of one's boundary half-edge reaches
    /// the other along a boundary edge, the two boundary edges are zipped into
    /// one; otherwise, unless the two share a neighbour (18137e2d0), the two
    /// boundary loops are spliced at the merged vertex. The merged vertex keeps
    /// <paramref name="keep"/>'s handle (Valve's is new; the export numbers
    /// vertices by first use, so the handle does not show). An edge from
    /// keep to gone (1813937a0) takes the edge collapse instead
    /// (<see cref="CollapseEdge"/>). Returns the merged vertex, or Null.
    /// </summary>
    private static int MergePair(HalfEdgeMesh mesh, int keep, int gone)
    {
        if (keep == gone)
            return keep;
        var edge = mesh.Between(keep, gone);
        if (edge != HalfEdgeMesh.Null)
            return CollapseEdge(mesh, edge, 0.5f);
        var outKeep = BoundaryOut(mesh, keep);
        var outGone = BoundaryOut(mesh, gone);
        if (outKeep == HalfEdgeMesh.Null || outGone == HalfEdgeMesh.Null)
            return HalfEdgeMesh.Null;
        if (mesh.He(mesh.He(outKeep).Twin).Face == mesh.He(mesh.He(outGone).Twin).Face)
            return HalfEdgeMesh.Null;

        // The far end of one boundary half-edge joined to the other by a
        // boundary edge: zip the two edges.
        var reach = mesh.Between(mesh.He(outKeep).Vertex, gone);
        if (reach != HalfEdgeMesh.Null)
        {
            if (mesh.He(reach).Face != HalfEdgeMesh.Null)
                return HalfEdgeMesh.Null;
            Zip(mesh, keep, gone, outKeep, reach);
            return keep;
        }
        reach = mesh.Between(mesh.He(outGone).Vertex, keep);
        if (reach != HalfEdgeMesh.Null)
        {
            if (mesh.He(reach).Face != HalfEdgeMesh.Null)
                return HalfEdgeMesh.Null;
            Zip(mesh, keep, gone, outGone, reach);
            return keep;
        }
        if (ShareNeighbour(mesh, keep, gone))
            return HalfEdgeMesh.Null;

        // Splice the boundary loops at the merged vertex.
        var intoKeep = mesh.Previous(outKeep);
        var intoGone = mesh.Previous(outGone);
        mesh.He(intoKeep).Next = outGone;
        mesh.He(intoGone).Next = outKeep;
        Repoint(mesh, gone, keep);
        return keep;
    }

    /// <summary>
    /// CollapseEdge (181383600) over CHalfEdgeMesh's collapse (181382170) for
    /// half-edge e (A to B), blending at t (0.5 with averaging, 1813a8ae0).
    /// The link test: for each neighbour X of A (circling A's out half-edges)
    /// with an edge X to B, the pair must be the other two edges of a
    /// triangle on e or its twin (either direction), or lie in one such face,
    /// else nothing changes (Null). Then a new vertex V (18137ad80) takes
    /// every half-edge into A, then into B (1813ab3b0, V's out the last one
    /// circled); e and its twin leave their loops (faces' firsts and V's out
    /// moved to e's next), A and B are freed (1813ae290), e and the twin
    /// freed in that order (1813ad160 -> 1813997a0); then each side that was
    /// a triangle, now a two-sided loop, is dissolved (<see cref="DissolveTwoGon"/>,
    /// e's side first). V's position is the blend of A and B (1813a3d50), and
    /// a face that survives on either side gets at V the blend of its two old
    /// corners (1813a3f40: e's face prev(e) then e, the twin's twin then
    /// prev(twin)), each (b - a) t + a.
    /// </summary>
    private static int CollapseEdge(HalfEdgeMesh mesh, int e, float t)
    {
        const int Null = HalfEdgeMesh.Null;
        var twin = mesh.He(e).Twin;
        int a = mesh.He(twin).Vertex, b = mesh.He(e).Vertex;
        int fE = mesh.He(e).Face, fT = mesh.He(twin).Face;
        // The snapshots, taken before anything moves.
        var position = HalfEdgeMesh.Lerp(mesh.Vertices[a].Position, mesh.Vertices[b].Position, t);
        int ePrev = mesh.Previous(e), tPrev = mesh.Previous(twin);
        var cornerE = (Paint: HalfEdgeMesh.Lerp(mesh.He(ePrev).Paint, mesh.He(e).Paint, t), Data: HalfEdgeMesh.Lerp(mesh.He(ePrev).Data, mesh.He(e).Data, t));
        var cornerT = (Paint: HalfEdgeMesh.Lerp(mesh.He(twin).Paint, mesh.He(tPrev).Paint, t), Data: HalfEdgeMesh.Lerp(mesh.He(twin).Data, mesh.He(tPrev).Data, t));
        // The bake's write stamp of the new vertex: the first argument's, as the merge path keeps it.
        var written = mesh.Vertices[a].Written;
        // The collapse itself runs on the pair's lower half-edge handle (181383600: the twin when its
        // index is not above e's, as 1810a8520 picks), the snapshots on e as given.
        var v = Collapse(mesh, twin <= e ? twin : e);
        if (v == Null)
            return Null;
        mesh.Vertices[v].Position = position;
        mesh.Vertices[v].Written = written;
        // The write-back of the corner snapshots where the faces survive (18138be80).
        if (fE != Null && mesh.Faces.Alive(fE) && mesh.Corner(fE, v) is var ce && ce != Null)
            (mesh.He(ce).Paint, mesh.He(ce).Data) = cornerE;
        if (fT != Null && mesh.Faces.Alive(fT) && mesh.Corner(fT, v) is var ct && ct != Null)
            (mesh.He(ct).Paint, mesh.He(ct).Data) = cornerT;
        return v;
    }

    /// <summary>181382170, CHalfEdgeMesh's edge collapse (the topology of <see cref="CollapseEdge"/>, no attributes).</summary>
    private static int Collapse(HalfEdgeMesh mesh, int e)
    {
        const int Null = HalfEdgeMesh.Null;
        var twin = mesh.He(e).Twin;
        int a = mesh.He(twin).Vertex, b = mesh.He(e).Vertex;
        int fE = mesh.He(e).Face, fT = mesh.He(twin).Face;
        int eN = Null, eP = Null, tN = Null, tP = Null;
        if (mesh.He(mesh.He(mesh.He(e).Next).Next).Next == e)
            (eN, eP) = (mesh.He(e).Next, mesh.He(mesh.He(e).Next).Next);
        if (mesh.He(mesh.He(mesh.He(twin).Next).Next).Next == twin)
            (tN, tP) = (mesh.He(twin).Next, mesh.He(mesh.He(twin).Next).Next);

        var start = mesh.Vertices[a].Out;
        var h = start;
        do
        {
            var found = mesh.Between(mesh.He(h).Vertex, b);
            if (found != Null)
            {
                int ht = mesh.He(h).Twin, ft = mesh.He(found).Twin;
                var matched = (h == eN && found == eP) || (h == eP && found == eN) || (h == tN && found == tP) || (h == tP && found == tN)
                              || (ft == eN && ht == eP) || (ft == eP && ht == eN) || (ft == tN && ht == tP) || (ft == tP && ht == tN);
                if (!matched)
                {
                    int hf = mesh.He(h).Face, ff = mesh.He(found).Face;
                    var inFace = hf == ff && hf != Null && ((hf == fE && (h == eN || h == eP)) || (hf == fT && (h == tN || h == tP)));
                    if (!inFace)
                    {
                        int tf = mesh.He(ft).Face, htf = mesh.He(ht).Face;
                        if (tf != htf || tf == Null)
                            return Null;
                        if (!((tf == fE && (ft == eN || ft == eP)) || (tf == fT && (ft == tN || ft == tP))))
                            return Null;
                    }
                }
            }
            h = mesh.He(mesh.He(h).Twin).Next;
        }
        while (h != start);

        var v = mesh.Vertices.Add(new HalfEdgeMesh.Vertex());
        Replace(a);
        Replace(b);
        int eNext = mesh.He(e).Next, ePrev = mesh.Previous(e), tNext = mesh.He(twin).Next, tPrev = mesh.Previous(twin);
        mesh.He(tPrev).Next = tNext;
        mesh.He(ePrev).Next = eNext;
        if (fE != Null)
            mesh.Faces[fE].First = eNext;
        if (fT != Null)
            mesh.Faces[fT].First = tNext;
        if (mesh.Vertices[v].Out == e || mesh.Vertices[v].Out == twin)
            mesh.Vertices[v].Out = eNext;
        mesh.Vertices.Remove(a);
        mesh.Vertices.Remove(b);
        mesh.HalfEdges.Remove(e);
        mesh.HalfEdges.Remove(twin);
        DissolveTwoGon(mesh, eN, eP);
        DissolveTwoGon(mesh, tN, tP);
        // 1813adf00 on both faces: a surviving face with a loop that doubles back on itself.
        foreach (var f in new[] { fE, fT })
            if (f != Null && mesh.Faces.Alive(f) && mesh.Loop(f).Any(x => mesh.He(x).Next == mesh.He(x).Twin))
                throw new NotSupportedException("subdivision bake: an edge collapse leaves a face folded on itself (1813adf00 -> 1813ad160), which is not ported");
        return v;

        void Replace(int old)
        {
            var first = mesh.Vertices[old].Out;
            var o = first;
            do
            {
                mesh.He(mesh.He(o).Twin).Vertex = v;
                mesh.Vertices[v].Out = o;
                o = mesh.He(mesh.He(o).Twin).Next;
            }
            while (o != first);
        }
    }

    /// <summary>
    /// 1813a71a0: a two-sided loop p (u to w), q (w to u), p.next q and q.next
    /// p in one face, with twins p' and q' distinct, is dissolved. The face is
    /// cleared and freed (181389630, 1813aca90). A new pair n1, n2
    /// (18137a900, n1 allocated first) takes p''s and q''s places: n1 copies
    /// p''s vertex, next and face, the half-edge before p' now leads to n1,
    /// n1's end vertex leaves by n2, and n1 becomes its face's first where p'
    /// was; then n2 likewise from q'. p, p', q, q' are freed in that order
    /// (1813997a0 twice). A pair left with no face on either side or doubling
    /// back on itself goes too (1813ad160), which is refused here. The pair's
    /// edge data and corners come through the mesh's attribute observer
    /// (+0xc0 vf 0x10 with p' and q'), taken here as n1 from p', n2 from q'.
    /// </summary>
    /// <summary>
    /// RemoveFace (1813acf00 -> 1813aca90, orphans removed): the face's loop
    /// is listed from its first; each half-edge in turn loses the face, and
    /// when its twin has no face either the edge goes, removed from the twin's
    /// side (<see cref="RemoveFreeEdge"/> on the twin); then the face is freed.
    /// </summary>
    private static void RemoveFace(HalfEdgeMesh mesh, int face)
    {
        foreach (var h in mesh.Loop(face).ToArray())
        {
            mesh.He(h).Face = HalfEdgeMesh.Null;
            if (mesh.He(mesh.He(h).Twin).Face == HalfEdgeMesh.Null)
                RemoveFreeEdge(mesh, mesh.He(h).Twin);
        }
        mesh.Faces.Remove(face);
    }

    /// <summary>
    /// 1813ad160 for an edge with no face on either side, orphans removed:
    /// each half (h, then its twin; 1813892f0) leaves its origin's fan. When
    /// the twin's next is h the origin keeps no edge: its out is cleared and
    /// the vertex freed (1813ae290). Otherwise the half-edge before h now
    /// leads to the twin's next, which becomes the origin's out, and the
    /// twin's next becomes h. Then h and its twin are freed (1813997a0).
    /// </summary>
    private static void RemoveFreeEdge(HalfEdgeMesh mesh, int h)
    {
        var t = mesh.He(h).Twin;
        void Unlink(int x, int xt)
        {
            var origin = mesh.He(xt).Vertex;
            if (mesh.He(xt).Next == x)
            {
                mesh.Vertices[origin].Out = HalfEdgeMesh.Null;
                mesh.Vertices.Remove(origin);
                return;
            }
            var before = mesh.Previous(x);
            mesh.He(before).Next = mesh.He(xt).Next;
            mesh.Vertices[origin].Out = mesh.He(xt).Next;
            mesh.He(xt).Next = x;
        }
        Unlink(h, t);
        Unlink(t, h);
        mesh.HalfEdges.Remove(h);
        mesh.HalfEdges.Remove(t);
    }

    private static void DissolveTwoGon(HalfEdgeMesh mesh, int p, int q)
    {
        const int Null = HalfEdgeMesh.Null;
        if (p == Null || q == Null || !mesh.HalfEdges.Alive(p) || !mesh.HalfEdges.Alive(q))
            return;
        if (mesh.He(p).Next != q || mesh.He(q).Next != p || mesh.He(p).Face != mesh.He(q).Face)
            return;
        int pt = mesh.He(p).Twin, qt = mesh.He(q).Twin;
        if (mesh.He(pt).Vertex != mesh.He(q).Vertex || mesh.He(qt).Vertex != mesh.He(p).Vertex || pt == qt)
            return;
        var face = mesh.He(p).Face;
        if (face != Null)
        {
            foreach (var x in mesh.Loop(face).ToArray())
                mesh.He(x).Face = Null;
            mesh.Faces.Remove(face);
        }
        var (n1, n2) = mesh.NewPair();
        void Take(int n, int old, int other)
        {
            var src = mesh.He(old);
            var dst = mesh.He(n);
            (dst.Vertex, dst.Next, dst.Face) = (src.Vertex, src.Next, src.Face);
            (dst.Paint, dst.Data, dst.Flags, dst.Added) = (src.Paint, src.Data, src.Flags, src.Added);
            mesh.He(mesh.Previous(old)).Next = n;
            mesh.Vertices[dst.Vertex].Out = other;
            if (dst.Face != Null && mesh.Faces[dst.Face].First == old)
                mesh.Faces[dst.Face].First = n;
        }
        Take(n1, pt, n2);
        Take(n2, qt, n1);
        mesh.HalfEdges.Remove(p);
        mesh.HalfEdges.Remove(pt);
        mesh.HalfEdges.Remove(q);
        mesh.HalfEdges.Remove(qt);
        if ((mesh.He(n1).Face == Null && mesh.He(n2).Face == Null) || mesh.He(n1).Next == n2 || mesh.He(n2).Next == n1)
            throw new NotSupportedException("subdivision bake: a dissolved two-sided loop leaves a free or folded edge (1813a71a0 -> 1813ad160), which is not ported");
    }

    // Two boundary half-edges, first (u to x) then second (x to w), with u
    // and w the vertices being merged: after the merge they run there and
    // back, so they go and their face half-edges become twins.
    private static void Zip(HalfEdgeMesh mesh, int keep, int gone, int first, int second)
    {
        var x = mesh.He(first).Vertex;
        Repoint(mesh, gone, keep);
        int faceA = mesh.He(first).Twin, faceB = mesh.He(second).Twin;
        var before = mesh.Previous(first);
        var after = mesh.He(second).Next;
        mesh.He(before).Next = after;
        mesh.He(faceA).Twin = faceB;
        mesh.He(faceB).Twin = faceA;
        foreach (var v in new[] { keep, x })
        {
            var o = mesh.Vertices[v].Out;
            if (o == first || o == second)
                mesh.Vertices[v].Out = mesh.He(faceA).Vertex == v ? faceB : faceA;
        }
        mesh.HalfEdges.Remove(first);
        mesh.HalfEdges.Remove(second);
    }

    // Every half-edge ending at gone ends at keep instead, and gone goes.
    private static void Repoint(HalfEdgeMesh mesh, int gone, int keep)
    {
        foreach (var h in mesh.HalfEdges.Handles)
            if (mesh.He(h).Vertex == gone)
                mesh.He(h).Vertex = keep;
        mesh.Vertices.Remove(gone);
    }

    // 1813861f0 and 1813920e0: the one boundary half-edge leaving v, or Null
    // when there is none or more than one.
    private static int BoundaryOut(HalfEdgeMesh mesh, int v)
    {
        var start = mesh.Vertices[v].Out;
        if (start == HalfEdgeMesh.Null)
            return HalfEdgeMesh.Null;
        int found = HalfEdgeMesh.Null, n = 0;
        var h = start;
        do
        {
            if (mesh.He(h).Face == HalfEdgeMesh.Null)
            {
                found = h;
                n++;
            }
            h = mesh.He(mesh.He(h).Twin).Next;
        }
        while (h != start);
        return n == 1 ? found : HalfEdgeMesh.Null;
    }

    // 18137e2d0: whether some neighbour of a has an edge to b.
    private static bool ShareNeighbour(HalfEdgeMesh mesh, int a, int b)
    {
        var start = mesh.Vertices[a].Out;
        var h = start;
        do
        {
            if (mesh.Between(mesh.He(h).Vertex, b) != HalfEdgeMesh.Null)
                return true;
            h = mesh.He(mesh.He(h).Twin).Next;
        }
        while (h != start);
        return false;
    }

    private static MeshTessellation.Result Export(HalfEdgeMesh mesh, bool withPaint, Func<Vector3, Vector3>? cutSpace, bool withData)
    {
        var positions = new List<Vector3>();
        var indices = new List<int>();
        var faces = new List<int>();
        var paint = withPaint ? new List<Vector4>() : null;
        var written = new List<long>();
        var data = withData ? new List<float[]>() : null;
        // Each baked vertex is its own, numbered as first met: two vertices at
        // one position stay two (the world mesh weld keeps vertices apart).
        var numbered = new Dictionary<int, int>();
        void Corner(int v, Vector4 cornerPaint, float[]? cornerData)
        {
            data?.Add(cornerData ?? []);
            if (!numbered.TryGetValue(v, out var index))
            {
                index = numbered[v] = positions.Count;
                positions.Add(mesh.Vertices[v].Position);
                written.Add(mesh.Vertices[v].Written);
            }
            indices.Add(index);
            paint?.Add(cornerPaint);
        }
        foreach (var f in mesh.Faces.Handles)
        {
            var hs = mesh.Loop(f).ToArray();
            var loop = hs.Select(h => mesh.He(h).Vertex).ToArray();
            var source = mesh.Faces[f].Source;
            if (loop.Length == 3)
            {
                foreach (var h in hs)
                    Corner(mesh.He(h).Vertex, mesh.He(h).Paint, mesh.He(h).Data);
                faces.Add(source);
                continue;
            }
            var corners = loop.Select(v => mesh.Vertices[v].Position);
            var cut = PolygonTriangulator.Triangulate([.. cutSpace == null ? corners : corners.Select(cutSpace)]);
            foreach (var j in cut)
                Corner(loop[j], mesh.He(hs[j]).Paint, mesh.He(hs[j]).Data);
            for (var t = 0; t < cut.Length / 3; t++)
                faces.Add(source);
        }
        return new MeshTessellation.Result(positions, indices, faces) { Paint = paint, Written = written, CornerData = data };
    }
}
