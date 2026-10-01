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

        // 1. Gather by level, face order within a level; grids from the corners.
        var order = new List<int>();
        for (var level = 1; level <= 5; level++)
            for (var f = 0; f < a.First.Length; f++)
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
            // Each cell's corners take the patch's paint grid (1813c94f0 on the
            // face-vertex stream), before the cells are cut.
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
                                    mesh.He(corner).Data = job.Data[k][i];
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
            var cos = MathF.Cos(MathF.Min(smoothingAngle, 180f) * 0.017453292f);
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
        if (a == b || mesh.Between(a, b) != HalfEdgeMesh.Null)
            return HalfEdgeMesh.Null;
        int ca = mesh.Corner(face, a), cb = mesh.Corner(face, b);
        if (ca == HalfEdgeMesh.Null || cb == HalfEdgeMesh.Null)
            return HalfEdgeMesh.Null;
        return mesh.AddEdge(ca, cb);
    }

    /// <summary>
    /// 181384470: the inner face (bounded by <paramref name="e"/>) and the m
    /// triangles between it and the corner regions collapse to one centre
    /// vertex. Faces leave the dense array as the edge collapses (181382170)
    /// empty them: the triangle at each step, the inner face with the
    /// second-last, the last two triangles last.
    /// </summary>
    private static int CollapseInner(HalfEdgeMesh mesh, int[] e)
    {
        var m = e.Length;
        // The inner face holds e[0] -> e[1]; the triangle beside it runs e[1] -> e[0].
        var inner = mesh.He(mesh.Between(e[0], e[1])).Face;
        // The triangles [e[k-1], mid, e[k]] by k.
        var triangles = new int[m];
        for (var k = 0; k < m; k++)
        {
            var from = e[(k + m - 1) % m];
            var h = mesh.Between(e[k], from);
            triangles[k] = mesh.He(h).Face;
        }
        var centre = mesh.Vertices.Add(new HalfEdgeMesh.Vertex());
        var gone = e.ToHashSet();
        // Each triangle's two outer edges become one edge from the centre.
        var dead = new List<int>();
        foreach (var t in triangles)
        {
            var loop = mesh.Loop(t).ToList();
            foreach (var h in loop)
                dead.Add(h);
            // loop: e[k-1] -> mid, mid -> e[k], e[k] -> e[k-1] (some rotation)
            var outer = loop.Where(h => !(gone.Contains(mesh.He(h).Vertex) && gone.Contains(mesh.He(mesh.He(h).Twin).Vertex))).ToList();
            var o1 = mesh.He(outer[0]).Twin;
            var o2 = mesh.He(outer[1]).Twin;
            mesh.He(o1).Twin = o2;
            mesh.He(o2).Twin = o1;
            foreach (var h in outer)
            {
                // h runs e -> mid: its twin, mid -> e, survives as the mid's outgoing edge.
                var toMid = mesh.He(h).Vertex;
                if (!gone.Contains(toMid))
                    mesh.Vertices[toMid].Out = mesh.He(h).Twin;
            }
        }
        foreach (var h in mesh.Loop(inner).ToList())
            dead.Add(h);
        foreach (var handle in mesh.HalfEdges.Handles.ToList())
        {
            if (dead.Contains(handle))
                continue;
            var he = mesh.He(handle);
            if (gone.Contains(he.Vertex))
                he.Vertex = centre;
        }
        mesh.Vertices[centre].Out = FindOut(mesh, centre, dead);
        foreach (var h in dead)
            mesh.HalfEdges.Remove(h);
        // Faces in the order the edge collapses empty them.
        for (var i = 1; i < m; i++)
        {
            mesh.Faces.Remove(triangles[i - 1]);
            if (i == m - 2)
                mesh.Faces.Remove(inner);
            if (i == m - 1)
                mesh.Faces.Remove(triangles[m - 1]);
        }
        if (m == 2)
            mesh.Faces.Remove(inner);
        foreach (var v in e)
            mesh.Vertices.Remove(v);
        return centre;
    }

    private static int FindOut(HalfEdgeMesh mesh, int v, List<int> dead)
    {
        foreach (var handle in mesh.HalfEdges.Handles)
        {
            if (dead.Contains(handle))
                continue;
            var t = mesh.He(handle).Twin;
            if (t != HalfEdgeMesh.Null && mesh.He(t).Vertex == v)
                return handle;
        }
        return HalfEdgeMesh.Null;
    }

    /// <summary>
    /// The export: faces in dense order, each from its first half-edge; a face
    /// of more than three corners is cut by <see cref="PolygonTriangulator"/>,
    /// its triangles together.
    /// </summary>
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
